using Repricing.Import;
using Repricing.Execution;
namespace Repricing.Tests;
public class MeilTemplateTests
{
    [Fact] public void RecognizesActualFourColumnHeaders()
    {
        var map=new PriceValidator().SuggestMapping(["产品","sku","调价动作","折扣价格"]);
        Assert.Equal("sku",map.Sku);Assert.Equal("折扣价格",map.TargetPrice);
    }
    sealed class EmptyStore:IZiniaoReader
    {
        public Task<ZiniaoStore[]> StoresAsync(CancellationToken ct)=>Task.FromResult(new[]{new ZiniaoStore("1","店铺A","MercadoLibre-巴西-本土")});
        public Task<ZiniaoProduct[]> ReadAsync(ZiniaoStore store,PriceInput input,CancellationToken ct)=>Task.FromResult(Array.Empty<ZiniaoProduct>());
    }
    [Fact] public async Task MissingSkuSkipsOnlyThatStore()
    {
        var result=await new ZiniaoReadOnlyExecutor(new EmptyStore()).ExecuteAsync(StorageTests.Input() with{Store="1"},default);
        Assert.Equal(RowStatus.Skipped,result.Status);Assert.False(result.PauseBatch);
    }
    static readonly ImportSheet Sheet=new("Sheet1",["产品","sku","调价动作","折扣价格"],[new(2,new(){["产品"]=new("测试产品",false,false),["sku"]=new("0001",false,false),["调价动作"]=new("降价",false,false),["折扣价格"]=new("75",false,false)})]);
    [Fact] public void MixedTemplateAllowsBlankActionAndNoActionColumn(){var v=new PriceValidator();var sessions=new ImportSessionStore(v);var sheet=Sheet with{Rows=[Sheet.Rows[0] with{Cells=new(Sheet.Rows[0].Cells){["调价动作"]=new("",false,false)}}]};var id=sessions.Add("混合.xlsx",[sheet]);var result=sessions.Validate(id,new("Sheet1",v.SuggestMapping(sheet.Headers),null,true,"1"),[new("1","A","MercadoLibre-巴西")]);Assert.NotNull(Assert.Single(result.Rows).Input);Assert.Null(result.Rows[0].Input!.Action);var noAction=sheet with{Headers=["sku","折扣价格"]};var next=sessions.Add("无动作.xlsx",[noAction]);Assert.NotNull(Assert.Single(sessions.Validate(next,new("Sheet1",v.SuggestMapping(noAction.Headers),null,true,"1"),[new("1","A","MercadoLibre-巴西")]).Rows).Input);}
    [Fact] public void FreezesCrossStoreScopeAndKeepsHeadersUnchanged()
    {
        var validator=new PriceValidator();var sessions=new ImportSessionStore(validator);var id=sessions.Add("四列表.xlsx",[Sheet]);
        var result=sessions.Validate(id,new("Sheet1",validator.SuggestMapping(Sheet.Headers),null,true),[new("1","A","MercadoLibre-巴西-本土"),new("2","B","MercadoLibre-巴西-本土"),new("3","C","自定义平台"),new("4","D","MercadoLibre-墨西哥-本土")]);
        Assert.Equal(2,result.Rows.Length);Assert.Equal(new[]{"1","2"},result.Rows.Select(r=>r.Input!.Store));Assert.All(result.Rows,r=>{Assert.Equal("MLB",r.Input!.Site);Assert.Equal("降价",r.Input.Action);Assert.Equal("0001",r.Input.Sku);Assert.Equal(4,r.Original.Count);});Assert.Single(result.Warnings!);Assert.Equal(result.Rows,sessions.GetValidation(result.ValidationId).Rows);
    }
    [Fact] public void InvalidActionAndDuplicatesRemainInvalidInEveryStore()
    {
        var v=new PriceValidator();var map=v.SuggestMapping(Sheet.Headers);var store=new ZiniaoStore("1","A","MercadoLibre-巴西-本土");
        var wrong=Sheet.Rows[0] with{Cells=new(Sheet.Rows[0].Cells){["调价动作"]=new("随便调",false,false)}};
        Assert.Null(v.Validate(Sheet with{Rows=[wrong]},map,"BR",store)[0].Input);
        Assert.All(v.Validate(Sheet with{Rows=[Sheet.Rows[0],Sheet.Rows[0] with{SourceRow=3}]},map,"BR",store),r=>Assert.Null(r.Input));
    }
    [Fact] public void SelectedStoreScopesAndFreezesTheJobRows()
    {
        var v=new PriceValidator();var sessions=new ImportSessionStore(v);var id=sessions.Add("四列表.xlsx",[Sheet]);
        var result=sessions.Validate(id,new("Sheet1",v.SuggestMapping(Sheet.Headers),null,true,"2"),[new("1","A","MercadoLibre-巴西-本土"),new("2","B","MercadoLibre-巴西-本土")]);
        Assert.Single(result.Stores!);Assert.Equal("2",Assert.Single(result.Rows).Input!.Store);Assert.Equal("B",result.Rows[0].Input!.StoreName);Assert.Equal("2",Assert.Single(sessions.GetValidation(result.ValidationId).Rows).Input!.Store);
    }
    [Theory][InlineData("missing")][InlineData("")][InlineData("3")][InlineData("4")]
    public void InvalidOrUnsupportedSelectionNeverFallsBackToAllStores(string selected)
    {
        var v=new PriceValidator();var sessions=new ImportSessionStore(v);var id=sessions.Add("四列表.xlsx",[Sheet]);
        Assert.Throws<AppException>(()=>sessions.Validate(id,new("Sheet1",v.SuggestMapping(Sheet.Headers),null,true,selected),[new("1","A","MercadoLibre-巴西-本土"),new("3","C","自定义平台"),new("4","D","MercadoLibre-墨西哥-本土")]));
    }
}
