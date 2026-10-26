using System.Globalization;
using System.Text.RegularExpressions;
namespace Repricing.Import;
public class PriceValidator
{
    public ColumnMapping SuggestMapping(string[] headers)
    {
        string Find(params string[] aliases)=>headers.FirstOrDefault(h=>aliases.Contains(h.Trim(),StringComparer.OrdinalIgnoreCase))??"";
        return new(Find("店铺","店铺名称","store"),Find("SKU","商品SKU","卖家SKU"),Find("目标售价","目标价格","新价格","target_price","折扣价格","报名价格"),Find("站点","国家","site"),Find("商品ID","商品 ID","item_id"),Find("变体ID","变体 ID","variation_id"),Find("活动ID","活动 ID","promotion_id"),Find("目标基础售价","新原价","base_price"),Find("是否允许退出活动","允许退出活动","allow_exit"),Find("是否允许修改基础售价","允许修改原价","allow_base_change"),Find("备注","note"),Find("调价动作","action"));
    }
    static readonly Dictionary<string,(string Site,string Currency,int Precision)> Sites=new(StringComparer.OrdinalIgnoreCase)
    {
        ["BR"]=("MLB","BRL",2),["MLB"]=("MLB","BRL",2),["MX"]=("MLM","MXN",2),["MLM"]=("MLM","MXN",2),["CL"]=("MLC","CLP",0),["MLC"]=("MLC","CLP",0),["CO"]=("MCO","COP",0),["MCO"]=("MCO","COP",0),["AR"]=("MLA","ARS",2),["MLA"]=("MLA","ARS",2),["UY"]=("MLU","UYU",2),["MLU"]=("MLU","UYU",2),["PE"]=("MPE","PEN",2),["MPE"]=("MPE","PEN",2)
    };
    public ValidatedRow[] Validate(ImportSheet sheet,ColumnMapping mapping,string? defaultSite,ZiniaoStore? defaultStore=null)
    {
        if((defaultStore==null?new[]{mapping.Store,mapping.Sku,mapping.TargetPrice}:new[]{mapping.Sku,mapping.TargetPrice}).Any(c=>string.IsNullOrEmpty(c)||!sheet.Headers.Contains(c)))throw new AppException("请完成店铺、SKU、目标售价的字段映射。");
        var mapped=typeof(ColumnMapping).GetProperties().Select(p=>p.GetValue(mapping) as string).Where(s=>!string.IsNullOrEmpty(s)).ToArray();
        if(mapped.Any(c=>!sheet.Headers.Contains(c!)) || mapped.Distinct().Count()!=mapped.Length)throw new AppException("字段映射包含不存在的列或重复使用的列。");
        var result=new List<ValidatedRow>();
        foreach(var row in sheet.Rows)
        {
            var errors=new List<string>();
            string Get(string? col,bool identity=false)=>col is not null && row.Cells.TryGetValue(col,out var c)?(identity?c.DisplayText??c.Text:c.Text).Trim():"";
            bool Flag(string? col){var value=Get(col);if(value=="")return false;if(new[]{"是","true","1","yes"}.Contains(value,StringComparer.OrdinalIgnoreCase))return true;if(!new[]{"否","false","0","no"}.Contains(value,StringComparer.OrdinalIgnoreCase))errors.Add($"{col} 只能填写是或否");return false;}
            decimal? Money(string? col,bool required,int precision)
            {
                var v=Get(col);if(v==""&&!required)return null;
                if(col is not null && row.Cells.TryGetValue(col,out var cell)&&cell.IsFormula){errors.Add($"{col} 不能使用公式，请粘贴为数值");return null;}
                if(!Regex.IsMatch(v,@"^\d+(\.\d+)?$")||!decimal.TryParse(v,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var n)||n<=0){errors.Add($"{col} 必须为大于 0 的明确数值（小数用点，不含千位分隔符）");return null;}
                if(n>1_000_000_000m || decimal.Round(n,precision)!=n){errors.Add($"{col} 金额超限或精度不符，当前币种最多 {precision} 位小数");return null;}return n;
            }
            var store=defaultStore?.Id??Get(mapping.Store);var sku=Get(mapping.Sku,true);
            if(store=="")errors.Add("店铺不能为空");if(sku=="")errors.Add("SKU 不能为空");
            if(row.Cells.Where(p=>mapped.Contains(p.Key)).Any(p=>p.Value.IsFormula))errors.Add("映射字段含公式，请先粘贴为静态值");
            if(row.Cells.Values.Any(c=>c.Text.Length>2000))errors.Add("单元格文本过长，最多 2,000 字符");
            var siteText=Get(mapping.Site);if(siteText=="")siteText=defaultSite??"";
            if(!Sites.TryGetValue(siteText,out var site))errors.Add("请选择或填写有效站点");
            var price=Money(mapping.TargetPrice,true,site.Currency is null?2:site.Precision);
            var basePrice=Money(mapping.BasePrice,false,site.Currency is null?2:site.Precision);
            var exit=Flag(mapping.AllowExit);var change=Flag(mapping.AllowBaseChange);
            var action=Get(mapping.Action);if(!string.IsNullOrEmpty(mapping.Action)&&action is not "" and not "提价" and not "降价")errors.Add("调价动作只能填写提价或降价");
            var input=errors.Count>0?null:new PriceInput(store,site.Site,site.Currency!,sku,Null(Get(mapping.ItemId,true)),Null(Get(mapping.VariationId,true)),Null(Get(mapping.PromotionId,true)),price!.Value,basePrice,exit,change,Null(Get(mapping.Note)),Null(action),defaultStore?.Name);
            var displayFields=new Dictionary<string,string>{["store"]=defaultStore?.Name??store,["action"]=action,["sku"]=sku,["site"]=siteText,["targetPrice"]=Get(mapping.TargetPrice)};
            result.Add(new(row.SourceRow,sheet.Name,row.Cells.ToDictionary(p=>p.Key,p=>p.Value.Text),input,errors.ToArray(),displayFields));
        }
        // Reject all members of a duplicate group; retaining the first would hide conflicting prices.
        var duplicates=result.Where(r=>r.Input!=null).GroupBy(r=>(r.Input!.Store,r.Input.Site,Identity:r.Input.ItemId??r.Input.Sku,Variation:r.Input.VariationId??"")).Where(g=>g.Count()>1).SelectMany(g=>g.Select(r=>r.SourceRow)).ToHashSet();
        return result.Select(r=>duplicates.Contains(r.SourceRow)?r with{Input=null,Errors=[..r.Errors,"同店铺、站点和商品身份重复，请只保留一行"]}:r).ToArray();
    }
    static string? Null(string value)=>value==""?null:value;
}
