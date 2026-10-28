namespace Repricing.Execution;

public static class PriceReadback
{
    static string Id(string value)=>value.Replace("MLB","",StringComparison.OrdinalIgnoreCase).TrimStart('#','-');
    public static bool SameIdentity(PriceCutPlan plan,ZiniaoProduct product)=>Id(product.ItemId)==Id(plan.Product.ItemId)&&
        (product.Sku==plan.Input.Sku||string.IsNullOrEmpty(product.Sku)&&(plan.Input.ItemId!=null||plan.Product.SharedSkus?.Contains(plan.Input.Sku,StringComparer.Ordinal)==true));

    public static ZiniaoProduct? ReconcileManager(PriceCutPlan plan,ZiniaoProduct card,ZiniaoProduct managed)
    {
        var activityId=plan.RejoinPromotionId??plan.PromotionId;
        var active=card.Promotions.Where(p=>p.Active).ToArray();
        if(plan.ChangeBase||!activityId.StartsWith("C-",StringComparison.Ordinal)||active.Length!=1||active[0].Id!=activityId||
            !SameIdentity(plan,card)||!SameIdentity(plan,managed)||card.Sku!=plan.Input.Sku||managed.Sku!=plan.Input.Sku||
            ZiniaoReadOnlyExecutor.UnderReview(card)||ZiniaoReadOnlyExecutor.UnderReview(managed)||
            ZiniaoReadOnlyExecutor.Money(card.BasePriceText)!=plan.BasePrice||ZiniaoReadOnlyExecutor.Money(managed.BasePriceText)!=plan.BasePrice||
            managed.Promotions.Length!=0||ZiniaoReadOnlyExecutor.Money(managed.ManagedPriceText??"")!=plan.Input.TargetPrice)return null;
        return card with{ManagedPriceText=managed.ManagedPriceText,ListingStatus=managed.ListingStatus,
            Promotions=card.Promotions.Select(p=>p.Active?p with{PriceText=managed.ManagedPriceText!}:p).ToArray(),
            PriceReadbackNote=$"促销卡片显示 {active[0].PriceText}；商品管理独立核验同一商品、SKU、原价及实际售价 {managed.ManagedPriceText}，唯一生效普通活动为 {activityId}。"};
    }

    public static decimal? EffectivePrice(ZiniaoProduct product)
    {
        var active=product.Promotions.Where(p=>p.Active).ToArray();
        if(active.Length>1)return null;
        var managed=ZiniaoReadOnlyExecutor.Money(product.ManagedPriceText??"");
        if(active.Length==0)return product.ManagedPriceText!=null?managed:ZiniaoReadOnlyExecutor.Money(product.BasePriceText);
        var price=ZiniaoReadOnlyExecutor.Money(active[0].PriceText);
        return product.ManagedPriceText!=null&&managed!=price?null:price;
    }

    public static bool SameSnapshot(ZiniaoProduct first,ZiniaoProduct second)=>
        Id(first.ItemId)==Id(second.ItemId)&&first.Sku==second.Sku&&first.Title==second.Title&&
        first.BasePriceText==second.BasePriceText&&first.ManagedPriceText==second.ManagedPriceText&&first.ListingStatus==second.ListingStatus&&
        first.Promotions.OrderBy(p=>p.Id,StringComparer.Ordinal).SequenceEqual(second.Promotions.OrderBy(p=>p.Id,StringComparer.Ordinal));

    public static PriceObservation Observe(PriceCutPlan plan,ZiniaoProduct product)
    {
        var price=EffectivePrice(product);
        return new(plan.Store.Id,product.ItemId,product.Title,product.BasePriceText,price,price.HasValue?plan.Input.TargetPrice-price:null,product.Promotions,DateTimeOffset.UtcNow){ReadbackNote=product.PriceReadbackNote};
    }

    public static string? Failure(PriceCutPlan plan,ZiniaoProduct product,decimal expectedBase,string? activityId,bool allowManaged=false)
    {
        if(ZiniaoReadOnlyExecutor.UnderReview(product))return "商品需审核未激活："+product.ListingStatus;
        if(!SameIdentity(plan,product))return "商品编号或SKU与提交绑定不一致";
        if(ZiniaoReadOnlyExecutor.Money(product.BasePriceText)!=expectedBase)return "原价与本阶段预期不一致";
        if(plan.Stage=="campaign-join")return OrdinaryCampaignIncreaseExecutor.Reached(plan,product)?null:"目标普通活动未达标或其他已参加活动价格变化";
        if(plan.Stage=="scheduled-edit")
        {
            var selected=product.Promotions.Where(p=>p.Id==plan.PromotionId&&p.Id.StartsWith("C-",StringComparison.Ordinal)&&ZiniaoReadOnlyExecutor.Scheduled(p)).ToArray();
            static string[] Active(ZiniaoProduct p)=>p.Promotions.Where(x=>x.Active).Select(x=>$"{x.Id}\u001f{ZiniaoReadOnlyExecutor.Money(x.PriceText)?.ToString(System.Globalization.CultureInfo.InvariantCulture)??"?"}").Order(StringComparer.Ordinal).ToArray();
            if(selected.Length!=1)return "目标预约活动未保持唯一且已预约状态";
            if(!Active(plan.Product).SequenceEqual(Active(product),StringComparer.Ordinal))return "编辑预约活动时生效活动或其价格发生变化";
            var original=plan.Product.Promotions.Where(p=>p.Id==plan.PromotionId).ToArray();
            if(original.Length!=1||selected[0].Name!=original[0].Name)return "目标预约活动名称或绑定发生变化";
            return ZiniaoReadOnlyExecutor.Money(selected[0].PriceText)==plan.Input.TargetPrice?null:"目标预约活动价格尚未达到表格目标价";
        }
        var active=product.Promotions.Where(p=>p.Active).ToArray();
        if(activityId==null)
        {
            if(active.Length!=0)return "仍有生效活动，原价不是最终售价";
        }
        else if(!(allowManaged&&product.Promotions.Length==0&&product.ManagedPriceText!=null))
        {
            if(active.Length>1)return "多个活动同时生效，不能确认唯一实际售价";
            if(active.Length==0)return product.Promotions.Any(p=>p.Id==activityId)?"目标活动尚未激活":"目标活动尚未返回";
            if(active[0].Id!=activityId)return "其他活动仍在生效，目标活动未接管售价";
        }
        return EffectivePrice(product)==plan.Input.TargetPrice?null:"实际售价尚未达到表格目标价";
    }
}

public sealed class PriceReadbackConfirmation
{
    ZiniaoProduct? candidate;
    public bool NeedsRefresh=>candidate is not null;
    public bool Observe(ZiniaoProduct current,bool valid,bool forcedRefresh)
    {
        if(!valid){candidate=null;return false;}
        if(candidate is not null&&forcedRefresh&&PriceReadback.SameSnapshot(candidate,current))return true;
        candidate=current;
        return false;
    }
}
