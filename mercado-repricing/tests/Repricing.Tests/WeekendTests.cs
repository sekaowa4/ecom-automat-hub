using Repricing.Execution;
namespace Repricing.Tests;
public class WeekendTests
{
    sealed class Client:IZiniaoPriceCutClient
    {
        public int Saves,Reads,WeekendReads;public bool LoseSaveResponse;public bool GroupTimeout;public ZiniaoProduct[] Products=[];public ZiniaoProduct[]? WeekendProducts;public List<string> Prepared=[],Submitted=[];
        public ZiniaoProduct[] ManagerProducts=[],ChildProducts=[];
        public Task<ZiniaoProduct[]> ReadManagerAsync(ZiniaoStore s,PriceInput i,CancellationToken ct)=>Task.FromResult(ManagerProducts);
        public Task<ZiniaoStore[]> StoresAsync(CancellationToken ct)=>Task.FromResult(new[]{new ZiniaoStore("123","测试店","MercadoLibre-巴西")});
        public Task<ZiniaoProduct[]> ReadAsync(ZiniaoStore s,PriceInput i,CancellationToken ct){Reads++;return Task.FromResult(Products);}
        public Task<ZiniaoProduct[]> ReadWeekendAsync(ZiniaoStore s,PriceInput i,CancellationToken ct){WeekendReads++;if(GroupTimeout)throw new AppException("读取 SKU-A 超时：等待分组子商品展开；未提交价格。",409);return Task.FromResult(ChildProducts.Any(p=>p.ItemId==i.ItemId)?ChildProducts.Select(p=>Saves>0?p with{Promotions=p.Promotions.Select(c=>c with{Status="已预约",PriceText="R$ 75,00",Joinable=false}).ToArray()}:p).ToArray():WeekendProducts??Products);}
        public Task PrepareAsync(PriceCutPlan p,CancellationToken ct){Prepared.Add(p.PromotionId);return Task.CompletedTask;}
        public Task SubmitAsync(PriceCutPlan p,CancellationToken ct){Saves++;Submitted.Add(p.PromotionId);ZiniaoProduct[] Update(ZiniaoProduct[] rows)=>rows.Select(x=>x with{Promotions=x.Promotions.Select(c=>c.Id==p.PromotionId?c with{Status="已预约",PriceText="R$ 75,00",Joinable=false}:c).ToArray()}).ToArray();Products=Update(Products);if(WeekendProducts!=null)WeekendProducts=Update(WeekendProducts);if(LoseSaveResponse)throw new AppException("连接中断",409);return Task.CompletedTask;}
        public Task<ZiniaoProduct> VerifyAsync(PriceCutPlan p,CancellationToken ct)=>throw new Exception("周末报名应核验指定活动，不能要求其接管所有活动售价");
        public Task CloseAsync()=>Task.CompletedTask;
    }
    static PriceInput Input()=>StorageTests.Input(75) with{Store="123",Sku="SKU-A"};
    [Fact] public async Task SharedConflictResolvesExpandedIndependentSkuBeforeRejecting()
    {
        var child=Product() with{ItemId="789"};var c=new Client{Products=[Product() with{SharedSkus=["SKU-A","SKU-B"]}],ManagerProducts=[child],ChildProducts=[child]};PriceCutPlan? saved=null;
        var r=await new WeekendEnrollmentExecutor(c).ExecuteAsync(Input(),Period(),p=>saved=p,default,batchInputs:[Input(),Input() with{Sku="SKU-B",TargetPrice=76}]);
        Assert.Equal(RowStatus.Succeeded,r.Status);Assert.Equal("789",saved!.Product.ItemId);Assert.Null(saved.Product.SharedSkus);Assert.Equal(1,c.Saves);
    }
    [Fact] public async Task UnknownSaveCanRecoverOnlyAfterTwoUnchangedJoinableReadbacks()
    {
        var c=new Client{Products=[Product()]};var p=c.Products[0];var previous=new PriceCutPlan(new("123","测试店","MercadoLibre-巴西"),Input(),p,"C-weekend",100,100,Join:true,Stage:"weekend-enroll");
        var r=await new WeekendEnrollmentExecutor(c).ExecuteAsync(Input(),Period(),_=>{},default,previous);
        Assert.Equal(RowStatus.Succeeded,r.Status);Assert.Equal(3,c.WeekendReads);Assert.Equal(1,c.Saves);
        c=new Client{Products=[p with{Promotions=p.Promotions.Select(x=>x with{PriceText="R$ 79"}).ToArray()}]};
        r=await new WeekendEnrollmentExecutor(c).ExecuteAsync(Input(),Period(),_=>{},default,previous);
        Assert.Equal(RowStatus.NeedsReview,r.Status);Assert.Equal(0,c.Saves);
    }
    [Fact] public async Task EmptyPromotionGroupExplainsExactSkuUnderReviewFromManager()
    {
        var c=new Client{GroupTimeout=true,ManagerProducts=[Product() with{ListingStatus="需审核未激活：品牌不匹配"}]};
        var r=await new WeekendEnrollmentExecutor(c).ExecuteAsync(Input(),Period(),_=>{},default);
        Assert.Contains("品牌不匹配",r.Message);Assert.Equal(0,c.Saves);Assert.Equal("456",r.Observation!.ItemId);
    }
    [Fact] public void RecoveredWeekendCheckpointKeepsSamePlanAndOnlyOneSavePerAttempt()
    {
        var dir=Path.Combine(Path.GetTempPath(),"weekend-recovered-"+Guid.NewGuid());
        try{
            var store=new Repricing.Storage.JobStore(dir);var job=store.Create(StorageTests.Snapshot(1),ExecutionMode.WeekendEnrollment);store.TryStart(job.Summary.Id);var row=store.ClaimNext(job.Summary.Id)!;var input=row.Source.Input!;
            var plan=new PriceCutPlan(new(input.Store,"测试","MercadoLibre-巴西"),input,Product() with{Sku=input.Sku},"C-weekend",100,100,Join:true,Stage:"weekend-enroll");
            store.SavePriceCutPlan(job.Summary.Id,row.Id,plan);store.UpdateRow(job.Summary.Id,row.Id,new(RowStatus.NeedsReview,"周末活动报名","提交未知","weekend-review"));store.ClaimNext(job.Summary.Id);
            store.RetryWeekendReviews(job.Summary.Id);store.TryStart(job.Summary.Id);store.ClaimNext(job.Summary.Id);
            Assert.Throws<AppException>(()=>store.SavePriceCutPlan(job.Summary.Id,row.Id,plan with{BasePrice=101}));
            store.SavePriceCutPlan(job.Summary.Id,row.Id,plan);
            Assert.Throws<AppException>(()=>store.SavePriceCutPlan(job.Summary.Id,row.Id,plan));
        }finally{Directory.Delete(dir,true);}
    }
    [Fact] public void RetryCompletedWeekendKeepsSuccessfulRowsAndSubmissionCheckpoint()
    {
        var dir=Path.Combine(Path.GetTempPath(),"weekend-repair-"+Guid.NewGuid());
        try{
            var store=new Repricing.Storage.JobStore(dir);var job=store.Create(StorageTests.Snapshot(2),ExecutionMode.WeekendEnrollment);store.TryStart(job.Summary.Id);
            var done=store.ClaimNext(job.Summary.Id)!;store.UpdateRow(job.Summary.Id,done.Id,new(RowStatus.Succeeded,"周末活动报名","已核验","weekend-verified"));
            var unknown=store.ClaimNext(job.Summary.Id)!;var input=unknown.Source.Input!;
            var product=new ZiniaoProduct("456",input.Sku,"产品","R$ 100",[new("C-weekend",Period().Label,"","R$ 80",false,Joinable:true)]);
            var plan=new PriceCutPlan(new(input.Store,"测试","MercadoLibre-巴西"),input,product,"C-weekend",100,100,Join:true,Stage:"weekend-enroll");
            store.SavePriceCutPlan(job.Summary.Id,unknown.Id,plan);store.UpdateRow(job.Summary.Id,unknown.Id,new(RowStatus.NeedsReview,"周末活动报名","提交未知","weekend-review"));Assert.Null(store.ClaimNext(job.Summary.Id));
            Assert.True(store.RetryWeekendReviews(job.Summary.Id));var fresh=store.Get(job.Summary.Id);Assert.Equal(RowStatus.Succeeded,fresh.Rows[0].Status);Assert.Equal(RowStatus.RetryPending,fresh.Rows[1].Status);Assert.Equal(System.Text.Json.JsonSerializer.Serialize(plan),System.Text.Json.JsonSerializer.Serialize(fresh.Rows[1].PriceCut));Assert.Equal(unknown.Attempts,fresh.Rows[1].Attempts);Assert.True(store.TryStart(job.Summary.Id));Assert.Equal(unknown.Id,store.ClaimNext(job.Summary.Id)!.Id);
        }finally{Directory.Delete(dir,true);}
    }
    static WeekendPeriod Period()=>WeekendActivity.Current(DateTimeOffset.UtcNow);
    static ZiniaoProduct Product(params PromotionObservation[] extra)=>new("456","SKU-A","产品","R$ 100",[new("C-weekend",Period().Label,"","R$ 80",false,Joinable:true),..extra]);
    [Fact] public async Task JoinsOnlyWeekendAndAcceptsScheduledWithoutChangingOtherCampaign(){var c=new Client{Products=[Product(new PromotionObservation("P-subsidy","补贴","激活","R$ 70",true))]};PriceCutPlan? saved=null;var r=await new WeekendEnrollmentExecutor(c).ExecuteAsync(Input(),Period(),p=>saved=p,default);Assert.Equal(RowStatus.Succeeded,r.Status);Assert.Equal("C-weekend",saved!.PromotionId);Assert.False(saved.ChangeBase);Assert.False(saved.Rejoin);Assert.Equal(1,c.Saves);Assert.Contains(c.Products[0].Promotions,p=>p.Id=="P-subsidy"&&p.Active);}
    [Fact] public async Task SharedPriceLinkEnrollsUnifiedParentWhenEverySiblingHasSameTarget()
    {
        var c=new Client{Products=[Product() with{SharedSkus=["SKU-A","SKU-B"]}]};PriceCutPlan? saved=null;
        var rows=new[]{Input(),Input() with{Sku="SKU-B"}};
        var executor=new WeekendEnrollmentExecutor(c);
        var result=await executor.ExecuteAsync(Input(),Period(),p=>saved=p,default,batchInputs:rows);
        Assert.Equal(RowStatus.Succeeded,result.Status);Assert.Equal("C-weekend",saved!.PromotionId);Assert.Equal("456",saved.Product.ItemId);Assert.Equal(1,c.Saves);Assert.Equal(new[]{"C-weekend"},c.Submitted);Assert.False(saved.ChangeBase);Assert.False(saved.Rejoin);
        c.Products=[c.Products[0] with{Sku="SKU-B"}];
        var sibling=await executor.ExecuteAsync(Input() with{Sku="SKU-B"},Period(),_=>throw new Exception("已经报名的父链接不得重复提交"),default,batchInputs:rows);
        Assert.Equal(RowStatus.Skipped,sibling.Status);Assert.Equal(1,c.Saves);Assert.Single(c.Submitted);
    }
    [Fact] public async Task SharedPriceLinkWithDifferentSiblingTargetsNeverSubmits()
    {
        var c=new Client{Products=[Product() with{SharedSkus=["SKU-A","SKU-B"]}]};
        var rows=new[]{Input(),Input() with{Sku="SKU-B",TargetPrice=76}};
        var result=await new WeekendEnrollmentExecutor(c).ExecuteAsync(Input(),Period(),_=>{},default,batchInputs:rows);
        Assert.Equal(RowStatus.NeedsReview,result.Status);Assert.Contains("目标价",result.Message);Assert.Equal(0,c.Saves);Assert.Empty(c.Prepared);Assert.Empty(c.Submitted);
    }
    [Fact] public async Task SharedPriceLinkWithMissingSiblingTargetNeverSubmits()
    {
        var c=new Client{Products=[Product() with{SharedSkus=["SKU-A","SKU-B"]}]};
        var result=await new WeekendEnrollmentExecutor(c).ExecuteAsync(Input(),Period(),_=>{},default,batchInputs:[Input()]);
        Assert.Equal(RowStatus.NeedsReview,result.Status);Assert.Contains("SKU-B",result.Message);Assert.Equal(0,c.Saves);Assert.Empty(c.Prepared);
    }
    [Fact] public async Task ExpandsMorePromotionsForWeekendOnlyAndEnrollsTheExactDateWithoutTouchingExistingCampaigns()
    {
        var active=new PromotionObservation("C-existing","日常活动","激活","R$ 90",true,Editable:true);
        var hiddenWeekend=new PromotionObservation("C-weekend",Period().Label,"","R$ 80",false,Joinable:true);
        var visible=Product(active) with{Promotions=[active]};var expanded=Product(hiddenWeekend,active) with{Promotions=[hiddenWeekend,active]};
        var c=new Client{Products=[visible],WeekendProducts=[expanded]};
        var result=await new WeekendEnrollmentExecutor(c).ExecuteAsync(Input(),Period(),_=>{},default);
        Assert.True(result.Status==RowStatus.Succeeded,$"{result.Status}: {result.Message}; weekend reads {c.WeekendReads}, saves {c.Saves}");Assert.Equal(2,c.WeekendReads);Assert.Equal(1,c.Saves);
        Assert.Equal(new[]{"C-weekend"},c.Prepared);Assert.Equal(new[]{"C-weekend"},c.Submitted);
        Assert.Contains(c.WeekendProducts![0].Promotions,p=>p.Id=="C-existing"&&p.Active);
    }
    [Fact] public async Task UnknownSaveReadsBeforeDoingAnythingElse(){var c=new Client{Products=[Product()],LoseSaveResponse=true};PriceCutPlan? saved=null;var executor=new WeekendEnrollmentExecutor(c);Assert.Equal(RowStatus.NeedsReview,(await executor.ExecuteAsync(Input(),Period(),p=>saved=p,default)).Status);var r=await executor.ExecuteAsync(Input(),Period(),_=>throw new Exception("must not resubmit"),default,saved);Assert.Equal(RowStatus.Succeeded,r.Status);Assert.Equal(1,c.Saves);}
    [Fact] public async Task AmbiguousSameDateNeverSubmits(){var c=new Client{Products=[Product(new PromotionObservation("C-other",Period().Label,"","R$ 80",false,Joinable:true))]};var r=await new WeekendEnrollmentExecutor(c).ExecuteAsync(Input(),Period(),_=>{},default);Assert.Equal(RowStatus.NeedsReview,r.Status);Assert.Equal(0,c.Saves);}
    [Fact] public async Task SameDateUnjoinableProposalDoesNotHideOrdinaryCampaign(){var c=new Client{Products=[Product(new PromotionObservation("C-proposal",Period().Label,"","R$ 70",false))]};var r=await new WeekendEnrollmentExecutor(c).ExecuteAsync(Input(),Period(),_=>{},default);Assert.Equal(RowStatus.Succeeded,r.Status);Assert.Equal(1,c.Saves);}
    [Fact] public async Task NoMatchingDateDoesNotChooseOtherOrdinary(){var c=new Client{Products=[Product() with{Promotions=[new("C-other","1.2-1.4","","R$ 80",false,Joinable:true)]}]};var r=await new WeekendEnrollmentExecutor(c).ExecuteAsync(Input(),Period(),_=>{},default);Assert.Equal(RowStatus.NeedsReview,r.Status);Assert.Equal(0,c.Saves);}
    [Fact] public async Task MissingWeekendActivityReviewIncludesExactPageReadback(){var other=new PromotionObservation("C-other","1.2-1.4","","R$ 80",false,Joinable:true);var c=new Client{Products=[Product() with{Promotions=[other]}]};var r=await new WeekendEnrollmentExecutor(c).ExecuteAsync(Input(),Period(),_=>{},default);Assert.Equal(RowStatus.NeedsReview,r.Status);Assert.Equal("456",r.Observation!.ItemId);Assert.Contains(r.Observation.Promotions,p=>p.Id=="C-other"&&p.Name=="1.2-1.4");Assert.Equal(0,c.Saves);}
    [Fact] public void WeekendJobFreezesDatesAndRejectsDailyPowers(){var dir=Path.Combine(Path.GetTempPath(),"weekend-"+Guid.NewGuid());try{var store=new Repricing.Storage.JobStore(dir);var snap=StorageTests.Snapshot(1);var job=store.Create(snap,ExecutionMode.WeekendEnrollment);Assert.Equal(Period(),job.Summary.Weekend);Assert.Equal(Period(),new Repricing.Storage.JobStore(dir).Get(job.Summary.Id).Summary.Weekend);Assert.Throws<AppException>(()=>store.Create(StorageTests.Snapshot(1),ExecutionMode.WeekendEnrollment,true,true));}finally{Directory.Delete(dir,true);}}
    [Fact] public void WeekendCheckpointAllowsSharedParentOnlyForAllEqualTargetRows()
    {
        var dir=Path.Combine(Path.GetTempPath(),"weekend-shared-"+Guid.NewGuid());
        try
        {
            var store=new Repricing.Storage.JobStore(dir);var snap=StorageTests.Snapshot(2);
            snap.Rows[0]=snap.Rows[0] with{Input=StorageTests.Input(75) with{Sku="SKU-A"}};
            snap.Rows[1]=snap.Rows[1] with{Input=StorageTests.Input(75) with{Sku="SKU-B"}};
            var job=store.Create(snap,ExecutionMode.WeekendEnrollment);store.TryStart(job.Summary.Id);var row=store.ClaimNext(job.Summary.Id)!;var input=row.Source.Input!;
            var campaign=new PromotionObservation("C-weekend",Period().Label,"","R$ 80",false,Joinable:true);
            var product=new ZiniaoProduct("456",input.Sku,"产品","R$ 100",[campaign],["SKU-A","SKU-B"]);
            var plan=new PriceCutPlan(new(input.Store,"测试","MercadoLibre-巴西"),input,product,campaign.Id,100,100,Join:true,Stage:"weekend-enroll");
            store.SavePriceCutPlan(job.Summary.Id,row.Id,plan);
            Assert.Equal("weekend-before-submit",store.Get(job.Summary.Id).Rows[0].Checkpoint);
            var badDir=Path.Combine(Path.GetTempPath(),"weekend-shared-bad-"+Guid.NewGuid());
            try
            {
                var badStore=new Repricing.Storage.JobStore(badDir);var badSnap=StorageTests.Snapshot(2);
                badSnap.Rows[0]=badSnap.Rows[0] with{Input=StorageTests.Input(75) with{Sku="SKU-A"}};
                badSnap.Rows[1]=badSnap.Rows[1] with{Input=StorageTests.Input(76) with{Sku="SKU-B"}};
                var badJob=badStore.Create(badSnap,ExecutionMode.WeekendEnrollment);badStore.TryStart(badJob.Summary.Id);var badRow=badStore.ClaimNext(badJob.Summary.Id)!;var badInput=badRow.Source.Input!;
                Assert.Throws<AppException>(()=>badStore.SavePriceCutPlan(badJob.Summary.Id,badRow.Id,plan with{Input=badInput,Product=product with{Sku=badInput.Sku}}));
            }
            finally{Directory.Delete(badDir,true);}
        }
        finally{Directory.Delete(dir,true);}
    }
    [Fact] public void WeekendResumeRetriesOnlyKnownPreSubmitMisses()
    {
        var dir=Path.Combine(Path.GetTempPath(),"weekend-retry-"+Guid.NewGuid());
        try
        {
            var store=new Repricing.Storage.JobStore(dir);var snap=StorageTests.Snapshot(4);
            for(var i=0;i<snap.Rows.Length;i++)snap.Rows[i]=snap.Rows[i] with{Input=snap.Rows[i].Input! with{Sku=$"SKU-{i}"}};
            var job=store.Create(snap,ExecutionMode.WeekendEnrollment);store.TryStart(job.Summary.Id);
            var missed=store.ClaimNext(job.Summary.Id)!;store.UpdateRow(job.Summary.Id,missed.Id,new(RowStatus.NeedsReview,"周末活动报名","未找到日期为 10.23-10.25 的普通活动。","weekend-review"));
            var shared=store.ClaimNext(job.Summary.Id)!;store.UpdateRow(job.Summary.Id,shared.Id,new(RowStatus.NeedsReview,"周末活动报名","未按父链接统一报名。","weekend-review"));
            var uncertain=store.ClaimNext(job.Summary.Id)!;var input=uncertain.Source.Input!;var campaign=new PromotionObservation("C-weekend",Period().Label,"","R$ 80",false,Joinable:true);
            var plan=new PriceCutPlan(new(input.Store,"测试","MercadoLibre-巴西"),input,new("456",input.Sku,"产品","R$ 100",[campaign]),campaign.Id,100,100,Join:true,Stage:"weekend-enroll");store.SavePriceCutPlan(job.Summary.Id,uncertain.Id,plan);
            store.UpdateRow(job.Summary.Id,uncertain.Id,new(RowStatus.NeedsReview,"周末活动报名","提交结果未知。","weekend-review"));
            Assert.True(store.RequestPause(job.Summary.Id));Assert.Null(store.ClaimNext(job.Summary.Id));Assert.Equal(JobStatus.Paused,store.Get(job.Summary.Id).Summary.Status);
            Assert.True(store.TryStart(job.Summary.Id));Assert.Equal(RowStatus.RetryPending,store.Get(job.Summary.Id).Rows.Single(r=>r.Id==missed.Id).Status);
            var remaining=store.ClaimNext(job.Summary.Id)!;Assert.Equal("SKU-3",remaining.Source.Input!.Sku);store.UpdateRow(job.Summary.Id,remaining.Id,new(RowStatus.Skipped,"周末活动报名","已存在。","weekend-verified"));
            var retry=store.ClaimNext(job.Summary.Id)!;
            Assert.Equal(missed.Id,retry.Id);Assert.Equal(2,retry.Attempts);Assert.Equal(RowStatus.NeedsReview,store.Get(job.Summary.Id).Rows.Single(r=>r.Id==uncertain.Id).Status);
        }
        finally{Directory.Delete(dir,true);}
    }
    [Theory][InlineData("2026-10-23T06:00:00Z","2026-10-23","2026-10-25")][InlineData("2026-10-25T10:00:00Z","2026-10-23","2026-10-25")][InlineData("2026-10-25T17:00:00Z","2026-10-30","2026-11-01")][InlineData("2026-12-31T06:00:00Z","2027-01-01","2027-01-03")]
    public void CalendarUsesBeijingWeek(string now,string start,string end){var p=WeekendActivity.Current(DateTimeOffset.Parse(now));Assert.Equal(DateOnly.Parse(start),p.Start);Assert.Equal(DateOnly.Parse(end),p.End);}
    [Theory][InlineData("C-1","10.23-10.25\n23/10月至25/10月",true)][InlineData("C-1","1023-1025\n23至25/10月",true)][InlineData("C-1","10.23-10.25\n23至25/10月",true)][InlineData("C-1","1023-1025\n22至25/10月",false)][InlineData("P-1","10.23-10.25",false)][InlineData("C-1","10.23-10.25\n22/10月至25/10月",false)][InlineData("C-1","10.23-10.25\n23/10/2025至25/10/2025",false)][InlineData("C-1","10.16-10.18",false)][InlineData("C-1","新提案！10.23-10.25",false)]
    [InlineData("C-1","1023-102523至25/10月",true)]
    [InlineData("C-1","1023-102522至25/10月",false)]
    [InlineData("C-1","10.23-10.2523/10月至25/10月",true)]
    [InlineData("C-1","1023-102523/10/2025至25/10/2025",false)]
    [InlineData("C-1","1023-1025任意文字",false)]
    public void OnlyExactOrdinaryDatesMatch(string id,string name,bool expected)=>Assert.Equal(expected,WeekendActivity.Matches(new(id,name,"","R$ 80",false),new(new(2026,10,23),new(2026,10,25))));
}
