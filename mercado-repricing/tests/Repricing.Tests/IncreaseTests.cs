using Repricing.Execution;
namespace Repricing.Tests;
public class IncreaseTests
{
 static PriceInput Input(decimal target=90)=>new("123","MLB","BRL","SKU-A",null,null,null,target,null,false,false,null,"提价");
 static ZiniaoProduct Product(decimal price=80,bool active=true)=>new("456","SKU-A","商品","R$ 100,00",[new("C-1","活动","",$"R$ {price:0.00}".Replace('.',','),active,active,false,!active)]);
 sealed class Client:IZiniaoPriceCutClient
 {
  public List<string> Calls=[];public List<string?> ReadIds=[];public ZiniaoProduct Current=Product();public bool ExitThrows,SaveThrows,MissingAfterExit;public int Reads;
  public Task<ZiniaoStore[]> StoresAsync(CancellationToken ct)=>Task.FromResult(new[]{new ZiniaoStore("123","测试","MercadoLibre-巴西")});
  public Task<ZiniaoProduct[]> ReadAsync(ZiniaoStore s,PriceInput i,CancellationToken ct){Calls.Add("read");ReadIds.Add(i.ItemId);Reads++;return Task.FromResult(MissingAfterExit&&Reads>1?Array.Empty<ZiniaoProduct>():new[]{Current});}
  public Task PrepareExitAsync(PriceCutPlan p,CancellationToken ct){Calls.Add("prepare-exit");return Task.CompletedTask;}
  public Task ConfirmExitAsync(PriceCutPlan p,CancellationToken ct){Calls.Add("exit");Current=Product(95,false);if(ExitThrows)throw new AppException("退出断线");return Task.CompletedTask;}
  public Task PrepareAsync(PriceCutPlan p,CancellationToken ct){Calls.Add("prepare");return Task.CompletedTask;}
  public Task SubmitAsync(PriceCutPlan p,CancellationToken ct){Calls.Add("save");Current=Product(p.Input.TargetPrice);if(SaveThrows)throw new AppException("保存断线");return Task.CompletedTask;}
  public Task<ZiniaoProduct> VerifyAsync(PriceCutPlan p,CancellationToken ct){Calls.Add("verify");return Task.FromResult(Current);}
  public Task CloseAsync()=>Task.CompletedTask;
 }
 static PriceCutPlan Previous(string stage="exit",decimal? offer=null)=>new(new("123","测试","MercadoLibre-巴西"),Input(),Product(),"C-1",80,100,Rejoin:true,Stage:stage,OfferPrice:offer);
 static Task<ExecutionResult> Run(Client c,PriceInput? input=null,PriceCutPlan? previous=null,Action<PriceCutPlan>? save=null)=>new ZiniaoPriceCutExecutor(c).ExecuteAsync(input??Input(),p=>{c.Calls.Add("checkpoint-"+p.Stage);save?.Invoke(p);},default,previous,allowIncrease:true);
 [Fact] public async Task TemporarilyMissingAfterExitIsRetryableAndNeverJoinsBlindly(){var c=new Client{MissingAfterExit=true};var r=await Run(c);Assert.True(r.Retryable);Assert.Contains("暂未返回",r.Message);Assert.Single(c.Calls,x=>x=="exit");Assert.DoesNotContain("save",c.Calls);}
 [Fact] public async Task SubsidizedCampaignNamesItsUnsupportedRuleWithoutExiting(){var p=Product() with{Promotions=[new("P-1","平台补贴","激活","R$ 80",true,true)]};var c=new Client{Current=p};var r=await Run(c);Assert.Equal(RowStatus.NeedsReview,r.Status);Assert.Contains("平台补贴活动",r.Message);Assert.DoesNotContain("exit",c.Calls);Assert.DoesNotContain("save",c.Calls);}
 [Fact] public async Task MixedTableAlsoCutsRowsLabeledIncrease(){var c=new Client();var r=await Run(c,Input(75));Assert.Equal(RowStatus.Succeeded,r.Status);Assert.DoesNotContain("exit",c.Calls);}
 [Fact] public async Task ExitDisconnectKeepsCheckpointForReadBeforeRecovery(){var c=new Client{ExitThrows=true};PriceCutPlan? saved=null;var first=await Run(c,save:p=>saved=p);Assert.True(first.Retryable);Assert.Equal("exit",saved!.Stage);c.Calls.Clear();c.ExitThrows=false;Assert.Equal(RowStatus.Succeeded,(await Run(c,previous:saved)).Status);Assert.DoesNotContain("exit",c.Calls);}
 [Fact] public async Task SaveDisconnectReconcilesWithoutExitingOrSavingAgain(){var c=new Client{SaveThrows=true};PriceCutPlan? saved=null;Assert.True((await Run(c,save:p=>saved=p)).Retryable);Assert.Equal("join",saved!.Stage);c.Calls.Clear();Assert.Equal(RowStatus.Succeeded,(await Run(c,previous:saved)).Status);Assert.DoesNotContain("exit",c.Calls);Assert.DoesNotContain("save",c.Calls);}
 [Fact] public async Task IncreasePersistsBothMutationsAndVerifies(){var c=new Client();var r=await Run(c);Assert.Equal(RowStatus.Succeeded,r.Status);Assert.Equal(new[]{"read","prepare-exit","checkpoint-exit","exit","read","prepare","checkpoint-join","save","verify"},c.Calls);}
 [Fact] public async Task MixedTableUsesActualDirectionInsteadOfOldLabel(){var c=new Client();Assert.Equal(RowStatus.Succeeded,(await Run(c,Input() with{Action="降价"})).Status);Assert.Contains("exit",c.Calls);}
 [Fact] public async Task MissingActionCanBeAutomaticallyRouted(){var c=new Client();Assert.Equal(RowStatus.Succeeded,(await Run(c,Input() with{Action=null})).Status);}
 [Fact] public async Task ThirdCaseSkipsBeforeExiting(){var c=new Client();Assert.Equal(RowStatus.Skipped,(await Run(c,Input(96))).Status);Assert.DoesNotContain("exit",c.Calls);}
 [Fact] public async Task CheckpointFailureNeverConfirmsExit(){var c=new Client();await Run(c,save:_=>throw new IOException());Assert.DoesNotContain("exit",c.Calls);}
 [Fact] public async Task ExitResultUnknownContinuesByReadingNotExitingAgain(){var c=new Client{Current=Product(95,false)};var r=await Run(c,previous:Previous());Assert.Equal(RowStatus.Succeeded,r.Status);Assert.DoesNotContain("exit",c.Calls);Assert.Contains("save",c.Calls);}
 [Fact] public async Task ExitStillActiveNeverExitsAgain(){var c=new Client();var r=await Run(c,previous:Previous());Assert.Equal(RowStatus.NeedsReview,r.Status);Assert.True(r.Retryable);Assert.DoesNotContain("exit",c.Calls);Assert.DoesNotContain("save",c.Calls);}
 [Fact] public async Task JoinedTargetNeverResubmits(){var c=new Client{Current=Product(90)};Assert.Equal(RowStatus.Succeeded,(await Run(c,previous:Previous("join",95))).Status);Assert.DoesNotContain("save",c.Calls);Assert.DoesNotContain("exit",c.Calls);Assert.Null(Assert.Single(c.ReadIds));}
 [Fact] public async Task OtherActivePriceNeverReexits(){var c=new Client{Current=Product(85)};Assert.Equal(RowStatus.NeedsReview,(await Run(c,previous:Previous("join",95))).Status);Assert.DoesNotContain("save",c.Calls);Assert.DoesNotContain("exit",c.Calls);}
 [Fact] public async Task PendingJoinRequiresTwoReadsAndSameOffer(){var c=new Client{Current=Product(95,false)};Assert.Equal(RowStatus.Succeeded,(await Run(c,previous:Previous("join",95))).Status);Assert.True(c.Reads>=2);Assert.DoesNotContain("exit",c.Calls);}
 [Fact] public async Task ChangedOfferNeverResubmits(){var c=new Client{Current=Product(94,false)};Assert.Equal(RowStatus.NeedsReview,(await Run(c,previous:Previous("join",95))).Status);Assert.DoesNotContain("save",c.Calls);}
 [Fact] public async Task ChangedBaseNeverSubmits(){var c=new Client{Current=Product() with{BasePriceText="R$ 110"}};Assert.Equal(RowStatus.NeedsReview,(await Run(c,previous:Previous())).Status);Assert.DoesNotContain("exit",c.Calls);}
 [Fact] public async Task JoinCheckpointFailureNeverSaves(){var c=new Client();await Run(c,save:p=>{if(p.Stage=="join")throw new IOException();});Assert.Contains("exit",c.Calls);Assert.DoesNotContain("save",c.Calls);}
}
