namespace Repricing.Execution;

// Preserve the table's final price; lower the base to put the discount at 75%.
public class ZiniaoDiscountBaseExecutor(IZiniaoPriceCutClient client)
{
 public static bool Valid(PriceCutPlan p)=>p.ChangeBase&&!p.Join&&!p.Rejoin&&p.DiscountBase is decimal b&&b>0&&b<p.BasePrice&&b/4==p.Input.TargetPrice&&p.Input.TargetPrice>0&&decimal.Round(p.Input.TargetPrice,2)==p.Input.TargetPrice&&p.Input.TargetPrice<p.BasePrice*.2m&&(p.Stage is "discount-exit" or "discount-base" or "discount-join")&&(p.Input.PromotionId==null||p.DiscountPromotionId==null||p.Input.PromotionId==p.DiscountPromotionId)&&(p.Stage!="discount-exit"||p.PromotionId.Length>0)&&(p.Stage!="discount-join"||p.DiscountPromotionId?.StartsWith("C-",StringComparison.Ordinal)==true&&p.OfferPrice is decimal q&&q>=b*.2m&&q<=b*.95m);
 public async Task<ExecutionResult> ExecuteAsync(PriceCutPlan original,ZiniaoProduct product,Action<PriceCutPlan> checkpoint,CancellationToken ct,bool resuming)
 {
  var plan=original;var changed=resuming;var newBase=plan.DiscountBase??0;
  bool Identity(ZiniaoProduct p)=>p.ItemId==plan.Product.ItemId&&p.Sku==plan.Input.Sku;
  decimal? Base()=>ZiniaoReadOnlyExecutor.Money(product.BasePriceText);
  PromotionObservation[] Active()=>product.Promotions.Where(p=>p.Active).ToArray();
  bool Reached()=>plan.DiscountPromotionId!=null&&PriceReadback.Failure(plan,product,newBase,plan.DiscountPromotionId)==null;
  PriceObservation Observe()=>PriceReadback.Observe(plan,product);
  ExecutionResult Review(string message,bool retry=false)=>new(RowStatus.NeedsReview,"降低原价后设置折扣价",(ZiniaoReadOnlyExecutor.UnderReview(product)?"商品需审核未激活，保留待核对："+product.ListingStatus+"；":"")+message,"discount-base-review",Observe(),Retryable:retry&&!ZiniaoReadOnlyExecutor.UnderReview(product));
  ExecutionResult Success()=>new(RowStatus.Succeeded,"降低原价后设置折扣价",$"已核验原价 {newBase:0.00} BRL、活动价 {plan.Input.TargetPrice:0.00} BRL，折扣75%；表格目标保持不变。","discount-base-verified",Observe());
  async Task Fresh(){var rows=(await client.ReadAsync(plan.Store,plan.Input with{ItemId=null,PromotionId=null},ct)).Where(p=>Identity(p)).ToArray();if(rows.Length!=1)throw new AppException("改原价后原商品暂未唯一返回，下轮先回读。",409);product=rows[0];if(ZiniaoReadOnlyExecutor.UnderReview(product))throw new AppException("商品需审核未激活，保留待核对："+product.ListingStatus,422);}
  PromotionObservation[] Offers()=>product.Promotions.Where(p=>!p.Active&&p.Joinable&&p.Id.StartsWith("C-",StringComparison.Ordinal)&&(plan.Input.PromotionId==null||p.Id==plan.Input.PromotionId)&&(plan.DiscountPromotionId!=null?p.Id==plan.DiscountPromotionId:plan.PromotionId.StartsWith("C-",StringComparison.Ordinal)?p.Id==plan.PromotionId:true)).ToArray();
  try
  {
   if(ZiniaoReadOnlyExecutor.UnderReview(product))return Review("审核通过后才能继续，不自动修改审核状态。");
   if(!Valid(plan)||!Identity(product))return Review("降低原价流程绑定、目标或阶段无效，未提交。");
   if(Reached()){product=await client.VerifyAsync(plan,ct);return Reached()?Success():Review("独立回读未确认目标活动价，下轮先核对。",true);}
   if(Base()!=plan.BasePrice&&Base()!=newBase)return Review("实际原价已变成其他数值，未覆盖价格。");
   if(Active().Length>0)
   {
    var active=Active();var same=Base()==plan.BasePrice&&active.Length==1&&active[0].Id==plan.PromotionId&&ZiniaoReadOnlyExecutor.Money(active[0].PriceText)==plan.Before;
    if(resuming)return Review("原提交阶段已有记录，但仍有生效活动；下轮只回读，不重复退出或报名。",same&&plan.Stage=="discount-exit");
    if(!same||plan.Stage!="discount-exit"||!active[0].Editable)return Review("退出前原商品、活动或旧价不符，未操作。");
    if(!plan.PromotionId.StartsWith("C-",StringComparison.Ordinal)&&Offers().Length!=1)return Review("未确认退出补贴活动后可报名的唯一普通活动，未退出或改原价。");
    await client.PrepareExitAsync(plan,ct);checkpoint(plan);changed=true;await client.ConfirmExitAsync(plan,ct);await Fresh();
    if(Active().Length>0)return Review("退出后仍显示生效活动，下轮先回读。",true);
   }
   if(Base()==plan.BasePrice)
   {
    if(plan.Stage=="discount-join")return Review("已有报名记录但原价回退，未重复保存。");
    var offers=Offers();if(offers.Length!=1)return Review("未返回唯一普通活动报名入口，未降低原价。",changed);
    plan=plan with{DiscountPromotionId=offers[0].Id};
    if(resuming&&plan.Stage=="discount-base")
    {
     await Fresh();if(Base()!=plan.BasePrice||Active().Length>0||Offers().Length!=1)return Review("两次原价或活动读数不一致，未重复保存。",true);
    }
    await client.PrepareBaseAsync(plan,ct);plan=plan with{Stage="discount-base"};checkpoint(plan);changed=true;await client.SubmitBaseAsync(plan,ct);
    product=await client.VerifyAsync(plan,ct);
   }
   await Fresh();
   if(Reached()){product=await client.VerifyAsync(plan,ct);return Reached()?Success():Review("最终核验尚未确认目标价。",true);}
   if(Base()!=newBase||Active().Length>0)return Review("原价保存后未确认新原价或发现其他活动，未继续报名。",Base()==plan.BasePrice&&Active().Length==0);
   var candidates=Offers();if(candidates.Length!=1)return Review($"原价已降为 {newBase:0.00}，普通活动入口暂未唯一返回，下轮先回读。",true);
   var quote=ZiniaoReadOnlyExecutor.Money(candidates[0].PriceText);if(quote==null||quote<newBase*.2m||quote>newBase*.95m)return Review("新原价下的普通活动报价无效，未报名。");
   if(plan.Stage=="discount-join")
   {
    if(plan.OfferPrice!=quote)return Review("上次报名结果未知且报价变化，未重复提交。");
    await Fresh();if(Reached())return Success();
    var again=Offers();if(Base()!=newBase||Active().Length>0||again.Length!=1||ZiniaoReadOnlyExecutor.Money(again[0].PriceText)!=quote)return Review("两次报名状态或报价不一致，未重复提交。",true);
   }
   plan=plan with{DiscountPromotionId=candidates[0].Id};
   var editor=plan with{Product=product,BasePrice=newBase,Before=newBase,PromotionId=plan.DiscountPromotionId!,Join=true,ChangeBase=false,DiscountBase=null,DiscountPromotionId=null};
   await client.PrepareAsync(editor,ct);plan=plan with{Stage="discount-join",OfferPrice=quote};checkpoint(plan);changed=true;await client.SubmitAsync(editor,ct);
   product=await client.VerifyAsync(plan,ct);return Reached()?Success():Review("报名后未确认原价和活动价同时达标，下轮先回读。",true);
  }
  catch(VerificationRequiredException){throw;}
  catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
  catch(Exception e)when(e is AppException or IOException or OperationCanceledException or System.Net.Http.HttpRequestException or System.Net.WebSockets.WebSocketException or System.Text.Json.JsonException or InvalidOperationException)
  {return Review((changed?"已记录退出、原价或报名阶段；下轮先回读，不盲目重复提交。 ":"提交前检查失败。 ")+(e is AppException?e.Message:"连接或记录失败。"),e is not AppException{Status:422}) with{RetryReason=TransientRetry.FromException(e)};}
 }
}
