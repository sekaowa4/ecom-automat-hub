using Repricing.Execution;
using Repricing.Storage;
namespace Repricing.Tests;

public class NetworkRetryTests:IDisposable
{
    readonly string dir=Path.Combine(Path.GetTempPath(),"network-retry-"+Guid.NewGuid());
    sealed class Clock:TimeProvider{public DateTimeOffset Now=new(2026,10,31,0,0,0,TimeSpan.Zero);public override DateTimeOffset GetUtcNow()=>Now;}
    [Theory]
    [InlineData("读取 SKU-1 超时：加载搜索结果；未提交价格。")]
    [InlineData("紫鸟店铺代理连接失败（ERR_PROXY_CONNECTION_FAILED）；本机页面控制通道已连接。")]
    [InlineData("商品分组展开未响应：无法读取对应 SKU 的子商品；未提交价格。")]
    [InlineData("分组展开按钮尚未加载完成；未提交价格。")]
    [InlineData("页面下拉失败，控件加载超时。")]
    public void RecognizesTemporaryFailures(string text)=>Assert.NotNull(TransientRetry.Classify(text));
    [Theory]
    [InlineData("回读价格不一致，保存结果未知。")]
    [InlineData("平台价格上限错误。")]
    [InlineData("身份不明确，不能确认商品。")]
    [InlineData("页面命令被验证跳转打断；验证已通过，先回读原操作结果。")]
    [InlineData("连接、页面或记录写入失败，未提交价格。")]
    public void DoesNotGuessNetworkForBusinessOrStorageFailures(string text)=>Assert.Null(TransientRetry.Classify(text));
    [Theory][InlineData(ExecutionMode.PriceCut)][InlineData(ExecutionMode.WeekendEnrollment)]
    public void RetriesBeyondThreeWithBackoffSkipsFinishedRowsAndHonorsPause(ExecutionMode mode)
    {
        var clock=new Clock();var s=new JobStore(dir,clock);var snapshot=StorageTests.Snapshot(2);
        for(int i=0;i<snapshot.Rows.Length;i++)snapshot.Rows[i]=snapshot.Rows[i] with{Input=snapshot.Rows[i].Input! with{Action=null,AllowExit=false,AllowBaseChange=false,BasePrice=null}};
        var j=s.Create(snapshot,mode);Assert.True(s.TryStart(j.Summary.Id));var first=s.ClaimNext(j.Summary.Id)!;
        var failure=new ExecutionResult(RowStatus.NeedsReview,"读取","页面加载超时，未提交价格。","cut-review",Retryable:true);
        s.UpdateRow(j.Summary.Id,first.Id,failure);
        var second=s.ClaimNext(j.Summary.Id)!;Assert.NotEqual(first.Id,second.Id);s.UpdateRow(j.Summary.Id,second.Id,new(RowStatus.Succeeded,"完成","已核验","verified"));
        for(int attempt=1;attempt<=7;attempt++)
        {
            var row=s.Get(j.Summary.Id).Rows[0];Assert.Equal(RowStatus.RetryPending,row.Status);Assert.NotNull(row.RetryReason);Assert.NotNull(row.RetryAfter);
            Assert.Null(s.ClaimNext(j.Summary.Id));Assert.Equal(JobStatus.Running,s.Get(j.Summary.Id).Summary.Status);
            Assert.InRange((row.RetryAfter!.Value-clock.Now).TotalSeconds,30,300);clock.Now=row.RetryAfter.Value;
            var next=s.ClaimNext(j.Summary.Id)!;Assert.Equal(first.Id,next.Id);Assert.Equal(attempt+1,next.Attempts);s.UpdateRow(j.Summary.Id,next.Id,failure);
        }
        Assert.Equal(1,s.Get(j.Summary.Id).Rows[1].Attempts);Assert.True(s.RequestPause(j.Summary.Id));Assert.Null(s.ClaimNext(j.Summary.Id));Assert.Equal(JobStatus.Paused,s.Get(j.Summary.Id).Summary.Status);
    }
    [Fact] public void ConnectionEventsDoNotPausePriceCutAndVerificationStillStopsIt()
    {
        var s=new JobStore(dir);var j=s.Create(StorageTests.Snapshot(1),ExecutionMode.PriceCut);s.TryStart(j.Summary.Id);var row=s.ClaimNext(j.Summary.Id)!;
        for(int i=0;i<5;i++)s.RecordVerification(j.Summary.Id,row.Id,new("connection-failed","读取","连接中断"));
        Assert.Equal(JobStatus.Running,s.Get(j.Summary.Id).Summary.Status);
        s.WaitForVerification(j.Summary.Id,row.Id,"请人工验证");Assert.Equal(JobStatus.AwaitingVerification,s.Get(j.Summary.Id).Summary.Status);Assert.Null(s.ClaimNext(j.Summary.Id));
    }
    [Fact] public void UnknownSubmissionCheckpointSurvivesEveryNetworkRetry()
    {
        var clock=new Clock();var s=new JobStore(dir,clock);var j=s.Create(StorageTests.Snapshot(1),ExecutionMode.PriceCut);s.TryStart(j.Summary.Id);var row=s.ClaimNext(j.Summary.Id)!;
        var plan=new PriceCutPlan(new("1","A","MercadoLibre-巴西-本土"),row.Source.Input!,new("123",row.Source.Input!.Sku,"商品","R$ 100",[]),"C-1",80,100);
        s.SavePriceCutPlan(j.Summary.Id,row.Id,plan);
        for(int i=0;i<5;i++)
        {
            s.UpdateRow(j.Summary.Id,row.Id,new(RowStatus.NeedsReview,"调价","已尝试保存，结果未知；下轮先回读。","cut-review",Retryable:true,RetryReason:TransientRetry.FromException(new System.Net.WebSockets.WebSocketException())));
            var waiting=s.Get(j.Summary.Id).Rows.Single();clock.Now=waiting.RetryAfter!.Value;
            row=s.ClaimNext(j.Summary.Id)!;Assert.Equal(1,row.PriceCutAttempt);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(plan),System.Text.Json.JsonSerializer.Serialize(row.PriceCut));
        }
    }
    [Fact] public void TypedNetworkExceptionsDoNotTurnStorageFailuresOrValidationIntoInfiniteRetries()
    {
        Assert.NotNull(TransientRetry.FromException(new System.Net.Http.HttpRequestException()));
        Assert.NotNull(TransientRetry.FromException(new OperationCanceledException()));
        Assert.Null(TransientRetry.FromException(new IOException("write failed")));
        Assert.Null(TransientRetry.FromException(new VerificationRequiredException()));
        Assert.Null(TransientRetry.FromException(new AppException("加载超时",422)));
    }
    public void Dispose(){if(Directory.Exists(dir))Directory.Delete(dir,true);}
}
