namespace Repricing.Execution;

public record PriceCutPlan(ZiniaoStore Store,PriceInput Input,ZiniaoProduct Product,string PromotionId,decimal Before,decimal BasePrice,string IdentityMethod="精确 SKU 或已确认商品 ID",bool Join=false,bool Rejoin=false,string Stage="price",decimal? OfferPrice=null,bool ChangeBase=false,decimal? DiscountBase=null,string? DiscountPromotionId=null,string? RejoinPromotionId=null,PriceCutPlan? PreviousExit=null,string? IncomeSubsidyId=null,string? IncomeOrdinaryId=null,decimal? IncomeOrdinaryPrice=null,decimal? IncomeSubsidyNet=null,decimal? IncomeOrdinaryNet=null,bool DirectBaseOnly=false);
public interface IZiniaoPriceCutClient:IZiniaoReader
{
    Task<ZiniaoProduct[]> ReadWeekendAsync(ZiniaoStore store,PriceInput input,CancellationToken ct)=>ReadAsync(store,input,ct);
    Task<decimal?> PreviewNetAsync(PriceCutPlan plan,CancellationToken ct)=>Task.FromResult<decimal?>(null);
    Task PrepareExitAsync(PriceCutPlan plan,CancellationToken ct)=>throw new AppException("此执行器不支持退出活动。",409);
    Task ConfirmExitAsync(PriceCutPlan plan,CancellationToken ct)=>throw new AppException("此执行器不支持退出活动。",409);
    Task PrepareBaseAsync(PriceCutPlan plan,CancellationToken ct)=>throw new AppException("此执行器不支持改原价。",409);
    Task SubmitBaseAsync(PriceCutPlan plan,CancellationToken ct)=>throw new AppException("此执行器不支持改原价。",409);
    Task PrepareAsync(PriceCutPlan plan,CancellationToken ct);
    Task SubmitAsync(PriceCutPlan plan,CancellationToken ct);
    Task<ZiniaoProduct> VerifyAsync(PriceCutPlan plan,CancellationToken ct);
    Task CloseEditorAsync()=>Task.CompletedTask;
}
public class ZiniaoPriceCutExecutor(IZiniaoPriceCutClient client)
{
    static decimal? LegacyStock(string? text)
    {
        if(string.IsNullOrWhiteSpace(text))return null;
        if(System.Text.RegularExpressions.Regex.IsMatch(text,"已售罄|无库存|没有库存|无货|sold\\s*out|out\\s*of\\s*stock|sem\\s*estoque|esgotad[oa]|agotad[oa]|sin\\s*stock|sin\\s*existencias",System.Text.RegularExpressions.RegexOptions.IgnoreCase))return 0;
        var number=System.Text.RegularExpressions.Regex.Match(text,@"\d[\d.,]*");if(!number.Success)return null;
        var digits=System.Text.RegularExpressions.Regex.Replace(number.Value,@"\D","");
        return decimal.TryParse(digits,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var stock)?stock:null;
    }
    static bool UserConfirmedSharedBaseOnly(ZiniaoProduct product,string[] siblings,string sku,string? note)
    {
        if(product.Sku!=sku||!siblings.Contains(sku,StringComparer.Ordinal))return false;
        if(note?.Trim()=="仅改原价（用户确认整链接统一调价）")return true;
        var variants=product.SharedVariants;if(variants==null)return true;
        var target=variants.Where(x=>x.Sku==sku).ToArray();if(target.Length==1&&LegacyStock(target[0].StockText)==0)return false;
        return !variants.Any(x=>x.Sku!=sku&&LegacyStock(x.StockText) is >0);
    }
    static bool ExplicitBaseOnly(PriceInput input)=>input.Note?.Trim().StartsWith("仅改原价",StringComparison.Ordinal)==true;
    public async Task<ExecutionResult> ExecuteAsync(PriceInput input,Action<PriceCutPlan> checkpoint,CancellationToken ct,PriceCutPlan? previous=null,bool allowIncrease=false,bool allowBaseChange=false,IReadOnlyList<PriceInput>? batchInputs=null,bool incomePriority=false)
    {
        PriceCutPlan? plan=null;bool submitted=false;
        ExecutionResult Review(string message,bool retry=false,PriceObservation? observed=null)
        {
            var observation=observed??(plan==null?null:Observe(plan,plan.Product));
            if(submitted&&observed==null&&observation!=null)observation=observation with{CurrentPrice=null,Difference=null};
            return new(RowStatus.NeedsReview,plan?.Stage=="scheduled-edit"?"预约普通活动改价":allowIncrease?"自动调价":"活动内降价",message,"cut-review",observation,Retryable:retry);
        }
        bool SameActivePromotions(PriceCutPlan scheduled,ZiniaoProduct current)
        {
            static string[] Active(ZiniaoProduct p)=>p.Promotions.Where(x=>x.Active).Select(x=>$"{x.Id}\u001f{ZiniaoReadOnlyExecutor.Money(x.PriceText)?.ToString(System.Globalization.CultureInfo.InvariantCulture)??"?"}").Order(StringComparer.Ordinal).ToArray();
            return Active(scheduled.Product).SequenceEqual(Active(current),StringComparer.Ordinal);
        }
        ExecutionResult ScheduledSuccess(PriceCutPlan scheduled,ZiniaoProduct current,string prefix)=>new(RowStatus.Succeeded,"预约普通活动改价",$"{prefix}已核验预约活动 {scheduled.PromotionId}：BRL {scheduled.Before:0.00} → {scheduled.Input.TargetPrice:0.00}；当前生效活动保持不变。","cut-scheduled-verified",Observe(scheduled,current));
        async Task<ExecutionResult> EditScheduled(PriceCutPlan scheduled,ZiniaoProduct current,bool resuming)
        {
            plan=scheduled;
            bool SameIdentity(ZiniaoProduct p)=>Id(p.ItemId)==Id(scheduled.Product.ItemId)&&p.Sku==scheduled.Input.Sku&&ZiniaoReadOnlyExecutor.Money(p.BasePriceText)==scheduled.BasePrice&&!ZiniaoReadOnlyExecutor.UnderReview(p);
            PromotionObservation[] Selected(ZiniaoProduct p)=>p.Promotions.Where(x=>x.Id==scheduled.PromotionId&&x.Id.StartsWith("C-",StringComparison.Ordinal)&&!x.Active&&ZiniaoReadOnlyExecutor.Scheduled(x)).ToArray();
            bool TargetReached(ZiniaoProduct p)=>SameIdentity(p)&&SameActivePromotions(scheduled,p)&&Selected(p) is [var offer]&&ZiniaoReadOnlyExecutor.Money(offer.PriceText)==scheduled.Input.TargetPrice;
            bool OriginalStillPresent(ZiniaoProduct p)=>SameIdentity(p)&&SameActivePromotions(scheduled,p)&&Selected(p) is [var offer]&&offer.Editable&&ZiniaoReadOnlyExecutor.Money(offer.PriceText)==scheduled.Before&&offer.Name==scheduled.Product.Promotions.Single(x=>x.Id==scheduled.PromotionId).Name;
            ExecutionResult ProductReview(ZiniaoProduct p,string message,bool retry=false)=>Review(message,retry,Observe(scheduled,p));
            if(scheduled.Stage!="scheduled-edit"||!scheduled.PromotionId.StartsWith("C-",StringComparison.Ordinal)||scheduled.Input.PromotionId!=scheduled.PromotionId||scheduled.Input.TargetPrice<scheduled.BasePrice*.2m||scheduled.Input.TargetPrice>scheduled.BasePrice*.95m||scheduled.Product.Sku!=scheduled.Input.Sku||!SameIdentity(current)||current.Promotions.Count(x=>x.Active)>1||current.Promotions.Any(x=>x.Active&&!x.Id.StartsWith("C-",StringComparison.Ordinal)))return ProductReview(current,"预约活动绑定、原价或目标范围不一致，未修改。");
            if(resuming)
            {
                if(TargetReached(current))return ScheduledSuccess(scheduled,current,"回读确认");
                if(!OriginalStillPresent(current))return ProductReview(current,"预约活动保存状态、原报价或生效活动发生变化；未重复提交。");
                var fresh=(await client.ReadAsync(scheduled.Store,scheduled.Input with{ItemId=null},ct)).Where(x=>Id(x.ItemId)==Id(scheduled.Product.ItemId)&&x.Sku==scheduled.Input.Sku).ToArray();
                if(fresh.Length!=1)return ProductReview(current,"预约活动回读暂未唯一返回该商品；未重复提交。",true);
                current=fresh[0];
                if(TargetReached(current))return ScheduledSuccess(scheduled,current,"二次回读确认");
                if(!OriginalStillPresent(current))return ProductReview(current,"预约活动二次回读不一致；未重复提交。");
            }
            else
            {
                var selected=Selected(current);
                if(selected.Length!=1)return ProductReview(current,"未找到唯一可修改的已预约普通活动，未提交。");
                var price=ZiniaoReadOnlyExecutor.Money(selected[0].PriceText);
                if(price==null)return ProductReview(current,"预约活动报价不是明确金额，未提交。");
                if(price==scheduled.Input.TargetPrice)return new(RowStatus.Skipped,"预约活动价已达标",$"已预约活动 {selected[0].Name.Split('\n')[0]} 已是目标价，未重复提交。","cut-scheduled-unchanged",Observe(scheduled,current));
                if(!selected[0].Editable)return ProductReview(current,"该预约活动当前不可修改，未提交。");
                if(scheduled.Input.TargetPrice>price)return ProductReview(current,"预约普通活动提价流程尚未开放，未提交。");
            }
            await client.PrepareAsync(scheduled,ct);checkpoint(scheduled);submitted=true;await client.SubmitAsync(scheduled,ct);
            current=await client.VerifyAsync(scheduled,ct);
            if(PriceReadback.Failure(scheduled,current,scheduled.BasePrice,scheduled.PromotionId) is {} failure)return ProductReview(current,"预约活动保存后核验："+failure+"；已记录提交，下轮先回读。",true);
            return ScheduledSuccess(scheduled,current,"提交并回读确认");
        }
        if(previous!=null)
        {
            if(previous.Input.Sku!=input.Sku||previous.Input.TargetPrice!=input.TargetPrice)return Review("重试目标与原提交记录不一致，需人工核对。");
            input=input with{ItemId=previous.Product.ItemId,PromotionId=previous.IncomeSubsidyId!=null?input.PromotionId:previous.PromotionId};
        }
        if(allowIncrease?input.Action is not null and not "" and not "提价" and not "降价":input.Action!="降价")return Review(allowIncrease?"仅支持自动、提价或降价；第三种改原价暂不执行。":"当前实操仅支持降价；提价与改原价需人工核对。");
        if(input.Site!="MLB"||input.Currency!="BRL"||input.VariationId!=null||input.AllowBaseChange||input.BasePrice!=null)return Review("仅支持巴西站活动调价，不修改原价或推断变体。");
        if(input.TargetPrice<=0||decimal.Round(input.TargetPrice,2)!=input.TargetPrice)return Review("目标价必须大于零且最多两位小数。");
        try
        {
            var stores=await ZiniaoReadOnlyExecutor.FindStoresAsync(client,input.Store,ct);
            if(stores.Length!=1)return Review("紫鸟店铺列表暂未返回唯一配置，下一轮重新读取，不更换店铺。",true);
            if(!stores[0].Platform.Contains("MercadoLibre",StringComparison.OrdinalIgnoreCase)||!stores[0].Platform.Contains("巴西"))return Review("紫鸟配置平台与巴西站不一致，不执行调价。");
            if(previous!=null&&stores[0].Id!=previous.Store.Id)return Review("重试店铺与原提交记录不同，需人工核对。");
            var readInput=previous!=null?input with{ItemId=ExplicitBaseOnly(input)?previous.Product.ItemId:null}:input;
            var products=allowBaseChange&&ExplicitBaseOnly(input)?await client.ReadManagerAsync(stores[0],readInput,ct):await client.ReadAsync(stores[0],readInput,ct);
            if(previous==null&&input.ItemId==null&&products.Any(p=>p.Sku==input.Sku&&p.SharedSkus is {Length:>1}))
            {
                var managed=await client.ReadManagerAsync(stores[0],input,ct);
                var independent=managed.Where(p=>p.Sku==input.Sku&&p.SharedSkus is not {Length:>1}&&System.Text.RegularExpressions.Regex.IsMatch(p.ItemId,@"^\d{1,30}$")&&!products.Any(old=>old.ItemId==p.ItemId&&old.SharedSkus is {Length:>1})).ToArray();
                if(independent.Length>0)
                {
                    var resolved=new List<ZiniaoProduct>();
                    foreach(var child in independent)
                    {
                        var fresh=await client.ReadAsync(stores[0],input with{ItemId=child.ItemId},ct);
                        var confirmed=fresh.Where(p=>p.ItemId==child.ItemId&&p.Sku==input.Sku&&p.SharedSkus is not {Length:>1}).ToArray();
                        if(confirmed.Length==0)return Review("商品管理已发现独立子商品，但对应活动页尚未完成身份核验，未按旧父链接改价。",true);
                        resolved.AddRange(confirmed);
                    }
                    if(resolved.Count==0)return Review("商品管理已发现独立子商品，但对应活动页尚未完成身份核验，未按旧父链接改价。",true);
                    products=resolved.ToArray();
                }
            }
            if(products.Length==0)return previous==null?new(RowStatus.Skipped,"活动内降价","该店铺未找到此 SKU，已跳过。","cut-sku-missing"):Review("原提交商品暂未读取到，保存结果仍未知，下轮继续核对。",true);
            ZiniaoProduct[] Matches(ZiniaoProduct[] list)=>list.Where(p=>(p.Sku==input.Sku||input.ItemId!=null&&string.IsNullOrEmpty(p.Sku))&&(input.ItemId==null||Id(p.ItemId)==Id(input.ItemId))).ToArray();
            var matches=Matches(products);
            if(matches.Length>1&&matches.Skip(1).All(p=>SameProduct(matches[0],p)))matches=[matches[0]];
            if(previous==null&&input.ItemId==null&&matches.Length>1&&matches.Length<=50&&matches.All(p=>p.Sku==input.Sku&&System.Text.RegularExpressions.Regex.IsMatch(Id(p.ItemId),@"^\d{1,30}$"))&&matches.Select(p=>Id(p.ItemId)).Distinct().Count()==matches.Length)return new(RowStatus.Skipped,"按商品拆分","同一精确 SKU 的不同商品分别执行并核验。","cut-split-products",SplitInputs:matches.Select(p=>input with{ItemId=p.ItemId}).ToArray());
            if(matches.Length!=1)
            {
                var ids=string.Join("、",products.Select(p=>Id(p.ItemId)).Where(id=>!string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
                if(!allowIncrease&&previous==null&&products.All(p=>{var a=p.Promotions.Where(x=>x.Active).ToArray();return a.Length==0&&!p.Promotions.Any(x=>x.Joinable)||a.Length==1&&ZiniaoReadOnlyExecutor.Money(a[0].PriceText) is decimal price&&price<=input.TargetPrice;}))return new(RowStatus.Skipped,"未提交价格",$"搜索结果没有需要降价的候选：活动价已不高于目标价，或没有可修改及可报名的普通活动。商品身份未确认，未修改；候选 ID：{ids}。","cut-no-eligible-candidate");
                return Review($"未唯一确认 SKU 与商品；候选商品 ID：{(ids.Length==0?"未返回":ids)}。重复链接或无 SKU 标签时请先确认商品 ID。");
            }
            var product=matches[0];
            if(ZiniaoReadOnlyExecutor.UnderReview(product))
            {
                var current=ZiniaoReadOnlyExecutor.Money(product.ManagedPriceText??product.Promotions.FirstOrDefault(p=>p.Active)?.PriceText??product.BasePriceText);
                var blockedObservation=new PriceObservation(stores[0].Id,product.ItemId,product.Title,product.BasePriceText,current,current.HasValue?input.TargetPrice-current:null,product.Promotions,DateTimeOffset.UtcNow);
                var originalBase=ZiniaoReadOnlyExecutor.Money(product.BasePriceText);
                var hint=allowBaseChange&&originalBase>0&&input.TargetPrice<originalBase*.2m?$" 折扣超80%时计划新原价 {input.TargetPrice*4:0.00}、活动价 {input.TargetPrice:0.00}；审核通过且有普通活动入口后才能执行。":"";
                return Review("商品需审核未激活，保留待核对："+product.ListingStatus+"；本次未继续修改价格或活动。"+hint,observed:blockedObservation);
            }
            if(previous?.Stage=="scheduled-edit")return await EditScheduled(previous,product,true);
            if(input.PromotionId is {Length:>0} requestedPromotion)
            {
                var scheduled=product.Promotions.Where(p=>p.Id==requestedPromotion&&p.Id.StartsWith("C-",StringComparison.Ordinal)&&!p.Active&&ZiniaoReadOnlyExecutor.Scheduled(p)).ToArray();
                if(scheduled.Length>0)
                {
                    var scheduledBasePrice=ZiniaoReadOnlyExecutor.Money(product.BasePriceText);var scheduledBefore=scheduled.Length==1?ZiniaoReadOnlyExecutor.Money(scheduled[0].PriceText):null;
                    var scheduledActive=product.Promotions.Where(p=>p.Active).ToArray();
                    if(scheduled.Length!=1||scheduledBasePrice is not decimal scheduledBase||scheduledBefore is not decimal scheduledOld||product.Sku!=input.Sku||product.SharedSkus is {Length:>1}||scheduledActive.Length>1||scheduledActive.Any(p=>!p.Id.StartsWith("C-",StringComparison.Ordinal)))return Review("预约普通活动或其他生效活动不唯一，未提交。");
                    var planBase=new PriceCutPlan(stores[0],input,product,requestedPromotion,scheduledOld,scheduledBase,Stage:"scheduled-edit");
                    if(scheduledOld==input.TargetPrice)return new(RowStatus.Skipped,"预约活动价已达标",$"预约活动 {scheduled[0].Name.Split('\n')[0]} 已是目标价，未重复提交。","cut-scheduled-unchanged",Observe(planBase,product));
                    if(input.TargetPrice<scheduledBase*.2m||input.TargetPrice>scheduledBase*.95m)return Review("预约活动目标超出原价允许的5%–80%折扣范围，未修改原价或活动。",observed:Observe(planBase,product));
                    if(input.TargetPrice>scheduledOld)return Review("预约普通活动提价流程尚未开放，未提交。",observed:Observe(planBase,product));
                    return await EditScheduled(planBase,product,false);
                }
            }
            if(product.ManagedPriceText!=null&&!ExplicitBaseOnly(input))
            {
                var current=ZiniaoReadOnlyExecutor.Money(product.ManagedPriceText);
                var managedObservation=new PriceObservation(stores[0].Id,product.ItemId,product.Title,product.BasePriceText,current,current.HasValue?input.TargetPrice-current.Value:null,[],DateTimeOffset.UtcNow);
                if(previous?.DiscountBase==null&&product.Sku==input.Sku&&current==input.TargetPrice&&(previous==null||ZiniaoReadOnlyExecutor.Money(product.BasePriceText)==previous.BasePrice))
                    return new(previous==null?RowStatus.Skipped:RowStatus.Succeeded,"商品管理回读核验",$"商品管理已确认目标价 BRL {current:0.00}；商品状态：{product.ListingStatus}。未重复提交，未修改平台审核状态。","cut-manager-reconciled",managedObservation);
                var baseAmount=ZiniaoReadOnlyExecutor.Money(product.BasePriceText);
                var discountNote=allowBaseChange&&baseAmount>0&&input.TargetPrice<baseAmount*.2m?$" 目标价低于原价20%，需先将原价降至 {input.TargetPrice*4:0.00} 再以75%折扣设置活动价；但当前商品审核或活动入口未通过，未修改原价或报名。":"";
                return Review($"促销中心未返回该子商品，商品管理状态：{product.ListingStatus}；实际价格或原价未通过核验，未继续提交。"+discountNote,observed:managedObservation);
            }
            if(incomePriority&&(previous?.IncomeSubsidyId!=null||previous==null&&product.Promotions.Any(p=>p.Active&&p.Id.StartsWith("P-",StringComparison.Ordinal))))
                return await new ZiniaoIncomeExecutor(client).ExecuteAsync(stores[0],input,product,checkpoint,ct,previous);
            if(product.SharedSkus is {Length:>0} siblings)
            {
                if(!siblings.Contains(input.Sku,StringComparer.Ordinal))return Review("旧式商品详情的 SKU 身份与本行不一致，未操作。");
                var stockOverride=allowBaseChange&&ExplicitBaseOnly(input)&&UserConfirmedSharedBaseOnly(product,siblings,input.Sku,input.Note);
                if(allowBaseChange&&ExplicitBaseOnly(input)&&!stockOverride)return Review("商品管理已读到目标SKU无库存或其他规格仍有库存；与“其他规格售罄”的确认冲突，未修改共享链接价格。");
                var live=product.Promotions.Where(p=>p.Active).ToArray();
                if(live.Length!=1||ZiniaoReadOnlyExecutor.Money(live[0].PriceText)!=input.TargetPrice||allowBaseChange&&ZiniaoReadOnlyExecutor.Money(product.BasePriceText) is decimal sharedBase&&(input.TargetPrice<sharedBase*.2m||input.TargetPrice>sharedBase*.95m))
                {
                    var scope=batchInputs??[input];
                    var unresolved=siblings.Where(s=>scope.Any(i=>(i.Store==stores[0].Id||i.Store==stores[0].Name)&&i.Sku==s&&(i.ItemId==null||Id(i.ItemId)==Id(product.ItemId))&&i.TargetPrice!=input.TargetPrice)).ToArray();
                    if(unresolved.Length>0)return Review($"旧式商品 {product.ItemId} 共用一个价格；关联 SKU {string.Join("、",unresolved)} 在本批指定了不同目标价，无法同时满足，未修改。");
                }
            }
            if(previous==null&&input.PromotionId==null&&product.Sku==input.Sku&&product.SharedSkus is not {Length:>1}&&
                ZiniaoReadOnlyExecutor.Money(product.BasePriceText) is decimal campaignBase&&campaignBase>0&&input.TargetPrice>=campaignBase*.2m&&input.TargetPrice<=campaignBase*.95m)
            {
                var campaigns=product.Promotions;
                var liveCampaigns=campaigns.Where(p=>p.Active).ToArray();
                var scheduledCampaigns=campaigns.Where(p=>!p.Active&&ZiniaoReadOnlyExecutor.Scheduled(p)&&p.Id.StartsWith("C-",StringComparison.Ordinal)).ToArray();
                var scopedCampaigns=liveCampaigns.Concat(scheduledCampaigns).ToArray();
                var uniqueIds=scopedCampaigns.Select(p=>p.Id).Distinct(StringComparer.Ordinal).Count()==scopedCampaigns.Length&&scopedCampaigns.All(p=>System.Text.RegularExpressions.Regex.IsMatch(p.Id,@"^C-[A-Za-z0-9-]{1,80}$"));
                if(liveCampaigns.Length==1&&liveCampaigns[0].Id.StartsWith("C-",StringComparison.Ordinal)&&scheduledCampaigns.Length>0)
                {
                    if(!uniqueIds)return Review("生效与预约普通活动编号不唯一，未只修改其中一个活动。");
                    if(scheduledCampaigns.Any(p=>!p.Editable))return Review("至少一个预约普通活动当前不可修改；为避免只更新生效活动，本批未提交。");
                    var campaignPrices=scopedCampaigns.Select(p=>ZiniaoReadOnlyExecutor.Money(p.PriceText)).ToArray();
                    if(campaignPrices.Any(p=>p==null))return Review("生效或预约活动当前价格不明确，未修改任何活动。");
                    if(campaignPrices.Any(p=>p!.Value<input.TargetPrice))return Review("目标价高于其中一个活动当前价；为避免只改部分活动，本批未提交。");
                    var inputs=scopedCampaigns.OrderBy(p=>p.Active?0:1).ThenBy(p=>p.Id,StringComparer.Ordinal)
                        .Select(p=>input with{ItemId=product.ItemId,PromotionId=p.Id}).ToArray();
                    return new(RowStatus.Skipped,"按活动拆分",$"同一商品已绑定 {liveCampaigns.Length} 个生效及 {scheduledCampaigns.Length} 个已预约普通活动；按活动编号分别执行与核验，不修改提案或其他活动。","cut-split-campaigns",SplitInputs:inputs);
                }
            }
            var originalPrice=ZiniaoReadOnlyExecutor.Money(product.BasePriceText);
            var directBaseOnly=allowBaseChange&&ExplicitBaseOnly(input)&&(product.SharedSkus is {Length:>1} stockVariants?UserConfirmedSharedBaseOnly(product,stockVariants,input.Sku,input.Note):product.Sku==input.Sku);
            if(allowBaseChange&&product.SharedSkus is {Length:>1} variants&&originalPrice is decimal legacyBase&&(directBaseOnly||input.TargetPrice<legacyBase*.2m||input.TargetPrice>legacyBase*.95m))
            {
                var scope=batchInputs??[input];
                var conflicting=variants.Where(s=>scope.Any(i=>(i.Store==stores[0].Id||i.Store==stores[0].Name)&&i.Sku==s&&(i.ItemId==null||Id(i.ItemId)==Id(product.ItemId))&&i.TargetPrice!=input.TargetPrice)).ToArray();
                if(conflicting.Length>0)return Review($"旧式商品 {product.ItemId} 共用一个价格；本批关联 SKU {string.Join("、",conflicting)} 指定了不同目标价，无法同时满足，未修改。");
                var wholeLinkConfirmed=allowBaseChange&&product.Sku==input.Sku&&variants.Contains(input.Sku,StringComparer.Ordinal);
                if(!directBaseOnly&&!wholeLinkConfirmed&&variants.Any(s=>!scope.Any(i=>(i.Store==stores[0].Id||i.Store==stores[0].Name)&&i.Sku==s&&(i.ItemId==null||Id(i.ItemId)==Id(product.ItemId))&&i.TargetPrice==input.TargetPrice)))
                    return Review($"已确认旧式商品 {product.ItemId} 的规格 {product.VariantLabel??input.Sku}；单一变体不能通过父链接统一改原价，否则会影响同链接 {variants.Length} 个SKU。未修改父链接价格。");
            }
            if(previous==null&&input.PromotionId==null&&(!input.AllowExit||!product.Promotions.Any(p=>p.Active&&p.Id.StartsWith("P-",StringComparison.Ordinal)))&&product.Promotions.Any(p=>p.Id.StartsWith("C-",StringComparison.Ordinal)&&!p.Active&&p.Editable&&System.Text.RegularExpressions.Regex.IsMatch(p.Status,"已预约|Agendada|Agendado|Scheduled",System.Text.RegularExpressions.RegexOptions.IgnoreCase)&&ZiniaoReadOnlyExecutor.Money(p.PriceText)==input.TargetPrice))
            {
                var live=product.Promotions.Where(p=>p.Active).ToArray();var effective=live.Length==1?ZiniaoReadOnlyExecutor.Money(live[0].PriceText):live.Length==0?originalPrice:null;
                return Review("普通活动已按表格目标价预约，尚未激活；当前生效价格仍由其他活动决定，保留待核对，未重复报名。",observed:new PriceObservation(stores[0].Id,product.ItemId,product.Title,product.BasePriceText,effective,effective.HasValue?input.TargetPrice-effective:null,product.Promotions,DateTimeOffset.UtcNow));
            }
            if(previous?.ChangeBase==true)
            {
                if(!allowIncrease||!allowBaseChange)return Review("原任务未确认改原价，禁止退出活动或保存原价。");
                return previous.DiscountBase!=null?await new ZiniaoDiscountBaseExecutor(client).ExecuteAsync(previous,product,checkpoint,ct,true):await new ZiniaoBasePriceExecutor(client).ExecuteAsync(previous,product,checkpoint,ct,true);
            }
            if(previous?.Rejoin==true){if(!allowIncrease)return Review("旧任务未授权混合调价，禁止退出或重报。");return await new ZiniaoIncreaseExecutor(client).ExecuteAsync(previous,product,checkpoint,ct,true);}
            // Count every active campaign before considering editability: proposals never replace an ambiguous active price.
            var active=product.Promotions.Where(p=>p.Active).ToArray();
            if(active is [var unidentified]&&string.IsNullOrWhiteSpace(unidentified.Id))return Review("平台未返回生效活动编号，无法绑定退出或修改；本轮不提交，下一轮重新加载核对。",true,new PriceObservation(stores[0].Id,product.ItemId,product.Title,product.BasePriceText,ZiniaoReadOnlyExecutor.Money(unidentified.PriceText),null,product.Promotions,DateTimeOffset.UtcNow));
            if(active.Length>0&&input.PromotionId!=null&&active.All(p=>p.Id!=input.PromotionId))return Review("指定活动与实际生效活动不符，未提交。");
            if(previous==null&&allowIncrease&&active.Length==1&&originalPrice is decimal unchangedBase&&input.TargetPrice>=unchangedBase*.2m&&input.TargetPrice<=unchangedBase*.95m&&ZiniaoReadOnlyExecutor.Money(active[0].PriceText)==input.TargetPrice)
            {
                var unchanged=new PriceCutPlan(stores[0],input,product,active[0].Id,input.TargetPrice,unchangedBase);
                return new(RowStatus.Skipped,"价格已达标","当前活动价已是目标价，未退出活动或修改原价。","cut-unchanged",Observe(unchanged,product));
            }
            if(previous==null&&allowIncrease&&originalPrice is decimal b&&b>0&&(directBaseOnly||input.TargetPrice<b*.2m||input.TargetPrice>b*.95m))
            {
                if(!allowBaseChange)return new(RowStatus.Skipped,"需改原价","目标价超出活动5%–80%折扣范围；原任务未确认第三种流程，未改原价。","third-case-skipped");
                if(active.Length>1||product.Sku!=input.Sku)return Review("第三种流程需精确SKU与唯一生效活动，未操作。");
                var beforeBaseRoute=active.Length==1?ZiniaoReadOnlyExecutor.Money(active[0].PriceText):b;
                if(beforeBaseRoute==null)return Review("第三种流程的活动价不明确，未操作。");
                var excessive=input.TargetPrice<b*.2m;
                plan=new(stores[0],input,product,active.FirstOrDefault()?.Id??"",beforeBaseRoute.Value,b,Stage:excessive&&!directBaseOnly?(active.Length==1?"discount-exit":"discount-base"):(active.Length==1?"base-exit":"base-save"),ChangeBase:true,DiscountBase:excessive&&!directBaseOnly?input.TargetPrice*4:null,DirectBaseOnly:directBaseOnly);
                return excessive&&!directBaseOnly?await new ZiniaoDiscountBaseExecutor(client).ExecuteAsync(plan,product,checkpoint,ct,false):await new ZiniaoBasePriceExecutor(client).ExecuteAsync(plan,product,checkpoint,ct,false);
            }
            var preserveSubsidy=allowIncrease&&previous==null&&active.Length==1&&active[0].Id.StartsWith("P-",StringComparison.Ordinal)&&!active[0].Editable&&!input.AllowExit;
            var joining=active.Length==0||preserveSubsidy;
            if(joining&&previous!=null&&!previous.Join)return Review("原提交的活动已不再生效，禁止改为重新报名，需核对原提交结果。",true);
            var eligible=joining?product.Promotions.Where(p=>p.Joinable&&!p.Active&&(input.PromotionId==null||p.Id==input.PromotionId)).ToArray():active;
            if(eligible.Length==0&&previous==null)return new(RowStatus.Skipped,"未提交价格",$"商品 {product.ItemId} 没有可修改的生效活动或可报名普通活动，仅有新提案；已跳过（未调价）。","cut-no-active-promotion");
            if(eligible.Length!=1||!joining&&!eligible[0].Editable&&!(input.AllowExit&&eligible[0].Id.StartsWith("P-",StringComparison.Ordinal)))return Review("未找到唯一可修改活动或普通活动报名入口；不参与新提案或停止现有活动。");
            var promotion=eligible[0];var basePrice=ZiniaoReadOnlyExecutor.Money(product.BasePriceText);var before=joining?basePrice:ZiniaoReadOnlyExecutor.Money(promotion.PriceText);
            if(before==null||basePrice==null)return Review("活动价或原价为区间、缺失或格式异常，请核对具体子商品。");
            if(ZiniaoReadOnlyExecutor.Money(promotion.PriceText)==null)return Review("活动报价不是明确金额，未提交。");
            plan=new(stores[0],input,product,promotion.Id,before.Value,basePrice.Value,"精确 SKU 或已确认商品 ID",joining);
            if(previous!=null&&basePrice!=previous.BasePrice)return Review("重试发现原价与原提交记录不同，不再提交，需人工核对。");
            if(input.TargetPrice==before)return previous==null?new(RowStatus.Skipped,"活动内降价","当前价格已是目标价，未提交。","cut-unchanged",Observe(plan,product)):new(RowStatus.Succeeded,"降价回读核验","重试回读确认已到目标价，未重复提交。","cut-reconciled",Observe(plan,product));
            if(allowIncrease&&previous==null&&(input.TargetPrice<basePrice*.2m||input.TargetPrice>basePrice*.95m))return new(RowStatus.Skipped,"需改原价","目标价超出原价允许的5%–80%折扣范围，属于第三种情况，已跳过；未改原价。","third-case-skipped",Observe(plan,product));
            if(allowIncrease&&!joining&&input.TargetPrice>before&&previous==null){
                var replacement=(promotion.Id.StartsWith("P-",StringComparison.Ordinal)||promotion.Id=="D-PRICE-DISCOUNT")?product.Promotions.Where(p=>ZiniaoReadOnlyExecutor.OrdinaryReplacement(p,input.TargetPrice,input.AllowExit&&promotion.Id.StartsWith("P-",StringComparison.Ordinal))).ToArray():[];
                if((promotion.Id.StartsWith("P-",StringComparison.Ordinal)||promotion.Id=="D-PRICE-DISCOUNT")&&replacement.Length!=1)return Review("平台补贴活动或限时抢购提价未找到唯一普通活动报名入口，未退出。",observed:Observe(plan,product));
                return await new ZiniaoIncreaseExecutor(client).ExecuteAsync(plan with{Rejoin=true,Stage="exit",RejoinPromotionId=replacement.FirstOrDefault()?.Id},product,checkpoint,ct,false);
            }
            if(input.TargetPrice>before)return previous==null?new(RowStatus.Skipped,"活动内降价","目标价高于当前活动价，属于尚未开放的提价流程，已跳过。","cut-increase-skipped",Observe(plan,product)):Review("重试时目标价已高于当前活动价，不再提交，需人工核对。");
            if(input.TargetPrice>=basePrice)return Review("目标价不低于原价，不属于活动内降价。");
            if(previous?.Join==true&&!joining)return Review("报名重试发现活动已生效但不是目标价，不再提交，需核对。");
            if(previous!=null&&joining)
            {
                var fresh=(await client.ReadAsync(stores[0],input with{ItemId=null},ct)).Where(p=>Id(p.ItemId)==Id(product.ItemId)&&(p.Sku==input.Sku||p.Sku=="")).ToArray();
                if(fresh.Length==1&&ZiniaoReadOnlyExecutor.UnderReview(fresh[0]))return Review("商品需审核未激活，保留待核对："+fresh[0].ListingStatus,observed:Observe(plan,fresh[0]));
                if(fresh.Length!=1||ZiniaoReadOnlyExecutor.Money(fresh[0].BasePriceText)!=previous.BasePrice)return Review("报名重试商品或原价读数不一致，未提交。");
                var live=fresh[0].Promotions.Where(p=>p.Active&&p.Id==previous.PromotionId).ToArray();
                if(live.Length==1&&ZiniaoReadOnlyExecutor.Money(live[0].PriceText)==input.TargetPrice)return new(RowStatus.Succeeded,"普通活动报名核验","报名已生效且到目标价，未重复提交。","cut-reconciled",Observe(plan,fresh[0]));
                var offers=fresh[0].Promotions.Where(p=>p.Id==previous.PromotionId&&p.Joinable&&!p.Active).ToArray();
                if(fresh[0].Promotions.Any(p=>p.Active)||offers.Length!=1||offers[0].PriceText!=previous.Product.Promotions.Single(p=>p.Id==previous.PromotionId).PriceText)return Review("报名状态或报价变化，未重复提交。");
            }
            if(previous!=null&&!joining)
            {
                if(before!=previous.Before)return Review("重试价格已变动且不是目标价，不再提交，需人工核对。");
                var fresh=(await client.ReadAsync(stores[0],input with{ItemId=null},ct)).Where(p=>Id(p.ItemId)==Id(product.ItemId)&&(p.Sku==input.Sku||p.Sku=="")).ToArray();
                if(fresh.Length==1&&ZiniaoReadOnlyExecutor.UnderReview(fresh[0]))return Review("商品需审核未激活，保留待核对："+fresh[0].ListingStatus,observed:Observe(plan,fresh[0]));
                var promo=fresh.Length==1?fresh[0].Promotions.Where(p=>p.Active&&p.Id==previous.PromotionId).ToArray():[];
                if(fresh.Length!=1||promo.Length!=1||ZiniaoReadOnlyExecutor.Money(fresh[0].BasePriceText)!=previous.BasePrice)return Review("再次回读的商品、活动或原价不一致，不再提交，需核对。");
                var freshPrice=ZiniaoReadOnlyExecutor.Money(promo[0].PriceText);
                if(freshPrice==input.TargetPrice)return new(RowStatus.Succeeded,"降价回读核验","再次回读确认已到目标价，未重复提交。","cut-reconciled",Observe(plan,fresh[0]));
                if(freshPrice!=previous.Before)return Review("两次回读价格不稳定，本轮不提交。",true,Observe(plan,fresh[0]));
            }
            await client.PrepareAsync(plan,ct);
            checkpoint(plan); // Must be durable before the only save attempt.
            submitted=true;await client.SubmitAsync(plan,ct);
            var after=await client.VerifyAsync(plan,ct);
            var observation=Observe(plan,after);
            if(ZiniaoReadOnlyExecutor.UnderReview(after))return Review("商品需审核未激活，保留待核对："+after.ListingStatus,observed:observation);
            if(ZiniaoReadOnlyExecutor.Money(after.BasePriceText)!=basePrice||Id(after.ItemId)!=Id(product.ItemId)||!string.IsNullOrEmpty(after.Sku)&&after.Sku!=input.Sku)return Review("保存后商品或原价发生变化，不再提交，需人工核对。",observed:observation);
            if(PriceReadback.Failure(plan,after,basePrice.Value,plan.PromotionId,allowManaged:true) is {} reason)return Review("保存后核验："+reason+"；已记录提交，下轮先回读。",true,observation);
            if(after.ManagedPriceText!=null)return new(RowStatus.Succeeded,"商品管理回读核验",$"商品管理独立核验已到目标价 BRL {input.TargetPrice:0.00}；平台状态：{after.ListingStatus}，不修改审核状态。","cut-manager-reconciled",observation);
            return new(RowStatus.Succeeded,preserveSubsidy?"保留补贴活动并报名普通活动":joining?"普通活动报名":"活动内降价",$"{(joining?"普通活动已生效，":"")}已核验 {before:0.00} → {input.TargetPrice:0.00} BRL，商品、活动与原价一致。{(preserveSubsidy?" 原有补贴活动保留，实际成交价仍可能由其他生效活动决定。":"")}","cut-verified",observation);
        }
        catch(VerificationRequiredException){throw;}
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception e)when(e is AppException or IOException or OperationCanceledException or System.Net.Http.HttpRequestException or System.Net.WebSockets.WebSocketException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return Review(submitted?"已尝试保存但结果未知，下轮先回读实际价格。":e is AppException?e.Message:"连接、页面或记录写入失败，未提交价格。",submitted||e is not AppException{Status:422});
        }
        finally{await client.CloseEditorAsync();}
    }
    static string Id(string value)=>value.Replace("MLB","",StringComparison.OrdinalIgnoreCase).TrimStart('#','-');
    static bool SameProduct(ZiniaoProduct a,ZiniaoProduct b)=>Id(a.ItemId)==Id(b.ItemId)&&a.Sku==b.Sku&&a.Title==b.Title&&a.BasePriceText==b.BasePriceText&&a.Promotions.OrderBy(p=>p.Id,StringComparer.Ordinal).SequenceEqual(b.Promotions.OrderBy(p=>p.Id,StringComparer.Ordinal));
    static PriceObservation Observe(PriceCutPlan plan,ZiniaoProduct p)=>PriceReadback.Observe(plan,p);
}
