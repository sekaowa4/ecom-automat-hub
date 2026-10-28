using Repricing.Storage;
namespace Repricing.Tests;
public class RetryRoundTests:IDisposable
{
    readonly string dir=Path.Combine(Path.GetTempPath(),"round-"+Guid.NewGuid());
    [Fact] public void RequeuesOnlyKnownUnconfirmedShippingAndExplicitPreSubmitTimeoutRows()
    {
        var s=new JobStore(dir);var j=s.Create(StorageTests.Snapshot(5),ExecutionMode.PriceCut,mixedPrices:true,basePrices:true,incomePriority:true);
        var rows=j.Rows.ToArray();var shippingInput=rows[0].Source.Input!;
        var product=new Execution.ZiniaoProduct("1234567890",shippingInput.Sku,"Product","R$ 500",[]);
        var plan=new Execution.PriceCutPlan(new("shop","Shop","MercadoLibre-巴西"),shippingInput,product,"C-OLD",450,500,ChangeBase:true,Stage:"discount-base",DiscountBase:shippingInput.TargetPrice*4,DiscountPromotionId:"C-OFFER");
        var shipping=rows[0] with{Status=RowStatus.NeedsReview,Route="降低原价后设置折扣价",Message="已记录阶段；原价submit：平台要求确认未识别的商品或费用变更；需支付运费R$12,95。",Checkpoint="discount-base-review",Attempts=1,PriceCut=plan,PriceCutAttempt=1};
        var changedOffer=rows[1] with{Status=RowStatus.NeedsReview,Route=shipping.Route,Message="提交结果未知，活动状态已变化。",Checkpoint="discount-base-review",Attempts=1,PriceCut=plan with{Product=product with{ItemId="9999999999"}} ,PriceCutAttempt=1};
        var timeout=rows[2] with{Status=RowStatus.NeedsReview,Route="自动调价",Message="读取 SKU-2 超时：等待分组子商品展开；未提交价格。",Checkpoint="cut-review",Attempts=3};
        var disconnectedInput=rows[3].Source.Input!;var disconnectedProduct=product with{ItemId="1234567893",Sku=disconnectedInput.Sku};var disconnectedPlan=plan with{Input=disconnectedInput,Product=disconnectedProduct,DiscountBase=disconnectedInput.TargetPrice*4};
        var observation=new PriceObservation("shop",disconnectedProduct.ItemId,"Product","R$ 300",300,-200,[new PromotionObservation("C-OFFER","Offer","","R$ 285",false,Joinable:true)],DateTimeOffset.UtcNow);
        var disconnected=rows[3] with{Status=RowStatus.NeedsReview,Route="降低原价后设置折扣价",Message="已记录退出、原价或报名阶段；连接或记录失败。",Checkpoint="discount-base-review",Attempts=3,Observation=observation,PriceCut=disconnectedPlan,PriceCutAttempt=1};
        var ambiguousInput=rows[4].Source.Input!;var ambiguousProduct=product with{ItemId="1234567894",Sku=ambiguousInput.Sku};var ambiguousPlan=plan with{Input=ambiguousInput,Product=ambiguousProduct,DiscountBase=ambiguousInput.TargetPrice*4};
        var ambiguous=rows[4] with{Status=RowStatus.NeedsReview,Route="降低原价后设置折扣价",Message="连接或记录失败。",Checkpoint="discount-base-review",Observation=observation with{ItemId=ambiguousProduct.ItemId},PriceCut=ambiguousPlan,PriceCutAttempt=1};
        using(var db=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(dir,"tasks.db")};Pooling=False")){db.Open();foreach(var row in new[]{shipping,changedOffer,timeout,disconnected,ambiguous}){using var c=db.CreateCommand();c.CommandText="UPDATE job_rows SET payload=$payload WHERE id=$id";c.Parameters.AddWithValue("$payload",System.Text.Json.JsonSerializer.Serialize(row));c.Parameters.AddWithValue("$id",row.Id.ToString());c.ExecuteNonQuery();}using var job=db.CreateCommand();job.CommandText="UPDATE jobs SET status='Completed',payload=json_set(payload,'$.Status',4) WHERE id=$id";job.Parameters.AddWithValue("$id",j.Summary.Id.ToString());job.ExecuteNonQuery();}
        s.AddEvent(j.Summary.Id,"SKU-3 · 第 1 次 · 商品 1234567893 · 活动  · 500.00 → 75.00 · 阶段 discount-base 提交前记录已保存");
        s.AddEvent(j.Summary.Id,"SKU-3 · 耗时 · 原价保存 · 29457ms · CDP 108次 · 整页跳转 1次");
        s.AddEvent(j.Summary.Id,"SKU-3 · 耗时 · 最终价格核验 · 42330ms · CDP 77次 · 整页跳转 2次");
        s.AddEvent(j.Summary.Id,"第 5 行 · SKU-3 · 开始实际调价，第 3 次尝试");
        s.AddEvent(j.Summary.Id,"SKU-3 · 耗时 · 价格编辑准备 · 92609ms · CDP 2次 · 整页跳转 0次");
        s.AddEvent(j.Summary.Id,"SKU-3 · 页面连接中断 · 节点 活动 open · 页面控制连接中断，尚未确认出现验证页；暂停批次，重连后先回读原提交结果。");
        s.AddEvent(j.Summary.Id,"SKU-3 · 已记录退出、原价或报名阶段；下轮先回读，不盲目重复提交。 连接或记录失败。 已达3次尝试上限，请核对原因后另行处理。");
        Assert.True(s.RetryPriceCutReviews(j.Summary.Id));var updated=s.Get(j.Summary.Id);Assert.Equal(JobStatus.Paused,updated.Summary.Status);Assert.Equal(3,updated.Rows.Count(r=>r.Status==RowStatus.RetryPending));Assert.Contains(updated.Rows,r=>r.Id==shipping.Id&&r.PriceCut?.Stage==plan.Stage&&r.PriceCutAttempt==1&&r.Attempts==1);Assert.Contains(updated.Rows,r=>r.Id==timeout.Id&&r.Attempts==3);Assert.Contains(updated.Rows,r=>r.Id==disconnected.Id&&r.Observation?.BasePriceText=="R$ 300"&&r.PriceCut?.Stage=="discount-base");Assert.Equal(RowStatus.NeedsReview,updated.Rows.Single(r=>r.Id==changedOffer.Id).Status);Assert.Equal(RowStatus.NeedsReview,updated.Rows.Single(r=>r.Id==ambiguous.Id).Status);Assert.False(s.RetryPriceCutReviews(j.Summary.Id));
        Assert.True(s.TryStart(j.Summary.Id));var first=s.ClaimNext(j.Summary.Id)!;Assert.Equal(shipping.Id,first.Id);Assert.Equal(2,first.Attempts);Assert.Equal(plan.Stage,first.PriceCut!.Stage);Assert.Equal(plan.DiscountBase,first.PriceCut.DiscountBase);
    }

