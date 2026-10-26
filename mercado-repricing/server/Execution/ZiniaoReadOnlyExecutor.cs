using System.Globalization;
using System.Text.RegularExpressions;
namespace Repricing.Execution;
public record LegacyVariant(string Sku,string? Label,string? StockText);
public record ZiniaoProduct(string ItemId,string Sku,string Title,string BasePriceText,PromotionObservation[] Promotions,string[]? SharedSkus=null,string? ManagedPriceText=null,string? ListingStatus=null,string? VariantLabel=null,LegacyVariant[]? SharedVariants=null,string? PriceReadbackNote=null);
public interface IZiniaoReader
{
    void SetVerificationReporter(Action<VerificationUpdate>? report){}
    void SetExecutionReporter(Action<ExecutionMetric>? report){}
    Task<ZiniaoStore[]> StoresAsync(CancellationToken ct);
    Task<ZiniaoProduct[]> ReadAsync(ZiniaoStore store,PriceInput input,CancellationToken ct);
    Task<ZiniaoProduct[]> ReadManagerAsync(ZiniaoStore store,PriceInput input,CancellationToken ct)=>ReadAsync(store,input,ct);
    Task CloseAsync()=>Task.CompletedTask;
}
public class ZiniaoReadOnlyExecutor(IZiniaoReader reader):IPriceExecutor
{
    public static bool Scheduled(PromotionObservation p)=>!p.Active&&Regex.IsMatch(p.Status.Trim(),"^(已预约|Agendada|Agendado|Programada|Programado|Scheduled)$",RegexOptions.IgnoreCase);
    public static bool OrdinaryReplacement(PromotionObservation p,decimal target,bool allowScheduled)=>p.Id.StartsWith("C-",StringComparison.Ordinal)&&(p.Joinable||p.Editable&&Scheduled(p)&&(Money(p.PriceText)==target||allowScheduled));
    public static async Task<ZiniaoStore[]> FindStoresAsync(IZiniaoReader reader,string key,CancellationToken ct)
    {
        for(var attempt=0;;attempt++)
        {
            var stores=(await reader.StoresAsync(ct)).Where(s=>s.Id==key||s.Name==key).ToArray();
            if(stores.Length!=0||attempt==2)return stores;
            await Task.Delay(350,ct);
        }
    }
    public static bool UnderReview(ZiniaoProduct p)=>Regex.IsMatch(p.ListingStatus??"",@"(?:需)?审核未激活|审核未通过|em revis[aã]o|inativ[oa].*(marca|revis)",RegexOptions.IgnoreCase);
    public async Task<ExecutionResult> ExecuteAsync(PriceInput input,CancellationToken ct)
    {
        ExecutionResult Review(string message,PriceObservation? observation=null)=>new(RowStatus.NeedsReview,"只读核对",message+"；未修改价格或活动。","readonly-review",observation);
        if(input.Site!="MLB"||input.Currency!="BRL")return Review("目前仅适配巴西站页面");
        if(input.VariationId!=null)return Review("变体需要单独核对，当前不推断变体价格");
        try
        {
            var stores=await FindStoresAsync(reader,input.Store,ct);
            if(stores.Length!=1)return Review("店铺名称或 ID 未唯一匹配紫鸟列表，请填写完整名称");
            var store=stores[0];
            if(!store.Platform.Contains("MercadoLibre",StringComparison.OrdinalIgnoreCase)||!store.Platform.Contains("巴西"))return Review("紫鸟店铺平台与巴西站不一致");
            var products=await reader.ReadAsync(store,input,ct);
            if(products.Length==0)return new(RowStatus.Skipped,"只读核对","该店铺未找到此 SKU，已跳过；未修改价格或活动。","readonly-sku-missing");
            var rows=products.Where(p=>p.Sku==input.Sku&&(input.ItemId==null||NormalizeId(input.ItemId)==NormalizeId(p.ItemId))).ToArray();
            if(rows.Length!=1)return Review(rows.Length==0?"未找到精确匹配的 SKU 与商品 ID":"SKU 对应多个商品，请补充商品 ID");
            var row=rows[0];var active=row.Promotions.Where(p=>p.Active&&(input.PromotionId==null||p.Id==input.PromotionId)).ToArray();
            var price=active.Length==1?Money(active[0].PriceText):null;
            var observation=new PriceObservation(store.Id,row.ItemId,row.Title,row.BasePriceText,price,price.HasValue?input.TargetPrice-price.Value:null,row.Promotions,DateTimeOffset.UtcNow);
            if(UnderReview(row))return Review("商品需审核未激活，保留待核对："+row.ListingStatus,observation);
            if(!price.HasValue)return Review("未能确定单一活动价：可能有多个活动、价格区间或活动未生效，请核对活动 ID",observation);
            if(input.Action=="提价"&&input.TargetPrice<price||input.Action=="降价"&&input.TargetPrice>price)return Review("表内调价动作与该店铺实时活动价方向不符，请人工核对",observation);
            if(input.ItemId==null)return Review($"已读取活动价 BRL {price:0.00}，但缺少预期商品 ID；请核对该紫鸟配置当前登录的卖家和商品身份",observation);
            if(price==input.TargetPrice)return new(RowStatus.Skipped,"只读核对","目标价与当前活动价相同，已跳过；未修改价格或活动。","readonly-unchanged",observation);
            return new(RowStatus.Inspected,"只读核对",$"指定商品已读取：活动价 BRL {price:0.00}，目标 BRL {input.TargetPrice:0.00}；未修改价格或活动。","readonly-product-read",observation);
        }
        catch(VerificationRequiredException){throw;}
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception e)when(e is AppException or OperationCanceledException or System.Net.Http.HttpRequestException or System.Net.WebSockets.WebSocketException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return new(RowStatus.NeedsReview,"只读核对",e is AppException?e.Message:"紫鸟连接或页面读取中断，请检查客户端后继续。","readonly-connection-error",PauseBatch:true);
        }
    }
    static string NormalizeId(string id)=>id.StartsWith("MLB",StringComparison.OrdinalIgnoreCase)?id[3..].TrimStart('-'):id.TrimStart('#');
    public static decimal? Money(string text)
    {
        var match=Regex.Match(text,@"^R\$\s*(\d{1,3}(?:\.\d{3})+|\d+)(?:,(\d{2}))?\s*$");
        return match.Success&&decimal.TryParse(match.Groups[1].Value.Replace(".","")+"."+(match.Groups[2].Success?match.Groups[2].Value:"00"),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var price)?price:null;
    }
}
