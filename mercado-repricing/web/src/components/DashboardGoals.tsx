import {Target,ChartNoAxesColumnIncreasing} from 'lucide-react';
import {money,percent,type DashboardData} from '../api/dashboard';

function GoalProgress({value,label}:{value:number|null|undefined;label:string}){
 const clamped=Math.min(100,Math.max(0,(value??0)*100));
 return <div className="goal-progress" role="progressbar" aria-label={label} aria-valuenow={value==null?undefined:clamped} aria-valuemin={0} aria-valuemax={100}><span style={{width:`${clamped}%`}}/></div>;
}
export function DashboardGoals({data,monthLabel}:{data:DashboardData;monthLabel:string}){
 const goals=data.goals;
 return <section className="glass-card target-card business-goals"><h2>{monthLabel}经营目标</h2>
  <div className="goal-item"><div className="goal-line"><b>GMV目标</b><span>{percent(data.completion)}</span></div><strong className="goal-amount">R$ {money(data.targetBrl)}</strong><small>¥{money(data.targetCny)} · 1 BRL = ¥{data.rate.toFixed(2)}</small><GoalProgress value={data.completion} label="GMV目标完成率"/></div>
  <div className="goal-item"><div className="goal-line"><b>订单目标</b><span>{percent(goals?.ordersCompletion)}</span></div><strong className="goal-amount">{goals?.ordersTarget?.toLocaleString()??'—'} <em>单</em></strong><small>本月已完成 {data.monthOrders.toLocaleString()} 单{goals?.ordersGap!=null?` · 差 ${goals.ordersGap.toLocaleString()} 单`:''}</small><GoalProgress value={goals?.ordersCompletion} label="订单目标完成率"/></div>
  <div className="goal-item"><div className="goal-line"><b>利润目标</b><span>{percent(goals?.profitCompletion)}</span></div><strong className="goal-amount">R$ {money(goals?.profitTarget)}</strong><small>本月预估利润 R$ {money(goals?.profit)}</small><small>本月利润率 {percent(goals?.margin)} / 目标 {percent(goals?.marginTarget)}</small><GoalProgress value={goals?.profitCompletion} label="利润目标完成率"/></div>
 </section>;
}
export function DashboardPace({data}:{data:DashboardData}){
 return <section className="glass-card pace-card"><div><h2><Target size={18} color="#ff596f"/>GMV差额</h2><strong>R$ {money(data.gapBrl)}</strong></div><div><h2><ChartNoAxesColumnIncreasing size={18} color="#b69aff"/>剩余日均</h2><strong>R$ {money(data.requiredDaily)}</strong></div><small>{data.remainingDays>0?`剩余${data.remainingDays}天 · 含当日后`:'统计月已结束'}{data.goals?.ordersRequiredDaily!=null?` · 日均需 ${Math.ceil(data.goals.ordersRequiredDaily)} 单`:''}</small></section>;
}
