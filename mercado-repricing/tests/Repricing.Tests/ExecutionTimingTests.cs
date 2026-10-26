using System.Text.Json;
using Repricing.Execution;
namespace Repricing.Tests;
public class ExecutionTimingTests
{
 [Fact] public void BrowserWaitsFitTheirParentTimeouts()
 {
  var b=JsonSerializer.SerializeToElement(ExecutionTiming.Browser);var dialog=b.GetProperty("dialogMs").GetInt32();var confirm=b.GetProperty("confirmationMs").GetInt32();
  Assert.True(ExecutionTiming.PreparePrice.TotalMilliseconds>dialog+b.GetProperty("validationMs").GetInt32()+b.GetProperty("settleMs").GetInt32());
  Assert.True(ExecutionTiming.ConfirmExit.TotalMilliseconds>confirm*2);Assert.All(b.GetProperty("reopenAtMs").EnumerateArray(),n=>Assert.InRange(n.GetInt32(),1,dialog-1));
 }
 [Fact] public void PackagedScriptsIncludeTheirSharedResource(){Assert.NotEmpty(ReadOnlyCdp.PriceCutScript);Assert.NotEmpty(ReadOnlyCdp.StopParticipationScript);Assert.NotEmpty(ReadOnlyCdp.WeekendPromotionExpandScript);}
 [Fact] public void FastVerificationAlwaysStartsWithAnIndependentReadAndRetainsSlowFallback()
 {
  Assert.Equal(2500,ExecutionTiming.VerifyDelay(0));Assert.True(ExecutionTiming.RefreshVerification(0));
  Assert.False(ExecutionTiming.RefreshVerification(1));Assert.False(ExecutionTiming.RefreshVerification(2));
  Assert.All(Enumerable.Range(3,ExecutionTiming.VerifyReads-3),i=>Assert.True(ExecutionTiming.RefreshVerification(i)));
  Assert.All(Enumerable.Range(0,50),i=>Assert.InRange(ExecutionTiming.ReadPollDelay(i),75,700));
  Assert.Equal(5000,ExecutionTiming.VerifyDelay(ExecutionTiming.VerifyReads-1));
 }
}
