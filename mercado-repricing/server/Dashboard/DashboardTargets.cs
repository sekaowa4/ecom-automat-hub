using System.Text.Json;
namespace Repricing.Dashboard;

// Actual monthly goals are private package data, separate from the public source snapshot.
public sealed class DashboardTargets(string appDirectory)
{
    public DashboardShop Apply(DashboardShop shop,DateOnly reportDate)
    {
        var path=Path.Combine(appDirectory,"dashboard-targets.json");if(!File.Exists(path))return shop;
        using var doc=JsonDocument.Parse(File.ReadAllText(path).TrimStart('\uFEFF'));
        if(!doc.RootElement.TryGetProperty(reportDate.ToString("yyyy-MM"),out var month)||!month.TryGetProperty(shop.Id,out var target))return shop;
        var gmv=target.GetProperty("gmv_cny").GetDecimal();var orders=target.GetProperty("orders").GetInt32();var margin=target.GetProperty("margin").GetDecimal();
        if(gmv<=0||orders<=0||margin is <=0 or >1)throw new AppException("内置月目标数据无效，请检查目标配置。",409);
        return shop with{TargetCny=gmv,TargetOrders=orders,TargetMargin=margin};
    }
}
