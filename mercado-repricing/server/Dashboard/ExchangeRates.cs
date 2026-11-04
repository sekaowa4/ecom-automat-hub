using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
namespace Repricing.Dashboard;

public sealed record ExchangeQuote(decimal? CnyToBrl,decimal? BrlToCny,DateTimeOffset? QuotedAt,DateTimeOffset? RetrievedAt,bool Stale,string? Error,string Source="AwesomeAPI");

// Display-only market reference. Never supplies the accounting conversion rate.
public sealed class ExchangeRates(HttpClient? client=null,TimeProvider? time=null)
{
    readonly HttpClient http=client??new HttpClient{Timeout=TimeSpan.FromSeconds(10)};
    readonly TimeProvider clock=time??TimeProvider.System;
    readonly SemaphoreSlim gate=new(1,1);
    ExchangeQuote cached=new(null,null,null,null,true,"行情暂不可用");
    ExchangeQuote? backup;
    DateTimeOffset nextRead=DateTimeOffset.MinValue,nextBackup=DateTimeOffset.MinValue;
    public async Task<ExchangeQuote> ReadAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var now=clock.GetUtcNow();
            if(now<nextRead)return Age(cached,now);
            ExchangeQuote? primary=null;
            try
            {
                using var doc=await ReadJson("https://economia.awesomeapi.com.br/json/last/CNY-BRL?_="+now.ToUnixTimeSeconds(),ct);
                var quote=doc.RootElement.GetProperty("CNYBRL");
                decimal Number(string name)=>decimal.Parse(quote.GetProperty(name).GetString()!,CultureInfo.InvariantCulture);
                var bid=Number("bid");var ask=Number("ask");
                var quotedAt=DateTimeOffset.FromUnixTimeSeconds(long.Parse(quote.GetProperty("timestamp").GetString()!,CultureInfo.InvariantCulture));
                if(quote.GetProperty("code").GetString()!="CNY"||quote.GetProperty("codein").GetString()!="BRL"||bid<=0||ask<bid||quotedAt>now.AddMinutes(5))throw new InvalidDataException();
                var mid=(bid+ask)/2;
                primary=new(mid,1m/mid,quotedAt,clock.GetUtcNow(),false,null);
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
            catch(Exception e)when(Expected(e)){}
            if(primary is null||Age(primary,now).Stale)
            {
                if(now>=nextBackup)
                {
                    // This source updates daily: poll at most hourly, including failures.
                    nextBackup=now.AddHours(1);
                    try
                    {
                        using var doc=await ReadJson("https://open.er-api.com/v6/latest/BRL",ct);var q=doc.RootElement;
                        var rate=q.GetProperty("rates").GetProperty("CNY").GetDecimal();
                        var at=DateTimeOffset.FromUnixTimeSeconds(q.GetProperty("time_last_update_unix").GetInt64());
                        if(q.GetProperty("result").GetString()!="success"||q.GetProperty("base_code").GetString()!="BRL"||rate<=0||at>now.AddMinutes(5))throw new InvalidDataException();
                        backup=new(1m/rate,rate,at,clock.GetUtcNow(),false,null,"ExchangeRate-API");
                    }
                    catch(OperationCanceledException)when(ct.IsCancellationRequested){nextBackup=now;throw;}
                    catch(Exception e)when(Expected(e)){}
                }
                if(backup is not null&&(primary is null||backup.QuotedAt>primary.QuotedAt))primary=backup;
            }
            if(primary is not null&&(cached.QuotedAt is null||primary.QuotedAt>=cached.QuotedAt))cached=primary;
            else if(primary is null)cached=cached with{Stale=true,Error="汇率源连接失败，等待自动重试"};
            nextRead=clock.GetUtcNow().AddMinutes(1);
            return Age(cached,clock.GetUtcNow());
        }
        finally{gate.Release();}
    }
    async Task<JsonDocument> ReadJson(string url,CancellationToken ct)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(6));
        using var request=new HttpRequestMessage(HttpMethod.Get,url);
        request.Headers.CacheControl=new CacheControlHeaderValue{NoCache=true};
        using var response=await http.SendAsync(request,timeout.Token);response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
    }
    static bool Expected(Exception e)=>e is HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or ArgumentOutOfRangeException or InvalidDataException;
    static ExchangeQuote Age(ExchangeQuote quote,DateTimeOffset now)=>quote.QuotedAt is {} at&&now-at>(quote.Source=="ExchangeRate-API"?TimeSpan.FromHours(26):TimeSpan.FromMinutes(10))?quote with{Stale=true,Error=quote.Error??"报价已过期，等待市场更新"}:quote;
}
