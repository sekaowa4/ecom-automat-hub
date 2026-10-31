namespace Repricing.Execution;

// A saved ordinary offer is the comparison source; previews never authorize a subsidy exit.
public class ZiniaoIncomeExecutor(IZiniaoPriceCutClient client)
{
 public static bool IsIncome(PriceCutPlan p)=>p.IncomeSubsidyId!=null;
 public static bool Valid(PriceCutPlan p)
 {
  if(!p.Input.AllowExit||p.ChangeBase||p.Rejoin||p.IncomeSubsidyId?.StartsWith("P-",StringComparison.Ordinal)!=true||p.IncomeOrdinaryId?.StartsWith("C-",StringComparison.Ordinal)!=true||p.Stage is not "income-join" and not "income-exit" and not "income-ordinary-exit"||ZiniaoReadOnlyExecutor.Money(p.Product.BasePriceText)!=p.BasePrice||p.BasePrice<=0||p.Input.TargetPrice<=0)return false;
  var subsidy=p.Product.Promotions.Where(x=>x.Id==p.IncomeSubsidyId&&x.Active).ToArray();
  var ordinary=p.Product.Promotions.Where(x=>x.Id==p.IncomeOrdinaryId).ToArray();
  if(subsidy.Length!=1||ordinary.Length!=1||p.Product.Promotions.Any(x=>x.Active&&x.Id!=p.IncomeSubsidyId&&x.Id!=p.IncomeOrdinaryId))return false;
  if(p.Stage=="income-ordinary-exit")return !p.Join&&p.PromotionId==p.IncomeOrdinaryId&&ordinary[0].Editable&&(ordinary[0].Active||ZiniaoReadOnlyExecutor.Scheduled(ordinary[0]))&&p.Before==ZiniaoReadOnlyExecutor.Money(ordinary[0].PriceText)&&p.Input.TargetPrice>p.Before&&p.Input.TargetPrice>=p.BasePrice*.2m&&p.Input.TargetPrice<=p.BasePrice*.95m&&p.IncomeOrdinaryNet>p.IncomeSubsidyNet&&p.IncomeSubsidyNet==ZiniaoReadOnlyExecutor.Money(subsidy[0].NetAmountText??"");
  if(p.Stage=="income-join")return p.PromotionId==p.IncomeOrdinaryId&&p.Input.TargetPrice>=p.BasePrice*.2m&&p.Input.TargetPrice<=p.BasePrice*.95m&&p.Join==ordinary[0].Joinable&&(ordinary[0].Joinable||ordinary[0].Editable&&(ordinary[0].Active||ZiniaoReadOnlyExecutor.Scheduled(ordinary[0])))&&p.Before==(p.Join?p.BasePrice:ZiniaoReadOnlyExecutor.Money(ordinary[0].PriceText));
  return !p.Join&&p.PromotionId==p.IncomeSubsidyId&&p.Before==ZiniaoReadOnlyExecutor.Money(subsidy[0].PriceText)&&p.IncomeOrdinaryPrice==p.Input.TargetPrice&&p.IncomeOrdinaryPrice==ZiniaoReadOnlyExecutor.Money(ordinary[0].PriceText)&&(ordinary[0].Active||ZiniaoReadOnlyExecutor.Scheduled(ordinary[0]))&&p.IncomeSubsidyNet>=0&&p.IncomeOrdinaryNet>p.IncomeSubsidyNet&&p.IncomeSubsidyNet==ZiniaoReadOnlyExecutor.Money(subsidy[0].NetAmountText??"")&&p.IncomeOrdinaryNet==ZiniaoReadOnlyExecutor.Money(ordinary[0].NetAmountText??"");
 }
 public async Task<ExecutionResult> ExecuteAsync(ZiniaoStore store,PriceInput input,ZiniaoProduct product,Action<PriceCutPlan> checkpoint,CancellationToken ct,PriceCutPlan? previous=null)
 {
  if(previous!=null)input=previous.Input;
  var attempted=previous!=null;var stage=previous?.Stage;var subsidyId=previous?.IncomeSubsidyId;
  PriceObservation Observation()=>new(store.Id,product.ItemId,product.Title,product.BasePriceText,product.Promotions.Where(x=>x.Active).ToArray() is [var a]?ZiniaoReadOnlyExecutor.Money(a.PriceText):null,product.Promotions.Where(x=>x.Active).ToArray() is [var b]?input.TargetPrice-ZiniaoReadOnlyExecutor.Money(b.PriceText):null,product.Promotions,DateTimeOffset.UtcNow);
  ExecutionResult Review(string m,bool retry=false)=>new(RowStatus.NeedsReview,"普通活动报名后比较补贴收益",m,"income-review",Observation(),Retryable:retry);
  async Task<ZiniaoProduct> Fresh()
  {
   var rows=(await client.ReadAsync(store,input with{ItemId=null,PromotionId=null},ct)).Where(x=>x.ItemId==product.ItemId&&(x.Sku==input.Sku||x.Sku==product.Sku&&x.SharedSkus?.Contains(input.Sku)==true)).ToArray();
   if(rows.Length!=1||ZiniaoReadOnlyExecutor.Money(rows[0].BasePriceText)!=ZiniaoReadOnlyExecutor.Money(product.BasePriceText)||ZiniaoReadOnlyExecutor.UnderReview(rows[0]))throw new AppException("收益比较回读的商品、原价或审核状态变化，未继续退出补贴。",422);
   return rows[0];
  }
  try
  {
   if(!string.IsNullOrEmpty(product.Sku)&&product.Sku!=input.Sku||product.SharedSkus is {Length:>0} known&&!known.Contains(input.Sku,StringComparer.Ordinal))return Review("收益流程的 SKU 或旧式商品身份不一致，未操作。");
   if(previous!=null&&(previous.Store.Id!=store.Id||previous.Product.ItemId!=product.ItemId||ZiniaoReadOnlyExecutor.Money(product.BasePriceText)!=previous.BasePrice))return Review("收益流程重试的商品或原价已变化，未继续提交。");
   if(!input.AllowExit)return Review("收益优先流程需要本任务已确认补贴操作范围，未操作。");
   var subsidies=product.Promotions.Where(x=>x.Active&&x.Id.StartsWith("P-",StringComparison.Ordinal)).ToArray();
   if(previous?.Stage=="income-exit"&&!product.Promotions.Any(x=>x.Id==subsidyId&&x.Active))
   {
    if(PriceReadback.Failure(previous,product,previous.BasePrice,previous.IncomeOrdinaryId!)==null)return new(RowStatus.Succeeded,"补贴退出回读核验","低收益补贴已退出，普通活动目标价已激活；未重复提交。","income-verified",Observation());
    return Review("已退出低收益补贴，但普通活动尚未激活或实际价格未到目标，保留待核对。",true);
   }
   if(subsidies.Length!=1||subsidyId!=null&&subsidies[0].Id!=subsidyId)return Review("未唯一绑定原补贴活动，未操作。");
   if(previous?.Stage=="income-exit")return Review("上次补贴退出已提交但原补贴仍显示激活；下轮只核对退出结果，不重复退出或改为保留完成。",true);
   var subsidy=subsidies[0];subsidyId=subsidy.Id;
   var ordinary=product.Promotions.Where(x=>x.Id.StartsWith("C-",StringComparison.Ordinal)&&(x.Active||x.Joinable||x.Editable&&ZiniaoReadOnlyExecutor.Scheduled(x))&&(previous?.IncomeOrdinaryId==null||x.Id==previous.IncomeOrdinaryId)).ToArray();
   if(ordinary.Length!=1)return Review("未找到唯一普通活动报名或实际价格，未退出补贴。");
   var offer=ordinary[0];var basePrice=ZiniaoReadOnlyExecutor.Money(product.BasePriceText);
   if(basePrice==null)return Review("原价不明确，未继续。");
   var atTarget=(offer.Active||ZiniaoReadOnlyExecutor.Scheduled(offer))&&ZiniaoReadOnlyExecutor.Money(offer.PriceText)==input.TargetPrice;
   var capped=false;
   async Task Report(PriceCutPlan p)
   {
    await client.PrepareAsync(p,ct);checkpoint(p);attempted=true;stage="income-join";await client.SubmitAsync(p,ct);
    product=await Fresh();
    offer=product.Promotions.SingleOrDefault(x=>x.Id==p.IncomeOrdinaryId)??throw new AppException("普通活动提交后尚未返回，先回读，不重复报名。",409);
    subsidy=product.Promotions.SingleOrDefault(x=>x.Active&&x.Id==p.IncomeSubsidyId)??throw new AppException("普通活动提交后原补贴状态已变，先核对。",409);
   }
   if(!atTarget&&previous?.Stage=="income-ordinary-exit")
   {
    if(!offer.Joinable||offer.Active)return Review("普通活动退出结果尚未确认，先回读，不重复退出或报名。",true);
    var rejoin=previous with{Product=product,Before=basePrice.Value,Join=true,Stage="income-join"};
    if(!Valid(rejoin))return Review("普通活动重新报名绑定不符，未提交。");
    await Report(rejoin);
    if(!(offer.Active||ZiniaoReadOnlyExecutor.Scheduled(offer))||ZiniaoReadOnlyExecutor.Money(offer.PriceText)!=input.TargetPrice)return Review("普通活动重报已提交，目标价尚未同步，先回读，不重复提交。",true);
    atTarget=true;
   }
   if(!atTarget&&previous==null)
   {
    var plan=new PriceCutPlan(store,input,product,offer.Id,offer.Joinable?basePrice.Value:ZiniaoReadOnlyExecutor.Money(offer.PriceText)??0,basePrice.Value,Join:offer.Joinable,Stage:"income-join",IncomeSubsidyId:subsidy.Id,IncomeOrdinaryId:offer.Id);
    if(!Valid(plan))return Review("普通活动目标价报名绑定不完整，未操作。");
    var preview=await client.PreviewNetAsync(plan,ct);var platformNet=ZiniaoReadOnlyExecutor.Money(subsidy.NetAmountText??"");
    if(preview==null||platformNet==null)return Review("目标价主列净收试算尚未准确返回，未退出任何活动。",true);
    if(preview<=platformNet)return new(RowStatus.Skipped,"目标价试算收益优先保留补贴",$"普通活动目标 {input.TargetPrice:0.00} 的主列净收试算 {preview:0.00} 不高于补贴净收 {platformNet:0.00}；保留补贴，未保存普通活动价格。","subsidy-preview-kept",Observation());
    if(product.SharedSkus is {Length:>1})return Review("共享链接目标价试算收益更高，但需先确认同链接各SKU目标价一致，未统一修改或退出活动。");
    try{await client.PrepareAsync(plan,ct);}
    catch(AppException e)when(e.Status==422&&System.Text.RegularExpressions.Regex.IsMatch(e.Message,"上限|最高|maximum|máximo",System.Text.RegularExpressions.RegexOptions.IgnoreCase)){capped=true;}
    if(capped)
    {
     await client.CloseEditorAsync();
     var exitOrdinary=plan with{Stage="income-ordinary-exit",IncomeOrdinaryNet=preview,IncomeSubsidyNet=platformNet};
     if(!Valid(exitOrdinary))return Review("普通活动退出重报范围未通过核验，未退出。");
     await client.PrepareExitAsync(exitOrdinary,ct);checkpoint(exitOrdinary);attempted=true;stage="income-ordinary-exit";await client.ConfirmExitAsync(exitOrdinary,ct);
     product=await Fresh();
     offer=product.Promotions.SingleOrDefault(x=>x.Id==plan.IncomeOrdinaryId)??throw new AppException("普通活动重新报名入口尚未返回，先回读。",409);
     if(!offer.Joinable||offer.Active)return Review("普通活动退出后尚未读到报名入口，先回读，不重复退出。",true);
     var rejoin=plan with{Product=product,Before=basePrice.Value,Join=true};
     if(!Valid(rejoin))return Review("普通活动重新报名绑定不符，未提交。");
     await Report(rejoin);
    }
    else
    {
     checkpoint(plan);attempted=true;stage="income-join";await client.SubmitAsync(plan,ct);product=await Fresh();
     offer=product.Promotions.SingleOrDefault(x=>x.Id==plan.IncomeOrdinaryId)??throw new AppException("普通活动提交后尚未返回，先回读。",409);
     subsidy=product.Promotions.SingleOrDefault(x=>x.Active&&x.Id==plan.IncomeSubsidyId)??throw new AppException("普通活动提交后原补贴状态已变，先核对。",409);
    }
    if(!(offer.Active||ZiniaoReadOnlyExecutor.Scheduled(offer))||ZiniaoReadOnlyExecutor.Money(offer.PriceText)!=input.TargetPrice)return Review("普通活动已提交，但目标价尚未同步回读，保留待核对；下轮只读取，不重复报名。",true);
   }
   else if(previous!=null&&!atTarget)return Review("上次普通活动已提交但目标价尚未回读，未重复报名或退出补贴。",true);
   return await Compare(offer,subsidy,capped);

   async Task<ExecutionResult> Compare(PromotionObservation normal,PromotionObservation platform,bool cap)
   {
    var ordinaryPrice=ZiniaoReadOnlyExecutor.Money(normal.PriceText);var normalNet=ZiniaoReadOnlyExecutor.Money(normal.NetAmountText??"");var subsidyNet=ZiniaoReadOnlyExecutor.Money(platform.NetAmountText??"");
    if(!(normal.Active||ZiniaoReadOnlyExecutor.Scheduled(normal))||normalNet==null||subsidyNet==null||normalNet<0||subsidyNet<0)return Review("普通活动与补贴活动实际净收款额尚未完整返回，保留补贴，下一轮先回读。",true);
    if(ordinaryPrice!=input.TargetPrice)return Review("普通活动尚未按表格目标价实报；旧价净收款不能用于判定补贴收益更高。需完成目标价报名后重新比较，未退出补贴。",true);
    if(subsidyNet>=normalNet)return new(RowStatus.Skipped,"收益优先保留补贴",$"已保留高收益补贴：实际净收 BRL {subsidyNet:0.00} ≥ 普通活动净收 BRL {normalNet:0.00}。实际售价 {ZiniaoReadOnlyExecutor.Money(platform.PriceText):0.00}，表格目标 {input.TargetPrice:0.00}；{(ordinaryPrice==input.TargetPrice?"普通活动已按目标报名":"普通活动现价 "+ordinaryPrice?.ToString("0.00")+"，未达到表格目标价")}。不表示目标售价已激活。","subsidy-income-kept",Observation());
    if(product.SharedSkus is {Length:>1})return Review("旧式共价商品只比较现有活动收益，未统一修改父链接或退出补贴。");
    if(ordinaryPrice!=input.TargetPrice)return Review("普通活动未达到表格目标，补贴净收更低，但未取得目标普通活动实报结果，未退出。",true);
    if(previous?.Stage=="income-exit")return Review("上次补贴退出已提交，尚未确认退出，不重复退出。",true);
    var exit=new PriceCutPlan(store,input,product,platform.Id,ZiniaoReadOnlyExecutor.Money(platform.PriceText)??0,basePrice.Value,Stage:"income-exit",IncomeSubsidyId:platform.Id,IncomeOrdinaryId:normal.Id,IncomeOrdinaryPrice:ordinaryPrice,IncomeSubsidyNet:subsidyNet,IncomeOrdinaryNet:normalNet);
    if(!Valid(exit))return Review("补贴退出的实际活动或净收绑定不完整，未退出。");
    await client.PrepareExitAsync(exit,ct);checkpoint(exit);attempted=true;stage="income-exit";await client.ConfirmExitAsync(exit,ct);
    product=await Fresh();
    if(product.Promotions.Any(x=>x.Active&&x.Id==platform.Id))return Review("补贴退出已提交但尚未回读确认，下轮只核对。",true);
    if(PriceReadback.Failure(exit,product,basePrice.Value,normal.Id)!=null)return Review("低收益补贴已退出，普通活动尚未激活或目标价未生效，保留待核对。",true);
    return new(RowStatus.Succeeded,"实报后比较并退出低收益补贴",$"普通活动已按目标 {input.TargetPrice:0.00} 报名；实际净收 {normalNet:0.00} 高于补贴净收 {subsidyNet:0.00}，已退出该补贴并核验目标价生效。","income-verified",Observation());
   }
  }
  catch(VerificationRequiredException){throw;}
  catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
  catch(Exception e)when(e is AppException or IOException or OperationCanceledException or System.Net.Http.HttpRequestException or System.Net.WebSockets.WebSocketException or System.Text.Json.JsonException or InvalidOperationException)
  {return Review((attempted?$"已记录 {stage} 提交，下一轮先回读，不重复提交。 ":"")+(e is AppException?e.Message:"连接或读取中断，未盲目退出补贴。"),e is not AppException{Status:422}) with{RetryReason=TransientRetry.FromException(e)};}
 }
}
