namespace Repricing.Execution;

// A bound ordinary campaign is restored independently; sibling campaigns retain their prices.
public class OrdinaryCampaignIncreaseExecutor(IZiniaoPriceCutClient client)
{
 public static bool Enrolled(PromotionObservation p)=>p.Active||ZiniaoReadOnlyExecutor.Scheduled(p);
 public static bool Valid(PriceCutPlan p)=>p.Rejoin&&!p.Join&&!p.ChangeBase&&p.DiscountBase==null&&p.PreviousExit==null&&p.RejoinPromotionId==null&&p.Stage is "campaign-exit" or "campaign-join"&&p.Input.PromotionId==p.PromotionId&&p.PromotionId.StartsWith("C-",StringComparison.Ordinal)&&p.Input.ItemId==p.Product.ItemId&&p.Input.Sku==p.Product.Sku&&p.Product.SharedSkus is not {Length:>1}&&p.Before<p.Input.TargetPrice&&p.Input.TargetPrice>=p.BasePrice*.2m&&p.Input.TargetPrice<=p.BasePrice*.95m&&p.Product.Promotions.Count(x=>x.Id==p.PromotionId&&Enrolled(x)&&x.Editable&&ZiniaoReadOnlyExecutor.Money(x.PriceText)==p.Before)==1&&!p.Product.Promotions.Any(x=>x.Active&&!x.Id.StartsWith("C-",StringComparison.Ordinal));
 public static bool Stable(PriceCutPlan plan,ZiniaoProduct current)
 {
  if(!PriceReadback.SameIdentity(plan,current)||ZiniaoReadOnlyExecutor.UnderReview(current)||ZiniaoReadOnlyExecutor.Money(current.BasePriceText)!=plan.BasePrice)return false;
  var expected=plan.Product.Promotions.Where(x=>x.Id!=plan.PromotionId&&Enrolled(x)).ToArray();var actual=current.Promotions.Where(x=>x.Id!=plan.PromotionId&&Enrolled(x)).ToArray();
  static string Name(string s)=>System.Text.RegularExpressions.Regex.Replace(s,@"\s+","");
  var original=plan.Product.Promotions.Where(x=>x.Id==plan.PromotionId).ToArray();var selected=current.Promotions.Where(x=>x.Id==plan.PromotionId).ToArray();
  if(original.Length!=1||selected.Length!=1||Name(original[0].Name)!=Name(selected[0].Name))return false;
  return expected.Length==actual.Length&&expected.All(x=>actual.Count(y=>x.Id==y.Id&&Name(x.Name)==Name(y.Name)&&ZiniaoReadOnlyExecutor.Money(x.PriceText)==ZiniaoReadOnlyExecutor.Money(y.PriceText))==1);
 }
 public static bool Reached(PriceCutPlan p,ZiniaoProduct current)=>Stable(p,current)&&current.Promotions.Count(x=>x.Id==p.PromotionId&&Enrolled(x)&&ZiniaoReadOnlyExecutor.Money(x.PriceText)==p.Input.TargetPrice)==1;
 public async Task<ExecutionResult> ExecuteAsync(PriceCutPlan plan,ZiniaoProduct current,Action<PriceCutPlan> checkpoint,CancellationToken ct,bool resuming)
 {
  ExecutionResult Review(string message,bool retry=false)=>new(RowStatus.NeedsReview,"普通活动独立提价",message,"campaign-increase-review",PriceReadback.Observe(plan,current),Retryable:retry);
  ExecutionResult Success()=>new(RowStatus.Succeeded,"普通活动独立提价",$"已核验活动 {plan.PromotionId}：{plan.Before:0.00} → {plan.Input.TargetPrice:0.00} BRL；其他已参加活动价格保持不变。","campaign-increase-verified",PriceReadback.Observe(plan,current));
  async Task Fresh(){var rows=(await client.ReadAsync(plan.Store,plan.Input,ct)).Where(x=>PriceReadback.SameIdentity(plan,x)).ToArray();if(rows.Length!=1)throw new AppException("活动提价回读未唯一返回原商品。",409);current=rows[0];}
  PromotionObservation[] Selected()=>current.Promotions.Where(x=>x.Id==plan.PromotionId).ToArray();
  bool Offer()=>Stable(plan,current)&&Selected() is [var p]&&!Enrolled(p)&&p.Joinable&&ZiniaoReadOnlyExecutor.Money(p.PriceText) is decimal q&&q>=plan.BasePrice*.2m&&q<=plan.BasePrice*.95m;
  try
  {
   if(!Valid(plan)||!Stable(plan,current))return Review("活动提价绑定或其他活动价格发生变化，未继续提交。");
   if(Reached(plan,current))return Success();
   if(Selected() is [var selected]&&Enrolled(selected))
   {
    if(resuming)return Review("已记录退出但原活动仍在参加，先保留待核对，不重复退出。");
    if(plan.Stage!="campaign-exit"||!selected.Editable||ZiniaoReadOnlyExecutor.Money(selected.PriceText)!=plan.Before)return Review("退出前目标活动价格或状态不一致。");
    await client.PrepareExitAsync(plan,ct);checkpoint(plan);await client.ConfirmExitAsync(plan,ct);await Fresh();
   }
   if(!Offer())return Review("退出后未唯一返回原普通活动报名入口，或其他活动变化；先回读。",true);
   var quote=ZiniaoReadOnlyExecutor.Money(Selected()[0].PriceText)!.Value;
   if(resuming){await Fresh();if(Reached(plan,current))return Success();if(!Offer()||ZiniaoReadOnlyExecutor.Money(Selected()[0].PriceText)!=quote||plan.Stage=="campaign-join"&&plan.OfferPrice!=quote)return Review("两次回读报名状态或报价不一致，未重复提交。");}
   var editor=plan with{Product=current,Join=true,Rejoin=false,Stage="campaign-join",Before=plan.BasePrice};
   await client.PrepareAsync(editor,ct);plan=plan with{Stage="campaign-join",OfferPrice=quote};checkpoint(plan);await client.SubmitAsync(editor,ct);
   current=await client.VerifyAsync(plan,ct);return Reached(plan,current)?Success():Review("普通活动报名保存后未核验到目标价，下轮先回读。",true);
  }
  catch(VerificationRequiredException){throw;}
  catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
  catch(Exception e)when(e is AppException or IOException or OperationCanceledException or System.Net.Http.HttpRequestException or System.Net.WebSockets.WebSocketException or System.Text.Json.JsonException or InvalidOperationException){return Review("已保留活动提价阶段；重试先回读，不盲目退出或保存。"+(e is AppException?e.Message:"页面连接或记录中断。"),e is not AppException{Status:422});}
 }
}
