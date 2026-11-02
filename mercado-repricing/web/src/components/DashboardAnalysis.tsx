import {memo,useId,useMemo,useState} from 'react';
import {money,percent,type DashboardData,type RankedSku} from '../api/dashboard';

const strategyColors:Record<string,string>={'清仓':'#ffb184','强制起量':'#b89aff','其他':'#51e7bf'};
function strategy(sku:string,data:DashboardData){const label=data.strategies?.[sku.trim().toUpperCase()];return label?.includes('清仓')?'清仓':label==='强制起量'?'强制起量':'其他';}
function FullComparisonCard({data}:{data:DashboardData}){
 const comparison=data.comparison;
 return <section className="glass-card comparison-card"><div className="analysis-heading"><h2>⚡ Full 与非Full经营贡献</h2><span>{data.reportDate.slice(5).replace('-','月')}日 · 同一订单明细口径{comparison?` · 剔除${comparison.excludedOrders}笔取消/无有效商品订单`:''}</span></div>
  {!comparison?<p className="analysis-warning" role="status">{data.comparisonError??'暂未读取订单对比数据，请主动刷新。'}</p>:<>
   {data.strategyError&&<p className="analysis-warning" role="status">{data.strategyError}</p>}
   <div className="contribution-panels">{comparison.groups.map(g=><article className={`contribution-panel ${g.name==='Full'?'full':'non-full'}`} key={g.name}><header><h3>{g.name==='Full'?'⚡ Full':g.name}</h3><span>{g.orders.toLocaleString()} 单 · {g.skuCount} SKU</span></header><div className="contribution-totals"><strong>R$ {money(g.sales)}</strong><span>预估毛利率 <b>{percent(g.margin)}</b></span></div>
    {!data.strategyError&&<><div className="strategy-stack" aria-label={`${g.name}销售额构成`}>{g.strategies.filter(s=>s.sales>0).map(s=><i key={s.name} style={{width:`${g.sales>0?s.sales/g.sales*100:0}%`,background:strategyColors[s.name]??'#91bbdd'}} title={`${s.name}：${percent(g.sales>0?s.sales/g.sales:null)}`}/>)}</div>
    <div className="strategy-table-scroll"><table><thead><tr><th>运营策略</th><th>SKU数</th><th>销售额（BRL）</th><th>预估毛利（BRL）</th><th>毛利率</th></tr></thead><tbody>{g.strategies.map(s=><tr key={s.name}><td><b><i style={{background:strategyColors[s.name]}}/>{s.name}</b><small>销售额占比 {percent(g.sales>0?s.sales/g.sales:null)}</small></td><td>{s.skuCount}</td><td>{money(s.sales)}</td><td className={s.profit!=null&&s.profit<0?'negative-value':''}>{money(s.profit)}</td><td className={s.margin!=null&&s.margin<0?'negative-value':''}>{percent(s.margin)}</td></tr>)}</tbody></table></div></>}
    {data.strategyError&&<p>预估毛利 R$ {money(g.profit)}</p>}
   </article>)}</div>
   {comparison.issues.length>0&&<details className="analysis-issues"><summary>部分预估毛利缺失（{comparison.issues.length}）</summary><ul>{comparison.issues.map(s=><li key={s}>{s}</li>)}</ul></details>}
  </>}
  <p className="analysis-note">清仓包含紧急、重要、一般清仓；强制起量单列。整体保留全部商品，订单商品销售额按单价×件数汇总，与销售日报分口径展示。</p>
 </section>;
}

