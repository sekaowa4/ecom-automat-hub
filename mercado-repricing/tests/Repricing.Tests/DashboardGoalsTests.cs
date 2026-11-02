using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Repricing.Dashboard;
namespace Repricing.Tests;

public class DashboardGoalsTests
{
    [Fact] public void CalculatesOrderAndProfitGoalsFromMonthlyTotals()
    {
        var days=Enumerable.Range(0,7).Select(i=>new DailySales(new DateOnly(2026,10,26).AddDays(i).ToString("yyyy-MM-dd"),100,2)).ToArray();
        SkuSale[] rows=[new("SYNTHETIC","Product",200,4,10,20)];
        var data=DashboardMetrics.Build(new("test","Demo",1000,20,.15m),2,new(2026,11,2),days,200,4,rows,rows,new Dictionary<string,string>(),new Dictionary<string,decimal?>{{"SYNTHETIC",.2m}});
        Assert.Equal(20,data.Goals!.OrdersTarget);Assert.Equal(.2m,data.Goals.OrdersCompletion);Assert.Equal(16,data.Goals.OrdersGap);
        Assert.Equal(75,data.Goals.ProfitTarget);Assert.Equal(40,data.Goals.Profit);Assert.Equal(.2m,data.Goals.Margin);Assert.Equal(40m/75,data.Goals.ProfitCompletion);
    }
    [Fact] public void MissingMonthlyMarginNeverBecomesZeroProfit()
    {
        var days=Enumerable.Range(0,7).Select(i=>new DailySales(new DateOnly(2026,10,26).AddDays(i).ToString("yyyy-MM-dd"),100,2)).ToArray();
        SkuSale[] rows=[new("UNKNOWN","Product",200,4,10,20)];
        var data=DashboardMetrics.Build(new("test","Demo",1000,20,.15m),2,new(2026,11,2),days,200,4,rows,rows,new Dictionary<string,string>(),new Dictionary<string,decimal?>());
        Assert.Null(data.Goals!.Profit);Assert.Null(data.Goals.ProfitCompletion);Assert.Null(data.Goals.Margin);
    }
    [Fact] public void PrivateGoalsAreBoundToExactShopAndSelectedMonth()
    {
        var dir=Path.Combine(Path.GetTempPath(),"dashboard-targets-"+Guid.NewGuid());Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir,"dashboard-targets.json"),"""{"2026-11":{"Meli Demo":{"gmv_cny":1000,"orders":20,"margin":0.15}}}""");
            var target=new DashboardTargets(dir);var original=new DashboardShop("Meli Demo","Demo",780);
            Assert.Equal(1000,target.Apply(original,new(2026,11,1)).TargetCny);
            Assert.Equal(20,target.Apply(original,new(2026,11,1)).TargetOrders);
            Assert.Equal(original,target.Apply(original,new(2026,10,31)));
            Assert.Equal(780,target.Apply(original with{Id="Other"},new(2026,11,1)).TargetCny);
        }finally{Directory.Delete(dir,true);}
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void MissingOrCorruptedSavedPathFallsBackToTheRelocatedBundle(bool corrupted)
    {
        var dir=Path.Combine(Path.GetTempPath(),"dashboard-relocation-"+Guid.NewGuid());var bundle=Path.Combine(dir,"relocated-app","每日消息推送");var profile=Path.Combine(dir,"fresh-profile");Directory.CreateDirectory(bundle);Directory.CreateDirectory(profile);
        try
        {
            File.WriteAllText(Path.Combine(bundle,"config.json"),"{}");
            File.WriteAllText(Path.Combine(profile,"dashboard-source.json"),corrupted?"{broken":JsonSerializer.Serialize(new UpSellerDashboard.SourceSettings(Path.Combine(dir,"deleted-old-computer"))));
            using var dashboard=new UpSellerDashboard(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"DataDirectory",profile},{"Dashboard:AppDirectory",Path.GetDirectoryName(bundle)}}).Build());
            Assert.Equal(bundle,dashboard.DirectoryPath);
        }finally{Directory.Delete(dir,true);}
    }
}
