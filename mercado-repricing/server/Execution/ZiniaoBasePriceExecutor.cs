namespace Repricing.Execution;

// The table target is the final base price; this flow never joins a campaign.
public class ZiniaoBasePriceExecutor(IZiniaoPriceCutClient client)
{
 public async Task<ExecutionResult> ExecuteAsync(PriceCutPlan original,ZiniaoProduct product,Action<PriceCutPlan> checkpoint,CancellationToken ct,bool resuming)
 {
  var plan=original;var changed=resuming;
  bool Identity(ZiniaoProduct p)=>p.ItemId==plan.Product.ItemId&&p.Sku==plan.Input.Sku;
  decimal? Base(ZiniaoProduct p)=>ZiniaoReadOnlyExecutor.Money(p.BasePriceText);
  bool Reached()=>plan.DirectBaseOnly?PriceReadback.SameIdentity(plan,product)&&Base(product)==plan.Input.TargetPrice&&!ZiniaoReadOnlyExecutor.UnderReview(product):PriceReadback.Failure(plan,product,plan.Input.TargetPrice,null)==null;
  PriceObservation Observe()=>PriceReadback.Observe(plan,product);
  ExecutionResult Review(string message,bool retry=false)=>new(RowStatus.NeedsReview,"修改商品原价",(ZiniaoReadOnlyExecutor.UnderReview(product)?"商品需审核未激活，保留待核对："+product.ListingStatus+"；":"")+message,"base-review",Observe(),Retryable:retry&&!ZiniaoReadOnlyExecutor.UnderReview(product));
  string SharedStockNote()=>plan.Product.SharedSkus is {Length:>1} siblings?plan.DirectBaseOnly&&plan.Input.Note?.Trim()=="仅改原价（用户确认整链接统一调价）"?$" 链接主价由 {siblings.Length} 个SKU共用；用户已确认整链接所有规格统一调价。":plan.DirectBaseOnly?$" 链接主价由 {siblings.Length} 个SKU共用；用户确认仅 {plan.Input.Sku} 有库存，其余 {siblings.Length-1} 个规格售罄。":$" 链接主价由 {siblings.Length} 个SKU共用；本次按确认的整链接规则统一修改，关联规格同步变价。":"";
  string CurrentPriceNote()=>plan.DirectBaseOnly&&ZiniaoReadOnlyExecutor.Money(product.ManagedPriceText??"") is decimal current&&current!=plan.Input.TargetPrice?$"当前售价仍为 R$ {current:0.00} BRL。":"";
  ExecutionResult Success()=>new(RowStatus.Succeeded,"修改商品原价",plan.DirectBaseOnly?$"已在商品管理核验原价为 {plan.Input.TargetPrice:0.00} BRL；按指示仅改原价，未退出或重新报名活动。{CurrentPriceNote()}{SharedStockNote()}":$"已核验原价与售价均为 {plan.Input.TargetPrice:0.00} BRL，且无生效促销活动；未重新报名。{SharedStockNote()}","base-verified",Observe());
  async Task<ExecutionResult> VerifyReached(){product=await client.VerifyAsync(plan,ct);return Reached()?Success():Review("独立回读未确认最终售价，下轮先核对。",true);}
  async Task Fresh(){var rows=(await (plan.DirectBaseOnly?client.ReadManagerAsync(plan.Store,plan.Input with{ItemId=plan.Product.ItemId},ct):client.ReadAsync(plan.Store,plan.Input with{ItemId=null},ct))).Where(p=>p.ItemId==plan.Product.ItemId&&p.Sku==plan.Input.Sku).ToArray();if(rows.Length!=1)throw new AppException("第三种回读商品未唯一匹配。",409);product=rows[0];if(ZiniaoReadOnlyExecutor.UnderReview(product))throw new AppException("商品需审核未激活，保留待核对："+product.ListingStatus,422);}
  try
  {
   if(ZiniaoReadOnlyExecutor.UnderReview(product))return Review("审核通过后才能继续，不自动修改审核状态。");
   if(!plan.ChangeBase||plan.Join||plan.Rejoin||plan.Stage is not "base-exit" and not "base-save"||!Identity(product)||!plan.DirectBaseOnly&&!(plan.Input.TargetPrice<plan.BasePrice*.2m||plan.Input.TargetPrice>plan.BasePrice*.95m))return Review("第三种绑定、SKU或目标范围不符，未提交。");
   if(Reached())return resuming?await VerifyReached():new(RowStatus.Skipped,"修改商品原价","原价及售价已是目标价，无需重复保存。","base-unchanged",Observe());
   if(Base(product)!=plan.BasePrice)return Review("商品原价已发生其他变化，未覆盖价格。");
   var active=product.Promotions.Where(p=>p.Active).ToArray();
   if(active.Length>0)
   {
    var same=active.Length==1&&active[0].Id==plan.PromotionId&&ZiniaoReadOnlyExecutor.Money(active[0].PriceText)==plan.Before;
    if(resuming)return Review(same&&plan.Stage=="base-exit"?"上次退出结果尚未确认，下轮只回读，不重复退出。":"发现新的活动或活动价，未继续改原价。",same&&plan.Stage=="base-exit");
    if(!same||!active[0].Editable)return Review("退出前活动未唯一确认或不可修改，未操作。");
    await client.PrepareExitAsync(plan,ct);checkpoint(plan);changed=true;await client.ConfirmExitAsync(plan,ct);
    await Fresh();if(Reached())return await VerifyReached();
    if(product.Promotions.Any(p=>p.Active))return Review("退出后仍显示生效活动，下轮先回读。",true);
    if(Base(product)!=plan.BasePrice)return Review("退出后原价发生其他变化，未覆盖价格。");
   }
   if(resuming&&plan.Stage=="base-save")
   {
    await Fresh();if(Reached())return await VerifyReached();
    if(Base(product)!=plan.BasePrice||product.Promotions.Any(p=>p.Active))return Review("两次原价或活动读数不一致，未重复保存。",true);
   }
   if(plan.Stage=="base-exit"&&plan.PromotionId.Length==0)return Review("退出活动的绑定缺失，未改价。");
   await client.PrepareBaseAsync(plan,ct);plan=plan with{Stage="base-save"};checkpoint(plan);changed=true;await client.SubmitBaseAsync(plan,ct);
   product=await client.VerifyAsync(plan,ct);
   return Reached()?Success():Review("保存后未确认原价及售价同时到达目标，下轮先回读。",true);
  }
  catch(VerificationRequiredException){throw;}
  catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
  catch(Exception e)when(e is AppException or IOException or OperationCanceledException or System.Net.Http.HttpRequestException or System.Net.WebSockets.WebSocketException or System.Text.Json.JsonException or InvalidOperationException)
  {
   return Review((changed?"已记录第三种提交阶段，活动可能已退出；下轮先回读，禁止盲目重复提交。 ":"第三种准备或记录失败，未继续提交。 ")+(e is AppException?e.Message:"连接或记录失败。"),e is not AppException{Status:422});
  }
 }
}
