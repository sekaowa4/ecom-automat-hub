import {useState,type ReactNode} from 'react';
import {Clock3,SlidersHorizontal,Monitor,Power,Palette,PanelTop,Scissors,ChevronDown,Tag,Package} from 'lucide-react';
import appPackage from '../../package.json';
import MercadoLogo from './MercadoLogo';
export type Page='workbench'|'weekend'|'history'|'settings'|'product-labels'|'carton-labels';
export default function AppShell({page,onNavigate,onExit,onOpenSkin,children}:{page:Page;onNavigate:(page:Page)=>void;onExit:()=>void;onOpenSkin:()=>void;children:ReactNode}){
 const [labelsOpen,setLabelsOpen]=useState(false);
 const [workbenchOpen,setWorkbenchOpen]=useState(true);
 const isLabels=page==='product-labels'||page==='carton-labels';
 return <div className="app-shell"><div className="skin-wallpaper" aria-hidden="true"/><aside className="sidebar"><div className="brand"><span className="brand-icon"><MercadoLogo/></span><div><strong>美客多工作台</strong><small>本地运营工具</small></div></div><nav aria-label="主导航">
  <div className="nav-group"><button className={`nav-item ${page==='workbench'||page==='weekend'?'group-active':''}`} aria-expanded={workbenchOpen} aria-controls="workbench-submenu" onClick={()=>setWorkbenchOpen(o=>!o)}><PanelTop size={20} aria-hidden="true"/><span>调价工作台</span><ChevronDown size={16} className={workbenchOpen?'nav-chevron open':'nav-chevron'}/></button>{workbenchOpen&&<div className="nav-submenu" id="workbench-submenu">{([{id:'workbench',label:'日常调价'},{id:'weekend',label:'周末活动报名'}] as const).map(item=><button key={item.id} className={`nav-item ${page===item.id?'active':''}`} onClick={()=>onNavigate(item.id)} aria-current={page===item.id?'page':undefined}>{item.label}</button>)}</div>}</div>
  <div className="nav-group"><button className={`nav-item ${isLabels?'group-active':''}`} aria-expanded={labelsOpen} aria-controls="label-submenu" onClick={()=>setLabelsOpen(o=>!o)}><Scissors size={20} aria-hidden="true"/><span>标签剪裁</span><ChevronDown size={16} className={labelsOpen?'nav-chevron open':'nav-chevron'} aria-hidden="true"/></button>
   {labelsOpen&&<div className="nav-submenu" id="label-submenu">{([{id:'product-labels',label:'产品标签',icon:Tag},{id:'carton-labels',label:'箱唛标签',icon:Package}] as const).map(item=><button key={item.id} className={`nav-item ${page===item.id?'active':''}`} onClick={()=>onNavigate(item.id)} aria-current={page===item.id?'page':undefined}><item.icon size={17} aria-hidden="true"/>{item.label}</button>)}</div>}
  </div>
  {([{id:'history',label:'任务记录',icon:Clock3},{id:'settings',label:'连接设置',icon:SlidersHorizontal}] as const).map(item=><button key={item.id} className={`nav-item ${page===item.id?'active':''}`} onClick={()=>onNavigate(item.id)} aria-current={page===item.id?'page':undefined}><item.icon size={20} aria-hidden="true"/>{item.label}</button>)}
 </nav><div className="sidebar-footer"><button className="nav-item skin-trigger" aria-label="皮肤中心" title="皮肤中心" aria-haspopup="dialog" onClick={onOpenSkin}><Palette size={20} aria-hidden="true"/><span>皮肤中心</span></button><div className="local-status"><Monitor size={18} aria-hidden="true"/><span>本地运行</span><span className="status-dot"/></div><button className="nav-item exit" onClick={onExit}><Power size={18} aria-hidden="true"/>退出程序</button><small>v{appPackage.version} · 数据保存在本机</small></div></aside><main className="main-content">{children}</main></div>;
}
