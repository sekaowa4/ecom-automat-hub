() => {
  const compact=value=>String(value||'').replace(/\s+/g,' ').trim();
  const canonical=value=>{
    const name=compact(String(value||'').split(/\r?\n/,1)[0]);
    return name.match(/^(\d{4}-\d{4}|\d{1,2}\.\d{1,2}-\d{1,2}\.\d{1,2}|11\.11|Black Friday)(?=$|\d{1,2}(?:\/|至|\s+a\s+|\s+to\s+))/i)?.[1]||name;
  };
  const modal=info=>{
    const label=/^(?:促销|活动|Promoção)\s*[:：]?$/i;
    const rows=[...(info?.querySelectorAll('.promotion-modal-cdp2026__promo-info--row-container')||[])];
    const main=rows.find(r=>label.test(compact(r.querySelector('.promotion-modal-cdp2026__promo-info--title')?.innerText)));
    if(main){const values=[...new Set([...main.querySelectorAll('.sc-list-promotion-info-list--value')].map(e=>compact(e.innerText)).filter(Boolean))];
      const badges=[...new Set([...main.querySelectorAll('.sc-list-promotion-info-list--icon-pill .andes-badge__content')].map(e=>compact(e.innerText)).filter(Boolean))];
      if(values.length===2&&badges.length===1&&/^(11\.11|Black Friday)$/i.test(badges[0])&&values.some(v=>/^(每日精选|Oferta do dia|Ofertas do dia|Destaques do dia|Daily deals)$/i.test(v)))return badges[0];
      return values.length===1?values[0]:'';}
    const lines=String(info?.innerText||'').split(/\r?\n/).map(compact).filter(Boolean);
    const index=lines.findIndex(line=>label.test(line)||/^(?:促销|活动|Promoção)\s*[:：]\s*\S+/i.test(line));
    return index<0?'':compact(lines[index].match(/^(?:促销|活动|Promoção)\s*[:：]\s*(\S.*)$/i)?.[1]||lines[index+1]||'');
  };
  return {canonical,modal};
}
