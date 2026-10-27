using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Repricing.Execution;
using Xunit.Abstractions;
namespace Repricing.Tests;

public class CdpPerformanceTests(ITestOutputHelper output)
{
    [Fact] public async Task ReadyAndDelayedReadsUseBoundedCommandsWithoutDroppingFreshNavigation()
    {
        await using var fixture=await CdpFixture.Start();
        await using var session=await ReadOnlyCdp.OpenAsync(fixture.Port,default);
        var started=Stopwatch.StartNew();
        for(var i=0;i<10;i++)Assert.Empty(await session.ReadAsync(StorageTests.Input() with{Sku="FAST-"+i},default));
        var ready=started.ElapsedMilliseconds;var readyCommands=fixture.PageCommands;
        output.WriteLine($"PERF ready: 10 SKU, {ready}ms, {readyCommands} CDP commands, {fixture.Navigations} navigations");
        fixture.PendingSnapshots=2;started.Restart();Assert.Empty(await session.ReadAsync(StorageTests.Input() with{Sku="DELAYED"},default));
        output.WriteLine($"PERF delayed: 2 incomplete snapshots, {started.ElapsedMilliseconds}ms");
        Assert.Equal(11,fixture.Navigations);
        Assert.True(readyCommands<=60,$"Ready pages used {readyCommands} commands; expected at most 60.");
        Assert.True(started.ElapsedMilliseconds<1200,"Two short DOM readiness changes should not require 1.4 seconds of fixed sleep.");
    }
    [Fact] public async Task ReuseRequiresSameSkuSameLoaderAndNoOpenEditor()
    {
        await using var fixture=await CdpFixture.Start();await using var session=await ReadOnlyCdp.OpenAsync(fixture.Port,default);
        var a=StorageTests.Input() with{Sku="SKU-A"};var b=a with{Sku="SKU-B"};
        await session.ReadAsync(a,default);await session.ReadAsync(a,default,reuseDocument:true);Assert.Equal(1,fixture.Navigations);
        await session.ReadAsync(b,default,reuseDocument:true);Assert.Equal(2,fixture.Navigations);
        fixture.ChangeLoader();await session.ReadAsync(b,default,reuseDocument:true);Assert.Equal(3,fixture.Navigations);
        fixture.EditorOpen=true;var e=await Assert.ThrowsAsync<AppException>(()=>session.ReadAsync(b,default,reuseDocument:true));Assert.Contains("编辑框",e.Message);Assert.Equal(3,fixture.Navigations);
    }
    [Fact] public async Task NativePromotionReadsExpandMoreActivitiesForTheRequestedSku()
    {
        await using var fixture=await CdpFixture.Start();fixture.ShowProduct=true;await using var session=await ReadOnlyCdp.OpenAsync(fixture.Port,default);
        var client=new NativeZiniao(new ConfigurationBuilder().Build());
        typeof(NativeZiniao).GetField("session",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(client,session);
        typeof(NativeZiniao).GetField("sessionStore",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(client,"123");
        var input=StorageTests.Input() with{Sku="00123",ItemId="456"};
        Assert.Single(await client.ReadAsync(new("123","测试","MercadoLibre-巴西"),input,default));
        Assert.Equal(1,fixture.MorePromotionExpandCalls);
        Assert.Contains("\"sku\":\"00123\"",fixture.MorePromotionExpandExpression,StringComparison.Ordinal);
        Assert.Contains("\"itemId\":\"456\"",fixture.MorePromotionExpandExpression,StringComparison.Ordinal);
    }
    [Fact] public async Task SameUrlFreshReadDoesNotAdoptThePreviousDocumentsLoaderBeforeCommit()
    {
        await using var fixture=await CdpFixture.Start();await using var session=await ReadOnlyCdp.OpenAsync(fixture.Port,default);
        var input=StorageTests.Input() with{Sku="SAME-SEARCH"};await session.ReadAsync(input,default);
        fixture.StaleFramesAfterNavigate=6;await session.ReadAsync(input,default);
        Assert.Equal(0,fixture.StaleSnapshots);Assert.Equal(2,fixture.Navigations);
    }
    [Fact] public async Task SameDocumentNavigationRequiresAnExplicitReloadBeforeFreshRead()
    {
        await using var fixture=await CdpFixture.Start();await using var session=await ReadOnlyCdp.OpenAsync(fixture.Port,default);
        var input=StorageTests.Input();await session.ReadAsync(input,default);
        fixture.SameDocumentNavigation=true;await session.ReadAsync(input,default);
        Assert.Equal(1,fixture.Reloads);Assert.Equal(3,session.NavigationCount);Assert.Equal(0,fixture.StaleSnapshots);
    }
    [Fact] public async Task ProductGroupExpansionIsNotClickedRepeatedlyWhileWaitingForChildRows()
    {
        await using var fixture=await CdpFixture.Start();fixture.ExpandableGroups=1;await using var session=await ReadOnlyCdp.OpenAsync(fixture.Port,default);
        using var cancel=new CancellationTokenSource(TimeSpan.FromMilliseconds(2600));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>session.ReadAsync(StorageTests.Input() with{Sku="GROUP-STUCK"},cancel.Token));
        Assert.Equal(1,fixture.ExpandCalls);Assert.Equal(1,fixture.Navigations);
    }
    [Fact] public async Task UnresponsiveProductGroupExpansionReturnsANonSubmitReviewPromptly()
    {
        await using var fixture=await CdpFixture.Start();fixture.ExpandableGroups=1;await using var session=await ReadOnlyCdp.OpenAsync(fixture.Port,default);
        var timer=Stopwatch.StartNew();var error=await Assert.ThrowsAsync<AppException>(()=>session.ReadAsync(StorageTests.Input() with{Sku="GROUP-STUCK"},default));
        Assert.Contains("分组展开未响应",error.Message);Assert.Contains("未提交价格",error.Message);Assert.Equal(1,fixture.ExpandCalls);Assert.Equal(1,fixture.Navigations);Assert.InRange(timer.ElapsedMilliseconds,9000,15000);
    }
    [Fact] public async Task DelayedProductGroupExpansionContinuesAfterTheSingleExpandClick()
    {
        await using var fixture=await CdpFixture.Start();fixture.ShowProduct=true;fixture.ExpandableGroups=1;fixture.ExpandAfterSnapshots=3;await using var session=await ReadOnlyCdp.OpenAsync(fixture.Port,default);
        var products=await session.ReadAsync(StorageTests.Input() with{Sku="00123"},default);
        Assert.Single(products);Assert.Equal(1,fixture.ExpandCalls);Assert.Equal(1,fixture.Navigations);
    }
    [Theory][InlineData(false)][InlineData(true)] public async Task VerificationReloadsOnceThenObservesDelayedDomWithoutResubmitting(bool delayed)
    {
        await using var fixture=await CdpFixture.Start();await using var session=await ReadOnlyCdp.OpenAsync(fixture.Port,default);
        fixture.ShowProduct=true;var input=StorageTests.Input() with{Sku="00123",TargetPrice=90};
        await session.ReadAsync(input,default);
        var client=new NativeZiniao(new ConfigurationBuilder().Build());
        var metrics=new List<ExecutionMetric>();client.SetExecutionReporter(metrics.Add);
        typeof(NativeZiniao).GetField("session",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(client,session);
        typeof(NativeZiniao).GetField("sessionStore",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(client,"123");
        if(delayed)fixture.UpdatePriceAtSnapshot=4;else fixture.ActivePrice=90;
        var plan=new PriceCutPlan(new("123","测试","MercadoLibre-巴西"),input,new("456","00123","商品","R$ 100",[new("C-1","活动","激活","R$ 80",true)]),"C-1",80,100);
        var timer=Stopwatch.StartNew();var actual=await client.VerifyAsync(plan,default);
        Assert.Equal("R$ 90",actual.Promotions.Single().PriceText);Assert.Equal(2,fixture.Navigations);
        Assert.InRange(timer.ElapsedMilliseconds,2500,5500);
        var finalMetric=Assert.Single(metrics,x=>x.Stage=="最终价格核验");Assert.Equal(1,finalMetric.Navigations);Assert.True(finalMetric.Commands>0);
        output.WriteLine($"PERF verification delayed={delayed}: {timer.ElapsedMilliseconds}ms, total navigations={fixture.Navigations}, snapshots={fixture.Snapshots}");
        client.SetExecutionReporter(_=>throw new IOException("diagnostic sink unavailable"));Assert.Equal("R$ 90",(await client.VerifyAsync(plan,default)).Promotions.Single().PriceText);
    }
    [Fact] public async Task DirectBaseVerificationRechecksLegacySkuIdentityPeriodically()
    {
        await using var fixture=await CdpFixture.Start();fixture.ShowProduct=true;fixture.LegacyProduct=true;await using var session=await ReadOnlyCdp.OpenAsync(fixture.Port,default);
        var input=StorageTests.Input() with{Sku="00123",TargetPrice=90};
        var client=new NativeZiniao(new ConfigurationBuilder().Build());
        typeof(NativeZiniao).GetField("session",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(client,session);
        typeof(NativeZiniao).GetField("sessionStore",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(client,"123");
        var product=new ZiniaoProduct("456","00123","商品","R$ 100",[]);
        var plan=new PriceCutPlan(new("123","测试","MercadoLibre-巴西"),input,product,"",100,100,ChangeBase:true,Stage:"base-save",DirectBaseOnly:true);
        using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>client.VerifyAsync(plan,cancel.Token));
        Assert.Equal(5,fixture.Navigations);Assert.Equal(5,fixture.ManagerReads);Assert.Equal(2,fixture.LegacyIdentityReads);
    }
    sealed class CdpFixture:IAsyncDisposable
    {
        readonly WebApplication app;string origin="",url="about:blank",oldUrl="";int loader,oldLoader,staleFrames;bool staleVisible;
        public int Port=>new Uri(origin).Port;
        public int PageCommands,Navigations,PendingSnapshots,Snapshots,UpdatePriceAtSnapshot,ManagerReads,LegacyIdentityReads,ExpandableGroups,ExpandedGroups,ExpandCalls,ExpandAfterSnapshots,MorePromotionExpandCalls;
        public string? MorePromotionExpandExpression;
        public bool EditorOpen,ShowProduct,LegacyProduct,SameDocumentNavigation;public decimal ActivePrice=80;public int Reloads;
        public int StaleFramesAfterNavigate,StaleSnapshots;
        public void ChangeLoader()=>loader++;
        CdpFixture(WebApplication app){this.app=app;}
        public static async Task<CdpFixture> Start()
        {
            var b=WebApplication.CreateBuilder();b.WebHost.UseUrls("http://127.0.0.1:0");b.Logging.ClearProviders();var app=b.Build();app.UseWebSockets();var f=new CdpFixture(app);
            app.MapGet("/json/version",()=>new{webSocketDebuggerUrl=f.origin.Replace("http:","ws:")+"/browser"});
            app.MapGet("/json/list",()=>new[]{new{id="owned",webSocketDebuggerUrl=f.origin.Replace("http:","ws:")+"/page"}});
            app.Map("/browser",c=>f.Handle(c,false));app.Map("/page",c=>f.Handle(c,true));await app.StartAsync();f.origin=app.Urls.Single();return f;
        }
        async Task Handle(HttpContext context,bool page)
        {
            using var ws=await context.WebSockets.AcceptWebSocketAsync();var buffer=new byte[32768];
            try{while(ws.State==WebSocketState.Open){using var message=new MemoryStream();WebSocketReceiveResult part;do{part=await ws.ReceiveAsync(new ArraySegment<byte>(buffer),default);if(part.MessageType==WebSocketMessageType.Close)return;message.Write(buffer,0,part.Count);}while(!part.EndOfMessage);
                using var doc=JsonDocument.Parse(message.ToArray());var root=doc.RootElement;var args=root.GetProperty("params");object result;
                if(page)PageCommands++;
                switch(root.GetProperty("method").GetString())
                {
                    case "Target.createTarget":result=new{targetId="owned"};break;
                    case "Target.closeTarget":result=new{success=true};break;
                    case "Page.navigate":oldUrl=url;oldLoader=loader;url=args.GetProperty("url").GetString()!;Navigations++;if(SameDocumentNavigation){result=new{};break;}loader++;staleFrames=StaleFramesAfterNavigate;staleVisible=staleFrames>0;result=new{loaderId=loader.ToString()};break;
                    case "Page.reload":loader++;Reloads++;result=new{};break;
                    case "Page.getFrameTree":if(staleFrames>0){staleFrames--;result=new{frameTree=new{frame=new{url=oldUrl,loaderId=oldLoader.ToString()}}};}else{staleVisible=false;result=new{frameTree=new{frame=new{url,loaderId=loader.ToString()}}};}break;
                    case "Runtime.evaluate":var script=args.GetProperty("expression").GetString();if(script!.StartsWith("("+ReadOnlyCdp.WeekendPromotionExpandScript+")(",StringComparison.Ordinal)){MorePromotionExpandCalls++;MorePromotionExpandExpression=script;result=new{result=new{value=0}};break;}var pending=PendingSnapshots>0&&script==ReadOnlyCdp.SnapshotScript;if(pending)PendingSnapshots--;if(script==ReadOnlyCdp.SnapshotScript){Snapshots++;if(staleVisible)StaleSnapshots++;if(ExpandAfterSnapshots>0&&Snapshots>=ExpandAfterSnapshots)ExpandedGroups=ExpandableGroups;}if(UpdatePriceAtSnapshot>0&&Snapshots>=UpdatePriceAtSnapshot)ActivePrice=90;
                        if(script==ReadOnlyCdp.ExpandScript){ExpandCalls++;result=new{result=new{value=ExpandableGroups}};break;}
                        if(script!.Contains("\"action\":\"legacy\"",StringComparison.Ordinal)){LegacyIdentityReads++;result=new{result=new{value=new{ready=true,variants=new[]{new{sku="00123",label="Rosa",stock="3",readable=true}}}}};break;}if(script.Contains("\"action\":\"manager\"",StringComparison.Ordinal)){ManagerReads++;object[] managed=ShowProduct?(LegacyProduct?[new{itemId="456",sku="",title="商品",basePriceText="R$ 100",editorLink="https://vendedores.mercadolivre.com.br/anuncios/MLB456/modificar/bomni",promotions=Array.Empty<object>()}]:[new{itemId="456",sku="00123",title="商品",basePriceText="R$ 100",promotions=Array.Empty<object>()}]):[];result=new{result=new{value=new{ready=true,products=managed}}};break;}object[] products=ShowProduct?[new{itemId="456",sku="00123",title="商品",basePriceText="R$ 100",promotions=new[]{new{id="C-1",name="活动",status="激活",priceText="R$ "+ActivePrice,active=true}}}]:[];
                        result=new{result=new{value=new{url,pageError=false,ready=!pending,incomplete=false,expandedGroups=ExpandedGroups,expandableGroups=ExpandableGroups,editorOpen=EditorOpen,products}}};break;
                    default:throw new InvalidOperationException("Unexpected CDP method");
                }
                await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new{id=root.GetProperty("id").GetInt32(),result}),WebSocketMessageType.Text,true,default);
            }}catch(WebSocketException){}
        }
        public async ValueTask DisposeAsync(){await app.StopAsync();await app.DisposeAsync();}
    }
}
