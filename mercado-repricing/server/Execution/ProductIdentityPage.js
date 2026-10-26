a => {
 const text=e=>(e?.innerText||'').trim(),content=e=>(e?.textContent||'').trim();
 if(location.origin!=='https://vendedores.mercadolivre.com.br'||document.readyState==='loading')return {ready:false};
 if(a.action==='legacy'){
  if(!new RegExp('^/anuncios/MLB'+a.itemId+'/modificar/bomni(?:/|$)').test(location.pathname)||!new RegExp('#'+a.itemId+'(?!\\d)').test(text(document.body)))return {ready:false};
  const collapsed=[...document.querySelectorAll('#task-content-variations_task .syi-variations-table tbody tr.syi-variations-table__row button[aria-expanded="false"]')];
  if(collapsed.length){collapsed.forEach(button=>button.click());return {ready:false};}
  const variationRows=[...document.querySelectorAll('#task-content-variations_task .syi-variations-table tbody tr')].filter(row=>row.matches('.syi-variations-table__row')||row.querySelector('[data-title="sellerSku"]'));
  const variants=variationRows.map(row=>{
   const skuCell=row.querySelector('[data-title="sellerSku"] .syi-variations-table__cell--values');
   let sku=content(skuCell),readable=!!skuCell,detail=row.nextElementSibling;
   while(!readable&&detail&&!detail.matches('.syi-variations-table__row')){
    const label=[...detail.querySelectorAll('label')].find(e=>/\bsku\b|识别码|código.*vendedor/i.test(text(e)));
    const field=label?.closest('[data-andes-textfield]'),input=field?.querySelector('input:not([type="file"]):not([type="radio"]),textarea')||label?.control;
    if(input){sku=String(input.value||content(input)).trim();readable=true;break;}
    detail=detail.nextElementSibling;
   }
   return {sku,label:content(row.querySelector('[data-testid="characteristic-text"]'))||content(row.querySelector('[data-title="nestedAttribute"]')),stock:content(row.querySelector('[data-title="nestedStock"]')),readable};
  });
  const skus=variants.map(v=>v.sku).filter(Boolean);
  return {ready:variants.length>0&&variants.every(v=>v.readable),skus:[...new Set(skus)],variants:variants.map(({sku,label,stock})=>({sku,label,stock}))};
 }
 if(a.action!=='manager'||location.href!==a.url)return {ready:false};
 const expand=[...document.querySelectorAll('button[aria-expanded="false"]')].filter(e=>/^(展开商品列表|展开变体|Expandir (?:lista de produtos|variações|variantes))$/i.test(e.getAttribute('aria-label')||''));
 if(expand.length){for(const e of expand)e.click();return {ready:false};}
 const rows=[...document.querySelectorAll('.sll-list-grid-row--item')];
 if(!rows.length)return {ready:false};
 const products=rows.filter(r=>/^MLB\d+$/.test(r.id)).map(r=>{
  // Legacy variation summaries have SKUs but no independent item/price; never assign their SKU to the parent.
  const owned=e=>e.closest('.sll-list-grid-row--item')===r&&!e.closest('.sll-list-grid-row-lite');
  const sku=[...r.querySelectorAll('.sc-copyable-element__text')].filter(owned).map(text).find(t=>/^SKU\s+/.test(t))?.replace(/^SKU\s+/,'')||'',priceCell=[...r.querySelectorAll('.sll-list-cell-price')].find(owned);
  const amounts=text(priceCell).match(/R\$\s*[\d.,]+/g)||[],promo=text(priceCell).match(/(?:正以|Você está vendendo por)\s*(R\$\s*[\d.,]+)\s*(?:促销|em promoção)/i);
  const links=[...new Set([...r.querySelectorAll('a[href]')].map(e=>e.href).filter(h=>{const u=new URL(h);return u.origin==='https://www.mercadolivre.com.br'&&u.pathname==='/syi/core/modify'&&u.searchParams.get('itemId')===r.id&&!u.searchParams.has('taskId')}))];
  return {itemId:r.id.slice(3),sku,title:text(r.querySelector('.sll-list-cell-description__title')),basePriceText:amounts[0]||'',promotions:[],managedPriceText:promo?.[1]||(/^R\$\s*[\d.,]+$/.test(text(priceCell))?amounts[0]:null),listingStatus:text(r.querySelector('.sll-list-cell-status')),editorLink:links.length===1?links[0]:null};
 });
 return {ready:products.length>0,products};
}
