// Browser plugin not available. Installed Playwright tests only mocked requests and local assets.
const fs=require('fs'),path=require('path'),assert=require('assert/strict'),{chromium}=require('../../web/node_modules/playwright');
(async()=>{
 const browser=await chromium.launch({executablePath:'C:/Program Files/Google/Chrome/Application/chrome.exe',headless:true});
 try{
  const p=await browser.newPage({viewport:{width:1440,height:1000}}),errors=[];p.on('pageerror',e=>errors.push(e.message));
  await p.addInitScript(()=>localStorage.setItem('mercado-repricing.eye-care','true'));
  const input={store:'test',storeName:'测试店铺',site:'MLB',currency:'BRL',sku:'SKIN-TEST',targetPrice:99};
  const job={summary:{id:'skin-demo',fileName:'今日调价表.xlsx',mode:'PriceCut',status:'Completed',basePrices:true,total:1,finished:1,createdAt:new Date().toISOString()},rows:[{id:'r1',source:{sourceRow:2,sheet:'Sheet1',input,original:{},errors:[]},status:'Succeeded',route:'活动内降价',checkpoint:'verified',message:'仅为皮肤展示的模拟数据',observation:{itemId:'100',basePriceText:'R$ 159',currentPrice:99,difference:0}}]};
  await p.route('**/*',async r=>{const u=new URL(r.request().url());assert.equal(u.origin,'http://fixture.local');
   if(u.pathname.startsWith('/api/')){assert.equal(r.request().method(),'GET');let data;
    if(u.pathname==='/api/ziniao/stores'||u.pathname.endsWith('/events'))data=[];
    else if(u.pathname==='/api/jobs/skin-demo')data=job;
    else throw Error('Unexpected API '+u.pathname);
    return r.fulfill({contentType:'application/json',body:JSON.stringify(data)});
   }
   const f=path.join(process.cwd(),'server/wwwroot',u.pathname==='/'?'index.html':u.pathname);return r.fulfill({contentType:f.endsWith('.js')?'text/javascript':f.endsWith('.css')?'text/css':'text/html',body:fs.readFileSync(f)});
  });
  const theme=()=>p.evaluate(()=>document.documentElement.dataset.theme);
  const open=()=>p.getByRole('button',{name:'皮肤中心',exact:true}).click();
  const choose=name=>p.getByRole('button',{name,exact:true}).click();
  const range=async(name,v)=>p.getByRole('slider',{name,exact:true}).evaluate((e,v)=>{Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(e,String(v));e.dispatchEvent(new Event('input',{bubbles:true}));e.dispatchEvent(new Event('change',{bubbles:true}));},v);
  await p.goto('http://fixture.local/?job=skin-demo');await p.getByRole('heading',{name:'今日调价表.xlsx'}).waitFor();assert.equal(await theme(),'midnight');
  await open();assert.equal(await p.getByRole('dialog',{name:'皮肤中心'}).count(),1);assert.equal(await p.locator('.skin-choice').count(),10);
  for(const [name,id] of [['午夜流光','midnight'],['经典蓝白','light'],['曜石荧绿','eye-care'],['樱花初绽','sakura'],['薄荷晨雾','mint'],['海盐晴空','sky'],['鸢尾暮色','iris'],['暖杏奶茶','latte'],['深海星夜','ocean'],['随心定制','custom']]){await choose(name);assert.equal(await theme(),id);assert.equal(await p.getByRole('button',{name,exact:true}).getAttribute('aria-pressed'),'true');}
  await p.getByLabel('自定义主题色',{exact:true}).fill('#8537c2');await p.getByLabel('自定义背景底色',{exact:true}).fill('#171a26');
  await range('面板透明度',45);await range('背景磨砂',18);
  const png=await p.evaluate(()=>{const c=document.createElement('canvas');c.width=500;c.height=500;const x=c.getContext('2d'),g=x.createLinearGradient(0,0,500,500);g.addColorStop(0,'#206fa7');g.addColorStop(1,'#ca71a7');x.fillStyle=g;x.fillRect(0,0,500,500);return c.toDataURL('image/png').split(',')[1];});
  await p.getByLabel('上传皮肤背景图片',{exact:true}).setInputFiles({name:'背景测试.png',mimeType:'image/png',buffer:Buffer.from(png,'base64')});
  await p.getByText('已设置专属背景',{exact:true}).waitFor();
  const stored=await p.evaluate(()=>JSON.parse(localStorage.getItem('mercado-repricing.appearance.v2')));assert.equal(stored.custom.opacity,55);assert.equal(stored.custom.blur,18);assert.ok(stored.custom.image.startsWith('data:image/jpeg;base64,'));
  await p.getByRole('button',{name:'完成',exact:true}).click();await p.reload();await p.getByRole('heading',{name:'今日调价表.xlsx'}).waitFor();assert.equal(await theme(),'custom');
  assert.ok((await p.locator('.skin-wallpaper').evaluate(e=>getComputedStyle(e).backgroundImage)).includes('data:image/jpeg'));
  fs.mkdirSync('docs/design/qa',{recursive:true});await p.screenshot({path:'docs/design/qa/skin-custom-desktop.png',fullPage:true});
  await open();await p.getByLabel('上传皮肤背景图片',{exact:true}).setInputFiles({name:'不支持.svg',mimeType:'image/svg+xml',buffer:Buffer.from('<svg/>')});await p.getByText('请选择 PNG、JPG 或 WebP 图片。',{exact:true}).waitFor();
  await choose('樱花初绽');await p.screenshot({path:'docs/design/qa/skin-center-desktop.png',fullPage:true});await p.keyboard.press('Escape');await p.getByRole('dialog').waitFor({state:'hidden'});await p.screenshot({path:'docs/design/qa/skin-sakura-desktop.png',fullPage:true});
  await p.setViewportSize({width:390,height:844});await open();await choose('随心定制');await p.screenshot({path:'docs/design/qa/skin-center-mobile.png',fullPage:true});
  assert.ok(await p.getByRole('dialog').evaluate(e=>e.scrollWidth<=e.clientWidth));
  await p.getByRole('button',{name:'重置',exact:true}).click();assert.equal(await p.getByLabel('自定义主题色',{exact:true}).inputValue(),'#b63868');assert.equal(await p.getByText('放一张喜欢的背景',{exact:true}).count(),1);
  await p.getByRole('button',{name:'关闭皮肤中心',exact:true}).click();assert.deepEqual(errors,[]);
  console.log('PASS: 10 skins, legacy migration, custom colors/image/glass, local persistence/reload, image validation, reset, Escape, desktop and mobile. All API reads mocked; no business writes.');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1});
