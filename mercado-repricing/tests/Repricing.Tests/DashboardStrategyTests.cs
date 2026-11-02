using Repricing.Dashboard;
namespace Repricing.Tests;
public class DashboardStrategyTests
{
    [Fact] public void NormalizesKeysAndRejectsConflictingLabels()
    {
        var dir=Path.Combine(Path.GetTempPath(),"strategy-test-"+Guid.NewGuid());Directory.CreateDirectory(dir);
        try{var path=Path.Combine(dir,"dashboard-strategies.json");File.WriteAllText(path,"""{" sku-a ":"重要清仓","SKU-B":"强制起量"}""");var mapping=DashboardStrategies.Read(dir);Assert.Equal("重要清仓",mapping["SKU-A"]);Assert.Equal("清仓",OrderComparisonMetrics.Strategy("SKU-A",mapping));Assert.Equal("其他",OrderComparisonMetrics.Strategy("SKU-C",mapping));File.WriteAllText(path,"""{"sku-a":"重要清仓","SKU-A":"强制起量"}""");Assert.Throws<InvalidDataException>(()=>DashboardStrategies.Read(dir));}finally{Directory.Delete(dir,true);}
    }
}
