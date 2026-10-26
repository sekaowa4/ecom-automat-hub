export const skins=[
 {id:'midnight',name:'午夜流光',description:'薄荷绿 · 午夜蓝玻璃',canvas:'#050d1b',accent:'#52efbd',dark:true},
 {id:'light',name:'经典蓝白',description:'熟悉的蓝白工作台',canvas:'#f5f7fb',accent:'#2563eb',dark:false},
 {id:'eye-care',name:'曜石荧绿',description:'保留原护眼配色',canvas:'#10120f',accent:'#c2f970',dark:true},
 {id:'sakura',name:'樱花初绽',description:'樱花粉 · 暖白',canvas:'#fcf3f6',accent:'#b63868',dark:false},
 {id:'mint',name:'薄荷晨雾',description:'薄荷绿 · 雾白',canvas:'#f0f8f4',accent:'#14745d',dark:false},
 {id:'sky',name:'海盐晴空',description:'天蓝 · 浅灰蓝',canvas:'#f0f7fc',accent:'#136aab',dark:false},
 {id:'iris',name:'鸢尾暮色',description:'淡紫 · 灰白',canvas:'#f5f2fc',accent:'#7451b3',dark:false},
 {id:'latte',name:'暖杏奶茶',description:'杏色 · 奶油白',canvas:'#faf5ed',accent:'#94602c',dark:false},
 {id:'ocean',name:'深海星夜',description:'深海蓝 · 冰蓝',canvas:'#101b2b',accent:'#85caff',dark:true},
 {id:'custom',name:'随心定制',description:'自己的颜色与背景',canvas:'#f5f7fb',accent:'#2563eb',dark:false},
] as const;
export type SkinId=typeof skins[number]['id'];
export interface CustomSkin {accent:string;background:string;opacity:number;blur:number;image:string}
export interface Appearance {skin:SkinId;custom:CustomSkin}
export const defaultAppearance:Appearance={skin:'midnight',custom:{accent:'#b63868',background:'#faf2f5',opacity:85,blur:12,image:''}};
const key='mercado-repricing.appearance.v2';
const color=(v:unknown,fallback:string)=>typeof v==='string'&&/^#[0-9a-f]{6}$/i.test(v)?v:fallback;
const limit=(v:unknown,min:number,max:number,fallback:number)=>typeof v==='number'&&Number.isFinite(v)?Math.max(min,Math.min(max,v)):fallback;
export function normalizeAppearance(value:unknown):Appearance{
 const v=value&&typeof value==='object'?value as Partial<Appearance>:{};
 const c=v.custom&&typeof v.custom==='object'?v.custom:defaultAppearance.custom;
 return {skin:skins.some(s=>s.id===v.skin)?v.skin!:defaultAppearance.skin,custom:{accent:color(c.accent,defaultAppearance.custom.accent),background:color(c.background,defaultAppearance.custom.background),opacity:limit(c.opacity,40,100,85),blur:limit(c.blur,0,24,12),image:typeof c.image==='string'&&c.image.length<=2000000&&/^data:image\/(png|jpeg|webp);base64,[A-Za-z0-9+/]+=*$/.test(c.image)?c.image:''}};
}
const rgb=(hex:string)=>[1,3,5].map(i=>parseInt(hex.slice(i,i+2),16));
const mix=(a:string,b:string,t:number)=>'#'+rgb(a).map((c,i)=>Math.round(c*(1-t)+rgb(b)[i]*t).toString(16).padStart(2,'0')).join('');
const lum=(hex:string)=>rgb(hex).map(v=>{const c=v/255;return c<=.04045?c/12.92:((c+.055)/1.055)**2.4;}).reduce((s,v,i)=>s+v*[.2126,.7152,.0722][i],0);
const contrast=(a:string,b:string)=>(Math.max(lum(a),lum(b))+.05)/(Math.min(lum(a),lum(b))+.05);
function readable(accent:string,bg:string){let c=accent;const target=contrast(bg,'#000000')>contrast(bg,'#ffffff')?'#000000':'#ffffff';for(let i=0;i<20&&contrast(c,bg)<4.5;i++)c=mix(c,target,.14);return contrast(c,bg)>=4.5?c:target;}
const properties=['canvas','surface','surface-raised','surface-hover','ink','secondary','muted','line','border','line-strong','accent','accent-hover','accent-text','accent-soft','on-accent','focus','success','warning','warning-soft','danger','danger-soft','danger-line','shadow','overlay','skin-image','glass-blur','skin-panel','wallpaper-tint'];
export function applyAppearance(value:Appearance){
 const a=normalizeAppearance(value),root=document.documentElement,skin=skins.find(s=>s.id===a.skin)!;
 root.dataset.theme=a.skin;for(const p of properties)root.style.removeProperty('--'+p);
 root.dataset.tone=skin.dark?'dark':'light';
 if(a.skin!=='light'&&a.skin!=='eye-care'&&a.skin!=='midnight'){
  const custom=a.skin==='custom',canvas=custom?a.custom.background:skin.canvas,dark=custom?contrast(canvas,'#ffffff')>contrast(canvas,'#000000'):skin.dark;
  const surface=dark?mix(canvas,'#ffffff',.045):mix(canvas,'#ffffff',.78),accent=readable(custom?a.custom.accent:skin.accent,dark?surface:canvas);
  root.dataset.tone=dark?'dark':'light';
  const alpha=custom?a.custom.opacity/100:1,paint=(c:string)=>alpha===1?c:`rgba(${rgb(c).join(',')},${alpha})`;
  const vars:Record<string,string>={canvas,surface:paint(surface),'surface-raised':paint(mix(surface,accent,.045)),'surface-hover':paint(mix(surface,accent,.08)),ink:readable(dark?'#edf3fc':'#202536',dark?surface:canvas),secondary:readable(dark?'#b2c1d4':'#616679',dark?surface:canvas),muted:readable(dark?'#b2c1d4':'#616679',dark?surface:canvas),line:mix(surface,accent,dark?.23:.16),border:mix(surface,accent,dark?.23:.16),'line-strong':mix(surface,accent,.35),accent,'accent-text':accent,'accent-hover':mix(accent,dark?'#ffffff':'#000000',.1),'accent-soft':paint(mix(surface,accent,dark?.16:.08)),'on-accent':contrast(accent,'#ffffff')>contrast(accent,'#000000')?'#ffffff':'#000000',focus:accent,success:dark?'#94dcae':'#167344',warning:dark?'#f0bd68':'#9c5715','warning-soft':dark?'#332a1c':'#fff5e8',danger:dark?'#ffaaaa':'#b33838','danger-soft':dark?'#361f29':'#fff0f1','danger-line':dark?'#684151':'#f2ced3',shadow:'transparent',overlay:'#08101c80','skin-panel':surface,'glass-blur':custom?a.custom.blur+'px':'0px'};
  if(custom){vars['skin-image']=a.custom.image?`url("${a.custom.image}")`:'none';vars['wallpaper-tint']=`rgba(${rgb(canvas).join(',')},.55)`;}
  for(const [k,v] of Object.entries(vars))root.style.setProperty('--'+k,v);
 }
 document.querySelector('meta[name="theme-color"]')?.setAttribute('content',a.skin==='eye-care'?'#10120f':a.skin==='custom'?a.custom.accent:skin.accent);
}
export function loadAppearance():Appearance{
 let a=defaultAppearance;
 try{const saved=localStorage.getItem(key);const previous=localStorage.getItem('mercado-repricing.appearance.v1');a=saved?normalizeAppearance(JSON.parse(saved)):previous?{...normalizeAppearance(JSON.parse(previous)),skin:'midnight'}:defaultAppearance;}catch{/* Use the default when preferences are unavailable. */}
 applyAppearance(a);return a;
}
export function saveAppearance(value:Appearance){const a=normalizeAppearance(value);applyAppearance(a);try{localStorage.setItem(key,JSON.stringify(a));return true;}catch{return false;}}
