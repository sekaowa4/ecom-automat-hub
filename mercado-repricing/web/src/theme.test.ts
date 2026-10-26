import { beforeEach,afterEach,describe,it,expect,vi } from 'vitest';
import { loadAppearance,saveAppearance,normalizeAppearance,skins,defaultAppearance } from './theme';
describe('skin preferences',()=>{
 let dataset:Record<string,string>,storage:Map<string,string>,styles:Map<string,string>;
 const setAttribute=vi.fn();
 beforeEach(()=>{dataset={};storage=new Map();styles=new Map();vi.stubGlobal('document',{documentElement:{dataset,style:{setProperty:(k:string,v:string)=>styles.set(k,v),removeProperty:(k:string)=>styles.delete(k)}},querySelector:()=>({setAttribute})});vi.stubGlobal('localStorage',{getItem:(k:string)=>storage.get(k)??null,setItem:(k:string,v:string)=>storage.set(k,v)});});
 afterEach(()=>vi.unstubAllGlobals());
 it('keeps both original palettes and six new named skins plus custom',()=>{expect(skins).toHaveLength(10);expect(new Set(skins.map(s=>s.id)).size).toBe(10);expect(loadAppearance().skin).toBe('midnight');expect(dataset.theme).toBe('midnight');});
 it('upgrades the visual default while retaining saved custom colors',()=>{storage.set('mercado-repricing.appearance.v1',JSON.stringify({...defaultAppearance,skin:'eye-care'}));expect(loadAppearance().skin).toBe('midnight');expect(dataset.theme).toBe('midnight');});
 it('remembers a new skin and removes its overrides on switching back',()=>{saveAppearance({...defaultAppearance,skin:'sakura'});expect(loadAppearance().skin).toBe('sakura');expect(styles.get('--accent')).toBeTruthy();saveAppearance({...defaultAppearance,skin:'light'});expect(styles.has('--accent')).toBe(false);});
 it('validates stored colors, limits and image data instead of accepting external URLs',()=>{const a=normalizeAppearance({skin:'custom',custom:{accent:'url(evil)',background:'#ffffff',opacity:-40,blur:100,image:'https://example.com/image'}});expect(a.custom.accent).toBe(defaultAppearance.custom.accent);expect(a.custom.opacity).toBe(40);expect(a.custom.blur).toBe(24);expect(a.custom.image).toBe('');expect(normalizeAppearance({skin:'unknown'}).skin).toBe('midnight');});
 it('keeps custom options and image across switching and reload',()=>{const custom={...defaultAppearance.custom,image:'data:image/png;base64,AAAA',opacity:70};saveAppearance({skin:'custom',custom});expect(styles.get('--skin-image')).toContain('data:image/png');saveAppearance({skin:'mint',custom});expect(styles.has('--skin-image')).toBe(false);expect(loadAppearance().custom).toEqual(custom);});
 it('applies the current choice even if saving fails',()=>{vi.stubGlobal('localStorage',{getItem:()=>{throw Error('blocked')},setItem:()=>{throw Error('full')}});expect(loadAppearance().skin).toBe('midnight');expect(saveAppearance({...defaultAppearance,skin:'sakura'})).toBe(false);expect(dataset.theme).toBe('sakura');});
 it('keeps midtone custom backgrounds readable',()=>{
  saveAppearance({skin:'custom',custom:{...defaultAppearance.custom,background:'#909090',opacity:100}});
  const lum=(h:string)=>[1,3,5].map(i=>parseInt(h.slice(i,i+2),16)/255).map(c=>c<=.04045?c/12.92:((c+.055)/1.055)**2.4).reduce((s,v,i)=>s+v*[.2126,.7152,.0722][i],0);
  const ratio=(a:string,b:string)=>(Math.max(lum(a),lum(b))+.05)/(Math.min(lum(a),lum(b))+.05);
  const surface=styles.get('--skin-panel')!;
  for(const property of ['--ink','--secondary','--accent'])expect(ratio(styles.get(property)!,surface)).toBeGreaterThanOrEqual(4.5);
 });
 it('recovers from malformed saved settings',()=>{storage.set('mercado-repricing.appearance.v2','{broken');expect(loadAppearance()).toEqual(defaultAppearance);});
});
