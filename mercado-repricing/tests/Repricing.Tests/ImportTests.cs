using System.Text;
using ClosedXML.Excel;
using Repricing;
using Repricing.Import;

namespace Repricing.Tests;
public class ImportTests
{
    static ImportSheet Sheet(params string[][] rows) => new("Sheet1", ["店铺","站点","SKU","目标售价"], rows.Select((r,i)=>new RawRow(i+2, new[]{"店铺","站点","SKU","目标售价"}.Zip(r).ToDictionary(x=>x.First,x=>new RawCell(x.Second,false,false)))).ToArray());
    static ValidatedRow[] Validate(ImportSheet s) => new PriceValidator().Validate(s, new PriceValidator().SuggestMapping(s.Headers), null);
    [Fact] public void ReadsLeadingZeroSku() => Assert.Equal("00123", Validate(Sheet(["A","BR","00123","75.25"]))[0].Input!.Sku);
    [Theory] [InlineData("1,234")] [InlineData("0")] [InlineData("-1")] [InlineData("1.234")] public void RejectsAmbiguousMoney(string value) => Assert.Null(Validate(Sheet(["A","BR","X",value]))[0].Input);
    [Fact] public void RejectsDuplicateIdentity() => Assert.All(Validate(Sheet(["A","BR","X","70"],["A","BR","X","75"])), r=>Assert.Contains(r.Errors,e=>e.Contains("重复")));
    [Fact] public async Task QuotedCsvPreservesRecords()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("店铺,站点,SKU,目标售价,备注\r\nA,BR,00123,70,\"a,b\nsecond line\"\r\n"));
        var result = await new WorkbookReader().ReadAsync(stream,"test.csv",default);
        Assert.Single(result[0].Rows); Assert.Equal("a,b\nsecond line",result[0].Rows[0].Cells["备注"].Text);
    }
    [Fact] public async Task RejectsPriceFormula()
    {
        using var book=new XLWorkbook(); var ws=book.AddWorksheet("价格");
        string[] headers=["店铺","站点","SKU","目标售价"];
        for(int i=0;i<4;i++)ws.Cell(1,i+1).Value=headers[i];
        ws.Cell(2,1).Value="A";ws.Cell(2,2).Value="BR";ws.Cell(2,3).Value="00123";ws.Cell(2,4).FormulaA1="10+20";
        using var stream=new MemoryStream();book.SaveAs(stream);stream.Position=0;
        var result=await new WorkbookReader().ReadAsync(stream,"test.xlsx",default);
        Assert.Null(Validate(result[0])[0].Input);
    }
    [Fact] public async Task RejectsOversizeAndTooManyRows()
    {
        using var big=new MemoryStream(new byte[10*1024*1024+1]);
        await Assert.ThrowsAsync<InvalidDataException>(()=>new WorkbookReader().ReadAsync(big,"x.csv",default));
        using var rows=new MemoryStream(Encoding.UTF8.GetBytes("店铺,站点,SKU,目标售价\n"+string.Join('\n',Enumerable.Range(0,5001).Select(i=>$"A,BR,{i},10"))));
        await Assert.ThrowsAsync<InvalidDataException>(()=>new WorkbookReader().ReadAsync(rows,"x.csv",default));
    }
    [Fact] public async Task EnglishPriceFormattingDoesNotHidePrecision()
    {
        using var book=new XLWorkbook();var ws=book.AddWorksheet("价格");
        string[] headers=["store","site","sku","target_price"];for(int c=0;c<4;c++)ws.Cell(1,c+1).Value=headers[c];
        ws.Cell(2,1).Value="A";ws.Cell(2,2).Value="BR";ws.Cell(2,3).Value=123;ws.Cell(2,3).Style.NumberFormat.Format="00000";
        ws.Cell(2,4).Value=75.123;ws.Cell(2,4).Style.NumberFormat.Format="0.00";
        using var stream=new MemoryStream();book.SaveAs(stream);stream.Position=0;
        var sheet=(await new WorkbookReader().ReadAsync(stream,"x.xlsx",default))[0];
        var rows=Validate(sheet);Assert.Null(rows[0].Input);Assert.Contains(rows[0].Errors,e=>e.Contains("精度"));
    }
    [Fact] public void InvalidManualMappingPreservesDisplayFields()
    {
        var sheet=new ImportSheet("价格",["账号","货号","售价列"],[new(2,new(){["账号"]=new("店铺A",false,false),["货号"]=new("0007",false,false),["售价列"]=new("0",false,false)})]);
        var row=new PriceValidator().Validate(sheet,new("账号","货号","售价列"),"BR")[0];
        var job=new JobDetail(new(Guid.NewGuid(),"x.csv",ExecutionMode.Simulation,JobStatus.Ready,DateTimeOffset.UtcNow,1,1),[new(Guid.NewGuid(),row,RowStatus.Invalid,"","无效价格","")]);
        var exported=Encoding.UTF8.GetString(Repricing.Api.CsvExport.Export(job));Assert.Contains("店铺A",exported);Assert.Contains("0007",exported);
    }
}
