using Repricing.Execution;
using Repricing.Storage;
namespace Repricing.Tests;
public class AutomaticCampaignTests
{
 public static PriceInput Input(string? id=null)=>new("1","MLB","BRL","SKU","123",null,id,76.9m,null,false,false,null,"降价");
 public static ZiniaoProduct Product()=>new("123","SKU","商品","R$ 129,90",[new("C-A","0930-1030","激活","R$ 75,90",true,true),new("C-B","1029-1128","已预约","R$ 75,90",false,true)]);
 sealed class Client:IZiniaoPriceCutClient
 {
  public ZiniaoProduct Current=Product();public List<string> Calls=[];public bool LoseSave;
  public Task<ZiniaoStore[]> StoresAsync(CancellationToken ct)=>Task.FromResult(new[]{new ZiniaoStore("1","店铺","MercadoLibre-巴西")});
  public Task<ZiniaoProduct[]> ReadAsync(ZiniaoStore s,PriceInput i,CancellationToken ct){Calls.Add("read");return Task.FromResult(new[]{Current});}
  public Task PrepareExitAsync(PriceCutPlan p,CancellationToken ct){Calls.Add("prepare-exit:"+p.PromotionId);return Task.CompletedTask;}
  public Task ConfirmExitAsync(PriceCutPlan p,CancellationToken ct){Calls.Add("exit:"+p.PromotionId);Current=Current with{Promotions=Current.Promotions.Select(x=>x.Id==p.PromotionId?x with{Active=false,Status="",Editable=false,Joinable=true}:x).ToArray()};return Task.CompletedTask;}
  public Task PrepareAsync(PriceCutPlan p,CancellationToken ct){Calls.Add("prepare:"+p.PromotionId);return Task.CompletedTask;}
  public Task SubmitAsync(PriceCutPlan p,CancellationToken ct){Calls.Add("save:"+p.PromotionId);Current=Current with{Promotions=Current.Promotions.Select(x=>x.Id==p.PromotionId?x with{PriceText="R$ 76,90",Active=p.PromotionId=="C-A",Status=p.PromotionId=="C-A"?"激活":"已预约",Editable=true,Joinable=false}:x).ToArray()};if(LoseSave)throw new IOException();return Task.CompletedTask;}
  public Task<ZiniaoProduct> VerifyAsync(PriceCutPlan p,CancellationToken ct)=>Task.FromResult(Current);
 }
 [Fact] public async Task MislabeledDecreaseSplitsBothIncreasingCampaigns(){var r=await new ZiniaoPriceCutExecutor(new Client()).ExecuteAsync(Input(null),_=>{},default,allowIncrease:true);Assert.Equal(new[]{"C-A","C-B"},r.SplitInputs!.Select(x=>x.PromotionId));}
 [Fact]
 public async Task ActiveCampaignIncreaseExitsRejoinsAndPreservesSibling()
 {
  var c=new Client();var plans=new List<PriceCutPlan>();var r=await new ZiniaoPriceCutExecutor(c).ExecuteAsync(Input("C-A"),plans.Add,default,allowIncrease:true);
  Assert.Equal(RowStatus.Succeeded,r.Status);Assert.Contains("exit:C-A",c.Calls);Assert.Contains("save:C-A",c.Calls);Assert.Equal(new[]{"campaign-exit","campaign-join"},plans.Select(p=>p.Stage));Assert.Equal("R$ 75,90",c.Current.Promotions.Single(x=>x.Id!="C-A").PriceText);
 }
 [Fact] public async Task ScheduledCampaignIncreaseEditsOnlyTheBoundReservation()
 {
  var c=new Client();var plans=new List<PriceCutPlan>();var r=await new ZiniaoPriceCutExecutor(c).ExecuteAsync(Input("C-B"),plans.Add,default,allowIncrease:true);
  Assert.Equal(RowStatus.Succeeded,r.Status);Assert.Contains("prepare:C-B",c.Calls);Assert.Contains("save:C-B",c.Calls);Assert.DoesNotContain("exit:C-B",c.Calls);Assert.Equal(new[]{"scheduled-edit"},plans.Select(p=>p.Stage));
  Assert.Equal("R$ 75,90",c.Current.Promotions.Single(x=>x.Id=="C-A").PriceText);Assert.Equal("R$ 76,90",c.Current.Promotions.Single(x=>x.Id=="C-B").PriceText);
 }
 [Fact] public async Task UnknownCampaignSaveRereadsWithoutReplayingExitOrSave()
 {
  var c=new Client{LoseSave=true};PriceCutPlan? last=null;var executor=new ZiniaoPriceCutExecutor(c);await executor.ExecuteAsync(Input("C-B"),p=>last=p,default,allowIncrease:true);Assert.NotNull(last);c.Calls.Clear();var r=await executor.ExecuteAsync(Input("C-B"),_=>{},default,last,allowIncrease:true);Assert.Equal(RowStatus.Succeeded,r.Status);Assert.Equal(2,c.Calls.Count(x=>x=="read"));Assert.DoesNotContain(c.Calls,x=>x.StartsWith("save:",StringComparison.Ordinal)||x.StartsWith("exit:",StringComparison.Ordinal));
 }
 [Fact] public void SplitChildrenStayAheadOfNextSku()
 {
  var dir=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());try{var s=new JobStore(dir);var snap=StorageTests.Snapshot(2);snap.Rows[0]=snap.Rows[0] with{Input=Input(null)};var j=s.Create(snap,ExecutionMode.PriceCut,true);s.TryStart(j.Summary.Id);var row=s.ClaimNext(j.Summary.Id)!;s.SplitPriceCutRow(j.Summary.Id,row.Id,[Input("C-A"),Input("C-B")]);row=s.ClaimNext(j.Summary.Id)!;s.UpdateRow(j.Summary.Id,row.Id,new(RowStatus.Succeeded,"","",""));Assert.Equal("C-B",s.ClaimNext(j.Summary.Id)!.Source.Input!.PromotionId);}finally{Directory.Delete(dir,true);}
 }
 [Fact] public void CampaignCheckpointsPersistBothStagesAndRejectChangedTarget()
 {
  var dir=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());try{var s=new JobStore(dir);var snap=StorageTests.Snapshot(1);snap.Rows[0]=snap.Rows[0] with{Input=Input("C-B")};var j=s.Create(snap,ExecutionMode.PriceCut,true);s.TryStart(j.Summary.Id);var row=s.ClaimNext(j.Summary.Id)!;var p=new PriceCutPlan(new("1","店铺","MercadoLibre-巴西"),Input("C-B"),Product(),"C-B",75.9m,129.9m,Rejoin:true,Stage:"campaign-exit");s.SavePriceCutPlan(j.Summary.Id,row.Id,p);Assert.Throws<AppException>(()=>s.SavePriceCutPlan(j.Summary.Id,row.Id,p with{Stage="campaign-join",OfferPrice=80,Input=p.Input with{TargetPrice=77}}));s.SavePriceCutPlan(j.Summary.Id,row.Id,p with{Stage="campaign-join",OfferPrice=80});Assert.Equal("campaign-join",s.Get(j.Summary.Id).Rows[0].PriceCut!.Stage);}finally{Directory.Delete(dir,true);}
 }
 [Fact] public void ThreeConsecutiveConnectionFailuresPauseWithoutInventingVerification()
 {
  var dir=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());try{var s=new JobStore(dir);var j=s.Create(StorageTests.Snapshot(1),ExecutionMode.PriceCut);s.TryStart(j.Summary.Id);var r=s.ClaimNext(j.Summary.Id)!;for(var i=0;i<3;i++)s.RecordVerification(j.Summary.Id,r.Id,new("connection-failed","活动 open","连接中断"));Assert.Equal(JobStatus.Pausing,s.Get(j.Summary.Id).Summary.Status);Assert.Null(s.Get(j.Summary.Id).Summary.Verification);}finally{Directory.Delete(dir,true);}
 }
}
