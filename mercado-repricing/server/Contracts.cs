namespace Repricing;

public record RawCell(string Text, bool IsFormula, bool IsNumeric, string? DisplayText=null);
public record RawRow(int SourceRow, Dictionary<string, RawCell> Cells);
public record ImportSheet(string Name, string[] Headers, RawRow[] Rows);
public record ColumnMapping(string Store="", string Sku="", string TargetPrice="", string? Site=null, string? ItemId=null, string? VariationId=null, string? PromotionId=null, string? BasePrice=null, string? AllowExit=null, string? AllowBaseChange=null, string? Note=null, string? Action=null);
public record ValidateImportRequest(string Sheet, ColumnMapping Mapping, string? DefaultSite, bool AllStores=false, string? SelectedStoreId=null);
public record PriceInput(string Store, string Site, string Currency, string Sku, string? ItemId, string? VariationId, string? PromotionId, decimal TargetPrice, decimal? BasePrice, bool AllowExit, bool AllowBaseChange, string? Note, string? Action=null, string? StoreName=null);
public record ValidatedRow(int SourceRow, string Sheet, Dictionary<string,string> Original, PriceInput? Input, string[] Errors, Dictionary<string,string>? Mapped=null);
public record ValidationResult(Guid ValidationId, Guid ImportId, ValidatedRow[] Rows, ZiniaoStore[]? Stores=null, string[]? Warnings=null);
public record ValidationSnapshot(Guid Id, string FileName, ValidatedRow[] Rows, byte[]? SourceFile=null);
public record ImportSession(Guid Id, string FileName, ImportSheet[] Sheets, DateTimeOffset CreatedAt, byte[]? SourceFile=null);
public record CreateJobRequest(Guid ValidationId, ExecutionMode Mode, bool ConfirmPriceCuts=false,bool ConfirmMixedPrices=false,bool ConfirmBasePrices=false,bool ConfirmSubsidyExits=false,DateOnly? ExpectedWeekendStart=null);
public enum ExecutionMode { Simulation, Live, ReadOnly, PriceCut, WeekendEnrollment }
public enum JobStatus { Ready, Running, Pausing, Paused, Completed, Interrupted, AwaitingVerification, Verifying }
public enum RowStatus { Invalid, Pending, Running, Simulated, Succeeded, Skipped, Failed, NeedsReview, Inspected, RetryPending, AwaitingVerification }
public record VerificationNotice(Guid RequestId,Guid JobId,Guid RowId,string StoreId,string StoreName,string Sku,string Message,DateTimeOffset RequestedAt,bool Automatic=false);
public record ContinueVerificationRequest(Guid RequestId);
public record JobSummary(Guid Id, string FileName, ExecutionMode Mode, JobStatus Status, DateTimeOffset CreatedAt, int Total, int Finished, int Round=1,bool MixedPrices=false,bool BasePrices=false,VerificationNotice? Verification=null,bool SubsidyExits=false,bool IncomePriority=false,string[]? StoreNames=null,bool HasSourceFile=false, WeekendPeriod? Weekend=null);
public record WeekendPeriod(DateOnly Start,DateOnly End){public string Label=>$"{Start:M.d}-{End:M.d}";}
public record VerificationUpdate(string Phase,string Operation,string Message);
public record JobRow(Guid Id, ValidatedRow Source, RowStatus Status, string Route, string Message, string Checkpoint, PriceObservation? Observation=null, Execution.PriceCutPlan? PriceCut=null, int Attempts=0, int PriceCutAttempt=0);
public record JobDetail(JobSummary Summary, JobRow[] Rows);
public record JobEvent(long Id, Guid JobId, DateTimeOffset At, string Message);
public record ExecutionResult(RowStatus Status, string Route, string Message, string Checkpoint, PriceObservation? Observation=null, bool PauseBatch=false, bool Retryable=false, PriceInput[]? SplitInputs=null);
public record ZiniaoStore(string Id,string Name,string Platform);
public record PromotionObservation(string Id,string Name,string Status,string PriceText,bool Active,bool Editable=false,bool Attractive=false,bool Joinable=false,string? NetAmountText=null);
public record PriceObservation(string StoreId,string ItemId,string Title,string BasePriceText,decimal? CurrentPrice,decimal? Difference,PromotionObservation[] Promotions,DateTimeOffset CheckedAt)
{
    public string? ReadbackNote {get;init;}
    public string IdentityNote=>"读数来自所选紫鸟配置；美客多当前卖家账号未独立核验。";
}
public record CliDiagnostic(bool Available, string Message, DateTimeOffset CheckedAt);
public class AppException(string message, int status=400) : Exception(message) { public int Status {get;}=status; }
public class VerificationRequiredException(string message="店铺需要验证或重新登录。请在紫鸟完成验证，再点击继续。") : AppException(message,409);
