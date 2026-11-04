using System.Net;
using Repricing.Dashboard;
namespace Repricing.Tests;
public class ExchangeRateTests
{
    sealed class FallbackHandler:HttpMessageHandler
    {
        public int BackupCalls;public bool FailPrimary=true;
        public List<HttpRequestMessage> Requests=new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Requests.Add(request);
            if(request.RequestUri!.Host=="open.er-api.com")
            {
                BackupCalls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("""{"result":"success","base_code":"BRL","time_last_update_unix":1793649933,"rates":{"CNY":1.25}}""")});
            }
            return Task.FromResult(new HttpResponseMessage(FailPrimary?HttpStatusCode.BadGateway:HttpStatusCode.OK){Content=new StringContent("""{"CNYBRL":{"code":"CNY","codein":"BRL","bid":"0.7","ask":"0.72","timestamp":"1793640000"}}""")});
        }
    }
    [Theory][InlineData(true)][InlineData(false)]
    public async Task FailureOrOldPrimaryUsesNewerDailyBackupWithHourlyCache(bool fail)
    {
        var handler=new FallbackHandler{FailPrimary=fail};var clock=new Clock();var service=new ExchangeRates(new HttpClient(handler),clock);
        var quote=await service.ReadAsync(default);
        Assert.Equal("ExchangeRate-API",quote.Source);Assert.Equal(1.25m,quote.BrlToCny);Assert.Equal(.8m,quote.CnyToBrl);Assert.False(quote.Stale);
        clock.Now+=TimeSpan.FromMinutes(2);await service.ReadAsync(default);Assert.Equal(1,handler.BackupCalls);
        Assert.True(handler.Requests[0].Headers.CacheControl?.NoCache);
    }
    sealed class Handler:HttpMessageHandler
    {
        public int Calls;public bool Fail;public string Body="""{"CNYBRL":{"code":"CNY","codein":"BRL","bid":"0.8","ask":"0.82","timestamp":"1793649933"}}""";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){Calls++;return Task.FromResult(new HttpResponseMessage(Fail?HttpStatusCode.BadGateway:HttpStatusCode.OK){Content=new StringContent(Body)});}
    }
    sealed class Clock:TimeProvider{public DateTimeOffset Now=DateTimeOffset.FromUnixTimeSeconds(1793649933);public override DateTimeOffset GetUtcNow()=>Now;}
    [Fact] public async Task UsesMidpointAndReciprocalAndCachesConcurrentRequests()
    {
        var handler=new Handler();var clock=new Clock();var service=new ExchangeRates(new HttpClient(handler),clock);
        var rows=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>service.ReadAsync(default)));
        Assert.Equal(1,handler.Calls);Assert.Equal(.81m,rows[0].CnyToBrl);Assert.Equal(1m/.81m,rows[0].BrlToCny);Assert.False(rows[0].Stale);
    }
    [Fact] public async Task FailedRefreshRetainsOldQuoteAndDoesNotPretendItIsCurrent()
    {
        var handler=new Handler();var clock=new Clock();var service=new ExchangeRates(new HttpClient(handler),clock);var first=await service.ReadAsync(default);
        clock.Now+=TimeSpan.FromMinutes(2);handler.Fail=true;var result=await service.ReadAsync(default);
        Assert.True(result.Stale);Assert.Equal(first.QuotedAt,result.QuotedAt);Assert.Equal(first.CnyToBrl,result.CnyToBrl);Assert.NotNull(result.Error);
        await service.ReadAsync(default);Assert.Equal(3,handler.Calls);
    }
    [Theory][InlineData("0")][InlineData("-2")][InlineData("bad")]
    public async Task InvalidQuotesNeverBecomeDisplayedRates(string bid)
    {
        var handler=new Handler();handler.Body=handler.Body.Replace("0.8",bid);var service=new ExchangeRates(new HttpClient(handler),new Clock());
        var result=await service.ReadAsync(default);Assert.Null(result.CnyToBrl);Assert.Null(result.BrlToCny);Assert.True(result.Stale);
    }
}
