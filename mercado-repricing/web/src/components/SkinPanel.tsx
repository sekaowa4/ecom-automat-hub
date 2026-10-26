import { useEffect,useRef,useState,type CSSProperties } from 'react';
import { Check,ImagePlus,Palette,RotateCcw,Trash2,X } from 'lucide-react';
import { skins,type Appearance,type CustomSkin,type SkinId } from '../theme';

async function backgroundImage(file:File){
 if(!['image/png','image/jpeg','image/webp'].includes(file.type))throw Error('请选择 PNG、JPG 或 WebP 图片。');
 if(file.size>10*1024*1024)throw Error('背景图片不能超过 10 MB。');
 const bitmap=await createImageBitmap(file);
 try{
  const scale=Math.min(1,1600/Math.max(bitmap.width,bitmap.height));
  const canvas=document.createElement('canvas');canvas.width=Math.max(1,Math.round(bitmap.width*scale));canvas.height=Math.max(1,Math.round(bitmap.height*scale));
  const context=canvas.getContext('2d');if(!context)throw Error('当前浏览器无法处理背景图片。');
  context.fillStyle='#ffffff';context.fillRect(0,0,canvas.width,canvas.height);context.drawImage(bitmap,0,0,canvas.width,canvas.height);
  const image=canvas.toDataURL('image/jpeg',.8);if(image.length>2000000)throw Error('图片处理后仍过大，请换一张较小的图片。');return image;
 }finally{bitmap.close();}
}
export default function SkinPanel({appearance,saved,onSelect,onCustom,onReset,onClose}:{appearance:Appearance;saved:boolean;onSelect:(skin:SkinId)=>void;onCustom:(patch:Partial<CustomSkin>)=>void;onReset:()=>void;onClose:()=>void}){
 const dialog=useRef<HTMLDialogElement>(null),file=useRef<HTMLInputElement>(null),generation=useRef(0);
 const [uploading,setUploading]=useState(false),[error,setError]=useState('');
 useEffect(()=>{const d=dialog.current!;d.showModal();return()=>{generation.current++;d.close();};},[]);
 async function upload(image:File){const id=++generation.current;setUploading(true);setError('');try{const data=await backgroundImage(image);if(id===generation.current)onCustom({image:data});}catch(e){if(id===generation.current)setError(e instanceof Error?e.message:'图片读取失败，请换一张图片。');}finally{if(id===generation.current)setUploading(false);}}
 const custom=appearance.custom;
 return <dialog ref={dialog} className="skin-drawer" aria-labelledby="skin-title" onCancel={e=>{e.preventDefault();onClose();}} onClick={e=>{if(e.target===e.currentTarget){const r=e.currentTarget.getBoundingClientRect();if(e.clientX<r.left||e.clientX>r.right||e.clientY<r.top||e.clientY>r.bottom)onClose();}}}>
  <header className="skin-header"><div><span className="skin-eyebrow"><Palette size={15}/> MAKE IT YOURS</span><h2 id="skin-title">皮肤中心</h2><p>让每天的工作，多一点喜欢。</p></div><button className="skin-close" onClick={onClose} aria-label="关闭皮肤中心"><X size={21}/></button></header>
  <div className="skin-content"><div className="skin-section-title"><h3>精选皮肤</h3><span>选择即预览</span></div>
   <div className="skin-grid">{skins.map((skin,i)=><button key={skin.id} className={`skin-choice ${appearance.skin===skin.id?'selected':''}`} aria-pressed={appearance.skin===skin.id} aria-label={skin.name} onClick={()=>{setError('');onSelect(skin.id);}}>
    <span className="skin-preview" style={{'--preview-bg':skin.id==='custom'?custom.background:skin.canvas,'--preview-accent':skin.id==='custom'?custom.accent:skin.accent,'--preview-surface':skin.dark?'#ffffff13':'#ffffffcf'} as CSSProperties}><span className="mini-sidebar"><i/><i/><i/></span><span className="mini-main"><i/><span/><b/><span/></span>{skin.id==='custom'&&<Palette className="custom-preview-icon" size={22}/>}</span>
    <span className="skin-choice-label"><strong>{skin.name}</strong>{appearance.skin===skin.id?<Check size={16}/>:<small>{i<2?'原有':i===8?'自定义':String(i-1).padStart(2,'0')}</small>}</span><span className="skin-description">{skin.description}</span>
   </button>)}</div>
   {appearance.skin==='custom'&&<section className="custom-skin"><div className="skin-section-title"><h3>随心定制</h3><button className="text-link" onClick={()=>{generation.current++;setUploading(false);setError('');onReset();}}><RotateCcw size={14}/>重置</button></div>
    <div className="skin-colors"><label>主题色<input type="color" aria-label="自定义主题色" value={custom.accent} onChange={e=>onCustom({accent:e.target.value})}/><span>{custom.accent.toUpperCase()}</span></label><label>背景底色<input type="color" aria-label="自定义背景底色" value={custom.background} onChange={e=>onCustom({background:e.target.value})}/><span>{custom.background.toUpperCase()}</span></label></div>
    <div className="skin-background"><div><ImagePlus size={20}/><strong>{custom.image?'已设置专属背景':'放一张喜欢的背景'}</strong><small>PNG / JPG / WebP · 最大 10 MB · 图片仅保存在本机浏览器</small></div><div className="button-row"><button className="button" disabled={uploading} onClick={()=>file.current?.click()}>{uploading?'正在处理…':custom.image?'更换图片':'选择图片'}</button>{custom.image&&<button className="button" aria-label="移除背景图片" onClick={()=>onCustom({image:''})}><Trash2 size={16}/></button>}</div><input ref={file} type="file" hidden aria-label="上传皮肤背景图片" accept="image/png,image/jpeg,image/webp" onChange={e=>{const image=e.currentTarget.files?.[0];e.currentTarget.value='';if(image)void upload(image);}}/></div>
    <label className="skin-slider"><span>面板透明度<strong>{100-custom.opacity}%</strong></span><input type="range" aria-label="面板透明度" min="0" max="60" step="1" value={100-custom.opacity} onChange={e=>onCustom({opacity:100-Number(e.target.value)})}/><small>只调整卡片与侧栏，文字始终清晰。</small></label>
    <label className="skin-slider"><span>背景磨砂<strong>{custom.blur===0?'关闭':custom.blur+' px'}</strong></span><input type="range" aria-label="背景磨砂" min="0" max="24" value={custom.blur} onChange={e=>onCustom({blur:Number(e.target.value)})}/></label>
    <p className="skin-help">透明效果透出所选背景图片，不会透出 Windows 桌面。明暗与文字颜色会根据底色自动适配；过浅的主题色会适当调整，以保证可读性。</p>
   </section>}
   {error&&<p className="warning-note" role="alert">{error}</p>}{!saved&&<p className="warning-note" role="alert">当前外观已应用，但浏览器未能保存。请尝试移除背景图片或释放浏览器存储空间。</p>}
  </div><footer className="skin-footer"><span><span className="status-dot"/>{saved?'已自动保存到此浏览器':'当前会话生效'}</span><button className="button primary" onClick={onClose}>完成</button></footer>
 </dialog>;
}
