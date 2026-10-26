using Repricing.Execution;
using Repricing.Storage;
namespace Repricing.Tests;
public class ScheduledSuccessorTests
{
 sealed class Client(decimal successorPrice=80):IZiniaoPriceCutClient
 {
  public List<string> Calls=[];
  public ZiniaoProduct Current=new("456","SKU-A","商品","R$ 100",[new("C-1","普通活动","激活","R$ 80",true,true),new("D-PRICE-DISCOUNT","百分比折扣\n16至31/10月","已预约","R$ 80",false,true)]);
  public Task<ZiniaoStore[]> StoresAsync(CancellationToken ct)=>Task.FromResult(new[]{new ZiniaoStore("123","测试","MercadoLibre-巴西")});
  public Task<ZiniaoProduct[]> ReadAsync(ZiniaoStore s,PriceInput i,CancellationToken ct)=>Task.FromResult(new[]{Current});
  public Task PrepareExitAsync(PriceCutPlan p,CancellationToken ct){Calls.Add("prepare-"+p.PromotionId);return Task.CompletedTask;}
  public Task ConfirmExitAsync(PriceCutPlan p,CancellationToken ct){Calls.Add("exit-"+p.PromotionId);Current=Current with{Promotions=Current.Promotions.Select(x=>x.Id=="C-1"?x with{Active=false,Editable=false,Joinable=true,PriceText="R$ 95"}:x with{Active=p.PromotionId=="C-1",Status=p.PromotionId=="C-1"?"激活":"",PriceText="R$ "+successorPrice}).ToArray()};return Task.CompletedTask;}
  public Task PrepareAsync(PriceCutPlan p,CancellationToken ct){Assert.Equal("C-1",p.PromotionId);Calls.Add("prepare-join");return Task.CompletedTask;}
  public Task SubmitAsync(PriceCutPlan p,CancellationToken ct){Calls.Add("save");Current=Current with{Promotions=Current.Promotions.Select(x=>x.Id=="C-1"?x with{Active=true,Editable=true,Joinable=false,PriceText="R$ 90"}:x).ToArray()};return Task.CompletedTask;}
  public Task<ZiniaoProduct> VerifyAsync(PriceCutPlan p,CancellationToken ct)=>Task.FromResult(Current);
 }
 static PriceInput Input(bool consent=true)=>new("123","MLB","BRL","SKU-A",null,null,null,90,null,consent,false,null,"提价");
 [Fact] public async Task KnownScheduledDiscountCanExitOnceWithBothCheckpointsPersisted()
 {
  var dir=Path.Combine(Path.GetTempPath(),"successor-"+Guid.NewGuid());try{
   var c=new Client();var s=new JobStore(dir);var v=StorageTests.Snapshot(1);v.Rows[0]=v.Rows[0] with{Input=Input(false)};var j=s.Create(v,ExecutionMode.PriceCut,true,true,true);s.TryStart(j.Summary.Id);var row=s.ClaimNext(j.Summary.Id)!;var plans=new List<PriceCutPlan>();
   var result=await new ZiniaoPriceCutExecutor(c).ExecuteAsync(Input(),p=>{s.SavePriceCutPlan(j.Summary.Id,row.Id,p);plans.Add(p);},default,allowIncrease:true);
   Assert.Equal(RowStatus.Succeeded,result.Status);Assert.Equal(new[]{"C-1","D-PRICE-DISCOUNT","D-PRICE-DISCOUNT"},plans.Select(p=>p.PromotionId));Assert.Equal("C-1",s.Get(j.Summary.Id).Rows[0].PriceCut!.PreviousExit!.PromotionId);Assert.Equal(2,c.Calls.Count(x=>x.StartsWith("exit-")));Assert.Single(c.Calls,x=>x=="save");
  }finally{Directory.Delete(dir,true);}
 }
 [Theory] [InlineData(false,80)] [InlineData(true,81)] public async Task UnconfirmedOrChangedSuccessorDoesNotExitAgain(bool consent,int price)
 {
  var c=new Client(price);var result=await new ZiniaoPriceCutExecutor(c).ExecuteAsync(Input(consent),_=>{},default,allowIncrease:true);
  Assert.Equal(RowStatus.NeedsReview,result.Status);Assert.Single(c.Calls,x=>x.StartsWith("exit-"));Assert.DoesNotContain("save",c.Calls);
 }
}
