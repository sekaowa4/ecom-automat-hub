namespace Repricing.Execution;
public interface IPriceExecutor { Task<ExecutionResult> ExecuteAsync(PriceInput input,CancellationToken ct); }
