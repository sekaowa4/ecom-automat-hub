using Repricing.Execution;
using Repricing.Storage;
namespace Repricing.Tests;
public class JobTests : IDisposable
{
    readonly string directory=Path.Combine(Path.GetTempPath(),"repricing-"+Guid.NewGuid());
    [Theory] [InlineData(75,false,null,false,"活动内降价")] [InlineData(90,true,null,false,"退出后重新报名")] [InlineData(110,false,120,true,"修改基础售价")]
    public async Task ThreeRoutesAreExplicitlySimulated(int target,bool exit,int? basePrice,bool change,string route){var r=await new SimulationExecutor(0).ExecuteAsync(StorageTests.Input(target,exit,basePrice,change),default);Assert.Equal(RowStatus.Simulated,r.Status);Assert.Equal(route,r.Route);Assert.Contains("模拟",r.Message);}
    [Fact] public async Task LiveExecutorRefuses()=>await Assert.ThrowsAsync<AppException>(()=>new ZiniaoExecutor().ExecuteAsync(StorageTests.Input(),default));
    [Fact] public void ConcurrentStartRunsOnce(){var s=new JobStore(directory);var j=s.Create(StorageTests.Snapshot(),ExecutionMode.Simulation);int wins=0;Parallel.For(0,20,_=>{if(s.TryStart(j.Summary.Id))Interlocked.Increment(ref wins);});Assert.Equal(1,wins);}
    [Fact] public void PauseStopsBeforeNextRow(){var s=new JobStore(directory);var j=s.Create(StorageTests.Snapshot(),ExecutionMode.Simulation);s.TryStart(j.Summary.Id);s.ClaimNext(j.Summary.Id);s.RequestPause(j.Summary.Id);s.UpdateRow(j.Summary.Id,j.Rows[0].Id,new(RowStatus.Simulated,"test","模拟完成","verified"));Assert.Null(s.ClaimNext(j.Summary.Id));Assert.Equal(JobStatus.Paused,s.Get(j.Summary.Id).Summary.Status);Assert.Equal(RowStatus.Pending,s.Get(j.Summary.Id).Rows[1].Status);}
    [Fact] public void PauseAtFinalRowCompletes(){var s=new JobStore(directory);var j=s.Create(StorageTests.Snapshot(1),ExecutionMode.Simulation);s.TryStart(j.Summary.Id);s.ClaimNext(j.Summary.Id);s.RequestPause(j.Summary.Id);s.UpdateRow(j.Summary.Id,j.Rows[0].Id,new(RowStatus.Simulated,"test","模拟完成","verified"));Assert.Null(s.ClaimNext(j.Summary.Id));Assert.Equal(JobStatus.Completed,s.Get(j.Summary.Id).Summary.Status);}
    [Fact] public void RestartMarksInFlightUnknown(){var s=new JobStore(directory);var j=s.Create(StorageTests.Snapshot(),ExecutionMode.Simulation);s.TryStart(j.Summary.Id);s.ClaimNext(j.Summary.Id);s.RecoverInterrupted();var recovered=s.Get(j.Summary.Id);Assert.Equal(JobStatus.Interrupted,recovered.Summary.Status);Assert.Equal(RowStatus.NeedsReview,recovered.Rows[0].Status);Assert.False(s.TryStart(j.Summary.Id));}
    public void Dispose(){Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
