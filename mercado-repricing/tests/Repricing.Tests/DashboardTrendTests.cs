using Repricing.Dashboard;
namespace Repricing.Tests;
public class DashboardTrendTests
{
    [Fact] public void DailyProfitUsesUnitsAndAveragePriceAndStoreSalesDenominator()
    {
        var day=DashboardMetrics.WithProfit(new("2026-10-20",200,2),[new("A","Product",180,2,3,60)],new Dictionary<string,decimal?>{{"A",.2m}});
        Assert.Equal(36m,day.ProfitBrl);Assert.Equal(.18m,day.Margin);Assert.Empty(day.ProfitIssues!);
    }
    [Fact] public void MissingSkuMarginLeavesGapAndReportsExactSku()
    {
        var day=DashboardMetrics.WithProfit(new("2026-10-20",200,2),[new("A","Product",180,2,3,60)],new Dictionary<string,decimal?>());
        Assert.Null(day.ProfitBrl);Assert.Null(day.Margin);Assert.Contains("A",Assert.Single(day.ProfitIssues!));
    }
    [Fact] public void NegativeAndZeroMarginsAreValidReadings()
    {
        var rows=new SkuSale[]{new("A","Product",100,1,1,100)};
        var day=DashboardMetrics.WithProfit(new("2026-10-20",100,1),rows,new Dictionary<string,decimal?>{{"A",-.2m}});
        Assert.Equal(-20m,day.ProfitBrl);Assert.Equal(-.2m,day.Margin);
        day=DashboardMetrics.WithProfit(day,rows,new Dictionary<string,decimal?>{{"A",0m}});
        Assert.Equal(0m,day.ProfitBrl);Assert.Equal(0m,day.Margin);
    }
    [Fact] public void SalesWithoutSkuRowsCannotBecomeZeroProfit()
    {
        var day=DashboardMetrics.WithProfit(new("2026-10-20",100,1),[],new Dictionary<string,decimal?>());
        Assert.Null(day.ProfitBrl);Assert.Null(day.Margin);Assert.NotEmpty(day.ProfitIssues!);
    }
}
