using Repricing.Storage;
namespace Repricing.Tests;
public class VerificationTests:IDisposable
{
 readonly string dir=Path.Combine(Path.GetTempPath(),"verification-"+Guid.NewGuid());
 [Fact] public void VerificationIsDurableAndNeverAdvancesWithoutExplicitContinue()
 {
  var s=new JobStore(dir);var j=s.Create(StorageTests.Snapshot(2),ExecutionMode.PriceCut);s.TryStart(j.Summary.Id);var row=s.ClaimNext(j.Summary.Id)!;
  s.WaitForVerification(j.Summary.Id,row.Id,"需要验证");var waiting=new JobStore(dir).Get(j.Summary.Id);
  Assert.Equal(JobStatus.AwaitingVerification,waiting.Summary.Status);Assert.Equal(0,waiting.Summary.Finished);Assert.NotNull(waiting.Summary.Verification);Assert.Null(s.ClaimNext(j.Summary.Id));Assert.False(s.TryStart(j.Summary.Id));
  Assert.False(s.ContinueAfterVerification(j.Summary.Id,Guid.NewGuid()));Assert.True(s.ContinueAfterVerification(j.Summary.Id,waiting.Summary.Verification!.RequestId));Assert.False(s.ContinueAfterVerification(j.Summary.Id,waiting.Summary.Verification.RequestId));
  var resumed=s.ClaimNext(j.Summary.Id)!;Assert.Equal(row.Id,resumed.Id);Assert.Equal(row.Attempts,resumed.Attempts);Assert.Equal(JobStatus.Running,s.Get(j.Summary.Id).Summary.Status);
 }
 [Fact] public void ASecondChallengeDoesNotConsumeRetryBudgetAndBlocksOtherBatches()
 {
  var s=new JobStore(dir);var j=s.Create(StorageTests.Snapshot(1),ExecutionMode.PriceCut);s.TryStart(j.Summary.Id);
  for(int i=0;i<5;i++){var r=s.ClaimNext(j.Summary.Id)!;s.WaitForVerification(j.Summary.Id,r.Id,"需要验证");Assert.Equal(0,s.Get(j.Summary.Id).Summary.Finished);Assert.Equal(j.Summary.Id,s.ActiveId());var other=s.Create(StorageTests.Snapshot(1),ExecutionMode.PriceCut);Assert.False(s.TryStart(other.Summary.Id));Assert.True(s.ContinueAfterVerification(j.Summary.Id,s.Get(j.Summary.Id).Summary.Verification!.RequestId));}
  Assert.Equal(1,s.ClaimNext(j.Summary.Id)!.Attempts);
 }
 [Fact] public void ResumeAfterSubmissionKeepsThePlanAndStartsAFreshReconciliationAttempt()
 {
  var s=new JobStore(dir);var j=s.Create(StorageTests.Snapshot(1),ExecutionMode.PriceCut);s.TryStart(j.Summary.Id);var row=s.ClaimNext(j.Summary.Id)!;
  var plan=new Execution.PriceCutPlan(new("1","A","MercadoLibre-巴西"),row.Source.Input!,new("123",row.Source.Input!.Sku,"商品","R$ 100",[]),"C-1",80,100);s.SavePriceCutPlan(j.Summary.Id,row.Id,plan);s.WaitForVerification(j.Summary.Id,row.Id,"保存后需验证");
  s.ContinueAfterVerification(j.Summary.Id,s.Get(j.Summary.Id).Summary.Verification!.RequestId);var resumed=s.ClaimNext(j.Summary.Id)!;Assert.Equal(2,resumed.Attempts);Assert.Equal(System.Text.Json.JsonSerializer.Serialize(plan),System.Text.Json.JsonSerializer.Serialize(resumed.PriceCut));
 }
 [Fact] public void WaitingCanBePostponedButNormalStartCannotBypassVerification()
 {
  var s=new JobStore(dir);var j=s.Create(StorageTests.Snapshot(1),ExecutionMode.PriceCut);s.TryStart(j.Summary.Id);var row=s.ClaimNext(j.Summary.Id)!;s.WaitForVerification(j.Summary.Id,row.Id,"需要验证");Assert.True(s.RequestPause(j.Summary.Id));Assert.Null(s.ActiveId());Assert.False(s.TryStart(j.Summary.Id));Assert.True(s.ContinueAfterVerification(j.Summary.Id,s.Get(j.Summary.Id).Summary.Verification!.RequestId));
 }
 public void Dispose(){if(Directory.Exists(dir))Directory.Delete(dir,true);}
}
