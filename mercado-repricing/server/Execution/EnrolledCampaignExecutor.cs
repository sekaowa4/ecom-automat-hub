namespace Repricing.Execution;

// Edit an explicitly bound, already-enrolled platform campaign without exiting it.
public class EnrolledCampaignExecutor(IZiniaoPriceCutClient client)
{
    public static bool Enrolled(PromotionObservation p)=>p.Active||ZiniaoReadOnlyExecutor.Scheduled(p);
    public static bool Valid(PriceCutPlan p)=>p.Stage=="enrolled-edit"&&!p.Join&&!p.Rejoin&&!p.ChangeBase&&p.DiscountBase==null&&p.IncomeSubsidyId==null&&p.Input.ItemId==p.Product.ItemId&&p.Input.PromotionId==p.PromotionId&&p.Product.Sku==p.Input.Sku&&p.Product.SharedSkus is not {Length:>1}&&p.PromotionId.StartsWith("P-",StringComparison.Ordinal)&&p.Input.TargetPrice>=p.BasePrice*.2m&&p.Input.TargetPrice<=p.BasePrice*.95m&&p.Product.Promotions.Count(x=>x.Id==p.PromotionId&&Enrolled(x)&&x.Editable&&ZiniaoReadOnlyExecutor.Money(x.PriceText)==p.Before)==1;
    public static bool Reached(PriceCutPlan plan,ZiniaoProduct current)
    {
        if(!PriceReadback.SameIdentity(plan,current)||ZiniaoReadOnlyExecutor.UnderReview(current)||ZiniaoReadOnlyExecutor.Money(current.BasePriceText)!=plan.BasePrice)return false;
        var selected=current.Promotions.Where(x=>x.Id==plan.PromotionId&&Enrolled(x)).ToArray();var original=plan.Product.Promotions.SingleOrDefault(x=>x.Id==plan.PromotionId);
        if(selected.Length!=1||original==null||selected[0].Name!=original.Name||ZiniaoReadOnlyExecutor.Money(selected[0].PriceText)!=plan.Input.TargetPrice)return false;
        var expected=plan.Product.Promotions.Where(x=>x.Id!=plan.PromotionId&&Enrolled(x)).OrderBy(x=>x.Id).ToArray();var actual=current.Promotions.Where(x=>x.Id!=plan.PromotionId&&Enrolled(x)).OrderBy(x=>x.Id).ToArray();return expected.SequenceEqual(actual);
    }
    public async Task<ExecutionResult> ExecuteAsync(PriceCutPlan plan,ZiniaoProduct product,Action<PriceCutPlan> checkpoint,CancellationToken ct,bool resuming)
    {
        ExecutionResult Review(string message,bool retry=false)=>new(RowStatus.NeedsReview,"已参加平台活动改价",message,"enrolled-review",PriceReadback.Observe(plan,product),Retryable:retry);
        async Task<ZiniaoProduct> Fresh(){var rows=(await client.ReadAsync(plan.Store,plan.Input,ct)).Where(x=>PriceReadback.SameIdentity(plan,x)).ToArray();if(rows.Length!=1)throw new AppException("已参加活动回读商品未唯一匹配。",409);return rows[0];}
        try
        {
            if(!Valid(plan))return Review("已参加活动绑定或目标范围不符，未提交。");
            if(resuming){product=await Fresh();if(!Reached(plan,product))return Review("原活动提交结果尚未确认，仅回读，不重复保存。",true);var second=await Fresh();if(!Reached(plan,second)||!PriceReadback.SameSnapshot(product,second))return Review("原活动两次回读不一致，未重放保存。",true);product=second;}
            else{
                try{await client.PrepareAsync(plan,ct);}
                catch(AppException e)when(e.Status==422&&plan.Input.AllowExit&&e.Message.Contains("上限",StringComparison.Ordinal)&&plan.Input.TargetPrice>plan.Before&&plan.Product.Promotions.Any(p=>p.Id==plan.PromotionId&&OrdinaryCampaignIncreaseExecutor.Event(p)))
                {await client.CloseEditorAsync();return await new OrdinaryCampaignIncreaseExecutor(client).ExecuteAsync(plan with{Rejoin=true,Stage="campaign-exit"},product,checkpoint,ct,false);}
                checkpoint(plan);await client.SubmitAsync(plan,ct);product=await client.VerifyAsync(plan,ct);if(!Reached(plan,product))return Review("活动保存后尚未到目标价或其他活动变化，下轮先回读。",true);
            }
            return new(RowStatus.Succeeded,"已参加平台活动改价",$"商品 {product.ItemId} · 活动 {plan.PromotionId}：{plan.Before:0.00} → {plan.Input.TargetPrice:0.00} BRL；已回读确认，未退出活动。","enrolled-verified",PriceReadback.Observe(plan,product));
        }
        catch(VerificationRequiredException){throw;}catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception e)when(e is AppException or IOException or OperationCanceledException or System.Net.Http.HttpRequestException or System.Net.WebSockets.WebSocketException){return Review((resuming?"原提交仅回读。 ":"")+(e is AppException?e.Message:"页面连接或记录中断，先回读确认。"),e is not AppException{Status:422}) with{RetryReason=TransientRetry.FromException(e)};}
    }
}
