import {useEffect,useRef,useState} from 'react';
import {Upload,Scissors,FileText,Download,CheckCircle2,AlertCircle,LoaderCircle,X,Package,ArrowRight,ShieldCheck,Plus,Square,Eye} from 'lucide-react';
import {cropLabels,message} from '../api/client';

type LabelFile={id:string;file:File;status:'pending'|'running'|'done'|'failed'|'stopped';rawUrl:string;url?:string;labels?:number;error?:string};
const maxBytes=10*1024*1024;
export default function Labels({kind='product'}:{kind?:'product'|'carton'}={}){
 const carton=kind==='carton',labelName=carton?'箱唛标签':'产品标签';
 const [preview,setPreview]=useState<{id:string;mode:'original'|'result'}|null>(null);
 const [files,setFiles]=useState<LabelFile[]>([]),[busy,setBusy]=useState(false),[notice,setNotice]=useState(''),[dragging,setDragging]=useState(false);
 const input=useRef<HTMLInputElement>(null),controller=useRef<AbortController|null>(null),urls=useRef(new Set<string>()),running=useRef(false),sequence=useRef(0);
 useEffect(()=>()=>{controller.current?.abort();for(const url of urls.current)URL.revokeObjectURL(url);},[]);
 function add(incoming:FileList|File[]|null){
  if(running.current||!incoming)return;
  const selected=Array.from(incoming);
  if(files.length+selected.length>20){setNotice('一次最多选择 20 个文件，请分批处理。');return;}
  const accepted:LabelFile[]=[],issues:string[]=[];
  for(const file of selected){
   if(!/\.pdf$/i.test(file.name)){issues.push(`${file.name}：仅支持 PDF`);continue;}
   if(file.size===0||file.size>maxBytes){issues.push(`${file.name}：文件为空或超过 10 MB`);continue;}
   const rawUrl=URL.createObjectURL(new Blob([file],{type:'application/pdf'}));urls.current.add(rawUrl);accepted.push({id:String(++sequence.current),file,rawUrl,status:'pending'});
  }
  setFiles(f=>[...f,...accepted]);setNotice(issues.join('；'));
 }
 function release(file:LabelFile){URL.revokeObjectURL(file.rawUrl);urls.current.delete(file.rawUrl);if(file.url){URL.revokeObjectURL(file.url);urls.current.delete(file.url);}}
 function clear(){if(running.current)return;files.forEach(release);setFiles([]);setNotice('');if(input.current)input.current.value='';}
 function remove(file:LabelFile){if(running.current)return;release(file);setFiles(f=>f.filter(x=>x.id!==file.id));}
 function update(id:string,patch:Partial<LabelFile>){setFiles(f=>f.map(x=>x.id===id?{...x,...patch}:x));}
 async function start(){
  if(running.current)return;
  const pending=files.filter(f=>f.status!=='done');if(!pending.length)return;
  const abort=new AbortController();controller.current=abort;running.current=true;setBusy(true);setNotice('');
  try{for(const row of pending){
   if(abort.signal.aborted)break;
   update(row.id,{status:'running',error:undefined});
   try{
    const result=await cropLabels(row.file,abort.signal,kind);
    if(abort.signal.aborted){update(row.id,{status:'stopped'});break;}
    const url=URL.createObjectURL(result.blob);urls.current.add(url);update(row.id,{status:'done',url,labels:result.labels});
   }catch(e){if(abort.signal.aborted){update(row.id,{status:'stopped'});break;}update(row.id,{status:'failed',error:message(e)});}
  }}finally{running.current=false;controller.current=null;setBusy(false);}
 }
 const done=files.filter(f=>f.status==='done'),failed=files.filter(f=>f.status==='failed'),remaining=files.filter(f=>f.status!=='done').length;
 return <section className="labels-page"><header className="page-header"><div><div className="page-eyebrow">标签剪裁 <ArrowRight size={12}/> {labelName}</div><h1>{labelName}剪裁</h1><p>{carton?'将横向整页箱唛拆成独立标签，完整保留条码、二维码和箱号。':'将整页产品标签拆成独立小标签，让打印准备更轻松。'}</p></div><span className="mode-badge"><Scissors size={14}/> 本地剪裁</span></header>
  <div className="label-layout"><div className="label-main"><div className="panel label-upload-panel"><div className="label-section-heading"><span className="label-step">01</span><div><h2>上传{labelName}</h2><p>{carton?'选择美客多导出的原始 A4 横向三列 PDF，可包含多页':'选择美客多导出的原始单页 PDF'}</p></div></div>
   <div className={`label-dropzone ${dragging?'dragging':''}`} onDragOver={e=>{e.preventDefault();if(!busy)setDragging(true);}} onDragLeave={()=>setDragging(false)} onDrop={e=>{e.preventDefault();setDragging(false);add(e.dataTransfer.files);}}>
    <span className="label-upload-icon"><Upload size={28}/></span><h3>拖放 PDF 到这里</h3><p>或从电脑选择文件，支持一次处理多个文件</p><button className="button primary" disabled={busy} onClick={()=>input.current?.click()}><Plus size={17}/>选择 PDF 文件</button><small>PDF · 每个文件最大 10 MB · 最多 20 个文件</small>
    <input ref={input} className="visually-hidden" type="file" accept=".pdf,application/pdf" multiple disabled={busy} aria-label={`选择${labelName} PDF`} onChange={e=>{add(e.currentTarget.files);e.currentTarget.value='';}}/>
   </div>{notice&&<p className="warning-note" role="alert">{notice}</p>}
  </div>
  <div className="panel label-results-panel"><div className="label-results-heading"><div className="label-section-heading"><span className="label-step">02</span><div><h2>剪裁与下载</h2><p>{files.length?`共 ${files.length} 个文件 · 已完成 ${done.length}${failed.length?` · 失败 ${failed.length}`:''}`:'添加文件后，剪裁结果会显示在这里'}</p></div></div>{files.length>0&&<button className="text-link" disabled={busy} onClick={clear}>清空列表</button>}</div>
   {files.length===0?<div className="label-empty"><FileText size={32}/><p>等待添加{labelName}</p><small>每个原始文件生成一份多页 PDF，每页一张标签</small></div>:<ul className="label-file-list">{files.map(row=><li key={row.id} className="label-file-row"><span className={`label-file-icon ${row.status}`}><FileText size={23}/></span><div className="label-file-info"><strong title={row.file.name}>{row.file.name}</strong><small>{(row.file.size/1024).toFixed(1)} KB{row.status==='done'?` · ${row.labels} 张标签`:''}</small>{row.error&&<p className="label-error">{row.error}</p>}</div><div className={`label-file-state ${row.status}`}><button className="button label-preview-button" aria-label={`预览原文件 ${row.file.name}`} onClick={()=>setPreview({id:row.id,mode:'original'})}><Eye size={15}/>原文件</button>{row.status==='done'&&<button className="button label-preview-button" aria-label={`预览结果 ${row.file.name}`} onClick={()=>setPreview({id:row.id,mode:'result'})}><Eye size={15}/>预览结果</button>}
    {row.status==='pending'&&<span>待处理</span>}{row.status==='running'&&<span><LoaderCircle size={15} className="spin"/>剪裁中</span>}{row.status==='done'&&<><span className="label-count"><CheckCircle2 size={15}/>{row.labels} 张标签</span><a className="button label-download" href={row.url} download={row.file.name.replace(/\.pdf$/i,'')+'-已剪裁.pdf'} aria-label={`下载 ${row.file.name}`}><Download size={15}/>下载</a></>}{row.status==='failed'&&<span><AlertCircle size={15}/>处理失败</span>}{row.status==='stopped'&&<span>已停止</span>}
   </div><button className="label-remove" disabled={busy} aria-label={`移除 ${row.file.name}`} onClick={()=>remove(row)}><X size={16}/></button></li>)}</ul>}
   <div className="label-action-bar"><span aria-live="polite">{busy?'正在逐个处理，失败文件会跳过并继续。':done.length?`已生成 ${done.reduce((n,f)=>n+(f.labels??0),0)} 张标签，可分别下载。`:'原始 PDF 保持不变'}</span>{busy?<button className="button" onClick={()=>controller.current?.abort()}><Square size={15}/>停止剪裁</button>:<button className="button primary" disabled={!remaining} onClick={start}><Scissors size={17}/>{done.length||failed.length?'继续剪裁':'开始剪裁'}</button>}</div>
  </div></div>
  <aside className="label-help"><div className="panel label-guide"><span className="label-guide-icon"><Scissors size={22}/></span><h2>为标签打印做好准备</h2><ol><li><strong>上传原始 PDF</strong><p>{carton?'使用完整边框的A4横向三列文件，支持多页和末页不足三张。':'使用包含完整标签边框的单页文件。'}</p></li><li><strong>自动识别与拆分</strong><p>按从上到下、从左到右的顺序生成标签页面。</p></li><li><strong>下载后打印</strong><p>保持原尺寸输出，条码和文字不转成图片。</p></li></ol></div><div className="label-local-note"><ShieldCheck size={20}/><div><strong>文件仅在本机处理</strong><p>无需登录店铺。下载完成后请自行保存，刷新页面会清空本次列表。</p></div></div></aside></div>
 {preview&&files.find(f=>f.id===preview.id)&&<LabelPreview row={files.find(f=>f.id===preview.id)!} mode={preview.mode} onMode={mode=>setPreview({...preview,mode})} onClose={()=>setPreview(null)}/>}
 </section>;
}

