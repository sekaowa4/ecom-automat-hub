const fs=require('fs'),path=require('path'),assert=require('assert/strict'),{chromium}=require('../../web/node_modules/playwright');
(async()=>{
 const browser=await chromium.launch({executablePath:'C:/Program Files/Google/Chrome/Application/chrome.exe',headless:true});
 try{
  const p=await browser.newPage({viewport:{width:1440,height:1000}}),errors=[];p.on('pageerror',e=>errors.push(e.message));
  const id='verification-fixture',requestId='request-one';let continued=0,paused=0;
  const job={summary:{id,fileName:'验证等待测试.xlsx',mode:'PriceCut',status:'AwaitingVerification',createdAt:new Date().toISOString(),total:2,finished:0,basePrices:true,verification:{requestId,storeName:'测试店铺',sku:'TEST-SKU'}},rows:[{id:'r1',source:{sourceRow:2,sheet:'Sheet1',original:{},errors:[],input:{store:'test',storeName:'测试店铺',site:'MLB',currency:'BRL',sku:'TEST-SKU',targetPrice:90}},status:'AwaitingVerification',checkpoint:'verification-wait',message:'请完成验证'}]};
  await p.route('**/*',async route=>{
   const url=new URL(route.request().url());assert.equal(url.origin,'http://fixture.local');
   if(url.pathname.startsWith('/api/')){
    let data;
    if(url.pathname==='/api/session')data={token:'fixture-token'};
    else if(url.pathname==='/api/ziniao/stores')data=[];
    else if(url.pathname.endsWith('/events'))data=[];
    else if(url.pathname.endsWith('/pause')){paused++;job.summary.status='Paused';data={};}
    else if(url.pathname.endsWith('/verification/continue')){assert.deepEqual(route.request().postDataJSON(),{requestId});continued++;job.summary.status='Running';job.summary.verification=null;job.rows[0].status='Running';data={};}
    else if(url.pathname==='/api/jobs/'+id)data=job;
    else throw Error('Unexpected API '+url.pathname);
    return route.fulfill({contentType:'application/json',body:JSON.stringify(data)});
   }
   const file=path.join(process.cwd(),'server/wwwroot',url.pathname==='/'?'index.html':url.pathname);
   await route.fulfill({contentType:file.endsWith('.js')?'text/javascript':file.endsWith('.css')?'text/css':'text/html',body:fs.readFileSync(file)});
  });
  await p.goto('http://fixture.local/?job='+id);
  await p.getByRole('heading',{name:'调价已暂停：需要完成店铺验证'}).waitFor();
  assert.equal(await p.getByRole('dialog').count(),0);
  assert.equal(await p.getByRole('button',{name:'开始实际调价',exact:true}).count(),0);
  await p.getByRole('button',{name:'稍后处理',exact:true}).click();
  await p.getByText('已暂停',{exact:true}).waitFor();assert.equal(paused,1);
  assert.equal(await p.getByRole('button',{name:'继续调价',exact:true}).count(),0);
  await p.getByRole('button',{name:'我已验证，继续调价',exact:true}).click();
  await p.getByRole('heading',{name:'调价已暂停：需要完成店铺验证'}).waitFor({state:'hidden'});
  assert.equal(continued,1);assert.deepEqual(errors,[]);
  console.log('PASS: verification banner, no web modal, postpone, explicit request-id Continue, paused start cannot bypass verification. All requests mocked.');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1});
