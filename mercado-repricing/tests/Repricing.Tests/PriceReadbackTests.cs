using Repricing.Execution;
namespace Repricing.Tests;
public class PriceReadbackTests
{
 static PriceCutPlan Plan()=>new(new("1","测试","MercadoLibre-巴西"),new("1","MLB","BRL","SKU-A",null,null,null,75,null,false,false,null),new("123","SKU-A","商品","R$ 100",[]),"C-1",80,100);
 static ZiniaoProduct Product()=>new("123","SKU-A","商品","R$ 100",[new("C-1","活动","激活","R$ 75",true,true)]);
 [Fact] public void ExactlyOneActiveTargetPasses()=>Assert.Null(PriceReadback.Failure(Plan(),Product(),100,"C-1"));
 [Fact] public void SubsidyAlongsideTargetDoesNotPass(){var p=Product() with{Promotions=[..Product().Promotions,new("P-2","补贴","激活","R$ 70",true)]};Assert.Contains("多个活动",PriceReadback.Failure(Plan(),p,100,"C-1"));Assert.Null(PriceReadback.Observe(Plan(),p).CurrentPrice);}
 [Fact] public void ScheduledTargetDoesNotPass(){var p=Product() with{Promotions=[new("C-1","活动","已预约","R$ 75",false,true)]};Assert.Contains("尚未激活",PriceReadback.Failure(Plan(),p,100,"C-1"));Assert.Equal(100,PriceReadback.Observe(Plan(),p).CurrentPrice);}
 [Theory] [InlineData("审核未激活")] [InlineData("需审核未激活")] public void ReviewCannotPassEvenAtTarget(string state)=>Assert.NotNull(PriceReadback.Failure(Plan(),Product() with{ListingStatus=state},100,"C-1"));
 [Fact] public void BasePriceSavedButActualManagerPriceDifferentDoesNotPass(){var p=Product() with{BasePriceText="R$ 75",ManagedPriceText="R$ 70",Promotions=[]};Assert.NotNull(PriceReadback.Failure(Plan(),p,75,null));Assert.Equal(70,PriceReadback.Observe(Plan(),p).CurrentPrice);}
 [Fact] public void BaseTargetWithoutPromotionsPasses()=>Assert.Null(PriceReadback.Failure(Plan(),Product() with{BasePriceText="R$ 75",Promotions=[]},75,null));
 [Fact] public void BaseTargetWithActivePromotionDoesNotPass()=>Assert.NotNull(PriceReadback.Failure(Plan(),Product() with{BasePriceText="R$ 75"},75,null));
 [Fact] public void WrongSkuOrItemDoesNotPass(){Assert.NotNull(PriceReadback.Failure(Plan(),Product() with{Sku="OTHER"},100,"C-1"));Assert.NotNull(PriceReadback.Failure(Plan(),Product() with{ItemId="124"},100,"C-1"));}
 [Fact] public void BaseDriftDoesNotPass()=>Assert.NotNull(PriceReadback.Failure(Plan(),Product() with{BasePriceText="R$ 101"},100,"C-1"));
 [Fact] public void LegacyEmptySkuNeedsConfirmedBinding(){var p=Product() with{Sku=""};Assert.NotNull(PriceReadback.Failure(Plan(),p,100,"C-1"));Assert.Null(PriceReadback.Failure(Plan() with{Input=Plan().Input with{ItemId="123"}},p,100,"C-1"));}
 [Fact] public void ManagerFallbackIsExplicitAndCannotHideOtherActivities(){var p=Product() with{ManagedPriceText="R$ 75",Promotions=[]};Assert.NotNull(PriceReadback.Failure(Plan(),p,100,"C-1"));Assert.Null(PriceReadback.Failure(Plan(),p,100,"C-1",allowManaged:true));Assert.NotNull(PriceReadback.Failure(Plan(),p with{Promotions=[new("C-1","活动","已预约","R$ 75",false)]},100,"C-1",allowManaged:true));}
 [Fact] public void ContradictoryManagerAndPromotionPricesAreUnknown(){var p=Product() with{ManagedPriceText="R$ 70"};Assert.Null(PriceReadback.Observe(Plan(),p).CurrentPrice);Assert.NotNull(PriceReadback.Failure(Plan(),p,100,"C-1"));}
 [Fact] public void UnreadableManagerPriceCannotFallBackToBase(){var p=Product() with{BasePriceText="R$ 75",ManagedPriceText="R$ 70 至 R$ 75",Promotions=[]};Assert.Null(PriceReadback.EffectivePrice(p));Assert.NotNull(PriceReadback.Failure(Plan(),p,75,null));}
 [Fact] public void StaleCardCanBeReconciledWithExactManagerSalePrice()
 {
  var stale=Product() with{Promotions=[Product().Promotions[0] with{PriceText="R$ 80"}]};
  var managed=Product() with{Promotions=[],ManagedPriceText="R$ 75"};
  var reconciled=PriceReadback.ReconcileManager(Plan(),stale,managed);
  Assert.NotNull(reconciled);Assert.Null(PriceReadback.Failure(Plan(),reconciled,100,"C-1"));
  Assert.Contains("R$ 80",reconciled.PriceReadbackNote);Assert.Contains("商品管理",PriceReadback.Observe(Plan(),reconciled).ReadbackNote);
  Assert.Equal("R$ 80",stale.Promotions[0].PriceText);
 }
 [Theory][InlineData("sku")][InlineData("item")][InlineData("base")][InlineData("price")][InlineData("review")][InlineData("scheduled")][InlineData("subsidy")]
 public void ReconciliationCannotHideIdentityPriceOrActivityConflicts(string conflict)
 {
  var row=Product() with{Promotions=[Product().Promotions[0] with{PriceText="R$ 80"}]};
  var managed=Product() with{Promotions=[],ManagedPriceText="R$ 75"};
  switch(conflict){case "sku":managed=managed with{Sku="OTHER"};break;case "item":managed=managed with{ItemId="124"};break;case "base":managed=managed with{BasePriceText="R$ 101"};break;case "price":managed=managed with{ManagedPriceText="R$ 70"};break;case "review":managed=managed with{ListingStatus="审核未激活"};break;case "scheduled":row=row with{Promotions=[row.Promotions[0] with{Active=false,Status="已预约"}]};break;case "subsidy":row=row with{Promotions=[..row.Promotions,new("P-2","补贴","激活","R$ 70",true)]};break;}
  Assert.Null(PriceReadback.ReconcileManager(Plan(),row,managed));
 }
}
