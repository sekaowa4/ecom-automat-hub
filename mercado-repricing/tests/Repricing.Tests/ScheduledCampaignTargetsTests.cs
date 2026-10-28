using Repricing.Execution;
using Repricing.Storage;

namespace Repricing.Tests;

public class ScheduledCampaignTargetsTests
{
    static PriceInput Input(string? promotionId=null)=>new("123","MLB","BRL","NW2601116LE","6765061492",null,promotionId,39.98m,null,false,false,null,"降价");
    static ZiniaoProduct Product(decimal scheduled=44.12m)=>new("6765061492","NW2601116LE","Kit 6 Discos","R$ 51,90",[
        new("C-ACTIVE","9.28-10.28\n28/9月至28/10月","激活","R$ 39,98",true,true),
        new("C-SCHEDULED","10.27-11.26\n27/10月至26/11月","已预约",$"R$ {scheduled:0.00}".Replace('.',','),false,true),
        new("C-PROPOSAL","11.11\n19/10月至12/11月","新提案","R$ 38,78",false,false,false,true)
    ]);

    sealed class Client:IZiniaoPriceCutClient
    {
        public ZiniaoProduct Current=Product();
        public List<string> Calls=[];
        public Task<ZiniaoStore[]> StoresAsync(CancellationToken ct)=>Task.FromResult(new[]{new ZiniaoStore("123","测试店铺","MercadoLibre-巴西")});
        public Task<ZiniaoProduct[]> ReadAsync(ZiniaoStore store,PriceInput input,CancellationToken ct){Calls.Add("read");return Task.FromResult(new[]{Current});}
        public Task PrepareAsync(PriceCutPlan plan,CancellationToken ct){Calls.Add("prepare:"+plan.PromotionId+":"+plan.Stage);return Task.CompletedTask;}
        public Task SubmitAsync(PriceCutPlan plan,CancellationToken ct){Calls.Add("save:"+plan.PromotionId);Current=Current with{Promotions=Current.Promotions.Select(p=>p.Id==plan.PromotionId?p with{PriceText=$"R$ {plan.Input.TargetPrice:0.00}".Replace('.',','),Status="已预约",Active=false}:p).ToArray()};return Task.CompletedTask;}
        public Task<ZiniaoProduct> VerifyAsync(PriceCutPlan plan,CancellationToken ct){Calls.Add("verify:"+plan.PromotionId);return Task.FromResult(Current);}
    }

    [Fact] public async Task ActiveAndScheduledCampaignsSplitIntoExactPerCampaignRows()
    {
        var client=new Client();
        var result=await new ZiniaoPriceCutExecutor(client).ExecuteAsync(Input(),_=>{},default);
        Assert.Equal(RowStatus.Skipped,result.Status);
        Assert.Equal(new[]{"C-ACTIVE","C-SCHEDULED"},result.SplitInputs!.Select(x=>x.PromotionId));
        Assert.All(result.SplitInputs!,x=>Assert.Equal(39.98m,x.TargetPrice));
        Assert.DoesNotContain("C-PROPOSAL",result.SplitInputs!.Select(x=>x.PromotionId));
        Assert.DoesNotContain("save:C-ACTIVE",client.Calls);
    }

    [Fact] public async Task MixedModeSplitsBothCampaignsWhenOneNeedsAnIncrease()
    {
        var client=new Client{Current=Product(30m)};
        var result=await new ZiniaoPriceCutExecutor(client).ExecuteAsync(Input(),_=>{},default,allowIncrease:true,allowBaseChange:true);
        Assert.Equal(RowStatus.Skipped,result.Status);
        Assert.Equal(2,result.SplitInputs!.Length);
        Assert.DoesNotContain(client.Calls,c=>c.StartsWith("prepare:",StringComparison.Ordinal)||c.StartsWith("save:",StringComparison.Ordinal));
    }

    [Fact] public async Task ScheduledCampaignIsSetToTargetWhileActiveCampaignIsPreserved()
    {
        var client=new Client();
        var result=await new ZiniaoPriceCutExecutor(client).ExecuteAsync(Input("C-SCHEDULED"),_=>{},default,allowIncrease:true,allowBaseChange:true);
        Assert.Equal(RowStatus.Succeeded,result.Status);
        Assert.Equal(new[]{"read","prepare:C-SCHEDULED:scheduled-edit","save:C-SCHEDULED","verify:C-SCHEDULED"},client.Calls);
        Assert.Contains(client.Current.Promotions,p=>p.Id=="C-ACTIVE"&&p.Active&&p.PriceText=="R$ 39,98");
        Assert.Contains(client.Current.Promotions,p=>p.Id=="C-SCHEDULED"&&!p.Active&&ZiniaoReadOnlyExecutor.Scheduled(p)&&p.PriceText=="R$ 39,98");
        Assert.Contains("预约活动",result.Message);
    }

