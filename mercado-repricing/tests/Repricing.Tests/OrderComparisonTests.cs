using System.Text.Json;
using Repricing.Dashboard;
namespace Repricing.Tests;
public class OrderComparisonTests
{
    static JsonElement[] Rows(string json){using var d=JsonDocument.Parse(json);return d.RootElement.EnumerateArray().Select(x=>x.Clone()).ToArray();}
    [Fact] public void GroupsBothFulfillmentTypesExcludesCancelledAndUsesProductSkuFallback()
    {
        var rows=Rows("""[{"orderId":"1","fulfillByPlatform":1,"orderStatePlatform":"paid_shipped","orderItemList":[{"variationSku":"","productSku":"A","price":10,"productCount":2}]},{"orderId":"2","fulfillByPlatform":0,"orderItemList":[{"variationSku":"B","price":30,"productCount":1}]},{"orderId":"3","fulfillByPlatform":1,"orderStatePlatform":"cancelled_cancelled","orderItemList":[{"variationSku":"A","price":99,"productCount":1}]}]""");
        var result=OrderComparisonMetrics.Build(rows,new Dictionary<string,decimal?>{{"A",-.2m},{"B",.1m}},new Dictionary<string,string>{{"A","紧急清仓"},{"B","强制起量"}});
        Assert.Equal(1,result.ExcludedOrders);var full=result.Groups[0];Assert.Equal("Full",full.Name);Assert.Equal(20m,full.Sales);Assert.Equal(-4m,full.Profit);Assert.Equal(-.2m,full.Margin);Assert.Equal(1,full.Orders);Assert.Equal(20m,Assert.Single(full.Strategies,s=>s.Name=="清仓").Sales);
        Assert.Equal(30m,result.Groups[1].Sales);Assert.Equal(3m,result.Groups[1].Profit);Assert.Empty(result.Issues);
    }
    [Fact] public void MissingMarginIsNotAZeroAndUnlistedIsOther()
    {
        var result=OrderComparisonMetrics.Build(Rows("""[{"orderId":"1","fulfillByPlatform":0,"orderItemList":[{"productSku":"A","price":10,"productCount":1}]}]"""),new Dictionary<string,decimal?>(),new Dictionary<string,string>());
        var other=result.Groups[1];Assert.Null(other.Profit);Assert.Null(other.Margin);Assert.Equal(10m,Assert.Single(other.Strategies,s=>s.Name=="其他").Sales);Assert.Contains(result.Issues,s=>s.Contains("A"));
    }
    [Theory][InlineData("[{\"orderId\":\"1\",\"orderItemList\":[]}]" )][InlineData("[{\"orderId\":\"1\",\"fulfillByPlatform\":3,\"orderItemList\":[]}]" )]
    public void UnknownFulfillmentCannotBeClassifiedAsNonFull(string json)=>Assert.Throws<Repricing.AppException>(()=>OrderComparisonMetrics.Build(Rows(json),new Dictionary<string,decimal?>(),new Dictionary<string,string>()));
    [Fact] public void DuplicateOrderIsRejected()
    {
        var row="""{"orderId":"1","fulfillByPlatform":1,"orderItemList":[{"productSku":"A","price":10,"productCount":1}]}""";
        Assert.Throws<Repricing.AppException>(()=>OrderComparisonMetrics.Build(Rows("["+row+","+row+"]"),new Dictionary<string,decimal?>(),new Dictionary<string,string>()));
    }
}
