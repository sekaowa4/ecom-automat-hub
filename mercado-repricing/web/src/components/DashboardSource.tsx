import {useEffect,useState} from 'react';
import {Database,Save} from 'lucide-react';
import {api,post,message} from '../api/client';
export default function DashboardSource(){
 const [directory,setDirectory]=useState(''),[busy,setBusy]=useState(false),[notice,setNotice]=useState(''),[error,setError]=useState('');
 useEffect(()=>{void api<{directory:string}>('/dashboard/source').then(s=>setDirectory(s.directory)).catch(e=>setError(message(e)));},[]);
 async function save(){setBusy(true);setNotice('');setError('');try{await post('/dashboard/source',{directory});setNotice('已连接本机配置，返回数据看板即可读取。');}catch(e){setError(message(e));}finally{setBusy(false);}}
 return <section className="settings-panel dashboard-source"><div className="setting-row"><span className="setting-icon"><Database size={22}/></span><div><h2>UpSeller 数据看板</h2><p>复用本机“每日消息推送”的 UpSeller 只读接口、月目标、分类表与利润表。登录凭据仅由本地服务读取，不发送到网页或随程序分发。</p><label htmlFor="dashboard-directory">每日消息推送配置文件夹</label><div className="dashboard-source-input"><input id="dashboard-directory" value={directory} onChange={e=>setDirectory(e.target.value)} placeholder="包含 config.json 的本机文件夹"/><button className="button primary" disabled={busy||!directory.trim()} onClick={()=>void save()}><Save size={15}/>{busy?'保存中…':'保存连接'}</button></div><p>默认读取桌面的“每日消息推送”文件夹。接口登录失效时，在原每日推送工具更新登录状态；看板每5分钟刷新，也可手动刷新。</p>{error&&<p className="warning-note" role="alert">{error}</p>}{notice&&<p role="status">{notice}</p>}</div></div></section>;
}
