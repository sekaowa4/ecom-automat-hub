// Browser plugin not available; use the repository's existing Playwright runtime.
const {chromium}=require('../../web/node_modules/playwright'),assert=require('assert/strict'),fs=require('fs'),path=require('path');
(async()=>{const b=await chromium.launch({executablePath:'C:/Program Files/Google/Chrome/Application/chrome.exe',headless:true});try{
 const p=await b.newPage({viewport:{width:1440,height:1000}}),errors=[];p.on('pageerror',e=>errors.push(e.message));let crops=0,cartonWrites=0,jobWrites=0;
 await p.route('**/*',async route=>{const u=new URL(route.request().url());if(['blob:','data:','chrome-extension:','chrome:'].includes(u.protocol))return route.continue();assert.equal(u.origin,'http://fixture.local',u.href);let data;
  if(u.pathname==='/api/session')data={token:'fixture-local'};
  else if(u.pathname==='/api/ziniao/stores')data=[];
  else if(u.pathname==='/api/labels/product/crop'){
   assert.equal(route.request().headers()['x-local-token'],'fixture-local');crops++;
   if(crops===1)return route.fulfill({status:400,contentType:'application/problem+json',body:JSON.stringify({title:'未找到标签边框，请确认上传的是原始产品标签 PDF。'})});
   if(crops===3)await new Promise(r=>setTimeout(r,600));
   return route.fulfill({contentType:'application/pdf',headers:{'X-Label-Count':'4','Content-Disposition':"attachment; filename*=UTF-8''labels.pdf"},body:fs.readFileSync('tests/fixtures/labels/four-labels.pdf')}).catch(()=>{});
  }else if(u.pathname==='/api/labels/carton/crop'){cartonWrites++;return route.fulfill({contentType:'application/pdf',headers:{'X-Label-Count':'4'},body:fs.readFileSync('tests/fixtures/labels/four-labels.pdf')});}
  else if(u.pathname.startsWith('/api/jobs')){if(route.request().method()!=='GET')jobWrites++;data=[];}
  else if(u.pathname.startsWith('/api/'))throw Error('Unexpected API '+u.pathname);
  else{const file=path.join(process.cwd(),'server/wwwroot',u.pathname==='/'?'index.html':u.pathname);return route.fulfill({contentType:file.endsWith('.js')?'text/javascript':file.endsWith('.css')?'text/css':file.endsWith('.svg')?'image/svg+xml':'text/html',body:fs.readFileSync(file)});}
  await route.fulfill({contentType:'application/json',body:JSON.stringify(data)});
 });
 await p.goto('http://fixture.local');assert.match(await p.title(),/美客多工作台/);
 const menu=p.getByRole('button',{name:'标签剪裁',exact:true});await menu.click();assert.equal(await menu.getAttribute('aria-expanded'),'true');
 await p.getByRole('button',{name:'产品标签',exact:true}).click();await p.getByRole('heading',{name:'产品标签剪裁',exact:true}).waitFor();
 const upload=p.getByLabel('选择产品标签 PDF');await upload.setInputFiles([{name:'无边框.pdf',mimeType:'application/pdf',buffer:fs.readFileSync('tests/fixtures/labels/four-labels.pdf')},{name:'正常标签.pdf',mimeType:'application/pdf',buffer:fs.readFileSync('tests/fixtures/labels/four-labels.pdf')}]);
 await p.getByRole('button',{name:'开始剪裁',exact:true}).click();await p.getByText('4 张标签',{exact:true}).waitFor();await p.getByText(/未找到标签边框/).waitFor();assert.equal(crops,2);
 await p.getByRole('button',{name:'预览原文件 正常标签.pdf',exact:true}).click();await p.getByRole('dialog').waitFor();assert.equal(await p.getByRole('dialog').evaluate(e=>getComputedStyle(e).backgroundColor),'rgb(255, 255, 255)');assert.match(await p.getByTitle('原文件PDF预览').getAttribute('src'),/^blob:/);await p.waitForTimeout(800);await p.screenshot({path:path.join(process.env.TEMP||'tmp','mercado-label-preview.png')});await p.getByRole('button',{name:'剪裁结果',exact:true}).click();assert.match(await p.getByTitle('剪裁结果PDF预览').getAttribute('src'),/^blob:/);await p.keyboard.press('Escape');await p.getByRole('dialog').waitFor({state:'hidden'});
 const download=p.getByRole('link',{name:'下载 正常标签.pdf'});assert.match(await download.getAttribute('href'),/^blob:/);const downloaded=p.waitForEvent('download');await download.click();assert.equal((await downloaded).suggestedFilename(),'正常标签-已剪裁.pdf');
 await p.getByRole('button',{name:'日常调价',exact:true}).click();if(await menu.getAttribute('aria-expanded')==='false')await menu.click();await p.getByRole('button',{name:'产品标签',exact:true}).click();await download.waitFor();
 const before=await p.locator('.brand-icon').evaluate(e=>getComputedStyle(e).color);await p.getByRole('button',{name:'皮肤中心',exact:true}).click();await p.getByRole('button',{name:/樱花初绽/}).click();await p.keyboard.press('Escape');const after=await p.locator('.brand-icon').evaluate(e=>getComputedStyle(e).color);assert.notEqual(before,after);
 const out=process.env.TEMP||'tmp';await p.screenshot({path:path.join(out,'mercado-labels-desktop.png'),fullPage:true});
 await p.getByRole('button',{name:'清空列表',exact:true}).click();await upload.setInputFiles({name:'停止.pdf',mimeType:'application/pdf',buffer:fs.readFileSync('tests/fixtures/labels/four-labels.pdf')});await p.getByRole('button',{name:'开始剪裁',exact:true}).click();await p.getByRole('button',{name:'停止剪裁',exact:true}).click();await p.getByText('已停止',{exact:true}).waitFor();
 await p.getByRole('button',{name:'箱唛标签',exact:true}).click();await p.getByRole('heading',{name:'箱唛标签剪裁',exact:true}).waitFor();const cartonUpload=p.getByLabel('选择箱唛标签 PDF');await cartonUpload.setInputFiles({name:'箱唛.pdf',mimeType:'application/pdf',buffer:fs.readFileSync('tests/fixtures/labels/four-labels.pdf')});await p.getByRole('button',{name:'开始剪裁',exact:true}).click();await p.getByRole('button',{name:'预览结果 箱唛.pdf',exact:true}).click();await p.getByRole('dialog').waitFor();assert.match(await p.getByTitle('剪裁结果PDF预览').getAttribute('src'),/^blob:/);await p.keyboard.press('Escape');await p.getByRole('button',{name:'产品标签',exact:true}).click();await p.getByRole('button',{name:'箱唛标签',exact:true}).click();assert.ok(await p.getByRole('link',{name:'下载 箱唛.pdf',exact:true}).isVisible());
 await p.setViewportSize({width:390,height:844});assert.ok(await p.getByRole('button',{name:'产品标签',exact:true}).isVisible());await p.getByRole('button',{name:'产品标签',exact:true}).click();await p.screenshot({path:path.join(out,'mercado-labels-mobile.png'),fullPage:true});assert.equal(await p.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
 assert.equal(cartonWrites,1);assert.equal(jobWrites,0);assert.deepEqual(errors,[]);console.log('PASS: label navigation, isolated upload, failure continues, download retained, cancellation, skin logo, carton cropping and both previews, mobile; no live writes');
}finally{await b.close()}})().catch(e=>{console.error(e);process.exitCode=1});
