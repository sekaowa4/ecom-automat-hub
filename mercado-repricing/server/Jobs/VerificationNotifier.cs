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
                    info.Environment["REPRICING_VERIFY_AUTOMATIC"]=notice.Automatic?"1":"0";
                    using var process=Process.Start(info)??throw new InvalidOperationException("无法显示验证提醒");
                    try
                    {
                        while(!process.HasExited && store.CurrentVerification()?.RequestId==notice.RequestId)
                            await Task.Delay(300,stoppingToken);
                        if(process.HasExited && store.CurrentVerification()?.RequestId==notice.RequestId)
                        {
                            if(process.ExitCode is not 0 and not 2)throw new InvalidOperationException($"验证提醒窗口退出异常，退出码 {process.ExitCode}");
                            if(notice.Automatic){/* Closing the reminder never resumes or cancels automatic verification. */}
                            else if(process.ExitCode==0)store.ContinueAfterVerification(notice.JobId,notice.RequestId);
                            else if(process.ExitCode==2)store.RequestPause(notice.JobId);
                        }
                    }
                    finally{if(!process.HasExited)process.Kill(true);}
                }
                catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){break;}
                catch(Exception e){shown=null;logger.LogError(e,"无法显示 Windows 验证弹窗，将重试；也可通过工作台查看验证任务。");await Task.Delay(5000,stoppingToken);}
            }
            await Task.Delay(500,stoppingToken);
        }
    }
}
