using System.Text.RegularExpressions;
namespace Repricing.Execution;

public static class TransientRetry
{
    public static bool Blocked(string message)=>Regex.IsMatch(message,"身份|不唯一|未唯一|不一致|权限|审核|价格上限|价格下限|平台限制|验证|重新登录|人工确认",RegexOptions.CultureInvariant);
    public static bool Interaction(string message)=>Regex.IsMatch(message,"分组展开未响应|分组展开按钮尚未加载完成|下拉失败|展开失败|控件加载超时",RegexOptions.CultureInvariant);
    public static string? Classify(string message)
    {
        if(Blocked(message)||message.Contains("或记录",StringComparison.Ordinal))return null;
        if(Interaction(message))return "页面卡顿";
        if(message.Contains("超时",StringComparison.Ordinal))return "请求超时";
        return Regex.IsMatch(message,"ERR_(PROXY|TUNNEL|SOCKS|CONNECTION|NAME_NOT_RESOLVED)|连接失败|连接中断|连接已关闭|通道已中断|页面控制命令未完成|页面导航失败|未能唯一确定该店铺的页面控制通道|紫鸟本地命令执行失败",RegexOptions.CultureInvariant)?"连接失败":null;
    }
    public static string? FromException(Exception error)=>error switch
    {
        VerificationRequiredException=>null,
        AppException{Status:422}=>null,
        AppException e=>Classify(e.Message),
        OperationCanceledException=>"请求超时",
        System.Net.Http.HttpRequestException or System.Net.WebSockets.WebSocketException=>"连接失败",
        _=>null
    };
    public static TimeSpan Delay(int attempts)=>TimeSpan.FromSeconds(Math.Min(300,30*Math.Pow(2,Math.Clamp(attempts-1,0,4))));
}
