namespace Repricing.Execution;

public static class ExecutionTiming
{
    public static readonly TimeSpan PageRead=TimeSpan.FromSeconds(210);
    public static readonly TimeSpan PreparePrice=TimeSpan.FromSeconds(90);
    public static readonly TimeSpan PrepareExit=TimeSpan.FromSeconds(140);
    public static readonly TimeSpan ConfirmExit=TimeSpan.FromSeconds(75);
    public static readonly TimeSpan BaseEdit=TimeSpan.FromSeconds(240);
    public static readonly TimeSpan Verify=TimeSpan.FromSeconds(180);
    public const int PollMs=500;
    public const int VerifyInitialMs=2500,VerifyPollMs=5000,VerifyReads=12;
    public static readonly object Browser=new{dialogMs=60000,pollMs=150,reopenAtMs=new[]{15000,40000},confirmationMs=30000,validationMs=10000,settleMs=500};
}
