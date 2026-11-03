using Repricing.Execution;
namespace Repricing.Tests;
public class EnrolledCampaignTests
{
    static PriceInput Input(string? promotion=null)=>new("123","MLB","BRL","SKU","456",null,promotion,90,null,false,false,null,"提价");
    static ZiniaoProduct Product()=>new("456","SKU","Product","R$ 100",[new("P-EVENT","11.11","激活","R$ 80",true,true),new("C-NEXT","Next","已预约","R$ 90",false,true),new("P-OFFER","Proposal","新提案","R$ 70",false,false)]);
    sealed class Client:IZiniaoPriceCutClient
    {
        public ZiniaoProduct Current=Product();public bool Cap;public int Saves,Exits;public List<string> Stages=[];
        public Task<ZiniaoStore[]> StoresAsync(CancellationToken ct)=>Task.FromResult(new[]{new ZiniaoStore("123","Store","MercadoLibre-巴西")});
        public Task<ZiniaoProduct[]> ReadAsync(ZiniaoStore s,PriceInput input,CancellationToken ct)=>Task.FromResult(new[]{Current});
        public Task PrepareAsync(PriceCutPlan p,CancellationToken ct){Stages.Add(p.Stage);if(Cap&&p.Stage=="enrolled-edit")throw new AppException("平台活动价上限为 R$ 80",422);return Task.CompletedTask;}
        public Task PrepareExitAsync(PriceCutPlan p,CancellationToken ct)=>Task.CompletedTask;
        public Task ConfirmExitAsync(PriceCutPlan p,CancellationToken ct){Exits++;Current=Current with{Promotions=Current.Promotions.Select(x=>x.Id==p.PromotionId?x with{Active=false,Editable=false,Status=""}:x).ToArray()};return Task.CompletedTask;}
        public Task SubmitAsync(PriceCutPlan p,CancellationToken ct){Saves++;Current=Current with{Promotions=Current.Promotions.Select(x=>x.Id==p.PromotionId?p.Join?x with{PriceText="R$ 90",Active=true,Editable=true,Status="激活"}:x with{PriceText="R$ 90"}:x).ToArray()};return Task.CompletedTask;}
        public Task<ZiniaoProduct> VerifyAsync(PriceCutPlan p,CancellationToken ct)=>Task.FromResult(Current);
    }
    [Fact] public async Task PlatformEventAndScheduledCampaignAreBothSplitWithoutJoiningProposals(){var c=new Client();var r=await new ZiniaoPriceCutExecutor(c).ExecuteAsync(Input(),_=>{},default,allowIncrease:true,incomePriority:true);Assert.Equal(new[]{"P-EVENT","C-NEXT"},r.SplitInputs!.Select(x=>x.PromotionId));Assert.Equal(0,c.Saves);}
    [Fact] public async Task ScheduledPlatformTargetDoesNotMasqueradeAsCurrentSellingPrice(){var c=new Client();c.Current=c.Current with{Promotions=[new("P-EVENT","11.11","已预约","R$ 90",false,true),new("C-ACTIVE","Active","激活","R$ 80",true,true)]};var r=await new ZiniaoPriceCutExecutor(c).ExecuteAsync(Input("P-EVENT"),_=>{},default,allowIncrease:true);Assert.Equal(RowStatus.Skipped,r.Status);Assert.Equal(80m,r.Observation!.CurrentPrice);Assert.Equal(10m,r.Observation.Difference);Assert.Equal(0,c.Saves);}
    [Fact] public async Task AuthorizedEventCapRejoinsTheSameEventAndKeepsOtherCampaign(){var c=new Client{Cap=true};var r=await new ZiniaoPriceCutExecutor(c).ExecuteAsync(Input("P-EVENT") with{AllowExit=true},_=>{},default,allowIncrease:true);Assert.Equal(RowStatus.Succeeded,r.Status);Assert.Equal(1,c.Exits);Assert.Equal(1,c.Saves);Assert.Equal("R$ 90",c.Current.Promotions.Single(x=>x.Id=="C-NEXT").PriceText);}
    [Fact] public async Task SubsidyCapNeverUsesTheEventExitException(){var c=new Client{Cap=true};c.Current=c.Current with{Promotions=c.Current.Promotions.Select(x=>x.Id=="P-EVENT"?x with{Name="由Mercado Libre承担"}:x).ToArray()};var r=await new ZiniaoPriceCutExecutor(c).ExecuteAsync(Input("P-EVENT") with{AllowExit=true},_=>{},default,allowIncrease:true);Assert.Equal(RowStatus.NeedsReview,r.Status);Assert.Equal(0,c.Exits);Assert.Equal(0,c.Saves);}
    [Fact] public async Task PlatformIncreaseEditsBoundEnrolledActivityWithoutExit(){var c=new Client();var plans=new List<PriceCutPlan>();var r=await new ZiniaoPriceCutExecutor(c).ExecuteAsync(Input("P-EVENT"),plans.Add,default,allowIncrease:true,incomePriority:true);Assert.Equal(RowStatus.Succeeded,r.Status);Assert.Equal(1,c.Saves);Assert.Equal("enrolled-edit",Assert.Single(plans).Stage);Assert.Equal("R$ 90",c.Current.Promotions.Single(x=>x.Id=="C-NEXT").PriceText);}
    [Fact] public async Task UnknownSaveOnlyRereadsAndNeverReplays(){var c=new Client();var input=Input("P-EVENT");var plan=new PriceCutPlan(new("123","Store","MercadoLibre-巴西"),input,c.Current,"P-EVENT",80,100,Stage:"enrolled-edit");var r=await new EnrolledCampaignExecutor(c).ExecuteAsync(plan,c.Current,_=>throw new Exception("No new checkpoint"),default,true);Assert.Equal(RowStatus.NeedsReview,r.Status);Assert.Equal(0,c.Saves);}
    [Fact] public void OtherCampaignChangesCannotBeReportedSuccessful(){var p=Product();var plan=new PriceCutPlan(new("123","Store","MercadoLibre-巴西"),Input("P-EVENT"),p,"P-EVENT",80,100,Stage:"enrolled-edit");var changed=p with{Promotions=p.Promotions.Select(x=>x with{PriceText=x.Id=="C-NEXT"?"R$ 91":"R$ 90"}).ToArray()};Assert.False(EnrolledCampaignExecutor.Reached(plan,changed));}
}
