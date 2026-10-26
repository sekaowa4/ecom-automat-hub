async (a,names) => {
  const fail=message=>{throw Error('REPRICING_GUARD:'+message);};
  const text=e=>(e?.innerText||'').trim();
  const compact=s=>String(s||'').replace(/\s+/g,' ').trim();
  const mainActivityName=info=>names.modal(info);
  const rowKey=r=>JSON.stringify({sku:text(r.querySelector('.sc-list-description__sku')),base:text(r.querySelector('.sc-list-description__price-text')),
    promotions:[...r.querySelectorAll('.sc-list-collapsible-row__promotion-box')].map(b=>[text(b.querySelector('.sc-list-collapsible-row__promotion-box__column__1')),text(b.querySelector('.sc-list-collapsible-row__promotion-box__column__3')),...[...b.querySelectorAll('button[data-testid]')].map(x=>[x.dataset.testid,text(x),x.disabled])])});
  const money=s=>{const m=s.trim().match(/^R\$\s*(\d{1,3}(?:\.\d{3})+|\d+)(?:,(\d{2}))?$/);return m?Number(m[1].replaceAll('.','')+'.'+(m[2]||'00')):NaN;};
  const numeric=s=>Number(s.trim().replaceAll('.','').replace(',','.'));
  const wait=ms=>new Promise(resolve=>setTimeout(resolve,ms));
  const timing=a.timing||{dialogMs:60000,pollMs:150,reopenAtMs:[15000,40000],confirmationMs:30000,validationMs:10000,settleMs:500};
  const reopen=i=>timing.reopenAtMs.some(ms=>i===Math.ceil(ms/timing.pollMs));
  const preview=a.preview===true;
  if(preview&&!['open','draft','net'].includes(a.action))fail('收益试算禁止提交');
  const netAmount=d=>{const main=d.querySelectorAll('.sc-multi-column-charges__header-slot:not(.sc-multi-column-charges__header-slot--inner)');if(main.length){const value=main.length===1?money(text(main[0])):NaN;return Number.isFinite(value)&&value>=0?value:null;}const rows=[...d.querySelectorAll('[class*="grid-row"]')].filter(e=>/^(净收款额|Você recebe|Valor líquido|Recebimento líquido)(?=\s|R\$|$)/i.test(text(e)));if(rows.length!==1)return null;const amounts=text(rows[0]).match(/R\$\s*[\d.,]+/g)||[];if(amounts.length!==1)return null;const value=money(amounts[0]);return Number.isFinite(value)&&value>=0?value:null;};
  const joining=a.join===true;
  const income=a.stage==='income-join'&&/^P-/.test(a.incomeSubsidyId||'')&&a.incomeOrdinaryId===a.promotionId&&/^C-/.test(a.promotionId);
  if(a.stage==='income-join'){
    const expected=a.preservedActive||[];
    if(!income||expected.filter(p=>p.id===a.incomeSubsidyId&&Number.isFinite(p.price)).length!==1||expected.some(p=>!Number.isFinite(p.price)||p.id!==a.incomeSubsidyId&&(joining||p.id!==a.incomeOrdinaryId||p.price!==a.before))||new Set(expected.map(p=>p.id)).size!==expected.length)fail('收益报名活动绑定无效');
  }
  const sellerDiscount=a.promotionId==='D-PRICE-DISCOUNT';
  const invalidPrice=d=>!!d.querySelector('.andes-form-control--error,[data-andes-state="error"]')||[...d.querySelectorAll('[aria-invalid="true"]')].some(e=>{
    const control=e.closest('.andes-form-control'),message=e.getAttribute('message')||'';
    return !control?.classList.contains('andes-form-control--warning')||control.dataset.andesState!=='warning'||!/^由\s*Mercado Libre\s*承担的\s*Meli\+\s*折扣将被修改[。.]*$/.test(message.trim());
  });
  const priceLimit=()=>{const d=document.querySelector('.promotion-modal-cdp2026');const m=text(d).match(/(?:最高可提供|O máximo que você pode oferecer é)\s*(R\$\s*[\d.,]+)/i);const minimum=text(d).match(/(?:至少提供|O mínimo que você pode oferecer é)\s*(R\$\s*[\d.,]+)/i);return invalidPrice(d)&&(m||minimum)?{priceValidationError:`平台活动价${m?'上限':'下限'}为 ${(m||minimum)[1]}；表格目标价未通过校验，未提交。`}:null;};
  const buttonName=joining?/^(立即参与|Participar|Participar agora)$/i:/^(修改|Modificar|Alterar)$/i;
  const guard=()=>{
    if(location.href!==a.url||location.origin!=='https://vendedores.mercadolivre.com.br')fail('页面位置已变化');
    const rows=[...document.querySelectorAll('.sc-list-row')].filter(r=>text(r.querySelector('.sc-list-description__id')).replace(/^#/,'')===a.itemId);
    if(rows.length===0||rows.some(r=>rowKey(r)!==rowKey(rows[0])))fail('商品不唯一或重复页面价格记录不一致');
    const row=rows[0],sku=text(row.querySelector('.sc-list-description__sku')).replace(/^SKU\s+/,'');
    if(sku&&sku!==a.sku)fail('SKU不匹配');
    if(money(text(row.querySelector('.sc-list-description__price-text')))!==a.basePrice)fail('原价已变化');
    const buttons=[...row.querySelectorAll('button[data-testid]')].filter(b=>b.dataset.testid===`cdp--promotion-box-button--${sellerDiscount?'':a.promotionId}-${a.itemId}`&&buttonName.test(text(b)));
    if(buttons.length!==1||buttons[0].disabled)fail('活动操作按钮不唯一或不可用');
    const box=buttons[0].closest('.sc-list-collapsible-row__promotion-box');
    if(sellerDiscount&&(joining||!a.promotionPeriod||!/^(百分比折扣|Desconto percentual|Desconto por porcentagem|Percentage discount)$/i.test(String(a.promotionName||'').split('\n')[0])||!text(box?.querySelector('.sc-list-collapsible-row__promotion-box__column__1')).includes(a.promotionPeriod)))fail('自建折扣名称或日期不符');
    if(joining){
      if(!box||!a.promotionId.startsWith('C-')||/新提案|Nova proposta|New proposal|Nova oferta/i.test(text(box)))fail('不是可报名普通活动');
      const preserved=a.preservedActive||[];
      if(preserved.some(p=>!(a.stage==='weekend-enroll'?/^(?:P-|C-|D-PRICE-DISCOUNT$)/:/^P-/).test(p.id)||!Number.isFinite(p.price)))fail('保留活动绑定无效');
      const live=[...row.querySelectorAll('.sc-list-collapsible-row__promotion-box')].filter(b=>b.querySelector('.andes-badge--green')).map(b=>({id:(b.querySelector('button[data-testid^="cdp--promotion-box-button--"]')?.dataset.testid||'').replace('cdp--promotion-box-button--','').replace(new RegExp('-'+a.itemId+'$'),''),price:money(text(b.querySelector('.sc-list-collapsible-row__promotion-box__column__3 > div > div > span')))}));
      if(live.length!==preserved.length||live.some(p=>!preserved.some(x=>x.id===p.id&&x.price===p.price)))fail('生效活动与保留范围不符');
    }
    else if(!box||!((preview||income)&&a.promotionId.startsWith('C-')&&[...box.querySelectorAll('.andes-badge__content')].some(e=>/^(已预约|Agendada|Agendado|Programada|Programado|Scheduled)$/i.test(text(e)))||[...box.querySelectorAll('.andes-badge--green')].some(e=>/^(激活|Ativa|Ativo|Active)$/i.test(text(e)))))fail('活动不可编辑');
    if(preview&&!income){const live=[...row.querySelectorAll('.sc-list-collapsible-row__promotion-box')].filter(b=>b.querySelector('.andes-badge--green'));if(!a.promotionId.startsWith('C-')||live.length!==1||!(a.preservedActive||[]).some(p=>p.id.startsWith('P-')&&live[0].querySelector('button')?.dataset.testid==='cdp--promotion-box-button--'+p.id+'-'+a.itemId&&money(text(live[0].querySelector('.sc-list-collapsible-row__promotion-box__column__3 > div > div > span')))===p.price))fail('试算时补贴绑定变化');}
    if(income){
      const expected=a.preservedActive||[];
      const live=[...row.querySelectorAll('.sc-list-collapsible-row__promotion-box')].filter(b=>b.querySelector('.andes-badge--green')).map(b=>({id:(b.querySelector('button[data-testid^="cdp--promotion-box-button--"]')?.dataset.testid||'').replace('cdp--promotion-box-button--','').replace(new RegExp('-'+a.itemId+'$'),''),price:money(text(b.querySelector('.sc-list-collapsible-row__promotion-box__column__3 > div > div > span')))}));
      if(live.length!==expected.length||new Set(live.map(p=>p.id)).size!==live.length||live.some(p=>!expected.some(x=>x.id===p.id&&x.price===p.price)))fail('实报时普通活动或补贴绑定变化');
    }
    if(money(text(box.querySelector('.sc-list-collapsible-row__promotion-box__column__3 > div > div > span')))!==(joining?a.offerPrice:a.before))fail('实时价格已变化');
    if(!(a.target>0&&(preview||income?a.target>=a.basePrice*.2&&a.target<=a.basePrice*.95:a.target<a.before&&a.target<a.basePrice)&&Math.abs(a.target*100-Math.round(a.target*100))<0.000001))fail('不是有效降价');
    return buttons[0];
  };
  const dialog=()=>{
    const ds=[...document.querySelectorAll('.promotion-modal-cdp2026')];if(ds.length!==1)fail('编辑框不唯一');const d=ds[0];
    const original=text(d.querySelector('.promotion-modal-cdp2026__original-price')).match(/R\$\s*[\d.,]+/g);
    if(original?.length!==1||money(original[0])!==a.basePrice)fail('编辑框原价不一致');
    const listName=compact(String(a.promotionName||'').split(/\r?\n/,1)[0]);
    const expectedName=names.canonical(listName);
    const shownName=names.canonical(mainActivityName(d.querySelector('.promotion-modal-cdp2026__promo-info')));
    if(sellerDiscount){const dates=[...new Set([...d.querySelectorAll('.promotion-modal-cdp2026__col-header-date--dark')].map(text))];if(dates.length!==1||dates[0]!==a.promotionPeriod||d.querySelectorAll('.promotion-modal-cdp2026__date input[readonly],.andes-datepicker input[readonly]').length!==1)fail('自建折扣弹框日期不符');}
    else if(!expectedName||shownName!==expectedName)fail('编辑框活动不一致');
    if(d.querySelector('input[type="checkbox"]:checked:not(:disabled),[role="checkbox"][aria-checked="true"]:not([aria-disabled="true"])'))fail('存在额外折扣选项');
    const fields=d.querySelectorAll('.promotion-modal-cdp2026__grid-row--final-price input:not([disabled])');if(fields.length!==1)fail('价格框不唯一');
    const save=d.querySelector('[data-testid="cdp--promotion-modal--confirm"]');if(!save)fail('缺少保存按钮');
    return {d,field:fields[0],save};
  };
  guard();
  if(a.action==='open'){
    if(document.querySelector('.promotion-modal-cdp2026'))fail('已有编辑框');guard().click();
    for(let i=0;i<Math.ceil(timing.dialogMs/timing.pollMs);i++){const ds=[...document.querySelectorAll('.promotion-modal-cdp2026')];if(reopen(i)&&ds.length===0)guard().click();if(ds.length>1)fail('活动编辑框出现多个');const d=ds[0];if(d&&d.getClientRects().length&&d.querySelectorAll('.promotion-modal-cdp2026__grid-row--final-price input:not(:disabled)').length===1&&text(d.querySelector('.promotion-modal-cdp2026__original-price')).includes('R$')&&d.querySelector('[data-testid="cdp--promotion-modal--confirm"]'))return true;await wait(timing.pollMs);}fail('活动编辑框加载超时');
  }
  if(a.action==='draft'){
    const {d,field}=dialog();if(preview){d.dataset.repricingNetBefore=String(netAmount(d));d.dataset.repricingNetPriceBefore=String(numeric(field.value));}
    if(numeric(field.value)!==(joining?a.offerPrice:a.before)){
      const row=[...document.querySelectorAll('.sc-list-row')].find(r=>text(r.querySelector('.sc-list-description__id')).replace(/^#/,'')===a.itemId);
      const box=row?.querySelector('button[data-testid="cdp--promotion-box-button--'+a.promotionId+'-'+a.itemId+'"]')?.closest('.sc-list-collapsible-row__promotion-box');
      const prices=[...new Set([...box?.querySelectorAll('.ps-simulator-detail-content__charges .ps-simulator-charge')||[]].filter(e=>/^(价格|Preço|Precio|Price)\*?$/i.test(text(e.querySelector('.ps-simulator-charge__label')))).map(e=>money(text(e.querySelector('.ps-simulator-charge__values')))))];
      const initial=numeric(field.value),sameTitle=compact(text(d.querySelector('.promotion-modal-cdp2026__header--title')))===compact(text(row?.querySelector('.sc-list-description__title')));
      if(joining||sellerDiscount||!sameTitle||!compact(text(d.querySelector('.promotion-modal-cdp2026__header--title')))||prices.length!==1||prices[0]!==initial||!Number.isFinite(initial)||initial<=0||initial>=a.basePrice||!(preview||income||a.target<initial))fail('编辑框当前价不符：列表 '+(joining?a.offerPrice:a.before)+'，输入框 '+String(field.value).slice(0,24));
    }
    const setter=Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set;
    field.focus();setter.call(field,a.target.toFixed(2).replace('.',','));field.dispatchEvent(new Event('input',{bubbles:true}));field.dispatchEvent(new Event('change',{bubbles:true}));field.blur();
    for(let i=0;i<Math.ceil(timing.validationMs/timing.pollMs);i++){const v=dialog();if(numeric(v.field.value)===a.target&&!v.save.disabled&&!invalidPrice(v.d)){await wait(timing.settleMs);guard();const final=dialog();if(numeric(final.field.value)!==a.target||final.save.disabled||invalidPrice(final.d)){const limit=priceLimit();if(limit)return limit;fail('价格未通过校验');}return true;}await wait(timing.pollMs);}const limit=priceLimit();if(limit)return limit;fail('价格未通过校验');
  }
  if(a.action==='net'){
    if(!preview)fail('只允许收益试算读取');const {d,field,save}=dialog();const value=netAmount(d);
    if(numeric(field.value)!==a.target||(save.disabled||invalidPrice(d))&&!priceLimit())return {net:null};
    if(d.dataset.repricingNetPriceBefore!==String(a.target)&&d.dataset.repricingNetBefore===String(value))return {net:null};
    return {net:value};
  }
  if(a.action==='submit'){
    const {d,field,save}=dialog();if(numeric(field.value)!==a.target||save.disabled||invalidPrice(d))fail('提交前校验失败');
    if(d.dataset.repricingSubmitted==='yes')fail('禁止重复提交');d.dataset.repricingSubmitted='yes';save.click();
    for(let i=0;i<Math.ceil(timing.confirmationMs/timing.pollMs);i++){if(!document.querySelector('.promotion-modal-cdp2026'))return true;await wait(timing.pollMs);}fail('保存结果未知');
  }
  fail('未知操作');
}
