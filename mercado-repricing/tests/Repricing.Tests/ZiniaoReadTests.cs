using Repricing.Execution;
namespace Repricing.Tests;
public class ZiniaoReadTests
{
    readonly PriceInput input=StorageTests.Input(220) with{Store="Sany店铺2-子账号",Sku="NW250924EAVNEGSA",ItemId="4279929175"};
    static ZiniaoProduct Product()=>new("4279929175","NW250924EAVNEGSA","商品","R$ 245",[new("C-MLB5494250","0919-1019","激活","R$ 221,91",true)]);
    sealed class Reader(params ZiniaoProduct[] products):IZiniaoReader
    {
        public int Reads;
        public Task<ZiniaoStore[]> StoresAsync(CancellationToken ct)=>Task.FromResult(new[]{new ZiniaoStore("27848865846994","Sany店铺2-子账号","MercadoLibre-巴西-本土")});
        public Task<ZiniaoProduct[]> ReadAsync(ZiniaoStore store,PriceInput input,CancellationToken ct){Reads++;return Task.FromResult(products);}
    }
    [Fact] public async Task ReadsCurrentCampaignAndDifferenceWithoutClaimingSuccess()
    {
        var r=await new ZiniaoReadOnlyExecutor(new Reader(Product())).ExecuteAsync(input,default);
        Assert.Equal(RowStatus.Inspected,r.Status);Assert.Equal(221.91m,r.Observation!.CurrentPrice);Assert.Equal(-1.91m,r.Observation.Difference);Assert.Contains("未修改",r.Message);
    }
    [Fact] public async Task DuplicateSkuIsNeverChosenByFirstResult(){var r=await new ZiniaoReadOnlyExecutor(new Reader(Product(),Product() with{ItemId="999"})).ExecuteAsync(input with{ItemId=null},default);Assert.Equal(RowStatus.NeedsReview,r.Status);Assert.Null(r.Observation);}
    [Fact] public async Task ItemIdMustStillMatchSku(){var r=await new ZiniaoReadOnlyExecutor(new Reader(Product())).ExecuteAsync(input with{ItemId="999"},default);Assert.Equal(RowStatus.NeedsReview,r.Status);}
    [Fact] public async Task SkuAloneCannotConfirmExpectedProductOwner(){var r=await new ZiniaoReadOnlyExecutor(new Reader(Product())).ExecuteAsync(input with{ItemId=null},default);Assert.Equal(RowStatus.NeedsReview,r.Status);Assert.Equal(221.91m,r.Observation!.CurrentPrice);Assert.Contains("商品 ID",r.Message);}
    [Fact] public async Task RangedPriceIsNeverParsedAsSingleAmount(){var p=Product() with{Promotions=[new("C-1","活动","激活","R$ 10 至 R$ 20",true)]};var r=await new ZiniaoReadOnlyExecutor(new Reader(p)).ExecuteAsync(input,default);Assert.Equal(RowStatus.NeedsReview,r.Status);Assert.Null(r.Observation!.CurrentPrice);}
    [Fact] public async Task MultipleActiveCampaignsRequireExplicitId(){var p=Product() with{Promotions=[..Product().Promotions,new("C-2","活动2","激活","R$ 200",true)]};var e=new ZiniaoReadOnlyExecutor(new Reader(p));Assert.Equal(RowStatus.NeedsReview,(await e.ExecuteAsync(input,default)).Status);Assert.Equal(200,(await e.ExecuteAsync(input with{PromotionId="C-2"},default)).Observation!.CurrentPrice);}
    [Fact] public async Task ProposedCampaignIsNotCurrentSellingPrice(){var p=Product() with{Promotions=[new("C-1","提案","新提案","R$ 171",false)]};var r=await new ZiniaoReadOnlyExecutor(new Reader(p)).ExecuteAsync(input,default);Assert.Null(r.Observation!.CurrentPrice);Assert.Equal(RowStatus.NeedsReview,r.Status);}
    [Fact] public async Task WrongStoreOrSiteNeverOpensBrowser(){var reader=new Reader(Product());var e=new ZiniaoReadOnlyExecutor(reader);Assert.Equal(RowStatus.NeedsReview,(await e.ExecuteAsync(input with{Store="Sany"},default)).Status);Assert.Equal(RowStatus.NeedsReview,(await e.ExecuteAsync(input with{Site="MLM",Currency="MXN"},default)).Status);Assert.Equal(0,reader.Reads);}
    [Fact] public async Task VariationRemainsUnresolved(){var r=await new ZiniaoReadOnlyExecutor(new Reader(Product())).ExecuteAsync(input with{VariationId="123"},default);Assert.Equal(RowStatus.NeedsReview,r.Status);}
    [Theory] [InlineData("提价",220)] [InlineData("降价",230)] public async Task ActionConflictsRequireReview(string action,decimal target){var result=await new ZiniaoReadOnlyExecutor(new Reader(Product())).ExecuteAsync(input with{Action=action,TargetPrice=target},default);Assert.Equal(RowStatus.NeedsReview,result.Status);Assert.Contains("方向不符",result.Message);}
    [Fact] public async Task SamePriceSkips(){var result=await new ZiniaoReadOnlyExecutor(new Reader(Product())).ExecuteAsync(input with{TargetPrice=221.91m},default);Assert.Equal(RowStatus.Skipped,result.Status);}
    [Fact] public async Task SamePriceWithoutExpectedIdStillNeedsIdentityReview(){var result=await new ZiniaoReadOnlyExecutor(new Reader(Product())).ExecuteAsync(input with{ItemId=null,TargetPrice=221.91m},default);Assert.Equal(RowStatus.NeedsReview,result.Status);Assert.NotNull(result.Observation);}
    [Theory] [InlineData("R$ 1.234,56",1234.56)] [InlineData("R$ 245",245)] [InlineData("R$ 221,91",221.91)]
    public void BrazilianPageMoneyUsesDeclaredCurrency(string text,decimal expected)=>Assert.Equal(expected,ZiniaoReadOnlyExecutor.Money(text));
}
