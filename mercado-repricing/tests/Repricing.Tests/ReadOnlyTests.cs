using Repricing.Storage;
using Repricing.Api;
namespace Repricing.Tests;
public class ReadOnlyTests : IDisposable
{
    readonly string directory=Path.Combine(Path.GetTempPath(),"repricing-read-"+Guid.NewGuid());
    [Fact] public void ReadOnlyJobCanPersistAndStartWithoutEnablingLive()
    {
        var store=new JobStore(directory);
        var mode=Enum.Parse<ExecutionMode>("ReadOnly");
        var job=store.Create(StorageTests.Snapshot(1),mode);
        Assert.Equal(mode,job.Summary.Mode);Assert.True(store.TryStart(job.Summary.Id));
        Assert.NotNull(store.ClaimNext(job.Summary.Id));
        Assert.DoesNotContain(store.Events(job.Summary.Id,0),e=>e.Message.Contains("模拟"));
    }
    [Fact] public void SameValidationCannotSilentlyReuseAnotherExecutionMode()
    {
        var store=new JobStore(directory);var snapshot=StorageTests.Snapshot(1);
        store.Create(snapshot,ExecutionMode.Simulation);
        Assert.Throws<AppException>(()=>store.Create(snapshot,Enum.Parse<ExecutionMode>("ReadOnly")));
    }
    [Fact] public void LiveStillCannotBeCreated()=>Assert.Throws<AppException>(()=>new JobStore(directory).Create(StorageTests.Snapshot(),ExecutionMode.Live));
    [Fact] public void ConnectionFailurePausesBatchAndPreservesRemainingRows()
    {
        var store=new JobStore(directory);var job=store.Create(StorageTests.Snapshot(),ExecutionMode.ReadOnly);store.TryStart(job.Summary.Id);
        var row=store.ClaimNext(job.Summary.Id)!;
        store.UpdateRow(job.Summary.Id,row.Id,new(RowStatus.NeedsReview,"只读核对","连接中断","readonly-error",PauseBatch:true));
        Assert.Null(store.ClaimNext(job.Summary.Id));Assert.Equal(JobStatus.Paused,store.Get(job.Summary.Id).Summary.Status);Assert.Equal(RowStatus.Pending,store.Get(job.Summary.Id).Rows[1].Status);
    }
    [Fact] public void ExportCarriesActualObservationAndReadOnlyLabel()
    {
        var store=new JobStore(directory);var job=store.Create(StorageTests.Snapshot(1),ExecutionMode.ReadOnly);store.TryStart(job.Summary.Id);
        var row=store.ClaimNext(job.Summary.Id)!;var observation=new PriceObservation("1","4279929175","测试","R$ 245",221.91m,-1.91m,[],DateTimeOffset.UtcNow);
        store.UpdateRow(job.Summary.Id,row.Id,new(RowStatus.Inspected,"只读核对","未改价","readonly-verified",observation));
        var exported=System.Text.Encoding.UTF8.GetString(CsvExport.Export(store.Get(job.Summary.Id)));
        Assert.Contains("只读核对",exported);Assert.Contains("221.91",exported);Assert.DoesNotContain("模拟",exported);Assert.Equal(221.91m,store.Get(job.Summary.Id).Rows[0].Observation!.CurrentPrice);
    }
    public void Dispose(){Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
