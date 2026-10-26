namespace Repricing.Execution;
public class ZiniaoExecutor : IPriceExecutor
{
    public Task<ExecutionResult> ExecuteAsync(PriceInput input,CancellationToken ct)=>Task.FromException<ExecutionResult>(new AppException("真实价格提交已关闭。本版只允许模拟与只读核对，不修改价格或活动。",409));
}