    [Fact] public void ScheduledReadbackRequiresTheActiveCampaignToRemainUnchanged()
    {
        var original=Product();var input=Input("C-SCHEDULED");
        var plan=new PriceCutPlan(new("123","测试店铺","MercadoLibre-巴西"),input,original,"C-SCHEDULED",44.12m,51.90m,Stage:"scheduled-edit");
        var saved=original with{Promotions=original.Promotions.Select(p=>p.Id=="C-SCHEDULED"?p with{PriceText="R$ 39,98"}:p).ToArray()};
        Assert.Null(PriceReadback.Failure(plan,saved,51.90m,"C-SCHEDULED"));
        var changedActive=saved with{Promotions=saved.Promotions.Select(p=>p.Id=="C-ACTIVE"?p with{PriceText="R$ 38,00"}:p).ToArray()};
        Assert.Contains("生效活动",PriceReadback.Failure(plan,changedActive,51.90m,"C-SCHEDULED"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CampaignSplitPersistsBothPromotionBindingsOnSeparateRows(bool itemAlreadyBound)
    {
        var directory=Path.Combine(Path.GetTempPath(),"campaign-split-"+Guid.NewGuid());
        try
        {
            var store=new JobStore(directory);var source=StorageTests.Snapshot(1);source.Rows[0]=source.Rows[0] with{Input=Input() with{ItemId=itemAlreadyBound?"6765061492":null}};
            var job=store.Create(source,ExecutionMode.PriceCut,true,true);store.TryStart(job.Summary.Id);var row=store.ClaimNext(job.Summary.Id)!;
            var split=new[]{Input("C-ACTIVE"),Input("C-SCHEDULED")};
            Assert.Throws<AppException>(()=>store.SplitPriceCutRow(job.Summary.Id,row.Id,[split[0],split[1] with{TargetPrice=1}]));
            if(itemAlreadyBound)Assert.Throws<AppException>(()=>store.SplitPriceCutRow(job.Summary.Id,row.Id,split.Select(x=>x with{ItemId="999"}).ToArray()));
            store.SplitPriceCutRow(job.Summary.Id,row.Id,split);
            var children=store.Get(job.Summary.Id).Rows;
            Assert.Equal(new[]{"C-ACTIVE","C-SCHEDULED"},children.Select(x=>x.Source.Input!.PromotionId));
            Assert.All(children,x=>Assert.Equal("6765061492",x.Source.Input!.ItemId));
            Assert.Equal(2,store.Get(job.Summary.Id).Summary.Total);
        }
        finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }

    [Theory]
    [InlineData("6765061492",null,true)]
    [InlineData(null,null,false)]
    [InlineData("6765061492","C-ACTIVE",false)]
    public void OnlyUnsubmittedItemBoundCampaignSplitGuardCanBeRequeued(string? item,string? promotion,bool expected)
    {
        var directory=Path.Combine(Path.GetTempPath(),"campaign-split-retry-"+Guid.NewGuid());
        try
        {
            var store=new JobStore(directory);var source=StorageTests.Snapshot(1);source.Rows[0]=source.Rows[0] with{Input=Input(promotion) with{ItemId=item}};
            var job=store.Create(source,ExecutionMode.PriceCut);store.TryStart(job.Summary.Id);var row=store.ClaimNext(job.Summary.Id)!;
            store.UpdateRow(job.Summary.Id,row.Id,new(RowStatus.NeedsReview,"执行校验","拆分商品或活动记录不符合原任务范围，未提交价格。","guard-review"));
            Assert.Null(store.ClaimNext(job.Summary.Id));
            Assert.Equal(expected,store.RetryPriceCutReviews(job.Summary.Id));
            var saved=store.Get(job.Summary.Id).Rows.Single();Assert.Equal(source.Rows[0].Input,saved.Source.Input);Assert.Null(saved.PriceCut);
            Assert.Equal(expected?RowStatus.RetryPending:RowStatus.NeedsReview,saved.Status);
        }
        finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }

    [Fact] public void LegacyUnsubmittedActivityConflictCanBeRetriedWithoutPriceOrScopeChanges()
    {
        var directory=Path.Combine(Path.GetTempPath(),"campaign-retry-"+Guid.NewGuid());
        try
        {
            var store=new JobStore(directory);var source=StorageTests.Snapshot(1);source.Rows[0]=source.Rows[0] with{Input=Input() with{Action="降价"}};
            var job=store.Create(source,ExecutionMode.PriceCut);store.TryStart(job.Summary.Id);var row=store.ClaimNext(job.Summary.Id)!;
            store.UpdateRow(job.Summary.Id,row.Id,new(RowStatus.NeedsReview,"自动调价","普通活动已按表格目标价预约，尚未激活；当前生效价格仍由其他活动决定，保留待核对，未重复报名。","cut-review"));
            Assert.Null(store.ClaimNext(job.Summary.Id));Assert.Equal(JobStatus.Completed,store.Get(job.Summary.Id).Summary.Status);
            Assert.True(store.RetryPriceCutReviews(job.Summary.Id));
            var retried=store.Get(job.Summary.Id).Rows.Single();Assert.Equal(RowStatus.RetryPending,retried.Status);Assert.Equal(39.98m,retried.Source.Input!.TargetPrice);Assert.Equal("降价",retried.Source.Input.Action);Assert.Null(retried.PriceCut);
        }
        finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
}
