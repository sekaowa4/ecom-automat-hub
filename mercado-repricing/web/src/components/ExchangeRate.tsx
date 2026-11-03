import {useEffect,useState} from 'react';
import {ArrowLeftRight} from 'lucide-react';
import {api} from '../api/client';

type Quote={cnyToBrl:number|null;brlToCny:number|null;quotedAt:string|null;retrievedAt:string|null;stale:boolean;error:string|null;source:string};
export function ExchangeRate({active}:{active:boolean}){
 const [quote,setQuote]=useState<Quote|null>(null),[failed,setFailed]=useState(false);
 useEffect(()=>{
  if(!active)return;
  let stopped=false,timer:ReturnType<typeof setTimeout>;const controller=new AbortController();
  async function read(){
   if(document.visibilityState==='hidden'){timer=setTimeout(read,60000);return;}
   try{const next=await api<Quote>('/dashboard/exchange-rate',{signal:controller.signal});if(!stopped){setQuote(next);setFailed(false);}}
   catch{if(!stopped)setFailed(true);}
   finally{if(!stopped)timer=setTimeout(read,60000);}
  }
  void read();return()=>{stopped=true;controller.abort();clearTimeout(timer);};
 },[active]);
 const old=failed||quote?.stale;
 const time=quote?.quotedAt?new Date(quote.quotedAt).toLocaleString('zh-CN',{month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit'}):'';
 return <div className={`dashboard-exchange ${old?'is-stale':''}`} aria-label="人民币雷亚尔参考汇率" title={`${quote?.source??'AwesomeAPI'} · 买卖中间价 · 每分钟刷新 · 仅供参考，不参与利润核算`}>
  <ArrowLeftRight size={17} aria-hidden="true"/><div><strong>{quote?.brlToCny&&quote?.cnyToBrl?<>R$ 1 ≈ ¥{quote.brlToCny.toFixed(4)}<span>¥1 ≈ R$ {quote.cnyToBrl.toFixed(4)}</span></>:failed||quote?.error?'汇率暂不可用':'汇率读取中…'}</strong><small>{old?(failed?'连接失败，保留上次报价':quote?.error):'参考汇率'}{time?` · ${time}`:''}</small></div>
 </div>;
}
