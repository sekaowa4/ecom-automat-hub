namespace Repricing.Execution;

public class ZiniaoIncreaseExecutor(IZiniaoPriceCutClient client)
{
 public static PriceCutPlan? FollowScheduledDiscount(PriceCutPlan previous,ZiniaoProduct current)
 {
  if(!previous.Input.AllowExit||previous.PreviousExit!=null||!previous.Rejoin||previous.Stage!="exit"||ZiniaoReadOnlyExecutor.UnderReview(current)||current.ItemId!=previous.Product.ItemId||current.Sku!=previous.Input.Sku||ZiniaoReadOnlyExecutor.Money(current.BasePriceText)!=previous.BasePrice)return null;
  var active=current.Promotions.Where(p=>p.Active).ToArray();if(active is not [var successor]||!successor.Editable||ZiniaoReadOnlyExecutor.Money(successor.PriceText) is not decimal price||price>=previous.Input.TargetPrice)return null;
  if(previous.PromotionId.StartsWith("P-",StringComparison.Ordinal)&&previous.RejoinPromotionId==successor.Id&&successor.Id.StartsWith("C-",StringComparison.Ordinal))
  {
   var scheduledOrdinary=previous.Product.Promotions.Where(p=>p.Id==successor.Id&&p.Editable&&ZiniaoReadOnlyExecutor.Scheduled(p)).ToArray();
   static string Name(string value)=>System.Text.RegularExpressions.Regex.Replace(value,@"\s+","");
   if(scheduledOrdinary.Length!=1||string.IsNullOrWhiteSpace(successor.Name)||Name(scheduledOrdinary[0].Name)!=Name(successor.Name)||ZiniaoReadOnlyExecutor.Money(scheduledOrdinary[0].PriceText)!=price)return null;
   return previous with{Product=current,PromotionId=successor.Id,Before=price,RejoinPromotionId=null,PreviousExit=previous};
  }
  if(!previous.PromotionId.StartsWith("C-",StringComparison.Ordinal)||previous.RejoinPromotionId!=null||successor.Id!="D-PRICE-DISCOUNT")return null;
  var scheduled=previous.Product.Promotions.Where(p=>p.Id==successor.Id&&!p.Active&&System.Text.RegularExpressions.Regex.IsMatch(p.Status,"已预约|Agendada|Agendado|Scheduled",System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToArray();
  if(scheduled.Length!=1||scheduled[0].Name!=successor.Name||ZiniaoReadOnlyExecutor.Money(scheduled[0].PriceText)!=price||!current.Promotions.Any(p=>p.Id==previous.PromotionId&&!p.Active&&p.Joinable))return null;
  return previous with{Product=current,PromotionId=successor.Id,Before=price,RejoinPromotionId=previous.PromotionId,PreviousExit=previous};
 }
 public async Task<ExecutionResult> ExecuteAsync(PriceCutPlan original,ZiniaoProduct product,Action<PriceCutPlan> checkpoint,CancellationToken ct,bool resuming)
 {
  var plan=original;var changed=resuming;var destination=plan.RejoinPromotionId??plan.PromotionId;
  PriceObservation Observe(ZiniaoProduct p)=>PriceReadback.Observe(plan,p);
  ExecutionResult Review(string message,bool retry=false)=>new(RowStatus.NeedsReview,"退出后重报提价",(ZiniaoReadOnlyExecutor.UnderReview(product)?"商品需审核未激活，保留待核对："+product.ListingStatus+"；":"")+message,"increase-review",Observe(product),Retryable:retry&&!ZiniaoReadOnlyExecutor.UnderReview(product));
  bool Same(ZiniaoProduct p)=>p.ItemId==plan.Product.ItemId&&p.Sku==plan.Input.Sku&&ZiniaoReadOnlyExecutor.Money(p.BasePriceText)==plan.BasePrice;
  bool Reached(ZiniaoProduct p)=>PriceReadback.Failure(plan,p,plan.BasePrice,destination)==null;
  ExecutionResult Success()=>new(RowStatus.Succeeded,"退出后重报提价",$"已核验 {plan.Before:0.00} → {plan.Input.TargetPrice:0.00} BRL，{(plan.RejoinPromotionId==null?"原活动":"替代普通活动")}与原价一致；已到目标时不重复提交。","increase-verified",Observe(product));
  async Task<ZiniaoProduct> Fresh(){var rows=(await client.ReadAsync(plan.Store,plan.Input with{ItemId=null,PromotionId=plan.PromotionId},ct)).Where(x=>x.ItemId==plan.Product.ItemId&&x.Sku==plan.Input.Sku).ToArray();if(rows.Length!=1)throw new AppException("退出后促销中心暂未返回唯一原商品，下轮先回读，不重复退出。",409);if(!Same(rows[0]))throw new AppException("提价回读原价已变化，未继续提交。",422);if(ZiniaoReadOnlyExecutor.UnderReview(rows[0])){product=rows[0];throw new AppException("商品需审核未激活，保留待核对："+product.ListingStatus,422);}return rows[0];}
  try
  {
   if(ZiniaoReadOnlyExecutor.UnderReview(product))return Review("审核通过后才能继续，不自动修改审核状态。");
   if(plan.PromotionId.StartsWith("P-",StringComparison.Ordinal)&&plan.RejoinPromotionId==null)return Review("当前生效的是平台补贴活动；现有提价流程只支持退出并重报普通活动，未退出补贴活动，也未修改已预约活动。需单独确认补贴活动提价规则。");
   if(!plan.Rejoin||plan.Join||plan.Stage is not "exit" and not "join"||!Same(product)||(!destination.StartsWith("C-",StringComparison.Ordinal)||plan.PromotionId!="D-PRICE-DISCOUNT"&&!System.Text.RegularExpressions.Regex.IsMatch(plan.PromotionId,@"^(C|P)-[A-Za-z0-9-]+$"))||plan.Before>=plan.Input.TargetPrice||plan.Input.TargetPrice<plan.BasePrice*.2m||plan.Input.TargetPrice>plan.BasePrice*.95m)return Review("提价绑定、SKU、原价或目标范围不一致，未退出活动。");
   if(Reached(product))return Success();
   var active=product.Promotions.Where(x=>x.Active).ToArray();
   if(active.Length>0)
   {
    if(resuming){if(FollowScheduledDiscount(plan,product) is {} successor)return await ExecuteAsync(successor,product,checkpoint,ct,false);return Review(active.Length==1&&active[0].Id==plan.PromotionId&&ZiniaoReadOnlyExecutor.Money(active[0].PriceText)==plan.Before?"上次退出结果尚未确认，下轮只重新读取，不重复退出。":"提价重试发现其他活动价，禁止重新退出。",plan.Stage=="exit"&&active.Length==1&&active[0].Id==plan.PromotionId&&ZiniaoReadOnlyExecutor.Money(active[0].PriceText)==plan.Before);}
    if(active.Length!=1||active[0].Id!=plan.PromotionId||!active[0].Editable&&!(plan.Input.AllowExit&&active[0].Id.StartsWith("P-",StringComparison.Ordinal))||ZiniaoReadOnlyExecutor.Money(active[0].PriceText)!=plan.Before)return Review("退出前活动或旧价不符，未退出。");
    await client.PrepareExitAsync(plan,ct);checkpoint(plan);changed=true;await client.ConfirmExitAsync(plan,ct);
    product=await Fresh();if(Reached(product))return Success();
    if(FollowScheduledDiscount(plan,product) is {} next)return await ExecuteAsync(next,product,checkpoint,ct,false);
    if(product.Promotions.Any(x=>x.Active))return Review("已尝试退出但页面仍显示生效活动，下轮先回读，不重复退出。",true);
   }
   else if(!resuming)return Review("首次提价未找到原生效活动，未操作。");
   var offers=product.Promotions.Where(x=>x.Id==destination&&x.Joinable&&!x.Active).ToArray();
   if(offers.Length!=1)return Review("原活动普通报名入口暂未唯一返回；活动可能已退出，下轮先核对。",true);
   var quote=ZiniaoReadOnlyExecutor.Money(offers[0].PriceText);if(quote==null)return Review("原活动报价无法确认；活动可能已退出，未报名。");
   if(resuming&&plan.Stage=="join")
   {
    if(plan.OfferPrice!=quote)return Review("上次报名结果未知且报价已变化，未重复提交。");
    product=await Fresh();if(Reached(product))return Success();
    var freshOffers=product.Promotions.Where(x=>x.Id==destination&&x.Joinable&&!x.Active).ToArray();
    if(product.Promotions.Any(x=>x.Active)||freshOffers.Length!=1||ZiniaoReadOnlyExecutor.Money(freshOffers[0].PriceText)!=quote)return Review("两次报名状态或报价不一致，未重复提交。",true);
   }
   // Keep the persisted original binding; the editor receives the current inactive offer.
   var editor=plan with{Product=product,PromotionId=destination,Before=plan.BasePrice,Join=true,Rejoin=false};
   await client.PrepareAsync(editor,ct);plan=plan with{Stage="join",OfferPrice=quote};checkpoint(plan);changed=true;await client.SubmitAsync(editor,ct);
   product=await client.VerifyAsync(plan,ct);if(!Same(product))return Review("提价后商品或原价变化，不再提交。");
   return Reached(product)?Success():Review("报名提交后未确认目标价，下轮先回读；不重复退出。",true);
  }
  catch(VerificationRequiredException){throw;}
  catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
  catch(Exception e)when(e is AppException or IOException or OperationCanceledException or System.Net.Http.HttpRequestException or System.Net.WebSockets.WebSocketException or System.Text.Json.JsonException or InvalidOperationException)
  {
   return Review((changed?(e is AppException{Status:422}?"活动可能已退出，请先核对；未继续提交。 ":"提价中断，活动可能已退出；下一轮先核对。 "):"退出前校验或记录失败，未确认退出。 ")+(e is AppException?e.Message:"连接或记录失败。"),e is not AppException{Status:422});
  }
 }
}
