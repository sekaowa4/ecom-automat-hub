using Repricing.Execution;
namespace Repricing.Tests;

public class ScheduledOrdinaryTransitionTests
{
    static PriceInput Input()=>new("123","MLB","BRL","SKU-A",null,null,null,97.98m,null,true,false,null,"提价");
    static ZiniaoProduct Original()=>new("456","SKU-A","商品","R$ 139",[new("P-1","补贴","激活","R$ 72,75",true,false),new("C-1","1008-1107\n8/10月至7/11月","已预约","R$ 90,90",false,true)]);
    sealed class Client:IZiniaoPriceCutClient
    {
        public ZiniaoProduct Current=Original();public List<string> Writes=[];
        public Task<ZiniaoStore[]> StoresAsync(CancellationToken ct)=>Task.FromResult(new[]{new ZiniaoStore("123","测试","MercadoLibre-巴西")});
        public Task<ZiniaoProduct[]> ReadAsync(ZiniaoStore s,PriceInput i,CancellationToken ct)=>Task.FromResult(new[]{Current});
        public Task PrepareExitAsync(PriceCutPlan p,CancellationToken ct)=>Task.CompletedTask;
        public Task ConfirmExitAsync(PriceCutPlan p,CancellationToken ct){Writes.Add("exit:"+p.PromotionId);Current=p.PromotionId=="P-1"?Current with{Promotions=[Original().Promotions[1] with{Active=true,Status="激活"}]}:Current with{Promotions=[Original().Promotions[1] with{Status="",Editable=false,Joinable=true}]};return Task.CompletedTask;}
        public Task PrepareAsync(PriceCutPlan p,CancellationToken ct){Assert.Equal("C-1",p.PromotionId);Assert.True(p.Join);return Task.CompletedTask;}
        public Task SubmitAsync(PriceCutPlan p,CancellationToken ct){Writes.Add("save:C-1");Current=Current with{Promotions=[Current.Promotions[0] with{Active=true,Editable=true,Joinable=false,PriceText="R$ 97,98"}]};return Task.CompletedTask;}
        public Task<ZiniaoProduct> VerifyAsync(PriceCutPlan p,CancellationToken ct)=>Task.FromResult(Current);
    }
    [Fact] public async Task KnownScheduledOrdinaryCanTakeOverSubsidyAndRaiseToTarget()
    {
        var c=new Client();var checkpoints=new List<PriceCutPlan>();
        var result=await new ZiniaoPriceCutExecutor(c).ExecuteAsync(Input(),checkpoints.Add,default,allowIncrease:true);
        Assert.Equal(RowStatus.Succeeded,result.Status);Assert.Equal(new[]{"exit:P-1","exit:C-1","save:C-1"},c.Writes);
        Assert.Contains(checkpoints,p=>p.PreviousExit?.PromotionId=="P-1"&&p.PromotionId=="C-1");
    }
    [Fact] public async Task ScheduledSubsidyTransitionPersistsThroughRealJobStore()
    {
        var directory=Path.Combine(Path.GetTempPath(),"scheduled-job-"+Guid.NewGuid());
        try{
            var store=new Repricing.Storage.JobStore(directory);var input=Input();
            var job=store.Create(new(Guid.NewGuid(),"test.csv",[new(2,"价格",new(),input,[])]),ExecutionMode.PriceCut,true,true,true);
            store.TryStart(job.Summary.Id);var row=store.ClaimNext(job.Summary.Id)!;var c=new Client();
            var result=await new ZiniaoPriceCutExecutor(c).ExecuteAsync(input,p=>store.SavePriceCutPlan(job.Summary.Id,row.Id,p),default,allowIncrease:true);
            Assert.Equal(RowStatus.Succeeded,result.Status);Assert.Equal(new[]{"exit:P-1","exit:C-1","save:C-1"},c.Writes);
            var saved=store.Get(job.Summary.Id).Rows.Single().PriceCut!;Assert.Equal("join",saved.Stage);Assert.Equal("P-1",saved.PreviousExit!.PromotionId);
        }finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(directory,true);}
    }
    [Fact] public async Task ScheduledActivityWithoutExitConsentDoesNotGetCancelled()
    {
        var c=new Client();var result=await new ZiniaoPriceCutExecutor(c).ExecuteAsync(Input() with{AllowExit=false},_=>{},default,allowIncrease:true);
        Assert.NotEqual(RowStatus.Succeeded,result.Status);Assert.Empty(c.Writes);
    }
    [Theory]
    [InlineData("C-OTHER","1008-1107\n8/10月至7/11月","R$ 90,90")]
    [InlineData("C-1","周末活动","R$ 90,90")]
    [InlineData("C-1","1008-1107\n8/10月至7/11月","R$ 91,90")]
    public void UnknownSuccessorOrChangedQuoteCannotTriggerSecondExit(string id,string name,string price)
    {
        var original=Original();var plan=new PriceCutPlan(new("123","测试","MercadoLibre-巴西"),Input(),original,"P-1",72.75m,139,Rejoin:true,Stage:"exit",RejoinPromotionId:"C-1");
        var current=original with{Promotions=[new(id,name,"激活",price,true,true)]};
        Assert.Null(ZiniaoIncreaseExecutor.FollowScheduledDiscount(plan,current));
    }
}