    [Fact] public void RequeuesOnlyUnsubmittedPromotionAndListingReadFailuresAfterFix()
    {
        var s=new JobStore(dir);var j=s.Create(StorageTests.Snapshot(4),ExecutionMode.PriceCut,mixedPrices:true);var rows=j.Rows.ToArray();
        var increaseInput=rows[0].Source.Input! with{Sku="NW260376SA",ItemId="7689757570",PromotionId="C-MLB6236239",TargetPrice=255,Action="提价"};
        var observation=new Repricing.PriceObservation(increaseInput.Store,increaseInput.ItemId!,"Product","R$ 314",255,-59,
            [new Repricing.PromotionObservation("C-MLB5759411","0930-1030","激活","R$ 255,00",true,Editable:true),new Repricing.PromotionObservation("C-MLB6236239","1029-1128","已预约","R$ 249,00",false,Editable:true)],DateTimeOffset.UtcNow);
        var increase=rows[0] with{Source=rows[0].Source with{Input=increaseInput},Status=RowStatus.NeedsReview,Route="普通活动独立提价",Message="已保留活动提价阶段；重试先回读，不盲目退出或保存。打开活动编辑框：重开编辑框时活动价已变化；未继续提交。 已达3次尝试上限，请核对原因后另行处理。",Checkpoint="campaign-increase-review",Attempts=3,Observation=observation};
        var groupInput=rows[1].Source.Input! with{Sku="NW260170SA",ItemId=null,PromotionId=null,TargetPrice=25.98m,Action="降价"};
        var group=rows[1] with{Source=rows[1].Source with{Input=groupInput},Status=RowStatus.NeedsReview,Route="自动调价",Message="商品分组展开未响应：无法读取对应 SKU 的子商品；未提交价格。 已达3次尝试上限，请核对原因后另行处理。",Checkpoint="cut-review",Attempts=4};
        var missingInput=rows[2].Source.Input! with{Sku="NW251159LL",ItemId=null,PromotionId=null,TargetPrice=32.98m,Action="降价"};
        var missing=rows[2] with{Source=rows[2].Source with{Input=missingInput},Status=RowStatus.NeedsReview,Route="自动调价",Message="未找到唯一可修改活动或普通活动报名入口；不参与新提案或停止现有活动。",Checkpoint="cut-review",Attempts=1};
        var unrelated=rows[3] with{Status=RowStatus.NeedsReview,Route="自动调价",Message="提交结果未知，活动状态已变化。",Checkpoint="cut-review",Attempts=1};
        using(var db=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(dir,"tasks.db")};Pooling=False")){db.Open();foreach(var row in new[]{increase,group,missing,unrelated}){using var c=db.CreateCommand();c.CommandText="UPDATE job_rows SET payload=$payload WHERE id=$id";c.Parameters.AddWithValue("$payload",System.Text.Json.JsonSerializer.Serialize(row));c.Parameters.AddWithValue("$id",row.Id.ToString());c.ExecuteNonQuery();}using var job=db.CreateCommand();job.CommandText="UPDATE jobs SET status='Completed',payload=json_set(payload,'$.Status',4) WHERE id=$id";job.Parameters.AddWithValue("$id",j.Summary.Id.ToString());job.ExecuteNonQuery();}
        Assert.True(s.RetryPriceCutReviews(j.Summary.Id));var updated=s.Get(j.Summary.Id);Assert.Equal(JobStatus.Paused,updated.Summary.Status);Assert.Equal(3,updated.Rows.Count(r=>r.Status==RowStatus.RetryPending));
        Assert.Contains(updated.Rows,r=>r.Id==increase.Id&&r.Source.Input!.TargetPrice==255&&r.Attempts==3&&r.PriceCut==null);
        Assert.Contains(updated.Rows,r=>r.Id==group.Id&&r.Source.Input!.TargetPrice==25.98m&&r.Attempts==4);
        Assert.Contains(updated.Rows,r=>r.Id==missing.Id&&r.Source.Input!.TargetPrice==32.98m&&r.Attempts==1);
        Assert.Equal(RowStatus.NeedsReview,updated.Rows.Single(r=>r.Id==unrelated.Id).Status);
    }
    static readonly ExecutionResult Retry=new(RowStatus.NeedsReview,"降价","读取超时","cut-review",Retryable:true);
    static readonly ExecutionResult Done=new(RowStatus.Succeeded,"降价","完成","cut-verified");
    [Fact] public void FinishesFirstPassBeforeRetryAndNeverRepeatsSuccessfulRows()
    {
        var s=new JobStore(dir);var j=s.Create(StorageTests.Snapshot(3),ExecutionMode.PriceCut);s.TryStart(j.Summary.Id);
        var a=s.ClaimNext(j.Summary.Id)!;s.UpdateRow(j.Summary.Id,a.Id,Retry);
        Assert.Equal(JobStatus.Running,s.Get(j.Summary.Id).Summary.Status);Assert.Equal(RowStatus.RetryPending,s.Get(j.Summary.Id).Rows[0].Status);
        for(int i=1;i<3;i++){var next=s.ClaimNext(j.Summary.Id)!;Assert.Equal(j.Rows[i].Id,next.Id);s.UpdateRow(j.Summary.Id,next.Id,Done);}
        var again=s.ClaimNext(j.Summary.Id)!;Assert.Equal(a.Id,again.Id);Assert.Equal(2,again.Attempts);Assert.Equal(2,s.Get(j.Summary.Id).Summary.Round);
        s.UpdateRow(j.Summary.Id,again.Id,Done);Assert.Null(s.ClaimNext(j.Summary.Id));Assert.Equal(JobStatus.Completed,s.Get(j.Summary.Id).Summary.Status);Assert.Equal(1,s.Get(j.Summary.Id).Rows[1].Attempts);
    }
    [Fact] public void StopsAutomaticRetriesAfterThreeAttempts()
    {
        var s=new JobStore(dir);var j=s.Create(StorageTests.Snapshot(1),ExecutionMode.PriceCut);s.TryStart(j.Summary.Id);
        for(int i=1;i<=3;i++){var r=s.ClaimNext(j.Summary.Id);Assert.NotNull(r);Assert.Equal(i,r.Attempts);s.UpdateRow(j.Summary.Id,r.Id,Retry);}
        Assert.Null(s.ClaimNext(j.Summary.Id));Assert.Equal(RowStatus.NeedsReview,s.Get(j.Summary.Id).Rows[0].Status);Assert.Equal(3,s.Get(j.Summary.Id).Rows[0].Attempts);
    }
    [Fact] public void UserPausePreventsStartingTheNextRound()
    {
        var s=new JobStore(dir);var j=s.Create(StorageTests.Snapshot(1),ExecutionMode.PriceCut);s.TryStart(j.Summary.Id);var row=s.ClaimNext(j.Summary.Id)!;s.RequestPause(j.Summary.Id);s.UpdateRow(j.Summary.Id,row.Id,Retry);
        Assert.Null(s.ClaimNext(j.Summary.Id));Assert.Equal(JobStatus.Paused,s.Get(j.Summary.Id).Summary.Status);Assert.Equal(1,s.Get(j.Summary.Id).Summary.Round);
    }
    [Fact] public void ExplicitResumeQueuesLegacyTimeoutAfterRemainingRows()
    {
        var s=new JobStore(dir);var j=s.Create(StorageTests.Snapshot(2),ExecutionMode.PriceCut);s.TryStart(j.Summary.Id);var a=s.ClaimNext(j.Summary.Id)!;
        s.UpdateRow(j.Summary.Id,a.Id,new(RowStatus.NeedsReview,"降价","连接、页面或记录写入失败，未提交价格，已暂停。","cut-review",PauseBatch:true));
        using(var db=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(dir,"tasks.db")};Pooling=False")){db.Open();using var c=db.CreateCommand();c.CommandText="UPDATE job_rows SET payload=json_set(payload,'$.Attempts',0) WHERE id=$id";c.Parameters.AddWithValue("$id",a.Id.ToString());c.ExecuteNonQuery();}
        Assert.True(s.TryStart(j.Summary.Id));Assert.Equal(j.Rows[1].Id,s.ClaimNext(j.Summary.Id)!.Id);Assert.Equal(RowStatus.RetryPending,s.Get(j.Summary.Id).Rows[0].Status);
    }
    [Fact] public void OneCheckpointPerAttemptAndNoChangedProductBinding()
    {
        var s=new JobStore(dir);var j=s.Create(StorageTests.Snapshot(1),ExecutionMode.PriceCut);s.TryStart(j.Summary.Id);var row=s.ClaimNext(j.Summary.Id)!;
        var p=new Execution.PriceCutPlan(new("1","A","MercadoLibre-巴西-本土"),row.Source.Input!,new("123",row.Source.Input!.Sku,"商品","R$ 100",[]),"C-1",80,100);
        s.SavePriceCutPlan(j.Summary.Id,row.Id,p);Assert.Throws<AppException>(()=>s.SavePriceCutPlan(j.Summary.Id,row.Id,p));s.UpdateRow(j.Summary.Id,row.Id,Retry);s.ClaimNext(j.Summary.Id);
        Assert.Throws<AppException>(()=>s.SavePriceCutPlan(j.Summary.Id,row.Id,p with{Product=p.Product with{ItemId="999"}}));
        s.SavePriceCutPlan(j.Summary.Id,row.Id,p);Assert.Equal(2,s.Get(j.Summary.Id).Rows[0].PriceCutAttempt);
    }
    public void Dispose(){if(Directory.Exists(dir))Directory.Delete(dir,true);}
}
