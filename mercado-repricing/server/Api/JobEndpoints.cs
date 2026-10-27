using Repricing.Import;
using Repricing.Jobs;
using Repricing.Storage;
namespace Repricing.Api;
public static class JobEndpoints
{
    public static void MapJobs(this WebApplication app)
    {
        app.MapGet("/api/jobs",(JobStore store)=>store.List());
        app.MapPost("/api/jobs/{id:guid}/retry-reviews",(Guid id,JobStore store)=>{if(!store.RetryWeekendReviews(id)&&!store.RetryPriceCutReviews(id))throw new AppException("当前批次没有可安全恢复的待核对行，或另一批任务正在执行。",409);return Results.Ok();});
        app.MapGet("/api/verification",(JobStore store)=>Results.Json(store.CurrentVerification()));
        app.MapPost("/api/jobs/{id:guid}/verification/continue",(Guid id,ContinueVerificationRequest request,JobStore store)=>{if(!store.ContinueAfterVerification(id,request.RequestId))throw new AppException("验证请求已变化或其他任务正在运行，请刷新后再继续。",409);return Results.Ok();});
        app.MapGet("/api/jobs/{id:guid}",(Guid id,JobStore store)=>store.Get(id));
        app.MapPost("/api/jobs",(CreateJobRequest request,ImportSessionStore sessions,JobStore store)=>{if(request.Mode is not ExecutionMode.Simulation and not ExecutionMode.ReadOnly and not ExecutionMode.PriceCut and not ExecutionMode.WeekendEnrollment)throw new AppException("仅支持模拟、只读核对和实际调价。",409);if(request.Mode is ExecutionMode.PriceCut or ExecutionMode.WeekendEnrollment&&!request.ConfirmPriceCuts)throw new AppException("请先确认本批店铺范围、各紫鸟配置登录的卖家与真实调价操作。",409);if(request.Mode==ExecutionMode.WeekendEnrollment&&request.ExpectedWeekendStart is {} expected&&expected!=Repricing.Execution.WeekendActivity.Current(DateTimeOffset.UtcNow).Start)throw new AppException("本周末日期已变化，请刷新并重新确认报名日期。",409);return store.Create(sessions.GetValidation(request.ValidationId),request.Mode,request.ConfirmMixedPrices,request.ConfirmBasePrices,request.ConfirmSubsidyExits,incomePriority:request.Mode==ExecutionMode.PriceCut);});
        app.MapPost("/api/jobs/{id:guid}/start",async (Guid id,JobRunner runner,CancellationToken ct)=>{if(!await runner.StartAsync(id,ct))throw new AppException("任务状态不允许启动，或另一批次正在运行。",409);return Results.Ok();});
        app.MapPost("/api/jobs/{id:guid}/pause",(Guid id,JobRunner runner)=>{if(!runner.Pause(id))throw new AppException("当前任务不在运行中。",409);return Results.Ok();});
        app.MapGet("/api/jobs/{id:guid}/events",(Guid id,long? after,JobStore store)=>store.Events(id,after??0));
        app.MapGet("/api/jobs/{id:guid}/source",(Guid id,JobStore store)=>Results.File(store.SourceFile(id),"application/octet-stream",store.Get(id).Summary.FileName));
        app.MapGet("/api/jobs/{id:guid}/log",(Guid id,JobStore store)=>Results.File(store.ExportLog(id),"text/plain; charset=utf-8",$"完整执行日志-{id.ToString()[..8]}.txt"));
        app.MapGet("/api/jobs/{id:guid}/export",(Guid id,JobStore store)=>Results.File(CsvExport.Export(store.Get(id)),"text/csv; charset=utf-8",$"价格核对结果-{id.ToString()[..8]}.csv"));
        app.MapPost("/api/shutdown",(JobStore store,IHostApplicationLifetime lifetime)=>{if(store.ActiveId()!=null)throw new AppException("仍有任务执行中，请先暂停任务再退出。",409);_=Task.Run(async()=>{await Task.Delay(350);lifetime.StopApplication();});return Results.Ok();});
    }
}
