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
        await using var app=builder.Build();app.UseWebSockets();string origin="",current="";int created=0,authFrames=0,autoConfirmed=0;bool authDuringEvaluation=false,failNextFrame=false;var closed=new List<string>();var navigated=new List<string>();
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
                        if(args.GetProperty("expression").GetString()!.Contains("const filled=")){autoConfirmed++;result=new{result=new{value=true}};break;}
                        if(authDuringEvaluation){authDuringEvaluation=false;authFrames=4;await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new{id=root.GetProperty("id").GetInt32(),error=new{message="Execution context was destroyed"}}),WebSocketMessageType.Text,true,default);continue;}
                        result=new{result=new{value=args.GetProperty("expression").GetString()!.StartsWith("(async()=>{try{await (")?(object)new{ok=false,error="活动编辑框加载超时"}:args.GetProperty("expression").GetString()==ReadOnlyCdp.ExpandScript?(object)0:new{url=current,pageError=false,ready=true,incomplete=false,expandedGroups=0,products=Array.Empty<object>()}}};break;
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
            authDuringEvaluation=true;await Assert.ThrowsAsync<AppException>(()=>session.Evaluate("true",default));Assert.Equal(0,authFrames);
            Assert.Equal(1,created);Assert.Empty(closed);Assert.Equal(26,navigated.Count);
            failNextFrame=true;var disconnected=await Assert.ThrowsAsync<AppException>(()=>session.Evaluate("true",default));Assert.Contains("连接中断",disconnected.Message);Assert.True(session.RequiresReconnect);Assert.Contains(updates,x=>x.Phase=="connection-failed");Assert.DoesNotContain(updates,x=>x.Phase=="failed"&&x.Message.Contains("验证状态读取失败"));await session.CancelAsync();
            session.ResetVerificationWait();authFrames=1000;using(var shortWait=new CancellationTokenSource(100)){await Assert.ThrowsAsync<VerificationRequiredException>(()=>session.Evaluate("true",shortWait.Token));}Assert.Contains(updates,x=>x.Phase=="failed"&&x.Message.Contains("操作超时"));await session.CancelAsync();authFrames=0;
        }
        Assert.Equal(new[]{"owned-tab"},closed);await app.StopAsync();
    }
}
