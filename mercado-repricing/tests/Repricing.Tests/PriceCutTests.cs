using Repricing.Execution;
using Repricing.Storage;
namespace Repricing.Tests;

public class PriceCutTests
{
    static PriceInput Input()=>new("123","MLB","BRL","SKU-A",null,null,null,75,null,false,false,null,"降价");
    static string Money(decimal price)=>price.ToString("'R$' 0.00",System.Globalization.CultureInfo.GetCultureInfo("pt-BR"));
    static ZiniaoProduct Product(string sku="SKU-A",string id="456",decimal price=80)=>new(id,sku,"商品","R$ 100,00",[new("C-1","活动","激活",Money(price),true,true,true)]);
    sealed class Client:IZiniaoPriceCutClient
    {
        public List<string> Calls=[];public ZiniaoProduct[] Products=[Product()];public ZiniaoProduct[]? FreshProducts;public bool SaveThrows,VerifyThrows,LimitRejects;public decimal After=75;public string AfterBase="R$ 100,00";public string? AfterStatus;
        public ZiniaoProduct[]? ManagerProducts;
        public Task<ZiniaoProduct[]> ReadManagerAsync(ZiniaoStore store,PriceInput input,CancellationToken ct)=>Task.FromResult(ManagerProducts??Products);
        public ZiniaoStore[] Stores=[new("123","测试","MercadoLibre-巴西-本土")];public int EmptyStoreReads,StoreReads;
        public Task<ZiniaoStore[]> StoresAsync(CancellationToken ct){StoreReads++;return Task.FromResult(StoreReads<=EmptyStoreReads?Array.Empty<ZiniaoStore>():Stores);}
        public Task<ZiniaoProduct[]> ReadAsync(ZiniaoStore store,PriceInput input,CancellationToken ct){Calls.Add("read");return Task.FromResult(Calls.Count(x=>x=="read")>1?FreshProducts??Products:Products);}
        public Task PrepareAsync(PriceCutPlan plan,CancellationToken ct){Calls.Add("prepare");if(LimitRejects)throw new AppException("平台活动价上限为 R$ 70,00；未提交。",422);return Task.CompletedTask;}
        public Task SubmitAsync(PriceCutPlan plan,CancellationToken ct){Calls.Add("save");if(SaveThrows)throw new AppException("断连");return Task.CompletedTask;}
        public Task<ZiniaoProduct> VerifyAsync(PriceCutPlan plan,CancellationToken ct){Calls.Add("verify");if(VerifyThrows)throw new AppException("回读超时");var p=Products.FirstOrDefault(x=>x.ItemId==plan.Product.ItemId)??FreshProducts?.FirstOrDefault(x=>x.ItemId==plan.Product.ItemId)??Product();return Task.FromResult(p with{BasePriceText=AfterBase,ListingStatus=AfterStatus,Promotions=p.Promotions.Select(x=>x.Id==plan.PromotionId?x with{PriceText=Money(After),Active=true,Editable=true,Joinable=false}:x).ToArray()});}
        public Task CloseAsync()=>Task.CompletedTask;
    }
    static Task<ExecutionResult> Run(Client c,PriceInput? input=null,Action<PriceCutPlan>? checkpoint=null,PriceCutPlan? previous=null)=>new ZiniaoPriceCutExecutor(c).ExecuteAsync(input??Input(),p=>{c.Calls.Add("checkpoint");checkpoint?.Invoke(p);},default,previous);
    [Fact] public async Task IndependentChildSupersedesLegacySharedConflictOnlyAfterPromotionIdentityRead(){var old=Product() with{SharedSkus=["SKU-A","SKU-B"]};var child=Product(id:"789");var c=new Client{Products=[old],ManagerProducts=[child with{Promotions=[],ManagedPriceText="R$ 80"}],FreshProducts=[child]};PriceCutPlan? saved=null;var result=await new ZiniaoPriceCutExecutor(c).ExecuteAsync(Input(),p=>saved=p,default,batchInputs:[Input(),Input() with{Sku="SKU-B",TargetPrice=90}]);Assert.Equal(RowStatus.Succeeded,result.Status);Assert.Equal("789",saved!.Product.ItemId);Assert.Null(saved.Product.SharedSkus);Assert.Single(c.Calls,x=>x=="save");}
    [Fact] public async Task RealSharedLinkConflictStillCannotSave(){var c=new Client{Products=[Product() with{SharedSkus=["SKU-A","SKU-B"]}]};var result=await new ZiniaoPriceCutExecutor(c).ExecuteAsync(Input(),_=>{},default,batchInputs:[Input(),Input() with{Sku="SKU-B",TargetPrice=90}]);Assert.Equal(RowStatus.NeedsReview,result.Status);Assert.Contains("共用一个价格",result.Message);Assert.DoesNotContain("save",c.Calls);}
    [Fact] public async Task ManagerChildWithoutMatchingPromotionIdentityNeverSavesParent(){var c=new Client{Products=[Product() with{SharedSkus=["SKU-A","SKU-B"]}],ManagerProducts=[Product(id:"789")],FreshProducts=[Product(id:"789",sku:"SKU-B")]};var result=await Run(c);Assert.Equal(RowStatus.NeedsReview,result.Status);Assert.Contains("独立子商品",result.Message);Assert.DoesNotContain("save",c.Calls);}
    [Fact] public async Task SavesOnceOnlyAfterDurableCheckpointAndFreshlyVerifies()
    {
        var c=new Client();var r=await Run(c);Assert.Equal(RowStatus.Succeeded,r.Status);Assert.Equal(new[]{"read","prepare","checkpoint","save","verify"},c.Calls);Assert.Equal(75,r.Observation!.CurrentPrice);
    }
    [Fact] public void SuccessReadbackRequiresTheSameTargetSnapshotAcrossAForcedRefresh()
    {
        var stable=new PriceReadbackConfirmation();var target=Product(price:75);var drift=target with{Promotions=target.Promotions.Select(p=>p with{PriceText=Money(53.90m)}).ToArray()};
        Assert.False(stable.Observe(target,true,false));
        Assert.False(stable.Observe(drift,false,true));
        Assert.False(stable.Observe(target,true,true));
        Assert.True(stable.Observe(target,true,true));
    }
    [Theory][InlineData(205.9,199.9,288.9)][InlineData(317.9,315.9,424.7)][InlineData(49.9,46.9,69)]
    public async Task ActualThreePriceCutsKeepCentsAndOriginalPrice(decimal before,decimal target,decimal original)
    {
        var c=new Client{Products=[Product(price:before) with{BasePriceText=Money(original)}],After=target,AfterBase=Money(original)};
        var r=await Run(c,Input() with{TargetPrice=target});Assert.Equal(RowStatus.Succeeded,r.Status);Assert.Equal(target,r.Observation!.CurrentPrice);Assert.Single(c.Calls,x=>x=="save");
    }
    [Theory][InlineData("提价",90)][InlineData("修改原价",75)]
    public async Task OtherRoutesNeverSubmit(string action,int target){var c=new Client();var r=await Run(c,Input() with{Action=action,TargetPrice=target});Assert.Equal(RowStatus.NeedsReview,r.Status);Assert.DoesNotContain("save",c.Calls);}
    [Fact] public async Task TargetAboveCurrentPriceIsSkippedWithoutSubmitting(){var c=new Client();var r=await Run(c,Input() with{TargetPrice=101});Assert.Equal(RowStatus.Skipped,r.Status);Assert.Contains("提价",r.Message);Assert.DoesNotContain("save",c.Calls);}
    [Fact] public async Task OrdinaryCampaignCanBeJoinedAtTablePriceAboveDefaultOffer()
    {
        var c=new Client{Products=[Product() with{Promotions=[new("C-1","普通活动","","R$ 70,00",false,false,false,true)]}],After=75};var r=await Run(c);Assert.Equal(RowStatus.Succeeded,r.Status);Assert.Equal(75,r.Observation!.CurrentPrice);Assert.Single(c.Calls,x=>x=="save");
    }
    static ZiniaoProduct Offer()=>Product() with{Promotions=[new("C-1","普通活动","","R$ 70,00",false,false,false,true)]};
    static PriceCutPlan PreviousJoin()=>new(new("123","测试","MercadoLibre-巴西-本土"),Input(),Offer(),"C-1",100,100,Join:true);
    [Fact] public async Task JoinedTargetIsReconciledWithoutResubmitting(){var c=new Client{Products=[Product(price:75)]};var r=await Run(c,previous:PreviousJoin());Assert.Equal(RowStatus.Succeeded,r.Status);Assert.DoesNotContain("save",c.Calls);}
    [Fact] public async Task PendingOrdinaryJoinRequiresTwoReadsBeforeRetry(){var c=new Client{Products=[Offer()]};var r=await Run(c,previous:PreviousJoin());Assert.Equal(RowStatus.Succeeded,r.Status);Assert.Equal(2,c.Calls.Count(x=>x=="read"));Assert.Single(c.Calls,x=>x=="save");}
    [Fact] public async Task JoinedUnexpectedPriceNeverResubmits(){var c=new Client{Products=[Product(price:73)]};Assert.Equal(RowStatus.NeedsReview,(await Run(c,previous:PreviousJoin())).Status);Assert.DoesNotContain("save",c.Calls);}
    [Fact] public async Task MissingOldActiveCampaignCannotTurnIntoNewJoin(){var c=new Client{Products=[Offer()]};Assert.Equal(RowStatus.NeedsReview,(await Run(c,previous:Previous())).Status);Assert.DoesNotContain("save",c.Calls);}
    [Fact] public async Task PlatformPriceLimitDoesNotRetryOrSave(){var c=new Client{Products=[Offer()],LimitRejects=true};var r=await Run(c);Assert.Equal(RowStatus.NeedsReview,r.Status);Assert.False(r.Retryable);Assert.Contains("上限",r.Message);Assert.DoesNotContain("checkpoint",c.Calls);Assert.DoesNotContain("save",c.Calls);}
    [Fact] public async Task MissingSkuSkips(){var c=new Client{Products=[]};Assert.Equal(RowStatus.Skipped,(await Run(c)).Status);Assert.DoesNotContain("prepare",c.Calls);}
    [Fact] public async Task ManagedTargetAfterUnknownSaveIsReconciledWithoutWriting()
    {
        var c=new Client{Products=[Product() with{Promotions=[],ManagedPriceText="R$ 75,00",ListingStatus="进行中"}]};
        var r=await Run(c,previous:Previous());Assert.Equal(RowStatus.Succeeded,r.Status);Assert.Equal(75,r.Observation!.CurrentPrice);Assert.Contains("商品管理",r.Message);Assert.DoesNotContain("prepare",c.Calls);Assert.DoesNotContain("save",c.Calls);
    }
    [Fact] public async Task ManagedTargetWithNoOldSubmissionSkipsWithoutWriting()
    {
        var c=new Client{Products=[Product() with{Promotions=[],ManagedPriceText="R$ 75,00",ListingStatus="进行中"}]};
        Assert.Equal(RowStatus.Skipped,(await Run(c)).Status);Assert.DoesNotContain("save",c.Calls);
    }
    [Theory][InlineData(false)][InlineData(true)] public async Task UnderReviewAlwaysStaysNeedsReviewEvenAtTarget(bool saved)
    {
        var c=new Client{Products=[Product() with{Promotions=[],ManagedPriceText="R$ 75,00",ListingStatus="需审核未激活\n填写的品牌与部分产品数据中识别的品牌不匹配。"}]};
        var r=await Run(c,previous:saved?Previous():null);Assert.Equal(RowStatus.NeedsReview,r.Status);Assert.Contains("需审核未激活",r.Message);Assert.False(r.Retryable);Assert.DoesNotContain("prepare",c.Calls);Assert.DoesNotContain("save",c.Calls);
    }
    [Fact] public async Task VerificationAtTargetButUnderReviewCannotReportSuccess(){var c=new Client{AfterStatus="需审核未激活"};var r=await Run(c);Assert.Equal(RowStatus.NeedsReview,r.Status);Assert.Contains("审核未激活",r.Message);Assert.Equal(75,r.Observation!.CurrentPrice);Assert.Single(c.Calls,x=>x=="save");}
    [Theory][InlineData(false,false)][InlineData(false,true)][InlineData(true,false)][InlineData(true,true)]
    public async Task SecondReadUnderReviewNeverReconcilesOrSubmits(bool joining,bool atTarget)
    {
        var first=joining?Offer():Product();var fresh=(atTarget?Product(price:75):first) with{ListingStatus="需审核未激活"};
        var c=new Client{Products=[first],FreshProducts=[fresh]};var r=await Run(c,previous:joining?PreviousJoin():Previous());Assert.Equal(RowStatus.NeedsReview,r.Status);Assert.Contains("审核未激活",r.Message);Assert.DoesNotContain("prepare",c.Calls);Assert.DoesNotContain("save",c.Calls);
    }
    [Theory][InlineData(75)][InlineData(80)] public async Task ReadOnlyUnderReviewIsNeverSkippedOrInspected(decimal price)
    {
        var c=new Client{Products=[Product(price:price) with{ListingStatus="需审核未激活"}]};var r=await new ZiniaoReadOnlyExecutor(c).ExecuteAsync(Input() with{ItemId="456"},default);Assert.Equal(RowStatus.NeedsReview,r.Status);Assert.Contains("审核未激活",r.Message);
    }
    [Fact] public async Task LegacySharedPriceAppliesToListingButRejectsConflictingTargets()
    {
        var c=new Client{Products=[Product() with{SharedSkus=["SKU-A","SKU-B"]}]};var e=new ZiniaoPriceCutExecutor(c);
        var conflicting=await e.ExecuteAsync(Input(),_=>{},default,batchInputs:[Input(),Input() with{Sku="SKU-B",TargetPrice=76}]);Assert.Equal(RowStatus.NeedsReview,conflicting.Status);Assert.DoesNotContain("prepare",c.Calls);
        var named=await e.ExecuteAsync(Input(),_=>{},default,batchInputs:[Input(),Input() with{Store="测试",Sku="SKU-B",TargetPrice=76}]);Assert.Equal(RowStatus.NeedsReview,named.Status);Assert.DoesNotContain("prepare",c.Calls);
        var single=await e.ExecuteAsync(Input(),_=>{},default,batchInputs:[Input()]);Assert.Equal(RowStatus.Succeeded,single.Status);c.Calls.Clear();
        var valid=await e.ExecuteAsync(Input(),_=>{},default,batchInputs:[Input(),Input() with{Sku="SKU-B"}]);Assert.Equal(RowStatus.Succeeded,valid.Status);Assert.Single(c.Calls,x=>x=="save");
    }
    [Theory][InlineData("R$ 74,00","R$ 100,00")][InlineData("R$ 75,00","R$ 101,00")]
    public async Task ManagedFallbackNeverRetriesSaveOrHidesChangedBase(string price,string original)
    {
        var c=new Client{Products=[Product() with{Promotions=[],ManagedPriceText=price,BasePriceText=original,ListingStatus="需审核未激活"}]};
        Assert.Equal(RowStatus.NeedsReview,(await Run(c,previous:Previous())).Status);Assert.DoesNotContain("save",c.Calls);
    }
    [Fact] public async Task MissingStoreMetadataQueuesNextRound(){var c=new Client{Stores=[]};var r=await Run(c);Assert.Equal(RowStatus.NeedsReview,r.Status);Assert.True(r.Retryable);Assert.DoesNotContain("read",c.Calls);}
    [Fact] public async Task BriefEmptyStoreListRetriesOnlyTheReadBeforeExecuting(){var c=new Client{EmptyStoreReads=1};var r=await Run(c);Assert.Equal(RowStatus.Succeeded,r.Status);Assert.Equal(2,c.StoreReads);Assert.Single(c.Calls,x=>x=="save");}
    [Fact] public async Task AmbiguousStoresNeverFallBackOrRetryIntoAnotherStore(){var c=new Client{Stores=[new("123","测试","MercadoLibre-巴西"),new("123","其他","MercadoLibre-巴西")]};var r=await Run(c);Assert.Equal(RowStatus.NeedsReview,r.Status);Assert.Equal(1,c.StoreReads);Assert.DoesNotContain("read",c.Calls);}
    [Fact] public async Task SamePriceDoesNotSubmit(){var c=new Client();Assert.Equal(RowStatus.Skipped,(await Run(c,Input() with{TargetPrice=80})).Status);Assert.DoesNotContain("save",c.Calls);}
    [Fact] public async Task DifferentIdsWithExactSkuSplitBeforeAnySave(){var c=new Client{Products=[Product(),Product(id:"789")]};var r=await Run(c);Assert.Equal(new[]{"456","789"},r.SplitInputs!.Select(x=>x.ItemId));Assert.All(r.SplitInputs!,x=>Assert.Equal(Input(),x with{ItemId=null}));Assert.DoesNotContain("prepare",c.Calls);}
    [Fact] public void SplitCreatesIndependentDurableRowsAndRejectsChangedTarget()
    {
        var dir=Path.Combine(Path.GetTempPath(),"split-"+Guid.NewGuid());try{var s=new JobStore(dir);var j=s.Create(StorageTests.Snapshot(1),ExecutionMode.PriceCut);s.TryStart(j.Summary.Id);var row=s.ClaimNext(j.Summary.Id)!;var input=row.Source.Input!;Assert.Throws<AppException>(()=>s.SplitPriceCutRow(j.Summary.Id,row.Id,[input with{ItemId="456"},input with{ItemId="789",TargetPrice=input.TargetPrice+1}]));s.SplitPriceCutRow(j.Summary.Id,row.Id,[input with{ItemId="456"},input with{ItemId="789"}]);var split=s.Get(j.Summary.Id);Assert.Equal(2,split.Summary.Total);Assert.Equal(2,split.Rows.Length);Assert.All(split.Rows,r=>{Assert.Equal(RowStatus.Pending,r.Status);Assert.Null(r.PriceCut);Assert.Equal(0,r.Attempts);});Assert.Equal(new[]{"456","789"},split.Rows.Select(r=>r.Source.Input!.ItemId));}finally{Directory.Delete(dir,true);}
    }
    [Fact] public async Task CandidateProposalCannotBeEdited(){var p=Product();var c=new Client{Products=[p with{Promotions=[p.Promotions[0] with{Active=false,Editable=false}]}]};Assert.Equal(RowStatus.Skipped,(await Run(c)).Status);Assert.DoesNotContain("save",c.Calls);}
    [Fact] public async Task IdenticalExactSkuRowsAreOneProduct(){var p=Product();var c=new Client{Products=[p,p]};Assert.Equal(RowStatus.Succeeded,(await Run(c)).Status);Assert.Single(c.Calls,x=>x=="save");}
    [Fact] public async Task UnidentifiedCandidatesAlreadyBelowTargetNeverNeedASave(){var c=new Client{Products=[Product("",price:70),Product("",id:"789",price:72)]};Assert.Equal(RowStatus.Skipped,(await Run(c)).Status);Assert.DoesNotContain("prepare",c.Calls);}
    [Fact] public async Task UnlabelledDuplicateLinksRequireExplicitProductId()
    {
        var c=new Client{Products=[Product("", "456"),Product("","789")]};var review=await Run(c);Assert.Equal(RowStatus.NeedsReview,review.Status);Assert.Contains("456",review.Message);Assert.Contains("789",review.Message);Assert.DoesNotContain("save",c.Calls);
        c.Calls.Clear();Assert.Equal(RowStatus.Succeeded,(await Run(c,Input() with{ItemId="456"})).Status);
    }
    [Fact] public async Task ConfirmedIdCollapsesOnlyIdenticalDuplicateRows()
    {
        var p=Product();var identical=new Client{Products=[p,p]};Assert.Equal(RowStatus.Succeeded,(await Run(identical,Input() with{ItemId="456"})).Status);
        var different=new Client{Products=[p,p with{Promotions=[p.Promotions[0] with{PriceText="R$ 79,00"}]}]};Assert.Equal(RowStatus.NeedsReview,(await Run(different,Input() with{ItemId="456"})).Status);Assert.DoesNotContain("save",different.Calls);
    }
    [Fact] public async Task SoleUnlabelledLinkStillRequiresConfirmedProductId()
    {
        var proposal=Product("","789") with{Promotions=[new("CANDIDATE-1","建议","新提案","R$ 60,00",false)]};
        var c=new Client{Products=[proposal,Product("")]};PriceCutPlan? saved=null;
        var review=await Run(c,checkpoint:p=>saved=p);Assert.Equal(RowStatus.NeedsReview,review.Status);Assert.Contains("456",review.Message);Assert.Null(saved);Assert.DoesNotContain("save",c.Calls);
        Assert.Equal(RowStatus.Succeeded,(await Run(c,Input() with{ItemId="456"},checkpoint:p=>saved=p)).Status);Assert.Equal("456",saved!.Product.ItemId);
    }
    [Fact] public async Task ActiveEditablePromotionDoesNotRequireAttractiveLabelOrCampaignPrefix()
    {
        var product=Product() with{Promotions=[new("PROMO-1","普通活动","激活","R$ 80,00",true,true,false)]};
        var c=new Client{Products=[product]};var r=await Run(c);Assert.Equal(RowStatus.Succeeded,r.Status);Assert.Equal("PROMO-1",r.Observation!.Promotions.Single().Id);Assert.Contains("save",c.Calls);
    }
    [Fact] public async Task SeveralLinksOnOneUnlabelledProductCannotBeHiddenByFiltering()
    {
        var a=Product("","789");a=a with{Promotions=[..a.Promotions,a.Promotions[0] with{Id="C-2"}]};
        var c=new Client{Products=[a,Product("")]};Assert.Equal(RowStatus.NeedsReview,(await Run(c)).Status);Assert.DoesNotContain("save",c.Calls);
    }
    [Fact] public async Task CheckpointFailurePreventsSaving(){var c=new Client();Assert.Equal(RowStatus.NeedsReview,(await Run(c,checkpoint:_=>throw new IOException("disk full"))).Status);Assert.DoesNotContain("save",c.Calls);}
    [Theory][InlineData(true,false)][InlineData(false,true)]
    public async Task UncertainSubmissionQueuesRetryWithoutAnotherSaveInThisAttempt(bool saveThrows,bool verifyThrows)
    {
        var c=new Client{SaveThrows=saveThrows,VerifyThrows=verifyThrows};var r=await Run(c);Assert.Equal(RowStatus.NeedsReview,r.Status);Assert.False(r.PauseBatch);Assert.True(r.Retryable);Assert.Single(c.Calls,x=>x=="save");Assert.Null(r.Observation!.CurrentPrice);
    }
    [Theory][InlineData(80,"R$ 100,00")][InlineData(75,"R$ 110,00")]
    public async Task OldPriceOrChangedBaseIsNotSuccess(int after,string afterBase){var c=new Client{After=after,AfterBase=afterBase};var r=await Run(c);Assert.Equal(RowStatus.NeedsReview,r.Status);Assert.False(r.PauseBatch);Assert.Equal(afterBase=="R$ 100,00",r.Retryable);Assert.Single(c.Calls,x=>x=="save");}
    static PriceCutPlan Previous()=>new(new("123","测试","MercadoLibre-巴西-本土"),Input(),Product(),"C-1",80,100);
    [Fact] public async Task RetryReadsTargetPriceAndDoesNotSubmitAgain()
    {
        var c=new Client{Products=[Product(price:75)]};var r=await Run(c,previous:Previous());Assert.Equal(RowStatus.Succeeded,r.Status);Assert.DoesNotContain("prepare",c.Calls);Assert.DoesNotContain("save",c.Calls);
    }
    [Fact] public async Task RetryOfUnchangedOldPriceRequiresTwoFreshReadsBeforeOneSave()
    {
        var c=new Client();var r=await Run(c,previous:Previous());Assert.Equal(RowStatus.Succeeded,r.Status);Assert.Equal(new[]{"read","read","prepare","checkpoint","save","verify"},c.Calls);
    }
    [Fact] public async Task RetryNeverResubmitsWhenOriginalPriceChanged()
    {
        var c=new Client{Products=[Product() with{BasePriceText="R$ 110,00"}]};Assert.Equal(RowStatus.NeedsReview,(await Run(c,previous:Previous())).Status);Assert.DoesNotContain("save",c.Calls);
    }
    [Fact] public async Task UnknownSubmissionCannotBecomeSkippedWhenProductIsTemporarilyMissing()
    {
        var c=new Client{Products=[]};var r=await Run(c,previous:Previous());Assert.Equal(RowStatus.NeedsReview,r.Status);Assert.True(r.Retryable);Assert.DoesNotContain("save",c.Calls);
    }
    [Fact] public void DurablePlanSurvivesInterruptedRecovery()
    {
        var dir=Path.Combine(Path.GetTempPath(),"cut-"+Guid.NewGuid());try{var s=new JobStore(dir);var snapshot=StorageTests.Snapshot(1);snapshot.Rows[0]=snapshot.Rows[0] with{Input=Input()};var j=s.Create(snapshot,ExecutionMode.PriceCut);s.TryStart(j.Summary.Id);var row=s.ClaimNext(j.Summary.Id)!;var plan=new PriceCutPlan(new("123","测试","MercadoLibre-巴西-本土"),Input(),Product(),"C-1",80,100);s.SavePriceCutPlan(j.Summary.Id,row.Id,plan);s.RecoverInterrupted();var recovered=s.Get(j.Summary.Id).Rows[0];Assert.Equal(System.Text.Json.JsonSerializer.Serialize(plan),System.Text.Json.JsonSerializer.Serialize(recovered.PriceCut));Assert.Equal(RowStatus.NeedsReview,recovered.Status);Assert.False(s.TryStart(j.Summary.Id));}finally{Directory.Delete(dir,true);}
    }
}
