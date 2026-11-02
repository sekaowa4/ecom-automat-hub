using System.Text.Json;
namespace Repricing.Dashboard;
public static class DashboardStrategies
{
    public static Dictionary<string,string> Read(string appDirectory)
    {
        var file=Path.Combine(appDirectory,"dashboard-strategies.json");
        if(!File.Exists(file))throw new InvalidDataException("未内置运营策略名单。");
        using var doc=JsonDocument.Parse(File.ReadAllText(file));var result=new Dictionary<string,string>();
        foreach(var entry in doc.RootElement.EnumerateObject())
        {
            var sku=entry.Name.Trim().ToUpperInvariant();var label=entry.Value.GetString();
            if(sku==""||label is not ("紧急清仓" or "重要清仓" or "一般清仓" or "强制起量"))throw new InvalidDataException("运营策略名单包含无效分类。");
            if(result.TryGetValue(sku,out var previous)&&previous!=label)throw new InvalidDataException("运营策略名单含冲突SKU。");result[sku]=label;
        }
        if(result.Count==0)throw new InvalidDataException("运营策略名单为空。");return result;
    }
}