export function CartonLabels(){return <Labels kind="carton"/>;}

function LabelPreview({row,mode,onMode,onClose}:{row:LabelFile;mode:'original'|'result';onMode:(mode:'original'|'result')=>void;onClose:()=>void}){
 const dialog=useRef<HTMLDialogElement>(null);
 useEffect(()=>{dialog.current?.showModal();},[]);
 const url=mode==='result'?row.url:row.rawUrl;
 return <dialog ref={dialog} className="label-preview-dialog" onClose={onClose} aria-labelledby="label-preview-title"><div className="label-preview-header"><div><h2 id="label-preview-title">{mode==='result'?'剪裁结果预览':'原文件预览'}</h2><small>{row.file.name}{mode==='result'&&row.labels?` · ${row.labels} 张标签`:''}</small></div><button className="label-remove" aria-label="关闭预览" onClick={()=>dialog.current?.close()}><X size={20}/></button></div><div className="label-preview-toolbar"><div className="filter-tabs"><button className={mode==='original'?'selected':''} onClick={()=>onMode('original')}>原文件</button><button className={mode==='result'?'selected':''} disabled={!row.url} onClick={()=>onMode('result')}>剪裁结果</button></div><a className="text-link" href={url} target="_blank" rel="noopener noreferrer">单独打开</a>{mode==='result'&&<a className="button" href={row.url} download={row.file.name.replace(/\.pdf$/i,'')+'-已剪裁.pdf'}><Download size={15}/>下载 PDF</a>}</div><iframe title={mode==='result'?'剪裁结果PDF预览':'原文件PDF预览'} src={url+'#toolbar=1&navpanes=0&view=Fit'}/><p className="label-preview-note">在预览中翻页或缩放；若浏览器无法显示，可点击“单独打开”。打印请选择原始尺寸。</p></dialog>;
}
