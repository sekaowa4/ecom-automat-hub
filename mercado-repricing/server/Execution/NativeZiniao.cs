using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Repricing.Execution;

public class NativeZiniao(IConfiguration config):IZiniaoPriceCutClient,IAsyncDisposable
{
    Action<VerificationUpdate>? verificationReporter;
    Action<ExecutionMetric>? executionReporter;
    public void SetExecutionReporter(Action<ExecutionMetric>? report)=>executionReporter=report;
    public void SetVerificationReporter(Action<VerificationUpdate>? report){verificationReporter=report;if(session!=null)session.VerificationReporter=report;}
    ReadOnlyCdp.Session? session;string? sessionStore;
    OperationTimer Track(string stage)=>new(this,stage);
    sealed class OperationTimer(NativeZiniao owner,string stage):IDisposable
    {
        readonly Stopwatch timer=Stopwatch.StartNew();
        readonly ReadOnlyCdp.Session? original=owner.session;
        readonly int commands=owner.session?.CommandCount??0,navigations=owner.session?.NavigationCount??0;
        public void Dispose()
        {
            var current=original??owner.session;
            try{owner.executionReporter?.Invoke(new(stage,timer.ElapsedMilliseconds,Math.Max(0,(current?.CommandCount??0)-commands),Math.Max(0,(current?.NavigationCount??0)-navigations)));}catch{/* Timing diagnostics must never affect a price submission. */}
        }
    }
    public string? Executable
    {
        get
        {
            var configured=config["Ziniao:Executable"];
            if(!string.IsNullOrWhiteSpace(configured))return Path.IsPathFullyQualified(configured)&&File.Exists(configured)?configured:null;
            var native=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"ziniaobrowser","cli","ziniao.exe");
            return File.Exists(native)?native:null;
        }
    }
    public async Task<ZiniaoStore[]> StoresAsync(CancellationToken ct)
    {
        var executable=Executable??throw new AppException("未找到紫鸟客户端自带命令，请安装并登录紫鸟。",409);
        using var doc=await Cli(executable,["browser","list","--all","--json"],ct);
        return doc.RootElement.GetProperty("data").GetProperty("items").EnumerateArray().Select(s=>new ZiniaoStore(s.GetProperty("storeId").GetString()!,s.GetProperty("storeName").GetString()!,s.GetProperty("platformName").GetString()??"")).ToArray();
    }
    public async Task<ZiniaoProduct[]> ReadAsync(ZiniaoStore store,PriceInput input,CancellationToken ct)
    {
        using var timing=Track("促销读取");
        await EnsureSessionAsync(store,ct);
        try{return await session!.ReadAsync(input,ct,expandMorePromotions:true);}catch(VerificationRequiredException){throw;}catch{await CloseAsync();throw;}
    }
    public async Task<ZiniaoProduct[]> ReadWeekendAsync(ZiniaoStore store,PriceInput input,CancellationToken ct)
    {
        using var timing=Track("周末活动读取");
        await EnsureSessionAsync(store,ct);
        try{return await session!.ReadWeekendAsync(input,ct);}catch(VerificationRequiredException){throw;}catch{await CloseAsync();throw;}
    }
    public async Task<ZiniaoProduct[]> ReadManagerAsync(ZiniaoStore store,PriceInput input,CancellationToken ct,bool reuseDocument=false)
    {
        using var timing=Track("商品管理读取");
        await EnsureSessionAsync(store,ct);
        try{return await session!.ReadManagerAsync(input,ct,reuseDocument);}catch(VerificationRequiredException){throw;}catch{await CloseAsync();throw;}
    }
    async Task EnsureSessionAsync(ZiniaoStore store,CancellationToken ct)
    {
        if(!Regex.IsMatch(store.Id,@"^\d{1,30}$"))throw new AppException("紫鸟返回的店铺 ID 格式无法识别。");
        if(sessionStore!=store.Id||session?.RequiresReconnect==true)await CloseAsync();
        if(session!=null)return;
        var executable=Executable??throw new AppException("紫鸟命令不可用。",409);
        // Agent startup is delegated to the client; no profile reset or authentication material is read.
        using var started=await Cli(executable,["browser","start","--id",store.Id,"--mode","agent","--json"],ct);
        var ports=await Ports(store.Id,ct);
        if(ports.Length!=1)throw new AppException("未能唯一确定该店铺的页面控制通道，请在紫鸟中检查店铺 Agent 状态后重试。",409);
        session=await ReadOnlyCdp.OpenAsync(ports[0],ct);session.VerificationReporter=verificationReporter;sessionStore=store.Id;
    }
    ReadOnlyCdp.Session For(PriceCutPlan plan)=>session!=null&&sessionStore==plan.Store.Id?session:throw new AppException("店铺工作标签已变化，禁止提交。",409);
    public async Task<decimal?> PreviewNetAsync(PriceCutPlan plan,CancellationToken ct){using var timing=Track("主列净收试算");return await For(plan).PreviewNetAsync(plan,ct);}
    async Task Timed(string stage,Func<Task> action){using var timing=Track(stage);await action();}
    public Task PrepareExitAsync(PriceCutPlan plan,CancellationToken ct)=>Timed("退出活动准备",()=>For(plan).PrepareExitAsync(plan,ct));
    public Task ConfirmExitAsync(PriceCutPlan plan,CancellationToken ct)=>Timed("退出活动确认",()=>For(plan).ConfirmExitAsync(plan,ct));
    public Task PrepareAsync(PriceCutPlan plan,CancellationToken ct)=>Timed("价格编辑准备",()=>For(plan).PrepareAsync(plan,ct));
    public Task SubmitAsync(PriceCutPlan plan,CancellationToken ct)=>Timed("价格保存",()=>For(plan).SubmitAsync(plan,ct));
    public Task PrepareBaseAsync(PriceCutPlan plan,CancellationToken ct)=>Timed("原价编辑准备",()=>For(plan).PrepareBaseAsync(plan,ct));
    public Task SubmitBaseAsync(PriceCutPlan plan,CancellationToken ct)=>Timed("原价保存",()=>For(plan).SubmitBaseAsync(plan,ct));
    public async Task<ZiniaoProduct> VerifyAsync(PriceCutPlan plan,CancellationToken ct)
    {
        using var timing=Track("最终价格核验");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(ExecutionTiming.Verify);ct=timeout.Token;
        var confirmation=new PriceReadbackConfirmation();
        for(int i=0;i<ExecutionTiming.VerifyReads;i++)
        {
            var confirmRefresh=confirmation.NeedsRefresh;
            await Task.Delay(confirmRefresh?250:ExecutionTiming.VerifyDelay(i),ct);
            var refresh=confirmRefresh||ExecutionTiming.RefreshVerification(i);
            var rows=(await (plan.DirectBaseOnly?ReadManagerAsync(plan.Store,plan.Input,ct,reuseDocument:!refresh):refresh?ReadAsync(plan.Store,plan.Input with{ItemId=null},ct):ReadVerificationDocument(plan,ct))).Where(p=>p.ItemId==plan.Product.ItemId&&(p.Sku==plan.Input.Sku||!plan.ChangeBase&&p.Sku=="")).ToArray();
            if(rows.Length!=1)throw new AppException("回读时商品未唯一匹配，保存结果未知。",409);
            var row=rows[0];if(ZiniaoReadOnlyExecutor.UnderReview(row))return row;var active=row.Promotions.Where(p=>p.Id==(plan.RejoinPromotionId??plan.PromotionId)&&p.Active).ToArray();
            var failure=PriceReadback.Failure(plan,row,plan.BasePrice,plan.RejoinPromotionId??plan.PromotionId,allowManaged:!plan.Rejoin);
            if(!plan.ChangeBase)
            {
                if(failure==null){if(confirmation.Observe(row,true,refresh))return row;continue;}
                confirmation.Observe(row,false,refresh);
            }
            if(i%4==3&&!plan.ChangeBase&&active.Length==1&&row.Promotions.Count(p=>p.Active)==1&&active[0].Id.StartsWith("C-",StringComparison.Ordinal)&&ZiniaoReadOnlyExecutor.Money(active[0].PriceText)!=plan.Input.TargetPrice)
            {
                var managed=await ReadManagerAsync(plan.Store,plan.Input with{ItemId=plan.Product.ItemId},ct);
                if(managed.Length==1&&PriceReadback.ReconcileManager(plan,row,managed[0]) is {} reconciled)
                {
                    var second=await ReadManagerAsync(plan.Store,plan.Input with{ItemId=plan.Product.ItemId},ct);
                    if(second.Length==1&&PriceReadback.ReconcileManager(plan,row,second[0]) is {} fresh&&PriceReadback.SameSnapshot(reconciled,fresh))return fresh;
                }
            }
            if(plan.DiscountBase is decimal adjusted)
            {
                var actualBase=ZiniaoReadOnlyExecutor.Money(row.BasePriceText);
                if(actualBase==adjusted&&plan.Stage=="discount-join"&&row.Promotions.Where(p=>p.Active).ToArray() is [var joined]&&joined.Id==plan.DiscountPromotionId&&ZiniaoReadOnlyExecutor.Money(joined.PriceText)==plan.Input.TargetPrice)
                {if(confirmation.Observe(row,true,refresh))return row;continue;}
                if(actualBase==adjusted&&plan.Stage=="discount-base"&&!row.Promotions.Any(p=>p.Active)&&await For(plan).VerifyBaseAsync(plan,ct))
                {if(confirmation.Observe(row,true,refresh))return row;continue;}
                if(actualBase!=plan.BasePrice&&actualBase!=adjusted)return row;
                confirmation.Observe(row,false,refresh);
                continue;
            }
            if(plan.ChangeBase)
            {
                if(ZiniaoReadOnlyExecutor.Money(row.BasePriceText)==plan.Input.TargetPrice&&!row.Promotions.Any(p=>p.Active)&&await For(plan).VerifyBaseAsync(plan,ct))
                {if(confirmation.Observe(row,true,refresh))return row;continue;}
                if(ZiniaoReadOnlyExecutor.Money(row.BasePriceText)!=plan.BasePrice&&ZiniaoReadOnlyExecutor.Money(row.BasePriceText)!=plan.Input.TargetPrice)return row;
                confirmation.Observe(row,false,refresh);continue;
            }
            if(ZiniaoReadOnlyExecutor.Money(row.BasePriceText)!=plan.BasePrice)return row;
        }
        throw new AppException("等待平台刷新后仍未读到目标价；不自动重复保存。",409);
    }
    async Task<ZiniaoProduct[]> ReadVerificationDocument(PriceCutPlan plan,CancellationToken ct)
    {
        using var timing=Track("同文档核验轮询");
        await EnsureSessionAsync(plan.Store,ct);
        try{return await session!.ReadAsync(plan.Input with{ItemId=null},ct,reuseDocument:true);}catch(VerificationRequiredException){throw;}catch{await CloseAsync();throw;}
    }
    public Task CloseEditorAsync()=>session?.CancelAsync()??Task.CompletedTask;
    public async Task CloseAsync(){var previous=session;session=null;sessionStore=null;if(previous!=null)await previous.DisposeAsync();}
    public async ValueTask DisposeAsync()=>await CloseAsync();
    static async Task<JsonDocument> Cli(string executable,string[] args,CancellationToken ct)
    {
        var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
        foreach(var arg in args)start.ArgumentList.Add(arg);
        var output=await Run(start,TimeSpan.FromSeconds(100),ct);
        foreach(var line in output.Split('\n').Reverse())
        {
            if(!line.TrimStart().StartsWith('{'))continue;
            JsonDocument doc;try{doc=JsonDocument.Parse(line);}catch(JsonException){continue;}
            if(!doc.RootElement.TryGetProperty("ok",out var ok)){doc.Dispose();continue;}
            if(ok.ValueKind==JsonValueKind.True)return doc;
            doc.Dispose();throw new AppException("紫鸟未完成命令，请检查客户端登录、店铺权限或 Agent 启动状态；未提交业务变更。",409);
        }
        throw new AppException("紫鸟命令未返回可识别的结果，请检查客户端版本。",409);
    }
    static async Task<int[]> Ports(string storeId,CancellationToken ct)
    {
        const string script="""
        $ErrorActionPreference='Stop'
        $taskId=$env:REPRICING_STORE_ID
        $taskProcesses=@(Get-CimInstance Win32_Process -Filter "Name='ziniaobrowser.exe'" | Where-Object { $_.CommandLine -notmatch '--type=' -and $_.CommandLine -match ('(?<!\d)'+[regex]::Escape($taskId)+'(?!\d)') })
        $taskPids=@($taskProcesses.ProcessId)
        # netstat avoids the slow system-wide NetTCPConnection CIM provider.
        $taskTable=(& "$env:WINDIR\System32\netstat.exe" -ano -p TCP) -join "`n"
        if($LASTEXITCODE -ne 0){throw 'Local TCP listener discovery failed'}
        @{processIds=$taskPids;table=$taskTable} | ConvertTo-Json -Compress
        """;
        var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","WindowsPowerShell","v1.0","powershell.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8};
        foreach(var arg in new[]{"-NoProfile","-NonInteractive","-EncodedCommand",Convert.ToBase64String(Encoding.Unicode.GetBytes(script))})start.ArgumentList.Add(arg);
        start.Environment["REPRICING_STORE_ID"]=storeId;
        var text=await Run(start,TimeSpan.FromSeconds(20),ct);
        using var snapshot=JsonDocument.Parse(text);
        var candidates=ZiniaoPortDiscovery.Parse(snapshot.RootElement.GetProperty("table").GetString()??"",snapshot.RootElement.GetProperty("processIds").EnumerateArray().Select(p=>p.GetInt32()));var result=new List<int>();
        using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(2)};
        foreach(var port in candidates.Where(p=>p is >0 and <=65535))
        {
            try{using var doc=JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/version",ct));if(doc.RootElement.TryGetProperty("webSocketDebuggerUrl",out _))result.Add(port);}
            catch(Exception e)when(e is HttpRequestException or JsonException or OperationCanceledException){if(ct.IsCancellationRequested)throw;}
        }
        return result.ToArray();
    }
    static async Task<string> Run(ProcessStartInfo start,TimeSpan limit,CancellationToken ct)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(limit);
        using var process=new Process{StartInfo=start};var running=false;
        try
        {
            process.Start();running=true;var stdout=process.StandardOutput.ReadToEndAsync(timeout.Token);var stderr=process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);await Task.WhenAll(stdout,stderr);
            if(process.ExitCode!=0)throw new AppException("紫鸟本地命令执行失败，请检查客户端和系统权限。",409);
            if(stdout.Result.Length>2_000_000)throw new AppException("紫鸟返回数据超出读取上限。");
            return stdout.Result;
        }
        catch(System.ComponentModel.Win32Exception){throw new AppException("无法运行紫鸟命令，请检查安装位置与权限。",409);}
        finally{if(running&&!process.HasExited)process.Kill(entireProcessTree:true);}
    }
}
