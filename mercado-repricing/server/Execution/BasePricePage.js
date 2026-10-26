async a => {
 const text=e=>(e?.innerText||'').trim(),visible=e=>!!e?.getClientRects().length&&getComputedStyle(e).visibility!=='hidden',fail=m=>{throw Error('REPRICING_GUARD:'+m)},number=s=>Number(s.replaceAll('.','').replace(',','.'));
 if(location.origin!=='https://vendedores.mercadolivre.com.br'){
  if(a.action==='open'&&location.origin==='https://www.mercadolivre.com.br'&&location.href===a.editorEntry)return {ready:false};
  fail('原价页面站点不符');
 }
 if(a.action==='wholesale-saved'){
  const pending=(reason,details={})=>({ready:false,reason,url:location.href,documentState:document.readyState,...details});
  if(document.readyState!=='complete')return pending('document-loading');
  if(location.href===a.url){
   const toggles=[...document.querySelectorAll('button[aria-label]')].filter(e=>visible(e)&&/^(销售条件|Condições de venda)$/i.test(e.getAttribute('aria-label')));
   const expanded=toggles.length===1?toggles[0].getAttribute('aria-expanded'):null,skuPresent=text(document.body).split(/[^A-Za-z0-9_-]+/).includes(a.sku);
   return toggles.length===1&&expanded==='false'&&skuPresent?{ready:true}:pending(toggles.length!==1?'sales-toggle-not-unique':!skuPresent?'sku-missing':'sales-panel-open',{toggleCount:toggles.length,expanded,skuPresent});
  }
  const manager='https://vendedores.mercadolivre.com.br/anuncios?page=1&sort=DEFAULT&search='+encodeURIComponent(a.sku);
  if(location.href!==manager)fail('批发保存后跳转页面未确认');
  return document.querySelector('.sll-list-grid-row')?{ready:true}:pending('manager-loading');
 }
 const restoring=a.discountBase!=null;
 if(restoring&&(!Number.isFinite(a.finalPrice)||a.finalPrice<=0||Math.abs(a.finalPrice*100-Math.round(a.finalPrice*100))>0.000001||Math.abs(a.discountBase-a.finalPrice*4)>.000001||a.target!==a.discountBase||!(a.finalPrice<a.basePrice*.2)||a.target>=a.basePrice))fail('降低原价后的折扣目标绑定不符');
 if(!a.changeBase||!/^\d{1,30}$/.test(a.itemId)||!Number.isFinite(a.target)||a.target<=0||Math.abs(a.target*100-Math.round(a.target*100))>0.000001||!restoring&&!a.directBaseOnly&&!(a.target<a.basePrice*.2||a.target>a.basePrice*.95))fail('第三种范围或商品绑定不符');
 if(a.action==='locate'||a.action==='verify'){
  if(location.href!==a.url)return {ready:false};
  if(document.readyState==='loading'||!document.querySelector('.sll-list-grid-row'))return {ready:false};
  const expand=[...document.querySelectorAll('button[aria-label="展开商品列表"]')].filter(e=>visible(e)&&e.getAttribute('aria-expanded')==='false');
  if(expand.length){for(const e of expand)e.click();return {ready:false};}
  const rows=[...document.querySelectorAll('.sll-list-grid-row--item')].filter(e=>e.id==='MLB'+a.itemId||[...e.querySelectorAll('.sc-copyable-element__text')].some(x=>text(x)==='#'+a.itemId)&&e.querySelector('.sll-list-grid-row--'+a.itemId));
  if(rows.length===0)return {ready:false};if(rows.length!==1)fail('原价商品行不唯一');const row=rows[0];
  if(![...row.querySelectorAll('.sc-copyable-element__text')].some(e=>text(e)==='SKU '+a.sku))fail('原价编辑入口SKU不符');
  if(a.action==='verify'){
   const scopes=row.querySelectorAll('.sll-list-grid-row--'+a.itemId),cells=(scopes.length===1?scopes[0]:row).querySelectorAll('.sll-list-cell-price');if(cells.length!==1)fail('原价回读未绑定唯一商品价格区块');
   const main=cells[0].querySelectorAll('.sll-list-no-wrap');if(main.length>1)fail('原价回读主价格不唯一');
   const cell=text(main.length===1?main[0]:cells[0]),m=cell.match(/^R\$\s*(\d{1,3}(?:\.\d{3})+|\d+)(?:,(\d{1,2}))?$/);
   const price=m?Number(m[1].replaceAll('.','')+'.'+(m[2]||'00')):null;
   return {ready:true,reached:price===a.target,priceText:cell};
  }
  const links=[...new Set([...row.querySelectorAll('a[href]')].map(e=>e.href).filter(h=>{const u=new URL(h);return u.origin==='https://www.mercadolivre.com.br'&&!u.searchParams.has('taskId')&&(/^\/anuncios\/MLBU\d+\/modificar\/omni\/variation$/.test(u.pathname)||u.pathname==='/syi/core/modify'&&u.searchParams.get('itemId')==='MLB'+a.itemId)}))];
  if(links.length!==1)fail('原价编辑链接未唯一确认');return {ready:true,href:links[0]};
 }
 if(a.action==='open'){
  if(document.readyState==='loading'||!text(document.body).includes(a.sku))return {ready:false};
  const toggles=[...document.querySelectorAll('button[aria-label]')].filter(e=>visible(e)&&/^(销售条件|Condições de venda)$/i.test(e.getAttribute('aria-label')));
  if(toggles.length!==1)return {ready:false};
  if(toggles[0].getAttribute('aria-expanded')!=='true'){if(!document.documentElement.dataset.repricingSalesOpened){document.documentElement.dataset.repricingSalesOpened='yes';toggles[0].click();}return {ready:false};}
 }
 else if(location.href!==a.url)fail('原价编辑页已变化');
 const candidates=[...document.querySelectorAll('input[id^="price__input-"]:not(:disabled)')].filter(visible),primary=candidates.filter(e=>/^(设置价格 BRL|Definir preço BRL)$/i.test(e.getAttribute('aria-label')||'')&&!e.closest('[class*="wholesale"],[class*="bulk-price"]'));const fields=primary.length?primary:candidates.filter(e=>!e.closest('[class*="wholesale"],[class*="bulk-price"]'));
 const confirms=e=>[...e.querySelectorAll('button')].filter(b=>visible(b)&&/^(确认|Confirmar)$/i.test(text(b)));
 const idPattern=new RegExp('#'+a.itemId+'(?!\\d)');
 const cardFor=field=>{const explicit=field.closest('[class*="syi-task-desktop--prices_task_MLB"]');if(explicit)return explicit.classList.contains('syi-task-desktop--prices_task_MLB'+a.itemId)?explicit:null;let card=field.parentElement;while(card&&card!==document.body&&card!==document.documentElement){if(idPattern.test(text(card))){const ids=[...text(card).matchAll(/#(\d{1,30})(?!\d)/g)].map(x=>x[1]);return ids.every(id=>id===a.itemId)?card:null;}card=card.parentElement;}return null;};
 const boundFields=fields.filter(field=>cardFor(field));
 if(boundFields.length===0&&a.action==='open')return {ready:false};if(boundFields.length!==1)fail('原价主价格字段未绑定唯一商品');const field=boundFields[0];
 const body=text(document.body);if(!body.split(/[^A-Za-z0-9_-]+/).includes(a.sku))fail('原价表单SKU缺失');
 const form=cardFor(field);let saveRoot=form;while(saveRoot&&confirms(saveRoot).length!==1)saveRoot=saveRoot.parentElement;
 if(!saveRoot||saveRoot===document.body||saveRoot===document.documentElement){if(a.action==='open')return {ready:false};fail('价格字段未绑定指定保存按钮');}
 for(let owner=confirms(saveRoot)[0].parentElement;owner&&owner!==saveRoot;owner=owner.parentElement){if(owner.querySelector('input[id^="price__input-"]')&&!owner.contains(field)){if(a.action==='open')return {ready:false};fail('保存按钮属于其他商品价格区块');}}
 const siblingState=()=>JSON.stringify([...saveRoot.querySelectorAll('input')].filter(e=>!form.contains(e)&&(e.id.startsWith('price__input-')||e.type==='checkbox')).map(e=>({id:e.id,value:e.value,checked:e.checked,disabled:e.disabled})));
 if(a.action==='open'){if(!form.dataset.repricingSiblingState)form.dataset.repricingSiblingState=siblingState();}
 else if(form.dataset.repricingSiblingState!==siblingState())fail('同页其他商品价格或选项已变化，禁止连带保存');
 const checks=[...form.querySelectorAll('input[type="checkbox"]')],wholesale=checks.filter(e=>/批发价格|Preços? (?:de |por )?atacado/i.test((e.getAttribute('aria-labelledby')||'').split(/\s+/).map(id=>text(document.getElementById(id))).join(' ')+text(e.closest('.andes-checkbox'))));
 const applyAll=checks.filter(e=>e.id==='prices_task_MLB'+a.itemId+'-footer-checkbox'&&/^将价格设置应用于所有变体$/.test(text(e.closest('.andes-checkbox'))));
 if(a.action==='open'&&applyAll.length===1&&visible(applyAll[0])&&applyAll[0].checked&&!applyAll[0].disabled){applyAll[0].click();return {ready:false,reason:'disabled-apply-price-to-all-variants'};}
 const extraChecks=checks.filter(e=>visible(e)&&e.checked&&!e.disabled&&!wholesale.includes(e));
 if(extraChecks.length)fail('存在额外勾选项：'+extraChecks.map(e=>((e.getAttribute('aria-labelledby')||'').split(/\s+/).map(id=>text(document.getElementById(id))).join(' ')||text(e.closest('.andes-checkbox'))||'未命名选项').slice(0,35)).join('、')+'；未改原价');
 if(/正以|您在促销时以|Você está vendendo.*promoção/i.test(text(form)))fail('商品仍有活动优惠，未保存原价');
 const key=JSON.stringify([a.itemId,a.sku,a.basePrice,a.target]);
 const wholesaleState={wholesaleEnabled:wholesale.some(e=>e.checked),wholesaleCount:wholesale.length,wholesaleChecked:wholesale.filter(e=>e.checked).length,wholesaleDisabled:wholesale.filter(e=>e.disabled).length};
 if(a.action==='wholesale-state')return {ready:true,...wholesaleState};
 const loaders=()=>[...saveRoot.querySelectorAll('[role="progressbar"],.andes-spinner')].filter(visible).length;
 const resetReady=()=>{delete form.dataset.repricingBaseReadySince;delete form.dataset.repricingWholesaleReadySince;};
 if(loaders()&&['open','draft'].includes(a.action)){resetReady();if(a.action==='open')return {ready:false,reason:'price-form-loading',loaders:loaders(),url:location.href};fail('原价表单仍在加载，禁止输入或保存');}
 const saveReadiness=kind=>{const name=kind==='wholesale'?'repricingWholesaleReadySince':'repricingBaseReadySince',loading=loaders(),disabled=confirms(saveRoot)[0].disabled;if(loading||disabled){resetReady();return {ready:false,reason:loading?'price-form-loading':'confirm-disabled',loaders:loading,url:location.href};}const now=performance.now(),since=Number(form.dataset[name]);if(!Number.isFinite(since)){form.dataset[name]=String(now);return {ready:false,reason:'price-form-stabilizing',url:location.href};}return {ready:now-since>=500,reason:now-since>=500?'price-form-ready':'price-form-stabilizing',url:location.href};};
 if(a.action==='open'){
  if(number(field.value)!==a.basePrice)fail('编辑器原价与回读不符');return {ready:true,url:location.href,...wholesaleState};
 }
 if(a.action==='wholesale-off'){
  if(wholesale.length!==1||!wholesale[0].checked||wholesale[0].disabled||number(field.value)!==a.basePrice)fail('批发关闭绑定不符');
  resetReady();
  wholesale[0].click();form.dataset.repricingWholesaleKey=key;return {ready:true,url:location.href};
 }
 if(a.action==='wholesale-check'||a.action==='wholesale-submit'){
  if(form.dataset.repricingWholesaleKey!==key||wholesale.some(e=>e.checked)||number(field.value)!==a.basePrice||form.dataset.repricingWholesaleSubmitted==='yes')fail('批发关闭保存绑定不符');
  const state=saveReadiness('wholesale');if(a.action==='wholesale-check')return state;
  if(!state.ready)fail('批发关闭确认尚未稳定就绪：'+state.reason);form.dataset.repricingWholesaleSubmitted='yes';confirms(saveRoot)[0].click();return {ready:true,url:location.href};
 }
 if(wholesale.some(e=>e.checked))fail('批发价格尚未关闭');
 if(a.action==='draft'){
  if(number(field.value)!==a.basePrice||form.dataset.repricingBaseSubmitted==='yes')fail('原价已变化或已提交');
  resetReady();
  Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(field,a.target.toFixed(2).replace('.',','));field.dispatchEvent(new Event('input',{bubbles:true}));field.dispatchEvent(new Event('change',{bubbles:true}));field.blur();
  if(number(field.value)!==a.target||field.getAttribute('aria-invalid')==='true')fail('目标原价输入校验失败');
  form.dataset.repricingBaseKey=key;return {ready:true,url:location.href};
 }
 if(!['check','submit'].includes(a.action)||form.dataset.repricingBaseKey!==key||form.dataset.repricingBaseSubmitted==='yes'||number(field.value)!==a.target||field.getAttribute('aria-invalid')==='true')fail('原价保存绑定或表单校验不符');
 const pendingConsequence=[...document.querySelectorAll('.andes-modal.consequences-modal')].some(visible);
 const state=pendingConsequence?{ready:true}:saveReadiness('base');if(a.action==='check')return state;
 if(!state.ready)fail('原价保存按钮尚未稳定就绪：'+state.reason);
 form.dataset.repricingBaseSubmitted='yes';if(!pendingConsequence)confirms(saveRoot)[0].click();
 const consequenceDeadline=performance.now()+(a.timing?.validationMs||10000);let consequence;
 while(performance.now()<consequenceDeadline){consequence=[...document.querySelectorAll('.andes-modal.consequences-modal')].filter(visible);if(consequence.length)break;await new Promise(resolve=>setTimeout(resolve,100));}
 if(consequence?.length){
  if(consequence.length!==1)fail('平台商品更新后果弹框不唯一，未确认');
  const modal=consequence[0],modalText=text(modal),oldAmounts=[...modal.querySelectorAll('del')],newAmounts=[...modal.querySelectorAll('strong')],money=e=>{const m=text(e).match(/(?:R\$\s*)?([\d.]+,\d{2})/);return m?number(m[1]):null;},oldCost=oldAmounts.length===1?money(oldAmounts[0]):null,newCost=newAmounts.length===1?money(newAmounts[0]):null;
  if(location.href!==a.url||!text(document.body).split(/[^A-Za-z0-9_-]+/).includes(a.sku)||!idPattern.test(text(document.body))||number(field.value)!==a.target)fail('运费确认弹框与目标商品或目标价脱离，未确认');
  if(!/(运费|frete|shipping)/i.test(modalText)||oldCost==null||newCost==null)fail('平台要求确认未识别的商品或费用变更，未确认：'+modalText.replace(/\s+/g,' ').slice(0,135));
  // Operators include shipping in the target price; confirm the exact listing's shipping consequence.
  const buttons=confirms(modal).filter(e=>!e.disabled&&e.getAttribute('aria-disabled')!=='true');
  if(buttons.length!==1)fail('运费确认按钮未唯一就绪，保存结果需回读');
  buttons[0].click();
  const closeDeadline=performance.now()+30000;
  while(visible(modal)&&performance.now()<closeDeadline)await new Promise(resolve=>setTimeout(resolve,100));
  if(visible(modal))fail('确认平台商品更新后果后弹框未关闭，保存结果需回读');
 }
 return {ready:true,url:location.href};
}
