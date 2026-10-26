const fs=require('fs'),path=require('path');
module.exports=name=>{
  if(!['PriceCutPage.js','StopParticipationPage.js','BasePricePage.js','ProductIdentityPage.js'].includes(name))throw Error('Unknown page script');
  const root=path.join(__dirname,'../server/Execution'),raw=fs.readFileSync(path.join(root,name),'utf8');
  return ['PriceCutPage.js','StopParticipationPage.js'].includes(name)?`async a => (${raw})(a,(${fs.readFileSync(path.join(root,'PromotionName.js'),'utf8')})())`:raw;
};
