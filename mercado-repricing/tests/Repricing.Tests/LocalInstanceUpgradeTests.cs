using System.Net;
using System.Net.Http.Json;
using Repricing.Api;
namespace Repricing.Tests;

public class LocalInstanceUpgradeTests
{
    [Theory]
    [InlineData("0.5.50")]
    [InlineData("0.5.51")]
    public async Task SameOrNewerInstanceIsOpenedWithoutShutdown(string existing)
    {
        using var handler=new InstanceHandler(existing);using var client=new HttpClient(handler){BaseAddress=new("http://127.0.0.1:5188")};
        var result=await LocalInstanceUpgrade.RequestAsync(client,new Version(0,5,50,0));
        Assert.Equal(ExistingInstanceAction.Open,result.Action);Assert.Equal(new[]{"/api/health"},handler.Paths);
    }
    [Fact] public async Task OlderIdleInstanceIsClosedThroughAuthenticatedSafeShutdown()
    {
        using var handler=new InstanceHandler("0.5.49");using var client=new HttpClient(handler){BaseAddress=new("http://127.0.0.1:5188")};
        var result=await LocalInstanceUpgrade.RequestAsync(client,new Version("0.5.50"));
        Assert.Equal(ExistingInstanceAction.Restart,result.Action);Assert.Equal(new[]{"/api/health","/api/session","/api/shutdown"},handler.Paths);
    }
    [Fact] public async Task RunningBatchPreventsAutomaticReplacement()
    {
        using var handler=new InstanceHandler("0.5.49",busy:true);using var client=new HttpClient(handler){BaseAddress=new("http://127.0.0.1:5188")};
        var result=await LocalInstanceUpgrade.RequestAsync(client,new Version("0.5.50"));
        Assert.Equal(ExistingInstanceAction.Busy,result.Action);
    }
    [Fact] public async Task AnotherApplicationOnThePortIsNeverStopped()
    {
        using var handler=new InstanceHandler("0.5.49",application:"OtherApp");using var client=new HttpClient(handler){BaseAddress=new("http://127.0.0.1:5188")};
        await Assert.ThrowsAsync<InvalidOperationException>(()=>LocalInstanceUpgrade.RequestAsync(client,new Version("0.5.50")));
        Assert.Equal(new[]{"/api/health"},handler.Paths);
    }
    sealed class InstanceHandler(string version,bool busy=false,string application="MercadoRepricing"):HttpMessageHandler
    {
        public List<string> Paths=[];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var path=request.RequestUri!.AbsolutePath;Paths.Add(path);
            if(path=="/api/health")return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=JsonContent.Create(new{application,version})});
            if(path=="/api/session")return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=JsonContent.Create(new{token="synthetic-local-session"})});
            Assert.Equal("/api/shutdown",path);Assert.Equal(HttpMethod.Post,request.Method);Assert.Equal("synthetic-local-session",Assert.Single(request.Headers.GetValues("X-Local-Token")));
            return Task.FromResult(new HttpResponseMessage(busy?HttpStatusCode.Conflict:HttpStatusCode.OK));
        }
    }
}
