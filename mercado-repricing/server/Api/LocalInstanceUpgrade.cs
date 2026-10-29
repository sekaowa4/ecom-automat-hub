using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
namespace Repricing.Api;

public enum ExistingInstanceAction { Open,Restart,Busy }
public record ExistingInstanceDecision(ExistingInstanceAction Action,string Version);
public static class LocalInstanceUpgrade
{
    public static async Task<ExistingInstanceDecision> RequestAsync(HttpClient client,Version target,CancellationToken ct=default)
    {
        var health=await client.GetFromJsonAsync<JsonElement>("/api/health",ct);
        if(health.GetProperty("application").GetString()!="MercadoRepricing")throw new InvalidOperationException("本机端口被其他程序占用，未关闭其他程序。");
        var text=health.GetProperty("version").GetString()??"";
        if(!Version.TryParse(text,out var existing)||existing.Build<0)throw new InvalidOperationException("无法确认已运行工作台的版本。");
        if(new Version(existing.Major,existing.Minor,existing.Build)>=new Version(target.Major,target.Minor,target.Build))return new(ExistingInstanceAction.Open,text);
        var session=await client.GetFromJsonAsync<JsonElement>("/api/session",ct);
        using var request=new HttpRequestMessage(HttpMethod.Post,"/api/shutdown");request.Headers.Add("X-Local-Token",session.GetProperty("token").GetString());
        using var response=await client.SendAsync(request,ct);
        if(response.StatusCode==HttpStatusCode.Conflict)return new(ExistingInstanceAction.Busy,text);
        response.EnsureSuccessStatusCode();return new(ExistingInstanceAction.Restart,text);
    }
}
