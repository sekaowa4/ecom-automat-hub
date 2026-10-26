using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Repricing.Api;

namespace Repricing.Labels;

public record LabelCropResult(byte[] Pdf,int Labels,string FileName);

public sealed partial class ProductLabelCropper(IConfiguration configuration,ILogger<ProductLabelCropper> logger,IHostApplicationLifetime lifetime)
{
    public const long MaxInputBytes=10*1024*1024;
    const long MaxOutputBytes=50*1024*1024;
    readonly SemaphoreSlim gate=new(1,1);

    public async Task<LabelCropResult> CropAsync(IFormFile file,CancellationToken cancellationToken,bool carton=false)
    {
        using var lifetimeCancellation=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,lifetime.ApplicationStopping);
        cancellationToken=lifetimeCancellation.Token;
        if(!string.Equals(Path.GetExtension(file.FileName),".pdf",StringComparison.OrdinalIgnoreCase))throw new AppException("仅支持 PDF 文件。",400);
        if(file.Length==0)throw new AppException("文件为空，请重新选择。",400);
        if(file.Length>MaxInputBytes)throw new AppException("单个 PDF 不能超过 10 MB。",400);
        await using var input=file.OpenReadStream();
        var header=new byte[5];
        if(await input.ReadAtLeastAsync(header,5,false,cancellationToken)!=5||!header.AsSpan().SequenceEqual("%PDF-"u8))throw new AppException("文件内容不是 PDF，请检查后重新上传。",400);
        var engine=configuration[carton?"CartonLabelEnginePath":"LabelEnginePath"]??Path.Combine(AppContext.BaseDirectory,"tools","labels",carton?"CartonLabelCropper.exe":"PDFLabelCropper.exe");
        if(!File.Exists(engine))throw new AppException("标签剪裁组件缺失，请使用完整程序包重新解压。",503);
        await gate.WaitAsync(cancellationToken);
        var directory=Path.Combine(Path.GetTempPath(),"MercadoWorkbench-labels",Guid.NewGuid().ToString("N"));
        try
        {
            var source=Path.Combine(directory,"input");var destination=Path.Combine(directory,"output");
            Directory.CreateDirectory(source);Directory.CreateDirectory(destination);
            await using(var target=File.Create(Path.Combine(source,"input.pdf")))
            {await target.WriteAsync(header,cancellationToken);await input.CopyToAsync(target,cancellationToken);}
            var start=new ProcessStartInfo(engine){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=directory,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
            start.ArgumentList.Add(source);start.ArgumentList.Add(destination);
            start.Environment["PYTHONIOENCODING"]="utf-8";
            // Keep the frozen runtime inside this request too, so cancellation leaves no unpacked engine behind.
            start.Environment["TEMP"]=directory;start.Environment["TMP"]=directory;
            using var process=new Process{StartInfo=start};
            if(!process.Start())throw new AppException("无法启动标签剪裁组件，请重试。",503);
            var stdout=process.StandardOutput.ReadToEndAsync();var stderr=ReadErrorAsync(process.StandardError);
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);deadline.CancelAfter(TimeSpan.FromSeconds(120));
            try{await process.WaitForExitAsync(deadline.Token);}
            catch(OperationCanceledException)
            {
                try{if(!process.HasExited)process.Kill(entireProcessTree:true);await process.WaitForExitAsync(CancellationToken.None);}catch(InvalidOperationException){}
                await Task.WhenAll(stdout,stderr);
                cancellationToken.ThrowIfCancellationRequested();
                throw new AppException("剪裁超过 120 秒，已停止。请缩小文件后重试。",408);
            }
            var report=await stdout;var error=await stderr;
            if(process.ExitCode!=0)
            {
                if(error.Contains("仅支持单页 PDF"))throw new AppException("当前产品标签工具仅支持单页 PDF，请上传原始单页标签文件。",400);
                if(error.Contains("未找到标签边框"))throw new AppException("未找到标签边框，请确认上传的是原始产品标签 PDF。",400);
                if(carton)throw new AppException("箱唛无法剪裁：请使用原始A4横向三列PDF，确保每张箱唛边框完整、箱号无重复，且文件未加密。",400);
                logger.LogWarning("标签工具处理失败，退出码 {Code}",process.ExitCode);
                throw new AppException("PDF 无法剪裁，可能已损坏、加密或格式不受支持。",400);
            }
            var output=Path.Combine(destination,"input.pdf");var match=PageCount().Match(report);
            if(!File.Exists(output)||!match.Success||!int.TryParse(match.Groups[1].Value,out var labels)||labels<1)throw new AppException("剪裁结果不完整，请重新处理。",502);
            if(new FileInfo(output).Length>MaxOutputBytes)throw new AppException("剪裁结果超过 50 MB，请减少标签数量后重试。",400);
            var pdf=await File.ReadAllBytesAsync(output,cancellationToken);
            if(pdf.Length<5||!pdf.AsSpan(0,5).SequenceEqual("%PDF-"u8))throw new AppException("剪裁结果不是有效的 PDF。",502);
            var name=Path.GetFileNameWithoutExtension(file.FileName.Replace('\\','/'));
            name=string.Concat(name.Where(c=>!char.IsControl(c)&&c is not '/' and not '\\' and not '"')).Trim();
            if(name.Length>120)name=name[..120];
            return new(pdf,labels,$"{(name.Length==0?"产品标签":name)}-已剪裁.pdf");
        }
        finally
        {
            try{if(Directory.Exists(directory))Directory.Delete(directory,true);}catch(IOException){logger.LogWarning("标签临时文件暂未清理 {Directory}",directory);}catch(UnauthorizedAccessException){logger.LogWarning("标签临时目录无法清理 {Directory}",directory);}
            gate.Release();
        }
    }

    [GeneratedRegex(@":\s*(\d+)\s+pages\b",RegexOptions.CultureInvariant)]
    private static partial Regex PageCount();

    static async Task<string> ReadErrorAsync(StreamReader reader)
    {
        using var buffer=new MemoryStream();await reader.BaseStream.CopyToAsync(buffer);var bytes=buffer.ToArray();
        try{return new UTF8Encoding(false,true).GetString(bytes);}
        catch(DecoderFallbackException){Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);return Encoding.GetEncoding(936).GetString(bytes);}
    }
}
