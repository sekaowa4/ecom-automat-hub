async (a,names) => {
 const timing=a.timing||{dialogMs:60000,pollMs:150,reopenAtMs:[15000,40000],confirmationMs:30000};
 const reopen=i=>timing.reopenAtMs.some(ms=>i===Math.ceil(ms/timing.pollMs));
 const text=e=>(e?.innerText||'').trim(),fail=m=>{throw Error("REPRICING_GUARD:"+m)},money=s=>{const m=s.match(/^R\$\s*(\d{1,3}(?:\.\d{3})+|\d+)(?:,(\d{2}))?$/);return m?Number(m[1].replaceAll('.','')+'.'+(m[2]||'00')):NaN};
 const mainActivityName=d=>names.modal(d.querySelector('.promotion-modal-cdp2026__promo-info'));
 const cents=x=>Number.isFinite(x)&&Math.abs(x*100-Math.round(x*100))<0.000001;
 const rowKey=r=>JSON.stringify({sku:text(r.querySelector('.sc-list-description__sku')),base:text(r.querySelector('.sc-list-description__price-text')),
  promotions:[...r.querySelectorAll('.sc-list-collapsible-row__promotion-box')].map(b=>[text(b.querySelector('.sc-list-collapsible-row__promotion-box__column__1')),text(b.querySelector('.sc-list-collapsible-row__promotion-box__column__3')),text(b.querySelector('.sc-list-collapsible-row__promotion-box__column__4')),...[...b.querySelectorAll('button[data-testid]')].map(x=>[x.dataset.testid,text(x),x.disabled])])});
 if(location.href!==a.url||location.origin!=='https://vendedores.mercadolivre.com.br')fail('页面位置不符');
 const income=a.stage==='income-exit'&&a.incomeSubsidyId===a.promotionId&&/^P-/.test(a.promotionId)&&/^C-/.test(a.incomeOrdinaryId||'')&&a.incomeOrdinaryPrice===a.target&&Number.isFinite(a.incomeSubsidyNet)&&a.incomeSubsidyNet>=0&&a.incomeOrdinaryNet>a.incomeSubsidyNet;
 if(a.stage==='income-exit'&&!income)fail('补贴收益退出绑定无效');
 const ordinaryExit=a.stage==='income-ordinary-exit'&&a.incomeOrdinaryId===a.promotionId&&/^C-/.test(a.promotionId)&&/^P-/.test(a.incomeSubsidyId||'')&&a.incomeOrdinaryNet>a.incomeSubsidyNet&&a.target>a.before;
 if(a.stage==='income-ordinary-exit'&&!ordinaryExit)fail('普通活动退出重报绑定无效');
 if(!cents(a.target)||a.target<=0||!cents(a.before)||!cents(a.basePrice)||(income?false:a.changeBase?!(a.target*100<a.basePrice*20||a.target*100>a.basePrice*95):!(a.target>a.before&&a.target*100>=a.basePrice*20&&a.target*100<=a.basePrice*95)))fail('非有效提价或第三种原价流程');
 const sellerDiscount=a.promotionId==='D-PRICE-DISCOUNT';
 if(sellerDiscount){if(!a.changeBase&&!/^C-[A-Za-z0-9-]+$/.test(a.replacementPromotionId||''))fail('自建折扣未绑定普通活动');}
 else if(!(income||a.changeBase||/^C-[A-Za-z0-9-]+$/.test(a.replacementPromotionId||'')?/^(C|P)-[A-Za-z0-9-]+$/:/^C-/).test(a.promotionId))fail('禁止退出未确认类型的活动');
 const rows=[...document.querySelectorAll('.sc-list-row')].filter(r=>text(r.querySelector('.sc-list-description__id')).replace(/^#/,'')===a.itemId);
 if(rows.length===0||rows.some(r=>rowKey(r)!==rowKey(rows[0])))fail('商品未唯一确认');
 const row=rows[0],sku=text(row.querySelector('.sc-list-description__sku')).replace(/^SKU\s+/,'');if((sku!==a.sku&&!(sku===''&&Array.isArray(a.sharedSkus)&&a.sharedSkus.includes(a.sku)))||money(text(row.querySelector('.sc-list-description__price-text')))!==a.basePrice)fail('SKU或原价不符');
 const allBoxes=[...row.querySelectorAll('.sc-list-collapsible-row__promotion-box')];
 const idOf=b=>(b.querySelector('button[data-testid^="cdp--promotion-box-button--"]')?.dataset.testid||'').replace('cdp--promotion-box-button--','').replace(new RegExp('-'+a.itemId+'$'),'');
 const net=b=>{const col=b.querySelector('.sc-list-collapsible-row__promotion-box__column__4'),main=[...col?.querySelectorAll('.ps-total-charges-with-tooltip__value-container > span')||[]];if(main.length)return main.length===1?money(text(main[0])):NaN;const t=text(col),m=t.match(/^R\$\s*[\d.,]+/);return m&&!/^\s*(至|a\s+R\$|até|–|—)/i.test(t.slice(m[0].length))?money(m[0]):NaN;};
 const boxes=allBoxes.filter(b=>b.querySelector('.andes-badge--green'));
 const enrolled=b=>b.querySelector('.andes-badge--green')||[...b.querySelectorAll('.andes-badge__content')].some(x=>/^(已预约|Agendada|Agendado|Programada|Programado|Scheduled)$/i.test(text(x)));
 const campaignExit=a.stage==='campaign-exit';
 if(campaignExit){const expected=a.preservedCampaigns||[],others=allBoxes.filter(b=>idOf(b)!==a.promotionId&&enrolled(b));if(!/^C-/.test(a.promotionId)||expected.some(x=>!/^C-/.test(x.id)||!Number.isFinite(x.price))||new Set(expected.map(x=>x.id)).size!==expected.length||others.length!==expected.length||others.some(b=>!expected.some(x=>x.id===idOf(b)&&x.price===money(text(b.querySelector('.sc-list-collapsible-row__promotion-box__column__3 > div > div > span'))))))fail('普通活动提价的其他活动范围或价格变化');}
 if(income){const normal=allBoxes.filter(b=>idOf(b)===a.incomeOrdinaryId&&(b.querySelector('.andes-badge--green')||[...b.querySelectorAll('.andes-badge__content')].some(x=>/^(已预约|Agendada|Agendado|Programada|Programado|Scheduled)$/i.test(text(x)))));const platform=boxes.filter(b=>idOf(b)===a.promotionId);if(platform.length!==1||normal.length!==1||boxes.some(b=>![a.promotionId,a.incomeOrdinaryId].includes(idOf(b)))||money(text(normal[0].querySelector('.sc-list-collapsible-row__promotion-box__column__3 > div > div > span')))!==a.incomeOrdinaryPrice||net(normal[0])!==a.incomeOrdinaryNet||net(platform[0])!==a.incomeSubsidyNet)fail('退出前普通活动或补贴净收变化');}
 else if(ordinaryExit){const normal=allBoxes.filter(b=>idOf(b)===a.promotionId&&(b.querySelector('.andes-badge--green')||[...b.querySelectorAll('.andes-badge__content')].some(x=>/^(已预约|Agendada|Agendado|Programada|Programado|Scheduled)$/i.test(text(x)))));if(normal.length!==1||boxes.length!==1||idOf(boxes[0])!==a.incomeSubsidyId||net(boxes[0])!==a.incomeSubsidyNet)fail('普通活动退出前补贴或原活动变化');}
 else if(!campaignExit&&boxes.length!==1)fail('生效活动不唯一');
 const selected=allBoxes.filter(b=>idOf(b)===a.promotionId&&enrolled(b));
 if(campaignExit&&selected.length!==1)fail('提价普通活动未唯一参加');
 const box=campaignExit?selected[0]:income?boxes.find(b=>idOf(b)===a.promotionId):ordinaryExit?allBoxes.find(b=>idOf(b)===a.promotionId):boxes[0];
 if(sellerDiscount&&(!/^(百分比折扣|Desconto percentual|Desconto por porcentagem|Percentage discount)$/i.test(a.promotionName)||!a.promotionPeriod||!text(box.querySelector('.sc-list-collapsible-row__promotion-box__column__1')).includes(a.promotionPeriod)))fail('自建折扣名称或日期不符');
 const edit=[...box.querySelectorAll('button[data-testid]')].filter(b=>b.dataset.testid===`cdp--promotion-box-button--${sellerDiscount?'':a.promotionId}-${a.itemId}`&&(/^(修改|Modificar|Alterar)$/i.test(text(b))||a.allowDirectStop&&a.promotionId.startsWith('P-')&&/^(停止参与|Deixar de participar|Parar de participar)$/i.test(text(b))));
 if(edit.length!==1||edit[0].disabled||money(text(box.querySelector('.sc-list-collapsible-row__promotion-box__column__3 > div > div > span')))!==a.before)fail('活动或实时价格不符');
 const exitKey=JSON.stringify([a.sku,a.itemId,a.promotionId,a.before,a.basePrice,a.target,!!a.changeBase,a.replacementPromotionId||'',!!a.allowDirectStop,a.stage||'',a.incomeOrdinaryId||'',a.incomeOrdinaryPrice,a.incomeSubsidyNet,a.incomeOrdinaryNet]);
 const directStop=/^(停止参与|Deixar de participar|Parar de participar)$/i.test(text(edit[0]));
 if(directStop&&a.action!=='confirm'){
  if(!a.allowDirectStop||[...document.querySelectorAll('.promotion-modal-cdp2026,.sc-list-prompt-modal')].some(e=>e.getClientRects().length&&getComputedStyle(e).visibility!=='hidden'))fail('直接退出入口状态不符');
  if(a.action==='open'||a.action==='check')return true;
  if(a.action!=='stop'||row.dataset.repricingDirectExit==='yes')fail('禁止重复直接退出');
  await new Promise(r=>setTimeout(r,timing.settleMs??1000));
  if(!edit[0].isConnected||edit[0].disabled||money(text(box.querySelector('.sc-list-collapsible-row__promotion-box__column__3 > div > div > span')))!==a.before)fail('等待按钮就绪后活动变化');
  row.dataset.repricingDirectExit='yes';document.documentElement.dataset.repricingExitKey=exitKey;edit[0].click();
  for(let i=0;i<Math.ceil(timing.confirmationMs/timing.pollMs);i++){const d=document.querySelector('.sc-list-prompt-modal');if(d&&d.getClientRects().length&&getComputedStyle(d).visibility!=='hidden')return true;await new Promise(r=>setTimeout(r,timing.pollMs));}fail('直接退出结果待核对，未重复提交');
 }
 if(a.action==='open'){
  if(document.querySelector('.promotion-modal-cdp2026'))fail('已有弹框');edit[0].click();
  for(let i=0;i<Math.ceil(timing.dialogMs/timing.pollMs);i++){
   const ds=[...document.querySelectorAll('.promotion-modal-cdp2026')];if(ds.length>1)fail('活动编辑框出现多个，禁止操作');
   if(reopen(i)&&ds.length===0&&!income){
    const current=[...document.querySelectorAll('.sc-list-row')].filter(r=>text(r.querySelector('.sc-list-description__id')).replace(/^#/,'')===a.itemId);
    if(!current.length||current.some(r=>rowKey(r)!==rowKey(current[0]))||money(text(current[0].querySelector('.sc-list-description__price-text')))!==a.basePrice)fail('重开编辑框时商品或原价已变化');
    const live=[...current[0].querySelectorAll('.sc-list-collapsible-row__promotion-box')].filter(b=>b.querySelector('.andes-badge--green'));if(live.length!==1||money(text(live[0].querySelector('.sc-list-collapsible-row__promotion-box__column__3 > div > div > span')))!==a.before)fail('重开编辑框时活动价已变化');
    const buttons=[...live[0].querySelectorAll('button[data-testid]')].filter(b=>b.dataset.testid===`cdp--promotion-box-button--${sellerDiscount?'':a.promotionId}-${a.itemId}`&&/^(修改|Modificar|Alterar)$/i.test(text(b)));if(buttons.length!==1||buttons[0].disabled)fail('重开编辑框入口不唯一');buttons[0].click();
   }
   const d=ds[0];if(d&&d.getClientRects().length&&(d.querySelectorAll('.promotion-modal-cdp2026__grid-row--final-price input:not(:disabled)').length===1||d.querySelectorAll('.promotion-modal-cdp2026__grid-row--final-price .promotion-modal-cdp2026__grid-cell--actual .promotion-modal-cdp2026__value-with-tooltip > span').length===1)&&text(d.querySelector('.promotion-modal-cdp2026__original-price')).includes('R$')&&d.querySelector('[data-testid="cdp--promotion-modal--delete"]'))return true;
   await new Promise(r=>setTimeout(r,timing.pollMs));
  }
  fail('活动编辑框加载超时，尚未确认退出');
 }
 if(a.action==='confirm'){
  if(document.documentElement.dataset.repricingExitKey!==exitKey||document.documentElement.dataset.repricingExitConfirmed==='yes')fail('退出确认未绑定或已提交');
  const prompts=[...document.querySelectorAll('.sc-list-prompt-modal')];if(prompts.length!==1||text(prompts[0].querySelector('.andes-modal__title'))!=='是否让商品退出此促销活动？')fail('退出确认框不明确');
  const confirm=prompts[0].querySelector('[data-testid="cdp--simple-modal--confirm"]');if(!confirm||confirm.disabled||text(confirm)!=='停止参与')fail('退出确认按钮不明确');
  document.documentElement.dataset.repricingExitConfirmed='yes';confirm.click();for(let i=0;i<Math.ceil(timing.confirmationMs/timing.pollMs);i++){const d=document.querySelector('.sc-list-prompt-modal');if(!d||!d.getClientRects().length||getComputedStyle(d).visibility==='hidden')return true;await new Promise(r=>setTimeout(r,timing.pollMs));}fail('退出结果未知，禁止重复退出');
 }
 const ds=[...document.querySelectorAll('.promotion-modal-cdp2026')];if(ds.length===0)fail('活动编辑框尚未加载');if(ds.length>1)fail('活动编辑框出现多个，禁止操作');const d=ds[0];
 if(sellerDiscount){const dates=[...new Set([...d.querySelectorAll('.promotion-modal-cdp2026__col-header-date--dark')].map(text))];if(dates.length!==1||dates[0]!==a.promotionPeriod||d.querySelectorAll('.promotion-modal-cdp2026__date input[readonly],.andes-datepicker input[readonly]').length!==1)fail('自建折扣弹框日期不符');}
 else if(!names.canonical(a.promotionName)||names.canonical(mainActivityName(d))!==names.canonical(a.promotionName))fail('弹框活动不符');
 const original=text(d.querySelector('.promotion-modal-cdp2026__original-price')).match(/R\$\s*[\d.,]+/g);if(original?.length!==1||money(original[0])!==a.basePrice)fail('弹框原价不符');
 const actual=d.querySelectorAll('.promotion-modal-cdp2026__grid-row--final-price .promotion-modal-cdp2026__grid-cell--actual .promotion-modal-cdp2026__value-with-tooltip > span'),fields=d.querySelectorAll('.promotion-modal-cdp2026__grid-row--final-price input:not(:disabled)');if(actual.length?actual.length!==1||money(text(actual[0]))!==a.before:fields.length!==1||Number(fields[0].value.replaceAll('.','').replace(',','.'))!==a.before)fail('弹框价格不符');
 if(d.querySelector('input[type="checkbox"]:checked:not(:disabled),[role="checkbox"][aria-checked="true"]:not([aria-disabled="true"])'))fail('额外选项被选中');
 const stop=d.querySelector('[data-testid="cdp--promotion-modal--delete"]');if(!stop||stop.disabled||!/^(停止参与|Deixar de participar|Parar de participar)$/i.test(text(stop)))fail('停止入口不明确');
 if(a.action==='check')return true;
 if(a.action!=='stop'||d.dataset.repricingExitAttempted==='yes')fail('禁止重复退出');d.dataset.repricingExitAttempted='yes';document.documentElement.dataset.repricingExitKey=exitKey;stop.click();for(let i=0;i<Math.ceil(timing.confirmationMs/timing.pollMs);i++){const d=document.querySelector('.sc-list-prompt-modal');if(d&&d.getClientRects().length&&getComputedStyle(d).visibility!=='hidden')return true;await new Promise(r=>setTimeout(r,timing.pollMs));}fail('退出确认框未打开');
}
