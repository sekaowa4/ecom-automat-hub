using Repricing.Execution;
namespace Repricing.Tests;
public class CampaignTransitionTests
{
 sealed class Client(bool editable):IZiniaoPriceCutClient
 {
  public List<string> Calls=[];
  public ZiniaoProduct Current=new("456","SKU-A","商品","R$ 100",[new("P-1","特殊活动","激活","R$ 80",true,editable),new("C-1","普通活动","","R$ 95",false,false,false,true)]);
  public Task<ZiniaoStore[]> StoresAsync(CancellationToken ct)=>Task.FromResult(new[]{new ZiniaoStore("123","测试","MercadoLibre-巴西")});
  public Task<ZiniaoProduct[]> ReadAsync(ZiniaoStore s,PriceInput i,CancellationToken ct)=>Task.FromResult(new[]{Current});
  public Task PrepareExitAsync(PriceCutPlan p,CancellationToken ct){Calls.Add("prepare-exit");return Task.CompletedTask;}
  public Task ConfirmExitAsync(PriceCutPlan p,CancellationToken ct){Calls.Add("exit");Current=Current with{Promotions=Current.Promotions.Where(x=>!x.Active).ToArray()};return Task.CompletedTask;}
  public Task PrepareAsync(PriceCutPlan p,CancellationToken ct){Assert.Equal("C-1",p.PromotionId);Assert.True(p.Join);Calls.Add("prepare-join");return Task.CompletedTask;}
  public Task SubmitAsync(PriceCutPlan p,CancellationToken ct){Calls.Add("save");Current=Current with{Promotions=Current.Promotions.Select(x=>x.Id=="C-1"?x with{Active=true,Editable=true,Joinable=false,PriceText="R$ 90"}:x).ToArray()};return Task.CompletedTask;}
  public Task<ZiniaoProduct> VerifyAsync(PriceCutPlan p,CancellationToken ct)=>Task.FromResult(Current);
 }
 static PriceInput Input()=>new("123","MLB","BRL","SKU-A",null,null,null,90,null,false,false,null,"提价");
 [Fact] public async Task PreservedSubsidyCannotPretendOrdinaryTargetIsActualSellingPrice()
 {
  var c=new Client(false);var r=await new ZiniaoPriceCutExecutor(c).ExecuteAsync(Input(),p=>c.Calls.Add("checkpoint"),default,allowIncrease:true);
  Assert.Equal(RowStatus.NeedsReview,r.Status);Assert.Null(r.Observation!.CurrentPrice);Assert.Contains(c.Current.Promotions,p=>p.Id=="P-1"&&p.Active);Assert.Equal(new[]{"prepare-join","checkpoint","save"},c.Calls);
 }
 [Fact] public async Task EditableSpecialActivityExitsOnceThenJoinsBoundOrdinaryActivity()
 {
  var c=new Client(true);var plans=new List<PriceCutPlan>();var r=await new ZiniaoPriceCutExecutor(c).ExecuteAsync(Input(),p=>{plans.Add(p);c.Calls.Add("checkpoint-"+p.Stage);},default,allowIncrease:true);
  Assert.Equal(RowStatus.Succeeded,r.Status);Assert.Equal(new[]{"prepare-exit","checkpoint-exit","exit","prepare-join","checkpoint-join","save"},c.Calls);Assert.All(plans,p=>Assert.Equal("C-1",p.RejoinPromotionId));Assert.Contains(c.Current.Promotions,p=>p.Id=="C-1"&&p.Active);Assert.DoesNotContain(c.Current.Promotions,p=>p.Id=="P-1"&&p.Active);
 }
 [Fact] public async Task ExplicitConsentAllowsStopOnlySubsidyExit()
 {
  var c=new Client(false);var r=await new ZiniaoPriceCutExecutor(c).ExecuteAsync(Input() with{AllowExit=true},_=>{},default,allowIncrease:true);
  Assert.Equal(RowStatus.Succeeded,r.Status);Assert.Equal(new[]{"prepare-exit","exit","prepare-join","save"},c.Calls);Assert.DoesNotContain(c.Current.Promotions,p=>p.Id=="P-1"&&p.Active);
 }
 [Fact] public async Task ScheduledTargetIsNotSuccessAndIsNeverResubmitted()
 {
  var c=new Client(false);c.Current=c.Current with{Promotions=[new("C-1","普通活动","已预约","R$ 90",false,true)]};
  var r=await new ZiniaoPriceCutExecutor(c).ExecuteAsync(Input(),_=>{},default,allowIncrease:true);
  Assert.Equal(RowStatus.NeedsReview,r.Status);Assert.Contains("尚未激活",r.Message);Assert.Empty(c.Calls);
 }
 [Fact] public async Task ConflictingLegacyVariantTargetsCannotChangeEveryVariantsBasePrice()
 {
  var c=new Client(false);c.Current=c.Current with{SharedSkus=["SKU-A","SKU-B"],VariantLabel="Cinza 2m"};
  // Whole-link changes are now explicitly authorized; conflicting sibling targets remain prohibited.
  var r=await new ZiniaoPriceCutExecutor(c).ExecuteAsync(Input() with{TargetPrice=99},_=>{},default,allowIncrease:true,allowBaseChange:true,batchInputs:[Input() with{TargetPrice=99},Input() with{Sku="SKU-B",TargetPrice=90}]);
  Assert.Equal(RowStatus.NeedsReview,r.Status);Assert.Contains("共用一个价格",r.Message);Assert.Empty(c.Calls);
 }
 [Fact] public async Task ReplacementJoinRetryUsesBoundOrdinaryId()
 {
  var c=new Client(false);var original=c.Current;c.Current=c.Current with{Promotions=c.Current.Promotions.Where(x=>x.Id=="C-1").ToArray()};
  var p=new PriceCutPlan(new("123","测试","MercadoLibre-巴西"),Input() with{AllowExit=true},original,"P-1",80,100,Rejoin:true,Stage:"join",OfferPrice:95,RejoinPromotionId:"C-1");
  var r=await new ZiniaoIncreaseExecutor(c).ExecuteAsync(p,c.Current,_=>{},default,true);
  Assert.Equal(RowStatus.Succeeded,r.Status);Assert.Equal(new[]{"prepare-join","save"},c.Calls);
 }
 [Fact] public async Task SellerPercentageDiscountExitsThenJoinsBoundOrdinaryOffer()
 {
  var c=new Client(true);c.Current=c.Current with{Promotions=c.Current.Promotions.Select(x=>x.Id=="P-1"?x with{Id="D-PRICE-DISCOUNT",Name="百分比折扣\n16/10月至8/11月"}:x).ToArray()};
  var r=await new ZiniaoPriceCutExecutor(c).ExecuteAsync(Input(),_=>{},default,allowIncrease:true);
  Assert.Equal(RowStatus.Succeeded,r.Status);Assert.Equal(new[]{"prepare-exit","exit","prepare-join","save"},c.Calls);
 }
 [Fact] public async Task UnknownMissingActivityIdRetriesReadWithoutAnyWrite()
 {
  var c=new Client(true);c.Current=c.Current with{Promotions=[new("","未知活动","激活","R$ 80",true,true)]};
  var r=await new ZiniaoPriceCutExecutor(c).ExecuteAsync(Input(),_=>{},default,allowIncrease:true);
  Assert.Equal(RowStatus.NeedsReview,r.Status);Assert.True(r.Retryable);Assert.Contains("平台未返回",r.Message);Assert.Empty(c.Calls);
 }
}
