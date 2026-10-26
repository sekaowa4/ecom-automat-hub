using Repricing.Execution;
namespace Repricing.Tests;

public class BasePriceTests
{
 static PriceInput Input(decimal target=99)=>new("123","MLB","BRL","SKU-A",null,null,null,target,null,false,false,null);
 static ZiniaoProduct Product(decimal original=100,bool active=false)=>new("456","SKU-A","商品",$"R$ {original:0.00}".Replace('.',','),active?[new("P-1","补贴活动","激活","R$ 80,00",true,true)]:[]);
 sealed class Client:IZiniaoPriceCutClient
 {
  public List<string> Calls=[];public ZiniaoProduct Current=Product();public bool ExitThrows,SaveThrows,ExitStaysActive,UnderReviewAfterSave,PreservePromotionsOnBaseSave;public int Reads;
  public Task<ZiniaoStore[]> StoresAsync(CancellationToken ct)=>Task.FromResult(new[]{new ZiniaoStore("123","测试","MercadoLibre-巴西")});
  public Task<ZiniaoProduct[]> ReadAsync(ZiniaoStore s,PriceInput i,CancellationToken ct){Calls.Add("read");Reads++;return Task.FromResult(new[]{Current});}
  public Task<ZiniaoProduct[]> ReadManagerAsync(ZiniaoStore s,PriceInput i,CancellationToken ct){Calls.Add("read-manager");return Task.FromResult(new[]{Current with{Promotions=[]}});}
  public Task PrepareExitAsync(PriceCutPlan p,CancellationToken ct){Calls.Add("prepare-exit");return Task.CompletedTask;}
  public Task ConfirmExitAsync(PriceCutPlan p,CancellationToken ct){Calls.Add("exit");if(!ExitStaysActive)Current=Current with{Promotions=[]};if(ExitThrows)throw new AppException("退出后断线");return Task.CompletedTask;}
  public Task PrepareBaseAsync(PriceCutPlan p,CancellationToken ct){Calls.Add("prepare-base");return Task.CompletedTask;}
  public Task SubmitBaseAsync(PriceCutPlan p,CancellationToken ct){Calls.Add("save-base");var promotions=Current.Promotions;Current=Product(p.Input.TargetPrice);if(PreservePromotionsOnBaseSave)Current=Current with{Promotions=promotions,ManagedPriceText="R$ 80,00"};if(UnderReviewAfterSave)Current=Current with{ListingStatus="需审核未激活"};if(SaveThrows)throw new AppException("保存后断线");return Task.CompletedTask;}
  public Task PrepareAsync(PriceCutPlan p,CancellationToken ct)=>throw new Exception("禁止报名或修改活动价");
  public Task SubmitAsync(PriceCutPlan p,CancellationToken ct)=>throw new Exception("禁止报名");
  public Task<ZiniaoProduct> VerifyAsync(PriceCutPlan p,CancellationToken ct){Calls.Add("verify");return Task.FromResult(Current);}
  public Task CloseAsync()=>Task.CompletedTask;
 }
 static Task<ExecutionResult> Run(Client c,PriceInput? input=null,PriceCutPlan? previous=null,Action<PriceCutPlan>? checkpoint=null,bool consent=true)=>new ZiniaoPriceCutExecutor(c).ExecuteAsync(input??Input(),p=>{c.Calls.Add("checkpoint-"+p.Stage);checkpoint?.Invoke(p);},default,previous,allowIncrease:true,allowBaseChange:consent);
 [Fact] public async Task BaseAtTargetButUnderReviewKeepsReview(){var c=new Client{UnderReviewAfterSave=true};var r=await Run(c);Assert.Equal(RowStatus.NeedsReview,r.Status);Assert.Contains("审核未激活",r.Message);Assert.False(r.Retryable);}
 [Fact] public async Task NoCampaignNeededToSaveTargetAsBase(){var c=new Client();var r=await Run(c);Assert.Equal(RowStatus.Succeeded,r.Status);Assert.Equal(99,r.Observation!.CurrentPrice);Assert.Equal(new[]{"read","prepare-base","checkpoint-base-save","save-base","verify"},c.Calls);}
 [Fact] public async Task SharedParentBasePriceCanChangeWhenRequestedVariantIsTheOnlyOneInStock()
 {
  var variants=new[]{new LegacyVariant("SKU-A","绿色","库存 12 u."),new LegacyVariant("SKU-B","蓝色","0 u."),new LegacyVariant("SKU-C","黑色","已售罄")};
  var c=new Client{Current=Product() with{SharedSkus=["SKU-A","SKU-B","SKU-C"],SharedVariants=variants}};
  var r=await Run(c,Input(75) with{Note="仅改原价（用户确认其他规格售罄）"});
  Assert.Equal(RowStatus.Succeeded,r.Status);Assert.Contains("read-manager",c.Calls);Assert.Single(c.Calls,x=>x=="save-base");Assert.Contains("SKU-A",r.Message);Assert.Contains("其余 2 个规格售罄",r.Message);
 }
 [Fact] public async Task ExplicitManagerOnlyBaseChangeDoesNotExitOrRejoinPromotion()
 {
  var variants=new[]{new LegacyVariant("SKU-A","绿色","库存 12 u."),new LegacyVariant("SKU-B","蓝色","0 u."),new LegacyVariant("SKU-C","黑色","已售罄")};
  var c=new Client{Current=Product(active:true) with{SharedSkus=["SKU-A","SKU-B","SKU-C"],SharedVariants=variants},PreservePromotionsOnBaseSave=true};
  var r=await Run(c,Input(75) with{Note="仅改原价"});
  Assert.Equal(RowStatus.Succeeded,r.Status);Assert.Contains("仅改原价",r.Message);Assert.Contains("R$ 80.00",r.Message);Assert.Equal(80,r.Observation!.CurrentPrice);Assert.True(r.Observation.Promotions.Single().Active);Assert.DoesNotContain("exit",c.Calls);Assert.DoesNotContain("prepare-exit",c.Calls);Assert.DoesNotContain("prepare",c.Calls);
 }
 [Fact] public async Task SharedParentBasePriceRemainsBlockedWhenSiblingHasKnownStock()
 {
  var variants=new[]{new LegacyVariant("SKU-A","绿色","库存 12 u."),new LegacyVariant("SKU-B","蓝色","库存 3 u."),new LegacyVariant("SKU-C","黑色","0 u.")};
  var c=new Client{Current=Product() with{SharedSkus=["SKU-A","SKU-B","SKU-C"],SharedVariants=variants}};
  var r=await Run(c,Input(75) with{Note="仅改原价（用户确认其他规格售罄）"});
  Assert.Equal(RowStatus.NeedsReview,r.Status);Assert.DoesNotContain("save-base",c.Calls);
 }
 [Theory][InlineData(null)][InlineData("普通备注")]
 public void DirectBaseFlagWithoutSourceConsentCannotPersist(string? note)
 {
  var dir=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());
  try{var store=new Repricing.Storage.JobStore(dir);var input=Input(75) with{Note=note};var snap=StorageTests.Snapshot(1);snap.Rows[0]=snap.Rows[0] with{Input=input};var job=store.Create(snap,ExecutionMode.PriceCut,true,true);store.TryStart(job.Summary.Id);var row=store.ClaimNext(job.Summary.Id)!;var plan=new PriceCutPlan(new("123","测试","MercadoLibre-巴西"),input,Product(),"",100,100,Stage:"base-save",ChangeBase:true,DirectBaseOnly:true);Assert.Throws<AppException>(()=>store.SavePriceCutPlan(job.Summary.Id,row.Id,plan));}
  finally{Directory.Delete(dir,true);}
 }
 [Fact] public async Task WholeListingConsentPersistsRealCheckpointBeforeSaving()
 {
  var dir=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());
  try
  {
   var store=new Repricing.Storage.JobStore(dir);var input=Input(75) with{Note="仅改原价（用户确认整链接统一调价）"};
   var snapshot=StorageTests.Snapshot(1);snapshot.Rows[0]=snapshot.Rows[0] with{Input=input};
   var job=store.Create(snapshot,ExecutionMode.PriceCut,true,true);store.TryStart(job.Summary.Id);var row=store.ClaimNext(job.Summary.Id)!;
   var c=new Client{Current=Product() with{SharedSkus=["SKU-A","SKU-B"],SharedVariants=[new("SKU-A","绿色","12 u."),new("SKU-B","蓝色","3 u.")]}};
   var result=await Run(c,input,checkpoint:p=>store.SavePriceCutPlan(job.Summary.Id,row.Id,p));
   Assert.Equal(RowStatus.Succeeded,result.Status);Assert.Single(c.Calls,x=>x=="save-base");
   var persisted=store.Get(job.Summary.Id).Rows[0].PriceCut!;Assert.True(persisted.DirectBaseOnly);Assert.Equal("base-save",persisted.Stage);
   Assert.Throws<AppException>(()=>store.SavePriceCutPlan(job.Summary.Id,row.Id,persisted));
  }
  finally{Directory.Delete(dir,true);}
 }
 [Fact] public async Task ExplicitWholeListingConsentAllowsInStockSiblings()
 {
  var c=new Client{Current=Product() with{SharedSkus=["SKU-A","SKU-B"],SharedVariants=[new("SKU-A","绿色","库存 12 u."),new("SKU-B","蓝色","库存 3 u.")]}};
  var r=await Run(c,Input(75) with{Note="仅改原价（用户确认整链接统一调价）"});
  Assert.Equal(RowStatus.Succeeded,r.Status);Assert.Single(c.Calls,x=>x=="save-base");Assert.Contains("整链接所有规格统一调价",r.Message);Assert.DoesNotContain("售罄",r.Message);
 }
 [Fact] public async Task ExplicitUserStockConfirmationAllowsUnknownSiblingStock()
 {
  var variants=new[]{new LegacyVariant("SKU-A","绿色","库存 12 u."),new LegacyVariant("SKU-B","蓝色",null),new LegacyVariant("SKU-C","黑色","0 u.")};
  var c=new Client{Current=Product() with{SharedSkus=["SKU-A","SKU-B","SKU-C"],SharedVariants=variants}};
  var r=await Run(c,Input(75) with{Note="仅改原价（用户确认其他规格售罄）"});
  Assert.Equal(RowStatus.Succeeded,r.Status);Assert.Single(c.Calls,x=>x=="save-base");
 }
 [Fact] public async Task SubsidyExitThenBaseSaveNeverJoins(){var c=new Client{Current=Product(active:true)};Assert.Equal(RowStatus.Succeeded,(await Run(c)).Status);Assert.Equal(new[]{"read","prepare-exit","checkpoint-base-exit","exit","read","prepare-base","checkpoint-base-save","save-base","verify"},c.Calls);}
 [Fact] public async Task AlreadyBaseTargetStillExitsActiveDiscount(){var c=new Client{Current=Product(99,true)};Assert.Equal(RowStatus.Succeeded,(await Run(c)).Status);Assert.Contains("exit",c.Calls);Assert.DoesNotContain("save-base",c.Calls);}
 [Fact] public async Task ExcessiveDiscountNeedsOrdinaryOfferBeforeChangingBase(){var c=new Client();Assert.Equal(RowStatus.NeedsReview,(await Run(c,Input(19))).Status);Assert.DoesNotContain("save-base",c.Calls);}
 [Fact] public async Task OldMixedTaskCannotChangeBase(){var c=new Client{Current=Product(active:true)};Assert.Equal(RowStatus.Skipped,(await Run(c,consent:false)).Status);Assert.DoesNotContain("exit",c.Calls);Assert.DoesNotContain("save-base",c.Calls);}
 [Fact] public async Task AlreadyTargetWithoutCampaignSkips(){var c=new Client{Current=Product(99)};Assert.Equal(RowStatus.Skipped,(await Run(c)).Status);Assert.DoesNotContain("save-base",c.Calls);}
 [Fact] public async Task AlreadyTargetButLessThanFivePercentStillCorrectsBase(){var c=new Client{Current=Product(active:true)};c.Current=c.Current with{Promotions=[c.Current.Promotions[0] with{PriceText="R$ 99,00"}]};Assert.Equal(RowStatus.Succeeded,(await Run(c)).Status);Assert.Contains("exit",c.Calls);Assert.Contains("save-base",c.Calls);}
 [Fact] public async Task ExitUnknownRecoversWithoutSecondExit(){var c=new Client{Current=Product(active:true),ExitThrows=true};PriceCutPlan? saved=null;Assert.True((await Run(c,checkpoint:p=>saved=p)).Retryable);Assert.Equal("base-exit",saved!.Stage);c.Calls.Clear();c.ExitThrows=false;Assert.Equal(RowStatus.Succeeded,(await Run(c,previous:saved)).Status);Assert.DoesNotContain("exit",c.Calls);}
 [Fact] public async Task StillActiveUnknownExitOnlyReads(){var c=new Client{Current=Product(active:true),ExitThrows=true,ExitStaysActive=true};PriceCutPlan? saved=null;await Run(c,checkpoint:p=>saved=p);c.Calls.Clear();Assert.True((await Run(c,previous:saved)).Retryable);Assert.DoesNotContain("exit",c.Calls);Assert.DoesNotContain("save-base",c.Calls);}
 [Fact] public async Task SavedButDisconnectedOnlyReconciles(){var c=new Client{SaveThrows=true};PriceCutPlan? saved=null;Assert.True((await Run(c,checkpoint:p=>saved=p)).Retryable);c.Calls.Clear();Assert.Equal(RowStatus.Succeeded,(await Run(c,previous:saved)).Status);Assert.DoesNotContain("save-base",c.Calls);}
 [Fact] public async Task ChangedBaseNeverOverwrittenOnRetry(){var c=new Client{SaveThrows=true};PriceCutPlan? saved=null;await Run(c,checkpoint:p=>saved=p);c.Current=Product(110);c.Calls.Clear();Assert.Equal(RowStatus.NeedsReview,(await Run(c,previous:saved)).Status);Assert.DoesNotContain("save-base",c.Calls);}
 [Fact] public async Task DurableRecordFailureNeverSaves(){var c=new Client();await Run(c,checkpoint:_=>throw new IOException());Assert.DoesNotContain("save-base",c.Calls);}
 [Fact] public async Task DurableRecordFailureNeverExits(){var c=new Client{Current=Product(active:true)};await Run(c,checkpoint:_=>throw new IOException());Assert.DoesNotContain("exit",c.Calls);}
 [Fact] public async Task MultipleActiveCampaignsNeverExitOrSave(){var c=new Client{Current=Product(active:true)};c.Current=c.Current with{Promotions=[..c.Current.Promotions,c.Current.Promotions[0] with{Id="P-2"}]};Assert.Equal(RowStatus.NeedsReview,(await Run(c)).Status);Assert.DoesNotContain("exit",c.Calls);Assert.DoesNotContain("save-base",c.Calls);}
}
