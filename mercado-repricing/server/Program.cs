using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Http.Features;
using Repricing.Api;
using Repricing.Execution;
using Repricing.Import;
using Repricing.Jobs;
using Repricing.Storage;

namespace Repricing;
public partial class Program
{
    const string Address="http://127.0.0.1:5188";
    public static async Task Main(string[] args)
    {
        try{await Run(args);}
        catch(Exception e)
        {
            var dir=Environment.GetEnvironmentVariable("DataDirectory")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MercadoRepricing");
            try{Directory.CreateDirectory(dir);File.AppendAllText(Path.Combine(dir,"startup.log"),$"{DateTimeOffset.Now:O}\n{e}\n");}catch{}
            if(OperatingSystem.IsWindows())MessageBoxW(IntPtr.Zero,"程序启动失败。请检查是否有其他程序占用 5188 端口，并查看本机数据目录中的 startup.log。","美客多工作台",0x10);
            Environment.ExitCode=1;
        }
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int MessageBoxW(IntPtr window,string text,string caption,uint type);
    static async Task Run(string[] args)
    {
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions{Args=args,ContentRootPath=AppContext.BaseDirectory,WebRootPath="wwwroot"});
        var testing=builder.Environment.IsEnvironment("Testing");Mutex? instance=null;
        builder.Logging.ClearProviders();builder.Logging.AddSimpleConsole();
        builder.Logging.AddProvider(new FileLogProvider(Path.Combine(builder.Configuration["DataDirectory"]??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MercadoRepricing"),"logs")));
        if(!testing)
        {
            instance=new Mutex(false,@"Local\MercadoRepricing-"+Environment.UserName,out var created);
            if(!created){await OpenExisting(args);instance.Dispose();return;}
            builder.WebHost.UseUrls(Address);
        }
        builder.Services.ConfigureHttpJsonOptions(o=>o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.Configure<FormOptions>(o=>{o.MultipartBodyLengthLimit=11*1024*1024;o.MemoryBufferThreshold=11*1024*1024;});
        builder.WebHost.ConfigureKestrel(o=>o.Limits.MaxRequestBodySize=11*1024*1024);
        builder.Services.AddSingleton<LocalSession>();builder.Services.AddSingleton<PriceValidator>();builder.Services.AddSingleton<WorkbookReader>();builder.Services.AddSingleton<ImportSessionStore>();
        builder.Services.AddSingleton(sp=>new JobStore(sp.GetRequiredService<IConfiguration>()["DataDirectory"]??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MercadoRepricing")));
        builder.Services.AddSingleton<IPriceExecutor>(sp=>new SimulationExecutor(sp.GetRequiredService<IConfiguration>().GetValue("SimulationDelayMs",650)));
        builder.Services.AddSingleton<NativeZiniao>();builder.Services.AddSingleton<IZiniaoReader>(sp=>sp.GetRequiredService<NativeZiniao>());builder.Services.AddSingleton<ZiniaoReadOnlyExecutor>();
        builder.Services.AddSingleton<IZiniaoPriceCutClient>(sp=>sp.GetRequiredService<NativeZiniao>());builder.Services.AddSingleton<ZiniaoPriceCutExecutor>();
        builder.Services.AddSingleton<JobRunner>();builder.Services.AddHostedService(sp=>sp.GetRequiredService<JobRunner>());builder.Services.AddSingleton<CliDiagnostics>();
        if(OperatingSystem.IsWindows()&&!testing)builder.Services.AddHostedService<VerificationNotifier>();
        builder.Services.AddSingleton<Repricing.Labels.ProductLabelCropper>();
        builder.Services.AddSingleton<Repricing.Dashboard.UpSellerDashboard>();
        var app=builder.Build();
        app.Use(async(ctx,next)=>
        {
            try{await next();}
            catch(Exception e)when(e is not OperationCanceledException)
            {
                if(ctx.Response.HasStarted)throw;
                var status=e switch{AppException a=>a.Status,InvalidDataException=>400,BadHttpRequestException b=>b.StatusCode,_=>500};
                if(status==500)app.Logger.LogError(e,"请求处理异常");
                ctx.Response.Clear();await Results.Problem(statusCode:status,title:status==500?"程序处理失败，请重试或查看日志。":e.Message).ExecuteAsync(ctx);
            }
        });
        app.UseMiddleware<LocalAccessMiddleware>();
        app.MapGet("/api/health",()=>new{application="MercadoRepricing",version=typeof(Program).Assembly.GetName().Version?.ToString(3),mode="PriceCutReadOnlyAndSimulation"});
        app.MapGet("/api/session",(LocalSession session)=>new{token=session.Token});
        app.MapGet("/api/environment",(CliDiagnostics cli)=>new{service="本地服务运行中",liveEnabled=false,priceCutEnabled=true,readOnlyEnabled=true,cli=cli.Current});
        app.MapPost("/api/environment/check",async(CliDiagnostics cli,CancellationToken ct)=>await cli.CheckAsync(ct));
        app.MapGet("/api/ziniao/stores",async(IZiniaoReader ziniao,CancellationToken ct)=>await ziniao.StoresAsync(ct));
        app.MapImports();app.MapJobs();app.MapLabels();app.MapDashboard();app.UseDefaultFiles();app.UseStaticFiles();
        app.MapFallback(async ctx=>{if(ctx.Request.Path.StartsWithSegments("/api")){ctx.Response.StatusCode=404;return;}var file=Path.Combine(app.Environment.WebRootPath??Path.Combine(AppContext.BaseDirectory,"wwwroot"),"index.html");if(File.Exists(file)){ctx.Response.ContentType="text/html; charset=utf-8";await ctx.Response.SendFileAsync(file);}else{ctx.Response.StatusCode=503;await ctx.Response.WriteAsync("网页尚未构建，请先运行前端构建。");}});
        if(!testing && !args.Contains("--no-browser"))app.Lifetime.ApplicationStarted.Register(OpenBrowser);
        try{await app.RunAsync();}finally{instance?.Dispose();}
    }
    static void OpenBrowser(){try{Process.Start(new ProcessStartInfo(Address){UseShellExecute=true});}catch{}}
    static async Task OpenExisting(string[] args)
    {
        using var client=new HttpClient{Timeout=TimeSpan.FromMilliseconds(800)};
        for(int i=0;i<10;i++){try{var text=await client.GetStringAsync(Address+"/api/health");if(text.Contains("MercadoRepricing")){if(!args.Contains("--no-browser"))OpenBrowser();return;}}catch{}await Task.Delay(400);}
        Console.Error.WriteLine("程序已在启动中或本地端口不可用，请稍后重试。");
    }
}
