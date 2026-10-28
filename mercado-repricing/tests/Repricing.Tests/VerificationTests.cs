using Repricing.Storage;
namespace Repricing.Tests;
public class VerificationTests:IDisposable
{
 readonly string dir=Path.Combine(Path.GetTempPath(),"verification-"+Guid.NewGuid());
 [Fact] public void AutomaticChallengeImmediatelyPublishesNoticeWithoutAllowingContinue()
 {
  var s=new JobStore(dir);var j=s.Create(StorageTests.Snapshot(1),ExecutionMode.PriceCut);s.TryStart(j.Summary.Id);var row=s.ClaimNext(j.Summary.Id)!;
  s.RecordVerification(j.Summary.Id,row.Id,new("entered","原价 submit","等待自动验证"));
  var notice=s.CurrentVerification();Assert.NotNull(notice);Assert.True(notice.Automatic);Assert.Equal(row.Id,notice.RowId);
  Assert.False(s.ContinueAfterVerification(j.Summary.Id,notice.RequestId));Assert.Null(s.ClaimNext(j.Summary.Id));
  s.RecordVerification(j.Summary.Id,row.Id,new("confirmed","原价 submit","自动填充后确认"));Assert.Equal(notice.RequestId,s.CurrentVerification()!.RequestId);
  s.RecordVerification(j.Summary.Id,row.Id,new("passed","原价 submit","先回读"));Assert.Null(s.CurrentVerification());Assert.Null(s.Get(j.Summary.Id).Summary.Verification);
 }
 [Fact] public void AutomaticTimeoutReplacesNoticeWithManualAction()
 {
  var s=new JobStore(dir);var j=s.Create(StorageTests.Snapshot(1),ExecutionMode.PriceCut);s.TryStart(j.Summary.Id);var row=s.ClaimNext(j.Summary.Id)!;
  s.RecordVerification(j.Summary.Id,row.Id,new("entered","活动 open","等待"));var automatic=s.CurrentVerification()!;
  s.WaitForVerification(j.Summary.Id,row.Id,"超时转人工");var manual=s.CurrentVerification()!;
  Assert.False(manual.Automatic);Assert.NotEqual(automatic.RequestId,manual.RequestId);Assert.False(s.ContinueAfterVerification(j.Summary.Id,automatic.RequestId));
  Assert.True(s.ContinueAfterVerification(j.Summary.Id,manual.RequestId));
 }
 [Fact] public void PausingDuringAutomaticVerificationClearsNoticeAfterPassing()
 {
  var s=new JobStore(dir);var j=s.Create(StorageTests.Snapshot(1),ExecutionMode.PriceCut);s.TryStart(j.Summary.Id);var row=s.ClaimNext(j.Summary.Id)!;
  s.RecordVerification(j.Summary.Id,row.Id,new("entered","活动 open","等待"));Assert.True(s.RequestPause(j.Summary.Id));
  s.RecordVerification(j.Summary.Id,row.Id,new("passed","活动 open","先回读"));Assert.Null(s.Get(j.Summary.Id).Summary.Verification);Assert.Equal(JobStatus.Pausing,s.Get(j.Summary.Id).Summary.Status);
 }
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
 [Fact] public void ConnectionFailureQueuesRereadWithoutPausingTheBatch()
 {
  var s=new JobStore(dir);var j=s.Create(StorageTests.Snapshot(2),ExecutionMode.PriceCut);s.TryStart(j.Summary.Id);var row=s.ClaimNext(j.Summary.Id)!;
  s.RecordVerification(j.Summary.Id,row.Id,new("connection-failed","活动 open","页面连接中断"));
  Assert.Equal(JobStatus.Running,s.Get(j.Summary.Id).Summary.Status);Assert.Null(s.CurrentVerification());
  var other=s.Create(StorageTests.Snapshot(1),ExecutionMode.PriceCut);Assert.False(s.TryStart(other.Summary.Id));
  s.UpdateRow(j.Summary.Id,row.Id,new(RowStatus.NeedsReview,"连接中断","重连后先回读","connection-review",Retryable:true));
  Assert.NotNull(s.ClaimNext(j.Summary.Id));Assert.Equal(JobStatus.Running,s.Get(j.Summary.Id).Summary.Status);Assert.Null(s.Get(j.Summary.Id).Summary.Verification);
 }
 public void Dispose(){if(Directory.Exists(dir))Directory.Delete(dir,true);}
}
