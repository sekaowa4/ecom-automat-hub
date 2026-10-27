using Repricing.Dashboard;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
namespace Repricing.Tests;
public class DashboardTests
{
    [Fact] public async Task BatchImportNormalizesSkusPersistsLocallyAndNeverInventsBatchForErrors()
    {
        var dir=Path.Combine(Path.GetTempPath(),"dashboard-batches-"+Guid.NewGuid());Directory.CreateDirectory(dir);
        try
        {
            using var book=new ClosedXML.Excel.XLWorkbook();var sheet=book.AddWorksheet("对应表");
            sheet.Cell(1,1).Value="说明";sheet.Cell(2,1).Value="顺序上架";sheet.Cell(2,2).Value="SKU";
            sheet.Cell(3,1).Value="第九批";sheet.Cell(3,2).Value=" sku-a ";
            sheet.Cell(4,1).Value="#N/A";sheet.Cell(4,2).Value="SKU-B";
            sheet.Cell(5,2).Value="SKU-C";
            using var input=new MemoryStream();book.SaveAs(input);var original=input.ToArray();input.Position=0;
            var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"DataDirectory",dir}}).Build();
            var result=await new DashboardBatches(config).ImportAsync(input,"批次.xlsx",CancellationToken.None);
            Assert.Equal("第九批",Assert.Single(result.Values).Value);Assert.Equal("SKU-A",Assert.Single(result.Values).Key);Assert.Equal(2,result.Unmatched);
            Assert.Equal(original,input.ToArray());Assert.Equal(result.Values,new DashboardBatches(config).Read().Values);
            sheet.Cell(6,1).Value="第十批";sheet.Cell(6,2).Value="SKU-A";using var conflict=new MemoryStream();book.SaveAs(conflict);conflict.Position=0;
            await Assert.ThrowsAsync<InvalidDataException>(()=>new DashboardBatches(config).ImportAsync(conflict,"冲突.xlsx",CancellationToken.None));
            Assert.Equal("第九批",new DashboardBatches(config).Read().Values["SKU-A"]);
        }finally{Directory.Delete(dir,true);}
    }
    [Theory]
    [InlineData("2026-10-26","2026-10-25","2026-10-01",6)]
    [InlineData("2026-11-01","2026-10-31","2026-10-01",0)]
    public void UsesCompletedReportDayForMonthAndExcludesItFromRemainingDays(string today,string report,string start,int remaining)
    {
        var period=DashboardPeriod.For(DateOnly.Parse(today));
        Assert.Equal(report,period.ReportDate.ToString("yyyy-MM-dd"));Assert.Equal(start,period.MonthStart.ToString("yyyy-MM-dd"));Assert.Equal(remaining,period.RemainingDays);
    }
    [Theory][InlineData(false)][InlineData(true)] public async Task FiltersIgnoredShopQueryAndNeverIncludesOtherStoreSkus(bool capped)
    {
        var dir=Path.Combine(Path.GetTempPath(),"dashboard-test-"+Guid.NewGuid());Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir,"config.json"),JsonSerializer.Serialize(new{brl_to_cny=1.3m,shops=new Dictionary<string,object>{{"Meli测试",new{upseller_name="测试店铺",target_cny=780}}}}));
            await File.WriteAllTextAsync(Path.Combine(dir,".upseller_cookie.txt"),"synthetic-fixture");
            using var client=new HttpClient(new StatisticsHandler(capped));using var service=new UpSellerDashboard(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Dashboard:DailyDirectory",dir}}).Build(),client);
            var result=await service.ReadAsync("Meli测试",false,CancellationToken.None);
            Assert.Equal("测试店铺",result.Store);Assert.Equal("ONLY-SELECTED",result.Ranking[0].Sku);Assert.Equal(capped?2:1,result.Ranking.Length);Assert.Equal(100,result.YesterdaySales);Assert.Equal(capped?150:100,result.CategoryTotal);
            Assert.Null(result.Margin);Assert.DoesNotContain("OTHER",JsonSerializer.Serialize(result));
            Assert.Same(result,await service.ReadAsync("Meli测试",false,CancellationToken.None));
            await Assert.ThrowsAsync<AppException>(()=>service.ReadAsync("other",false,CancellationToken.None));
            await File.WriteAllTextAsync(Path.Combine(dir,"config.json"),JsonSerializer.Serialize(new{brl_to_cny=1.3m,shops=new Dictionary<string,object>{{"Meli测试",new{upseller_name="测试店铺",target_cny=1560}}}}));
            Assert.Equal(1200,(await service.ReadAsync("Meli测试",false,CancellationToken.None)).TargetBrl);
            await File.WriteAllTextAsync(Path.Combine(dir,"config.json"),JsonSerializer.Serialize(new{brl_to_cny=1.3m,shops=new Dictionary<string,object>{{"Meli测试",new{upseller_name="重映射新店铺",target_cny=1560}}}}));
            var mismatch=await Assert.ThrowsAsync<AppException>(()=>service.ReadAsync("Meli测试",false,CancellationToken.None));Assert.Contains("未唯一",mismatch.Message);
        }finally{Directory.Delete(dir,true);}
    }
    sealed class StatisticsHandler(bool capped=false):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Assert.Equal("app.upseller.com",request.RequestUri!.Host);Assert.Equal(HttpMethod.Get,request.Method);
            Assert.DoesNotContain("endDate="+DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8)).ToString("yyyy-MM-dd"),request.RequestUri.Query);
            var variants=request.RequestUri.AbsolutePath.Contains("variation");
            var selected=variants?(object)new{shopName="测试店铺",shopId=123,idStr="1",currency="BRL",variationSku="ONLY-SELECTED",sales=100,validOrders=1,productSales=1,averagePrice=100,productName="测试产品"}:new{shopName="测试店铺",shopId=123,currency="BRL",validSales=100,validOrders=1};
            var other=variants?(object)new{shopName="其他店铺",shopId=456,idStr="2",currency="BRL",variationSku="OTHER",sales=999}:new{shopName="其他店铺",shopId=456,validSales=999};
            var second=variants&&request.RequestUri.Query.Contains("pageNum=2");
            var rows=second?new object[]{new{shopName="测试店铺",shopId=123,idStr="3",currency="BRL",variationSku="SECOND-SELECTED",sales=50,validOrders=1,productSales=1,averagePrice=50,productName="第二页产品"}}:new[]{selected,other};
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{code=0,data=new{list=rows,pageNum=second?2:1,total=variants&&capped?3:2}}),Encoding.UTF8,"application/json")});
        }
    }
    [Fact] public void ReadsSavedFormulaValuesAndEmptySharedStringCellsWithoutEditingWorkbook()
    {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".xlsx");
        try
        {
            using(var zip=ZipFile.Open(path,ZipArchiveMode.Create))
            {
                void Add(string name,string content){using var writer=new StreamWriter(zip.CreateEntry(name).Open());writer.Write(content);}
                Add("xl/workbook.xml","<workbook xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><sheets><sheet name='利润表-Meli' r:id='r1'/></sheets></workbook>");
                Add("xl/_rels/workbook.xml.rels","<Relationships><Relationship Id='r1' Target='worksheets/sheet1.xml'/></Relationships>");
                Add("xl/worksheets/sheet1.xml","<worksheet xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'><sheetData><row><c r='C1' t='inlineStr'><is><t>SKU-A</t></is></c><c r='AB1'><f>1/5</f><v>0.2</v></c><c r='D1' t='s'/></row></sheetData></worksheet>");
            }
            var bytes=File.ReadAllBytes(path);Assert.Equal(.2m,LocalWorkbook.Margins(path)["SKU-A"]);Assert.Equal(bytes,File.ReadAllBytes(path));
        }finally{File.Delete(path);}
    }
    [Fact] public void KeepsShopGmvAndSkuDenominatorsSeparateAndSortsRevenue()
    {
        var days=Enumerable.Range(0,7).Select(i=>new DailySales(new DateOnly(2026,10,19).AddDays(i).ToString("yyyy-MM-dd"),i==6?120:100,2)).ToArray();
        var rows=new[]{new SkuSale("B","乙",30,1,1,30),new SkuSale("A","甲",100,2,2,50)};
        var result=DashboardMetrics.Build(new("test","示例店铺",780),1.3m,new(2026,10,26),days,450,10,rows,rows,new Dictionary<string,string>{{"A","P"}},new Dictionary<string,decimal?>{{"A",.2m},{"B",.1m}});
        Assert.Equal(600,result.TargetBrl);Assert.Equal(.75m,result.Completion);Assert.Equal(.2m,result.Growth);
        Assert.Equal(150,result.GapBrl);Assert.Equal(25,result.RequiredDaily);Assert.Equal(6,result.RemainingDays);
        Assert.Equal(130,result.CategoryTotal);Assert.Equal("A",result.Ranking[0].Sku);
        Assert.Equal(23,result.ProfitBrl);Assert.Equal(23m/120,result.Margin);
    }
    [Fact] public void MissingProfitOrZeroPreviousSalesNeverInventsZeroMarginOrGrowth()
    {
        var days=Enumerable.Range(0,7).Select(i=>new DailySales($"2026-10-{19+i}",i==6?50:0,0)).ToArray();
        var row=new[]{new SkuSale("UNKNOWN","商品",50,1,1,50)};
        var result=DashboardMetrics.Build(new("test","店铺",0),1,new(2026,10,31),days,50,1,row,row,new Dictionary<string,string>(),new Dictionary<string,decimal?>());
        Assert.Null(result.Margin);Assert.Null(result.ProfitBrl);Assert.Null(result.Growth);Assert.Null(result.Completion);Assert.Null(result.RequiredDaily);
        Assert.Single(result.ProfitIssues);Assert.Equal("未分类",Assert.Single(result.Categories).Name);
    }
}
