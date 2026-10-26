using Repricing.Storage;
namespace Repricing.Tests;
public class RetryRoundTests:IDisposable
{
    readonly string dir=Path.Combine(Path.GetTempPath(),"round-"+Guid.NewGuid());
    static readonly ExecutionResult Retry=new(RowStatus.NeedsReview,"降价","读取超时","cut-review",Retryable:true);
    static readonly ExecutionResult Done=new(RowStatus.Succeeded,"降价","完成","cut-verified");
    [Fact] public void FinishesFirstPassBeforeRetryAndNeverRepeatsSuccessfulRows()
    {
        var s=new JobStore(dir);var j=s.Create(StorageTests.Snapshot(3),ExecutionMode.PriceCut);s.TryStart(j.Summary.Id);
        var a=s.ClaimNext(j.Summary.Id)!;s.UpdateRow(j.Summary.Id,a.Id,Retry);
        Assert.Equal(JobStatus.Running,s.Get(j.Summary.Id).Summary.Status);Assert.Equal(RowStatus.RetryPending,s.Get(j.Summary.Id).Rows[0].Status);
        for(int i=1;i<3;i++){var next=s.ClaimNext(j.Summary.Id)!;Assert.Equal(j.Rows[i].Id,next.Id);s.UpdateRow(j.Summary.Id,next.Id,Done);}
        var again=s.ClaimNext(j.Summary.Id)!;Assert.Equal(a.Id,again.Id);Assert.Equal(2,again.Attempts);Assert.Equal(2,s.Get(j.Summary.Id).Summary.Round);
        s.UpdateRow(j.Summary.Id,again.Id,Done);Assert.Null(s.ClaimNext(j.Summary.Id));Assert.Equal(JobStatus.Completed,s.Get(j.Summary.Id).Summary.Status);Assert.Equal(1,s.Get(j.Summary.Id).Rows[1].Attempts);
    }
    [Fact] public void StopsAutomaticRetriesAfterThreeAttempts()
    {
        var s=new JobStore(dir);var j=s.Create(StorageTests.Snapshot(1),ExecutionMode.PriceCut);s.TryStart(j.Summary.Id);
        for(int i=1;i<=3;i++){var r=s.ClaimNext(j.Summary.Id);Assert.NotNull(r);Assert.Equal(i,r.Attempts);s.UpdateRow(j.Summary.Id,r.Id,Retry);}
        Assert.Null(s.ClaimNext(j.Summary.Id));Assert.Equal(RowStatus.NeedsReview,s.Get(j.Summary.Id).Rows[0].Status);Assert.Equal(3,s.Get(j.Summary.Id).Rows[0].Attempts);
    }
    [Fact] public void UserPausePreventsStartingTheNextRound()
    {
        var s=new JobStore(dir);var j=s.Create(StorageTests.Snapshot(1),ExecutionMode.PriceCut);s.TryStart(j.Summary.Id);var row=s.ClaimNext(j.Summary.Id)!;s.RequestPause(j.Summary.Id);s.UpdateRow(j.Summary.Id,row.Id,Retry);
        Assert.Null(s.ClaimNext(j.Summary.Id));Assert.Equal(JobStatus.Paused,s.Get(j.Summary.Id).Summary.Status);Assert.Equal(1,s.Get(j.Summary.Id).Summary.Round);
    }
    [Fact] public void ExplicitResumeQueuesLegacyTimeoutAfterRemainingRows()
    {
        var s=new JobStore(dir);var j=s.Create(StorageTests.Snapshot(2),ExecutionMode.PriceCut);s.TryStart(j.Summary.Id);var a=s.ClaimNext(j.Summary.Id)!;
        s.UpdateRow(j.Summary.Id,a.Id,new(RowStatus.NeedsReview,"降价","连接、页面或记录写入失败，未提交价格，已暂停。","cut-review",PauseBatch:true));
        using(var db=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(dir,"tasks.db")};Pooling=False")){db.Open();using var c=db.CreateCommand();c.CommandText="UPDATE job_rows SET payload=json_set(payload,'$.Attempts',0) WHERE id=$id";c.Parameters.AddWithValue("$id",a.Id.ToString());c.ExecuteNonQuery();}
        Assert.True(s.TryStart(j.Summary.Id));Assert.Equal(j.Rows[1].Id,s.ClaimNext(j.Summary.Id)!.Id);Assert.Equal(RowStatus.RetryPending,s.Get(j.Summary.Id).Rows[0].Status);
    }
    [Fact] public void OneCheckpointPerAttemptAndNoChangedProductBinding()
    {
        var s=new JobStore(dir);var j=s.Create(StorageTests.Snapshot(1),ExecutionMode.PriceCut);s.TryStart(j.Summary.Id);var row=s.ClaimNext(j.Summary.Id)!;
        var p=new Execution.PriceCutPlan(new("1","A","MercadoLibre-巴西-本土"),row.Source.Input!,new("123",row.Source.Input!.Sku,"商品","R$ 100",[]),"C-1",80,100);
        s.SavePriceCutPlan(j.Summary.Id,row.Id,p);Assert.Throws<AppException>(()=>s.SavePriceCutPlan(j.Summary.Id,row.Id,p));s.UpdateRow(j.Summary.Id,row.Id,Retry);s.ClaimNext(j.Summary.Id);
        Assert.Throws<AppException>(()=>s.SavePriceCutPlan(j.Summary.Id,row.Id,p with{Product=p.Product with{ItemId="999"}}));
        s.SavePriceCutPlan(j.Summary.Id,row.Id,p);Assert.Equal(2,s.Get(j.Summary.Id).Rows[0].PriceCutAttempt);
    }
    public void Dispose(){if(Directory.Exists(dir))Directory.Delete(dir,true);}
}
