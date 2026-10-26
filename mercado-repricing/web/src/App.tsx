import { useEffect,useState } from 'react';
import AppShell,{type Page} from './components/AppShell';
import Workbench from './pages/Workbench';
import History from './pages/History';
import Settings from './pages/Settings';
import Dashboard from './pages/Dashboard';
import Labels,{CartonLabels} from './pages/Labels';
import { api,post,message } from './api/client';
import { loadAppearance,saveAppearance,defaultAppearance,type SkinId,type CustomSkin } from './theme';
import SkinPanel from './components/SkinPanel';
export default function App(){
 const [appearance,setAppearance]=useState(loadAppearance),[skinOpen,setSkinOpen]=useState(false),[appearanceSaved,setAppearanceSaved]=useState(true);
 useEffect(()=>{setAppearanceSaved(saveAppearance(appearance));},[appearance]);
 function selectSkin(skin:SkinId){setAppearance(a=>({...a,skin}));}
 function customize(patch:Partial<CustomSkin>){setAppearance(a=>({...a,custom:{...a.custom,...patch}}));}
 const [workbenchKind,setWorkbenchKind]=useState<'daily'|'weekend'>('daily');
 const [workbenchVisited,setWorkbenchVisited]=useState(()=>new URLSearchParams(location.search).has('job'));
 const [labelsVisited,setLabelsVisited]=useState(false),[cartonVisited,setCartonVisited]=useState(false);
 function navigate(next:Page){if(next==='workbench'||next==='weekend')setWorkbenchVisited(true);if(next==='product-labels')setLabelsVisited(true);if(next==='carton-labels')setCartonVisited(true);if((next==='weekend'||next==='workbench')&&(next==='weekend'?'weekend':'daily')!==workbenchKind){setWorkbenchKind(next==='weekend'?'weekend':'daily');setJobId(null);const url=new URL(location.href);url.searchParams.delete('job');history.replaceState(null,'',url);}setPage(next);}
 const [page,setPage]=useState<Page>(()=>new URLSearchParams(location.search).has('job')?'workbench':'dashboard');const [jobId,setJobId]=useState<string|null>(()=>new URLSearchParams(location.search).get('job'));const [exitPrompt,setExitPrompt]=useState(false);const [exitError,setExitError]=useState('');const [stopped,setStopped]=useState(false);
 function selectJob(id:string|null,jobMode?:string){setWorkbenchVisited(true);setJobId(id);const url=new URL(location.href);if(id)url.searchParams.set('job',id);else url.searchParams.delete('job');history.replaceState(null,'',url);const kind=jobMode?(jobMode==='WeekendEnrollment'?'weekend':'daily'):workbenchKind;setWorkbenchKind(kind);setPage(kind==='weekend'?'weekend':'workbench');}
 useEffect(()=>{const initialId=new URLSearchParams(location.search).get('job');if(initialId)void api<{summary:{mode:string}}>(`/jobs/${initialId}`).then(j=>{if(j.summary.mode==='WeekendEnrollment'){setWorkbenchKind('weekend');setPage('weekend');}}).catch(()=>{});},[]);
 async function exit(){setExitError('');try{await post('/shutdown');setStopped(true);setExitPrompt(false);}catch(e){setExitError(message(e));}}
 if(stopped)return <div className="stopped-screen"><h1>程序已退出</h1><p>可以关闭此网页。下次双击启动入口即可继续使用。</p></div>;
 return <AppShell onOpenSkin={()=>setSkinOpen(true)} page={page} onNavigate={navigate} onExit={()=>setExitPrompt(true)}>{page==='dashboard'&&<Dashboard onSettings={()=>navigate('settings')}/>}<div hidden={page!=='workbench'&&page!=='weekend'}>{workbenchVisited&&<Workbench key={workbenchKind} weekend={workbenchKind==='weekend'} jobId={jobId} onJob={selectJob}/>}</div>{page==='history'&&<History onOpen={selectJob}/>} {page==='settings'&&<Settings/>}{labelsVisited&&<div hidden={page!=='product-labels'}><Labels/></div>}{cartonVisited&&<div hidden={page!=='carton-labels'}><CartonLabels/></div>}{exitPrompt&&<div className="modal-backdrop"><section className="modal" role="dialog" aria-modal="true" aria-labelledby="exit-title"><h2 id="exit-title">退出本地程序？</h2><p>退出后后台服务停止。仍在执行的任务需要先暂停。</p>{exitError&&<p className="warning-note" role="alert">{exitError}</p>}<div className="button-row"><button className="button" autoFocus onClick={()=>setExitPrompt(false)}>取消</button><button className="button primary" onClick={exit}>退出程序</button></div></section></div>}{skinOpen&&<SkinPanel appearance={appearance} saved={appearanceSaved} onSelect={selectSkin} onCustom={customize} onReset={()=>setAppearance(a=>({...a,custom:{...defaultAppearance.custom}}))} onClose={()=>setSkinOpen(false)}/>}</AppShell>;
}
