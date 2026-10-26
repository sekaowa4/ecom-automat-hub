namespace Repricing.Execution;
public class CliDiagnostics(NativeZiniao native)
{
    readonly SemaphoreSlim gate=new(1,1);
    CliDiagnostic cached=new(false,"尚未检查。可复用紫鸟客户端现有登录态，无需在本程序输入密钥。",DateTimeOffset.MinValue);
    public CliDiagnostic Current=>cached;
    public async Task<CliDiagnostic> CheckAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if(DateTimeOffset.UtcNow-cached.CheckedAt<TimeSpan.FromSeconds(15))return cached;
            try{var stores=await native.StoresAsync(ct);return cached=new(true,$"已连接紫鸟客户端，可读取 {stores.Length} 个店铺。仅开放只读核对，价格提交已关闭。",DateTimeOffset.UtcNow);}
            catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
            catch(Exception e){return cached=new(false,e is AppException?e.Message:"紫鸟检查未完成，请确认客户端已登录并可正常运行。",DateTimeOffset.UtcNow);}
        }
        finally{gate.Release();}
    }
}
