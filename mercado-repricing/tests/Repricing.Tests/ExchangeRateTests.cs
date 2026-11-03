using System.Net;
using Repricing.Dashboard;
namespace Repricing.Tests;
public class ExchangeRateTests
{
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
        await service.ReadAsync(default);Assert.Equal(2,handler.Calls);
    }
    [Theory][InlineData("0")][InlineData("-2")][InlineData("bad")]
    public async Task InvalidQuotesNeverBecomeDisplayedRates(string bid)
    {
        var handler=new Handler();handler.Body=handler.Body.Replace("0.8",bid);var service=new ExchangeRates(new HttpClient(handler),new Clock());
        var result=await service.ReadAsync(default);Assert.Null(result.CnyToBrl);Assert.Null(result.BrlToCny);Assert.True(result.Stale);
    }
}
