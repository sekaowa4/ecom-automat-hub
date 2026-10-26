() => {
  const compact=value=>String(value||'').replace(/\s+/g,' ').trim();
  const canonical=value=>{
    const name=compact(String(value||'').split(/\r?\n/,1)[0]);
    return name.match(/^(\d{4}-\d{4}|\d{1,2}\.\d{1,2}-\d{1,2}\.\d{1,2})(?=$|\d{1,2}(?:\/|至|\s+a\s+|\s+to\s+))/i)?.[1]||name;
  };
  const modal=info=>{
    const label=/^(?:促销|活动|Promoção)\s*[:：]?$/i;
    const rows=[...(info?.querySelectorAll('.promotion-modal-cdp2026__promo-info--row-container')||[])];
    const main=rows.find(r=>label.test(compact(r.querySelector('.promotion-modal-cdp2026__promo-info--title')?.innerText)));
    if(main){const values=[...new Set([...main.querySelectorAll('.sc-list-promotion-info-list--value')].map(e=>compact(e.innerText)).filter(Boolean))];return values.length===1?values[0]:'';}
    const lines=String(info?.innerText||'').split(/\r?\n/).map(compact).filter(Boolean);
    const index=lines.findIndex(line=>label.test(line)||/^(?:促销|活动|Promoção)\s*[:：]\s*\S+/i.test(line));
    return index<0?'':compact(lines[index].match(/^(?:促销|活动|Promoção)\s*[:：]\s*(\S.*)$/i)?.[1]||lines[index+1]||'');
  };
  return {canonical,modal};
}
