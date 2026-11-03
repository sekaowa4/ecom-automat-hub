using System.Globalization;
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
    DateTimeOffset nextRead=DateTimeOffset.MinValue;
    public async Task<ExchangeQuote> ReadAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var now=clock.GetUtcNow();
            if(now<nextRead)return Age(cached,now);
            try
            {
                using var response=await http.GetAsync("https://economia.awesomeapi.com.br/json/last/CNY-BRL",ct);response.EnsureSuccessStatusCode();
                using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));var quote=doc.RootElement.GetProperty("CNYBRL");
                decimal Number(string name)=>decimal.Parse(quote.GetProperty(name).GetString()!,CultureInfo.InvariantCulture);
                var bid=Number("bid");var ask=Number("ask");
                var quotedAt=DateTimeOffset.FromUnixTimeSeconds(long.Parse(quote.GetProperty("timestamp").GetString()!,CultureInfo.InvariantCulture));
                if(quote.GetProperty("code").GetString()!="CNY"||quote.GetProperty("codein").GetString()!="BRL"||bid<=0||ask<bid||quotedAt>now.AddMinutes(5))throw new InvalidDataException();
                var mid=(bid+ask)/2;
                cached=new(mid,1m/mid,quotedAt,clock.GetUtcNow(),false,null);
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
            catch(Exception e)when(e is HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or ArgumentOutOfRangeException or InvalidDataException)
            {cached=cached with{Stale=true,Error="行情连接失败，等待自动重试"};}
            nextRead=clock.GetUtcNow().AddMinutes(1);
            return Age(cached,clock.GetUtcNow());
        }
        finally{gate.Release();}
    }
    static ExchangeQuote Age(ExchangeQuote quote,DateTimeOffset now)=>quote.QuotedAt is {} at&&now-at>TimeSpan.FromMinutes(10)?quote with{Stale=true,Error=quote.Error??"报价较早，等待市场更新"}:quote;
}
