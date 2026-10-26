using System.Diagnostics;
using System.Text;
using Repricing.Storage;
namespace Repricing.Jobs;

// The helper owns only its notification window; seller browser windows remain untouched.
public class VerificationNotifier(JobStore store,ILogger<VerificationNotifier> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Guid? shown=null;
        using var resource=typeof(VerificationNotifier).Assembly.GetManifestResourceStream("VerificationPrompt.ps1")!;
        using var reader=new StreamReader(resource);
        var encoded=Convert.ToBase64String(Encoding.Unicode.GetBytes(await reader.ReadToEndAsync(stoppingToken)));
        while(!stoppingToken.IsCancellationRequested)
        {
            var notice=store.CurrentVerification();
            if(notice!=null && shown!=notice.RequestId)
            {
                shown=notice.RequestId;
                try
                {
                    var info=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","WindowsPowerShell","v1.0","powershell.exe")){UseShellExecute=false,CreateNoWindow=true};
                    foreach(var arg in new[]{"-NoProfile","-NonInteractive","-STA","-EncodedCommand",encoded})info.ArgumentList.Add(arg);
                    info.Environment["REPRICING_VERIFY_STORE"]=notice.StoreName;
                    info.Environment["REPRICING_VERIFY_SKU"]=notice.Sku;
                    using var process=Process.Start(info)??throw new InvalidOperationException("无法显示验证提醒");
                    try
                    {
                        while(!process.HasExited && store.CurrentVerification()?.RequestId==notice.RequestId)
                            await Task.Delay(300,stoppingToken);
                        if(process.HasExited && store.CurrentVerification()?.RequestId==notice.RequestId)
                        {
                            if(process.ExitCode==0)store.ContinueAfterVerification(notice.JobId,notice.RequestId);
                            else if(process.ExitCode==2)store.RequestPause(notice.JobId);
                            else logger.LogError("验证提醒窗口退出异常，任务保持暂停。退出码 {Code}",process.ExitCode);
                        }
                    }
                    finally{if(!process.HasExited)process.Kill(true);}
                }
                catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){break;}
                catch(Exception e){logger.LogError(e,"无法显示 Windows 验证弹窗，请通过工作台继续验证任务。");}
            }
            await Task.Delay(500,stoppingToken);
        }
    }
}
