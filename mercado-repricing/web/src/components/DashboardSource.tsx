import {useEffect,useState} from 'react';
import {Database,Save} from 'lucide-react';
import {api,post,message} from '../api/client';
import type {BatchMapping} from '../api/dashboard';
export default function DashboardSource(){
 const [directory,setDirectory]=useState(''),[busy,setBusy]=useState(false),[notice,setNotice]=useState(''),[error,setError]=useState('');
 const [mapping,setMapping]=useState<BatchMapping|null>(null);
 useEffect(()=>{void api<{directory:string}>('/dashboard/source').then(s=>setDirectory(s.directory)).catch(e=>setError(message(e)));},[]);
 useEffect(()=>{void api<BatchMapping>('/dashboard/batches').then(setMapping).catch(e=>setError(message(e)));},[]);
 async function save(){setBusy(true);setNotice('');setError('');try{await post('/dashboard/source',{directory});window.dispatchEvent(new Event('dashboard-source-changed'));setNotice('已连接本机配置，返回数据看板即可读取。');}catch(e){setError(message(e));}finally{setBusy(false);}}
 const count=mapping?Object.keys(mapping.values).length:0;
 const mappingStatus=mapping?.fileName==='批次对应表.xlsx'&&mapping.importedAt===null?`内置映射 · ${count} 个 SKU`:count?`本机现存映射 · ${count} 个 SKU`:'未找到内置批次对应表';
 return <section className="settings-panel dashboard-source"><div className="setting-row"><span className="setting-icon"><Database size={22}/></span><div><h2>UpSeller 数据看板</h2><p>优先使用随本地压缩包附带的 UpSeller 只读配置。压缩包中的登录凭据只由本地服务读取，请仅发给有权限的同事。</p><label htmlFor="dashboard-directory">每日消息推送配置文件夹</label><div className="dashboard-source-input"><input id="dashboard-directory" value={directory} onChange={e=>setDirectory(e.target.value)} placeholder="包含 config.json 的本机文件夹"/><button className="button primary" disabled={busy||!directory.trim()} onClick={()=>void save()}><Save size={15}/>{busy?'保存中…':'保存连接'}</button></div><p>默认使用压缩包内置的“每日消息推送”配置。登录失效时，在原每日推送工具更新登录状态；看板保留已加载数据，点击顶部刷新才更新。更换数据源会重新读取。</p><label>SKU 批次对应表</label><p role="status">{mapping?mappingStatus:'正在读取内置批次映射…'}</p>{mapping?.unmatched? <p>{mapping.unmatched} 个 SKU 无有效批次，排行中显示“未匹配”。</p>:null}<p>批次表随本机压缩包内置，无需上传；缺失或 #N/A 显示未匹配，冲突会报告表格问题。</p>{error&&<p className="warning-note" role="alert">{error}</p>}{notice&&<p role="status">{notice}</p>}</div></div></section>;
}