function SkuScatterCard({data}:{data:DashboardData}){
 const [active,setActive]=useState<string|null>(null),tooltipId=useId();
 const rows=useMemo(()=>data.ranking.filter(r=>r.margin!=null&&Number.isFinite(r.margin)&&Number.isFinite(r.sales)).sort((a,b)=>b.orders-a.orders),[data.ranking]);
 const missing=data.ranking.length-rows.length,counts=['清仓','强制起量','其他'].map(name=>({name,count:rows.filter(r=>strategy(r.sku,data)===name).length}));
 const minSales=Math.min(0,...rows.map(r=>r.sales)),maxSales=Math.max(1,...rows.map(r=>r.sales)),step=10**Math.floor(Math.log10(Math.max(1,Math.abs(minSales),maxSales)))/2;
 const xMin=Math.floor(minSales/step)*step,xMax=Math.ceil(maxSales/step)*step;
 const yMin=Math.min(0,Math.floor(Math.min(0,...rows.map(r=>r.margin!))/.4)*.4),yMax=Math.max(.4,Math.ceil(Math.max(0,...rows.map(r=>r.margin!))/.1)*.1);
 const x=(v:number)=>65+(v-xMin)/(xMax-xMin)*820,y=(v:number)=>348-(v-yMin)/(yMax-yMin)*304;
 const selected=rows.find(r=>r.sku===active),pointColor=(r:RankedSku)=>r.margin!<0?'#ffb184':r.margin!>=.15?'#51e7bf':'#b89aff';
 function mark(kind:string,cx:number,cy:number,r:number,color:string){const shared={fill:color,stroke:color,fillOpacity:.6,strokeWidth:1.2};return kind==='清仓'?<polygon points={`${cx},${cy-r} ${cx+r*.95},${cy+r*.75} ${cx-r*.95},${cy+r*.75}`} {...shared}/>:kind==='强制起量'?<polygon points={`${cx},${cy-r} ${cx+r},${cy} ${cx},${cy+r} ${cx-r},${cy}`} {...shared}/>:<circle cx={cx} cy={cy} r={r} {...shared}/>;}
 return <section className="glass-card sku-scatter-card"><div className="analysis-heading"><h2>SKU销售额 × 预估毛利率</h2><span>{data.reportDate.slice(5).replace('-','月')}日 · {rows.length}个SKU · 销售日报口径</span></div>
  <div className="scatter-description"><p>颜色表示毛利率 · 形状表示策略 · 气泡大小表示订单量</p>{!data.strategyError&&<div className="scatter-strategies">{counts.map(c=><span key={c.name}><svg viewBox="0 0 20 20" aria-hidden="true">{mark(c.name,10,10,6,'#cfdeee')}</svg>{c.name} {c.count}</span>)}</div>}</div>
  {data.strategyError&&<p className="analysis-warning">{data.strategyError}</p>}
  {rows.length===0?<p className="scatter-empty">所选日期没有可绘制的SKU毛利率数据。</p>:<div className="scatter-plot" onMouseLeave={()=>setActive(null)}>
   <svg viewBox="0 0 960 404" role="img" aria-label="SKU销售额与毛利率散点图"><rect x="65" y="44" width="820" height={Math.max(0,y(.15)-44)} fill="#51e7bf" fillOpacity=".04"/><rect x="65" y={y(0)} width="820" height={348-y(0)} fill="#ffb184" fillOpacity=".03"/>
    {[0,1,2,3,4,5].map(i=>{const value=yMax-(yMax-yMin)*i/5;return <g key={i}><line x1="65" x2="885" y1={y(value)} y2={y(value)} className="chart-grid"/><text x="54" y={y(value)+4} textAnchor="end" className="axis-label">{(value*100).toFixed(0)}%</text></g>;})}
    {[0,1,2,3,4].map(i=>{const value=xMin+(xMax-xMin)*i/4;return <g key={i}><line x1={x(value)} x2={x(value)} y1="44" y2="348" className="chart-grid"/><text x={x(value)} y="372" textAnchor="middle" className="axis-label">{Math.round(value).toLocaleString('en-US')}</text></g>;})}
    <line x1="65" x2="885" y1={y(.15)} y2={y(.15)} className="scatter-target"/><line x1="65" x2="885" y1={y(0)} y2={y(0)} className="scatter-zero"/><text x="896" y={y(.15)+4} className="scatter-target-label">15%</text><text x="65" y="25" className="axis-label">预估毛利率</text><text x="885" y="396" textAnchor="end" className="axis-label">销售额（BRL）</text>
    {rows.map(r=><g key={r.sku} data-sku={r.sku} className={`scatter-bubble ${active===r.sku?'selected':''}`} tabIndex={0} role="button" aria-label={`SKU ${r.sku} 销售额 R$ ${money(r.sales)} 毛利率 ${percent(r.margin)}`} aria-describedby={active===r.sku?tooltipId:undefined} onMouseEnter={()=>setActive(r.sku)} onFocus={()=>setActive(r.sku)} onBlur={()=>setActive(null)} onClick={()=>setActive(r.sku)} onKeyDown={e=>{if(e.key==='Escape')setActive(null);if(e.key==='Enter'||e.key===' '){e.preventDefault();setActive(r.sku);}}}>{mark(data.strategyError?'其他':strategy(r.sku,data),x(r.sales),y(r.margin!),5+Math.sqrt(Math.max(0,r.orders))*2.7,pointColor(r))}</g>)}
   </svg>
   {selected&&<div id={tooltipId} role="tooltip" className="scatter-tooltip" style={{left:`clamp(8px,${x(selected.sales)/960*100}%,calc(100% - 268px))`,top:`clamp(8px,calc(${y(selected.margin!)/404*100}% - 114px),calc(100% - 130px))`}}><strong>{selected.sku}</strong><span title={selected.name}>{selected.name}</span><span>{data.strategyError?'策略未加载':data.strategies?.[selected.sku.toUpperCase()]??'其他'} · {selected.orders} 单</span><b>R$ {money(selected.sales)} · {percent(selected.margin)}</b></div>}
  </div>}
  <div className="scatter-profit-legend"><span><i style={{background:'#51e7bf'}}/>≥15%</span><span><i style={{background:'#b89aff'}}/>0—15%</span><span><i style={{background:'#ffb184'}}/>＜0%</span></div>
  <p className="analysis-note">完整保留负毛利和极端值；毛利率取自当前利润表，尚未按每笔成交价重新核算。{missing>0?` ${missing}个SKU缺少毛利率，未绘制为0%。`:''}</p>
 </section>;
}
export const FullComparison=memo(FullComparisonCard);
export const SkuScatter=memo(SkuScatterCard);
