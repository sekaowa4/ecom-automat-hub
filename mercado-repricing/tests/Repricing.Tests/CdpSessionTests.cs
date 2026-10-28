using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Repricing.Execution;
namespace Repricing.Tests;

public class CdpSessionTests
{
    [Fact] public async Task AbruptPageTransportFailureRequiresAnewSession()
    {
        var builder=WebApplication.CreateBuilder();builder.WebHost.UseUrls("http://127.0.0.1:0");await using var app=builder.Build();app.UseWebSockets();string origin="",url="";
        app.MapGet("/json/version",()=>new{webSocketDebuggerUrl=origin.Replace("http:","ws:")+"/browser"});
        app.MapGet("/json/list",()=>new[]{new{id="owned",webSocketDebuggerUrl=origin.Replace("http:","ws:")+"/page"}});
        async Task Handle(HttpContext context){using var ws=await context.WebSockets.AcceptWebSocketAsync();var buffer=new byte[16384];try{while(ws.State==WebSocketState.Open){var received=await ws.ReceiveAsync(new ArraySegment<byte>(buffer),default);if(received.MessageType==WebSocketMessageType.Close)return;using var doc=JsonDocument.Parse(buffer.AsMemory(0,received.Count));var root=doc.RootElement;var method=root.GetProperty("method").GetString();if(method=="Runtime.evaluate"){ws.Abort();return;}if(method=="Page.navigate")url=root.GetProperty("params").GetProperty("url").GetString()!;object result=method switch{"Target.createTarget"=>new{targetId="owned"},"Target.closeTarget"=>new{success=true},"Page.navigate"=>new{loaderId="one"},"Page.getFrameTree"=>new{frameTree=new{frame=new{url,loaderId=url==""?"initial":"one"}}},_=>new{}};await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new{id=root.GetProperty("id").GetInt32(),result}),WebSocketMessageType.Text,true,default);}}catch(WebSocketException){}}
        app.Map("/browser",Handle);app.Map("/page",Handle);await app.StartAsync();origin=app.Urls.Single();await using(var session=await ReadOnlyCdp.OpenAsync(new Uri(origin).Port,default)){var events=new List<VerificationUpdate>();session.VerificationReporter=events.Add;await Assert.ThrowsAnyAsync<Exception>(()=>session.ReadAsync(StorageTests.Input(),default));Assert.True(session.RequiresReconnect);Assert.Contains(events,e=>e.Phase=="connection-failed");await session.CancelAsync();Assert.Single(events,e=>e.Phase=="connection-failed");}await app.StopAsync();
    }
    [Fact] public async Task ManySkusReuseOneOwnedTargetAndCloseOnlyThatTarget()
    {
        var builder=WebApplication.CreateBuilder();builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app=builder.Build();app.UseWebSockets();string origin="",current="";int created=0,authFrames=0,autoConfirmed=0;bool authDuringEvaluation=false,interruptExitOpen=false,failNextFrame=false;var closed=new List<string>();var navigated=new List<string>();var exitActions=new List<string>();
        app.MapGet("/json/version",()=>new{webSocketDebuggerUrl=origin.Replace("http:","ws:")+"/browser"});
        app.MapGet("/json/list",()=>new[]{new{id="user-tab",webSocketDebuggerUrl="ws://127.0.0.1/never-connect"},new{id="owned-tab",webSocketDebuggerUrl=origin.Replace("http:","ws:")+"/page"}});
        async Task Handle(HttpContext context)
        {
            using var ws=await context.WebSockets.AcceptWebSocketAsync();var buffer=new byte[16384];
            try{while(ws.State==WebSocketState.Open)
            {
                using var message=new MemoryStream();WebSocketReceiveResult received;
                do{received=await ws.ReceiveAsync(new ArraySegment<byte>(buffer),default);if(received.MessageType==WebSocketMessageType.Close)return;message.Write(buffer,0,received.Count);}while(!received.EndOfMessage);
                using var doc=JsonDocument.Parse(message.ToArray());var root=doc.RootElement;var args=root.GetProperty("params");object result;
                switch(root.GetProperty("method").GetString())
                {
                    case "Target.createTarget":created++;result=new{targetId="owned-tab"};break;
                    case "Target.closeTarget":closed.Add(args.GetProperty("targetId").GetString()!);result=new{success=true};break;
                    case "Page.navigate":current=args.GetProperty("url").GetString()!;navigated.Add(current);if(current.Contains("SKU-AUTH"))authFrames=4;result=new{loaderId="fresh-"+navigated.Count};break;
                    case "Page.getFrameTree":if(failNextFrame){failNextFrame=false;await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new{id=root.GetProperty("id").GetInt32(),error=new{message="frame unavailable"}}),WebSocketMessageType.Text,true,default);continue;}var auth=authFrames>0;if(auth)authFrames--;result=new{frameTree=new{frame=new{loaderId=auth?"verification":"fresh-"+navigated.Count,url=auth?"https://vendedores.mercadolivre.com.br/totp/validation":current}}};break;
                    case "Runtime.evaluate":
                        var expression=args.GetProperty("expression").GetString()!;
                        if(expression.Contains("const filled=")){autoConfirmed++;result=new{result=new{value=true}};break;}
                        var exitAction=System.Text.RegularExpressions.Regex.Match(expression,"\"action\":\"([^\"]+)\"");
                        if(expression.StartsWith("(async()=>{try{await (",StringComparison.Ordinal)&&current.Contains("SKU-EXIT-AUTH")&&exitAction.Success)
                        {
                            var action=exitAction.Groups[1].Value;exitActions.Add(action);
                            if(action=="open"&&interruptExitOpen){interruptExitOpen=false;authFrames=4;await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new{id=root.GetProperty("id").GetInt32(),error=new{message="Execution context was destroyed"}}),WebSocketMessageType.Text,true,default);continue;}
                            result=new{result=new{value=new{ok=true}}};break;
                        }
                        if(authDuringEvaluation){authDuringEvaluation=false;authFrames=4;await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new{id=root.GetProperty("id").GetInt32(),error=new{message="Execution context was destroyed"}}),WebSocketMessageType.Text,true,default);continue;}
                        object[] products=current.Contains("SKU-EXIT-AUTH")?[new{itemId="456",sku="SKU-EXIT-AUTH",title="Product",basePriceText="R$ 100",promotions=new[]{new{id="C-1",name="09-10",status="激活",priceText="R$ 80",active=true,editable=true,attractive=true,joinable=false,netAmountText=(string?)null}}}]:[];
                        result=new{result=new{value=expression.StartsWith("(async()=>{try{await (",StringComparison.Ordinal)?(object)new{ok=false,error="活动编辑框加载超时"}:expression==ReadOnlyCdp.ExpandScript?(object)0:new{url=current,pageError=false,ready=true,incomplete=false,expandedGroups=0,products}}};break;
                    default:throw new InvalidOperationException("Unexpected command");
                }
                await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new{id=root.GetProperty("id").GetInt32(),result}),WebSocketMessageType.Text,true,default);
            }}catch(WebSocketException){}
        }
        app.Map("/browser",Handle);app.Map("/page",Handle);await app.StartAsync();origin=app.Urls.Single();
        var port=new Uri(origin).Port;
        await using(var session=await ReadOnlyCdp.OpenAsync(port,default))
        {
            var updates=new List<VerificationUpdate>();session.VerificationReporter=updates.Add;
            for(int i=0;i<25;i++)Assert.Empty(await session.ReadAsync(StorageTests.Input() with{Sku=$"SKU-{i}"},default));
            // A verification redirect that resolves automatically is now resumed by the existing TOTP flow.
            Assert.Empty(await session.ReadAsync(StorageTests.Input() with{Sku="SKU-AUTH"},default));Assert.Equal(0,authFrames);
            Assert.Equal(1,autoConfirmed);Assert.Contains(updates,x=>x.Phase=="entered"&&x.Operation=="读取促销活动");Assert.Contains(updates,x=>x.Phase=="confirmed");Assert.Contains(updates,x=>x.Phase=="passed");
            var plan=new PriceCutPlan(new("123","测试","MercadoLibre-巴西"),StorageTests.Input(),new("456","00123","商品","R$ 100",[new("C-1","活动","","R$ 80",true)]),"C-1",80,100);
            var error=await Assert.ThrowsAsync<AppException>(()=>session.PrepareExitAsync(plan,default));Assert.Contains("打开活动编辑框",error.Message);Assert.Contains("加载超时",error.Message);
            var exitInput=StorageTests.Input() with{Sku="SKU-EXIT-AUTH",ItemId="456",PromotionId="C-1",TargetPrice=90};var exitProduct=new ZiniaoProduct("456","SKU-EXIT-AUTH","Product","R$ 100",[new("C-1","09-10","激活","R$ 80",true,true)]);var exitPlan=new PriceCutPlan(new("123","测试","MercadoLibre-巴西"),exitInput,exitProduct,"C-1",80,100,Rejoin:true,Stage:"campaign-exit");Assert.Single(await session.ReadAsync(exitInput with{ItemId=null},default));
            interruptExitOpen=true;var exitError=await Record.ExceptionAsync(()=>session.PrepareExitAsync(exitPlan,default));Assert.True(exitError==null,$"{exitError?.Message}; current={current}; navigated={string.Join("|",navigated)}; actions={string.Join(",",exitActions)}");Assert.Equal(new[]{"open","open","check"},exitActions);Assert.Equal(2,autoConfirmed);Assert.Contains(updates,x=>x.Phase=="passed"&&x.Operation=="退出活动 open");
            authDuringEvaluation=true;await Assert.ThrowsAsync<AppException>(()=>session.Evaluate("true",default));Assert.Equal(0,authFrames);
            Assert.Equal(1,created);Assert.Empty(closed);Assert.Equal(28,navigated.Count);
            failNextFrame=true;var disconnected=await Assert.ThrowsAsync<AppException>(()=>session.Evaluate("true",default));Assert.Contains("连接中断",disconnected.Message);Assert.True(session.RequiresReconnect);Assert.Contains(updates,x=>x.Phase=="connection-failed");Assert.DoesNotContain(updates,x=>x.Phase=="failed"&&x.Message.Contains("验证状态读取失败"));await session.CancelAsync();
            session.ResetVerificationWait();authFrames=1000;using(var shortWait=new CancellationTokenSource(100)){await Assert.ThrowsAsync<VerificationRequiredException>(()=>session.Evaluate("true",shortWait.Token));}Assert.Contains(updates,x=>x.Phase=="failed"&&x.Message.Contains("操作超时"));await session.CancelAsync();authFrames=0;
        }
        Assert.Equal(new[]{"owned-tab"},closed);await app.StopAsync();
    }
}
