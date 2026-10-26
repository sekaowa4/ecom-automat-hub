using Repricing.Execution;
using Repricing.Storage;
namespace Repricing.Jobs;
public class JobRunner(JobStore store,IPriceExecutor executor,ZiniaoReadOnlyExecutor reader,ZiniaoPriceCutExecutor cutter,IZiniaoReader ziniao,ILogger<JobRunner> logger) : BackgroundService
{
    public Task<bool> StartAsync(Guid id,CancellationToken ct)=>Task.FromResult(store.TryStart(id));
    public bool Pause(Guid id)=>store.RequestPause(id);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        store.RecoverInterrupted();
        while(!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if(store.ActiveId() is Guid id && store.ClaimNext(id) is JobRow row)
                {
                    try{ziniao.SetVerificationReporter(update=>store.RecordVerification(id,row.Id,update));var job=store.Get(id);var summary=job.Summary;var mode=summary.Mode;var result=mode==ExecutionMode.WeekendEnrollment?await new WeekendEnrollmentExecutor((IZiniaoPriceCutClient)ziniao).ExecuteAsync(row.Source.Input!,summary.Weekend??throw new AppException("周末任务缺少已冻结日期。",409),plan=>store.SavePriceCutPlan(id,row.Id,plan),stoppingToken,row.PriceCut,job.Rows.Where(r=>r.Source.Input!=null).Select(r=>r.Source.Input!).ToArray()):mode==ExecutionMode.PriceCut?await cutter.ExecuteAsync(summary.SubsidyExits?row.Source.Input! with{AllowExit=true}:row.Source.Input!,plan=>store.SavePriceCutPlan(id,row.Id,plan),stoppingToken,row.PriceCut,summary.MixedPrices,summary.BasePrices,job.Rows.Where(r=>r.Source.Input!=null).Select(r=>r.Source.Input!).ToArray(),incomePriority:summary.IncomePriority):await (mode==ExecutionMode.Simulation?executor:(IPriceExecutor)reader).ExecuteAsync(row.Source.Input!,stoppingToken);if(result.SplitInputs is {} inputs)store.SplitPriceCutRow(id,row.Id,inputs);else store.UpdateRow(id,row.Id,result);}
                    catch(VerificationRequiredException e){store.WaitForVerification(id,row.Id,e.Message);}
                    catch(AppException e){logger.LogWarning(e,"任务校验未通过，SKU {Sku}",row.Source.Input?.Sku);store.UpdateRow(id,row.Id,new(RowStatus.NeedsReview,"执行校验",e.Message,"guard-review"));}
                    catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){break;}
                    catch(Exception e){logger.LogError(e,"执行失败");var cut=store.Get(id).Summary.Mode==ExecutionMode.PriceCut;store.UpdateRow(id,row.Id,new(RowStatus.Failed,"执行失败","执行器异常，请查看程序日志。","failed",PauseBatch:!cut,Retryable:cut));}
                    finally{ziniao.SetVerificationReporter(null);}
                }
                else {if(store.CurrentVerification()==null)await ziniao.CloseAsync();await Task.Delay(150,stoppingToken);}
            }
            catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){break;}
            catch(Exception e){logger.LogError(e,"任务服务异常");await Task.Delay(1000,stoppingToken);}
        }
        await ziniao.CloseAsync();
    }
}
