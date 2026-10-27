namespace Repricing.Execution;
public record ExecutionMetric(string Stage,long ElapsedMs,int Commands,int Navigations);

public static class ExecutionTiming
{
    public static readonly TimeSpan PageRead=TimeSpan.FromSeconds(210);
    public static readonly TimeSpan PreparePrice=TimeSpan.FromSeconds(90);
    public static readonly TimeSpan PrepareExit=TimeSpan.FromSeconds(140);
    public static readonly TimeSpan ConfirmExit=TimeSpan.FromSeconds(75);
    public static readonly TimeSpan BaseEdit=TimeSpan.FromSeconds(240);
    public static readonly TimeSpan Verify=TimeSpan.FromSeconds(180);
    public static readonly TimeSpan GroupExpansionWait=TimeSpan.FromSeconds(10);
    public const int PollMs=500;
    public const int VerifyInitialMs=2500,VerifyPollMs=5000,VerifyReads=12;
    public static int ReadPollDelay(int poll)=>poll switch{0=>75,1=>125,2=>200,3=>350,4=>500,_=>700};
    public static int VerifyDelay(int attempt)=>attempt switch{0=>VerifyInitialMs,1=>250,2=>500,3=>1000,4=>2000,5=>3000,_=>VerifyPollMs};
    public static bool RefreshVerification(int attempt)=>attempt>=0&&attempt%4==0;
    public static readonly object Browser=new{dialogMs=60000,pollMs=150,reopenAtMs=new[]{15000,40000},confirmationMs=30000,validationMs=10000,settleMs=500};
}
