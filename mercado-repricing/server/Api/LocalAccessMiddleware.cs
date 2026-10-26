using System.Security.Cryptography;
namespace Repricing.Api;
public class LocalSession { public string Token {get;}=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); }
public class LocalAccessMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context,LocalSession session)
    {
        var request=context.Request;
        if(request.Host.Host is not "localhost" and not "127.0.0.1" || request.Headers["Sec-Fetch-Site"]=="cross-site") {context.Response.StatusCode=403;return;}
        if(request.Path.StartsWithSegments("/api"))
        {
            context.Response.Headers.CacheControl="no-store";
            var origin=request.Headers.Origin.ToString();
            if(origin!="" && (!Uri.TryCreate(origin,UriKind.Absolute,out var uri) || uri.Scheme!="http" || uri.Host!=request.Host.Host || uri.Port!=request.Host.Port.GetValueOrDefault(80))){context.Response.StatusCode=403;return;}
            if(request.Method is not "GET" and not "HEAD" && request.Headers["X-Local-Token"]!=session.Token){context.Response.StatusCode=403;return;}
        }
        context.Response.Headers["X-Content-Type-Options"]="nosniff";
        context.Response.Headers["X-Frame-Options"]="DENY";
        context.Response.Headers["Referrer-Policy"]="no-referrer";
        context.Response.Headers["Content-Security-Policy"]="default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-src 'self' blob:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
        await next(context);
    }
}
