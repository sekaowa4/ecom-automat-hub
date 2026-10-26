namespace Repricing.Dashboard;

public record DashboardShop(string Id,string Name,decimal TargetCny);
public record DailySales(string Date,decimal Sales,int Orders);
public record SkuSale(string Sku,string Name,decimal Sales,int Orders,int Units,decimal AveragePrice);
public record RankedSku(string Sku,string Name,decimal Sales,int Orders,int Units,decimal? Margin,string Category);
public record CategorySales(string Name,decimal Sales,decimal? Share);
public record DashboardData(string Store,string Currency,DateTimeOffset RetrievedAt,string ReportDate,string MonthThrough,
    decimal Rate,decimal TargetCny,decimal? TargetBrl,decimal YesterdaySales,int YesterdayOrders,int YesterdayUnits,
    decimal? Growth,decimal MonthSales,int MonthOrders,decimal? Completion,decimal? GapBrl,int RemainingDays,decimal? RequiredDaily,
    DailySales[] History,CategorySales[] Categories,decimal CategoryTotal,RankedSku[] Ranking,decimal? ProfitBrl,decimal? Margin,string[] ProfitIssues);

public static class DashboardMetrics
{
    public static DashboardData Build(DashboardShop shop,decimal rate,DateOnly today,DailySales[] days,decimal monthSales,int monthOrders,
        SkuSale[] yesterday,SkuSale[] month,IReadOnlyDictionary<string,string> categories,IReadOnlyDictionary<string,decimal?> margins)
    {
        var last=days[^1];var previous=days[^2];var remaining=DashboardPeriod.For(today).RemainingDays;
        decimal? target=shop.TargetCny>0&&rate>0?shop.TargetCny/rate:null,gap=target is decimal t?Math.Max(t-monthSales,0):null;
        var ranking=yesterday.GroupBy(r=>r.Sku).Select(g=>new RankedSku(g.Key,g.First().Name,g.Sum(r=>r.Sales),g.Sum(r=>r.Orders),g.Sum(r=>r.Units),margins.GetValueOrDefault(g.Key),categories.GetValueOrDefault(g.Key,"未分类"))).OrderByDescending(r=>r.Sales).ThenBy(r=>r.Sku).ToArray();
        var categoryTotal=month.Sum(r=>r.Sales);
        var buckets=month.GroupBy(r=>categories.GetValueOrDefault(r.Sku,"未分类")).Select(g=>new CategorySales(g.Key,g.Sum(r=>r.Sales),categoryTotal>0?g.Sum(r=>r.Sales)/categoryTotal:null)).OrderByDescending(r=>r.Sales).ToArray();
        var issues=new List<string>();decimal profit=0;
        foreach(var row in yesterday.Where(r=>r.Units!=0))
        {
            if(row.Units<0||row.AveragePrice<=0||!margins.TryGetValue(row.Sku,out var margin)||margin==null){issues.Add(row.Sku+"：利润数据缺失、重复冲突或销量/均价异常");continue;}
            profit+=row.AveragePrice*row.Units*margin.Value;
        }
        if(last.Sales>0&&!yesterday.Any(r=>r.Units>0))issues.Add("有销售额但无有效产品销量");
        decimal? validProfit=issues.Count==0&&yesterday.Any(r=>r.Units>0)?profit:null;
        return new(shop.Name,"BRL",DateTimeOffset.UtcNow,last.Date,last.Date,rate,shop.TargetCny,target,last.Sales,last.Orders,yesterday.Sum(r=>r.Units),previous.Sales!=0?last.Sales/previous.Sales-1:null,monthSales,monthOrders,target>0?monthSales/target:null,gap,remaining,remaining>0?gap/remaining:null,days,buckets,categoryTotal,ranking,validProfit,last.Sales>0?validProfit/last.Sales:null,issues.Distinct().ToArray());
    }
}
