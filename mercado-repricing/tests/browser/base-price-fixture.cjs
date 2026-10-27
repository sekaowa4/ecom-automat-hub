const fs=require('fs'),assert=require('assert/strict'),{chromium}=require('../../web/node_modules/playwright');
const source=fs.readFileSync('server/Execution/BasePricePage.js','utf8');
(async()=>{
 const browser=await chromium.launch({executablePath:'C:/Program Files/Google/Chrome/Application/chrome.exe',headless:true});
 try{
  const p=await browser.newPage(),url='https://vendedores.mercadolivre.com.br/anuncios/MLBU123/modificar/bomni/variation';
  const html=`<meta charset="utf-8"><div>在您的仓库中：1 单位 | 代码SKU-A</div><section id="sales"><span>#456</span><button aria-label="销售条件" aria-expanded="true"></button><div><input id="price__input-main" value="100" oninput="setTimeout(()=>document.querySelector('#confirm').disabled=false,150)"><input id="price__input-tier" disabled value="20"></div><label><input id="apply-all" type="checkbox">将价格设置应用于所有变体</label><button id="confirm" disabled onclick="document.querySelector('output').textContent=Number(document.querySelector('output').textContent)+1">确认</button></section><output>0</output>`;
  await p.route('**/*',r=>r.fulfill({contentType:'text/html',body:html}));
  const a={url,itemId:'456',sku:'SKU-A',basePrice:100,target:99,changeBase:true};
  const run=(action,extra={})=>p.evaluate('('+source+')('+JSON.stringify({...a,...extra,action})+')');
  const waitReady=async(action,extra={})=>{for(let i=0;i<100;i++){const state=await run(action,extra);if(state.ready)return state;await p.waitForTimeout(50);}throw Error('Timed out waiting for '+action);};
  const entry='https://www.mercadolivre.com.br/anuncios/MLBU123/modificar/omni/variation';
  await p.route(entry,r=>r.fulfill({contentType:'text/html',body:`<script>setTimeout(()=>location.href=${JSON.stringify(url)},1600)</script>`}));
  await p.goto(entry);await assert.rejects(run('open',{editorEntry:entry+'?other'}));assert.equal((await run('open',{editorEntry:entry})).ready,false);await p.waitForURL(url);
  await p.goto(url);
  await p.locator('#confirm').evaluate(e=>{const html=e.outerHTML;e.remove();setTimeout(()=>document.querySelector('#sales').insertAdjacentHTML('beforeend',html),800);});
  assert.equal((await run('open')).ready,false);await p.locator('#confirm').waitFor();assert.equal((await run('open')).ready,true);
  await assert.rejects(run('draft',{changeBase:false}));await assert.rejects(run('draft',{target:95}));await assert.rejects(run('draft',{itemId:'999'}));await assert.rejects(run('draft',{sku:'SKU-B'}));await assert.rejects(run('submit'));
  await p.locator('#apply-all').check();await assert.rejects(run('draft'));await p.locator('#apply-all').uncheck();
  await run('draft');assert.equal((await run('check')).ready,false);await p.waitForFunction(()=>!document.querySelector('#confirm').disabled);assert.equal((await waitReady('check')).ready,true);assert.equal(await p.locator('#price__input-main').inputValue(),'99,00');assert.equal(await p.locator('#price__input-tier').inputValue(),'20');assert.equal(await p.locator('output').innerText(),'0');await run('submit');assert.equal(await p.locator('output').innerText(),'1');await assert.rejects(run('submit'));
  await p.goto(url);await p.locator('#sales').evaluate(e=>e.insertAdjacentHTML('beforeend','<span>您在促销时以 R$80 出售</span>'));await assert.rejects(run('open'));
  await p.goto(url);await p.locator('#price__input-main').fill('110');await assert.rejects(run('open'));
  await p.goto(url);await p.locator('#price__input-main').fill('169');
  const adjusted={basePrice:169,target:75.6,discountBase:75.6,finalPrice:18.9};
  assert.equal((await run('open',adjusted)).ready,true);
  await assert.rejects(run('open',{...adjusted,finalPrice:19}));await assert.rejects(run('open',{...adjusted,discountBase:80}));
  await run('draft',adjusted);await p.waitForFunction(()=>!document.querySelector('#confirm').disabled);
  await waitReady('check',adjusted);assert.equal(await p.locator('#price__input-main').inputValue(),'75,60');assert.equal(await p.locator('output').innerText(),'0');await run('submit',adjusted);assert.equal(await p.locator('output').innerText(),'1');await assert.rejects(run('submit',adjusted));
  const manager='https://vendedores.mercadolivre.com.br/anuncios?page=1&sort=DEFAULT&search=SKU-A';
  const row=`<div class="sll-list-grid-row sll-list-grid-row--item" id="MLB456"><span class="sc-copyable-element__text">SKU SKU-A</span><div class="sll-list-cell-price">R$ 99</div><a href="https://www.mercadolivre.com.br/anuncios/MLBU123/modificar/omni/variation">商品</a></div>`;
  await p.route('**/anuncios?page=*',r=>r.fulfill({contentType:'text/html',body:row}));await p.goto(manager);assert.equal((await run('locate',{url:manager})).href,'https://www.mercadolivre.com.br/anuncios/MLBU123/modificar/omni/variation');
  assert.equal((await run('verify',{url:manager})).reached,true);await p.locator('.sll-list-cell-price').evaluate(e=>e.textContent='R$ 99 正以 R$ 80 促销');assert.equal((await run('verify',{url:manager})).reached,false);
  await p.locator('.sll-list-grid-row--item').evaluate(e=>e.insertAdjacentHTML('beforeend','<a href="https://www.mercadolivre.com.br/syi/core/modify?itemId=MLB456&taskId=prices_task">添加批发价格</a>'));
  assert.equal((await run('locate',{url:manager})).href,entry);
  await p.locator('.sc-copyable-element__text').evaluate(e=>e.textContent='SKU SKU-AB');await assert.rejects(run('locate',{url:manager}));
  await p.goto(url);await p.evaluate(()=>{const check=document.querySelector('#apply-all');check.checked=true;check.setAttribute('aria-labelledby','wholesale-label');check.parentElement.insertAdjacentHTML('beforeend','<span id="wholesale-label">批发价格</span>');check.onchange=()=>document.querySelector('#confirm').disabled=false;const tier=document.querySelector('#price__input-tier');tier.disabled=false;tier.parentElement.insertAdjacentHTML('beforeend','<div class="wholesale-prices" id="tier-wrap"></div>');document.querySelector('#tier-wrap').append(tier);});assert.equal((await run('open')).wholesaleEnabled,true);await assert.rejects(run('draft'));await run('wholesale-off');assert.equal(await p.locator('#price__input-main').inputValue(),'100');assert.equal((await waitReady('wholesale-check')).ready,true);await run('wholesale-submit');assert.equal(await p.locator('output').innerText(),'1');await assert.rejects(run('wholesale-submit'));
  assert.equal((await run('wholesale-saved')).ready,false);
  await p.goto(url);assert.equal((await run('wholesale-saved')).ready,false);
  await p.locator('button[aria-label]').evaluate(e=>e.setAttribute('aria-expanded','false'));assert.equal((await run('wholesale-saved')).ready,true);
  await p.goto(manager);assert.equal((await run('wholesale-saved',{url})).ready,true);
  await p.goto(manager.replace('SKU-A','OTHER'));await assert.rejects(run('wholesale-saved',{url}));
  const regressions=[];
  const regression=async(name,test)=>{try{await test();}catch(error){regressions.push(name+': '+error.message);}};
  await regression('enabled base Confirm waits for bound loaders and then a stable ready interval',async()=>{
   await p.goto(url);await run('open');await p.locator('#sales').evaluate(e=>e.insertAdjacentHTML('beforeend','<div class="andes-spinner">Loading</div>'));await p.locator('#confirm').evaluate(e=>e.disabled=false);
   assert.equal((await run('open')).ready,false);await assert.rejects(run('draft'));assert.equal(await p.locator('#price__input-main').inputValue(),'100');
   await p.locator('.andes-spinner').evaluate(e=>e.remove());await run('open');await run('draft');await p.waitForFunction(()=>!document.querySelector('#confirm').disabled);
   await p.locator('#sales').evaluate(e=>e.insertAdjacentHTML('beforeend','<div role="progressbar">Loading</div>'));assert.equal((await run('check')).ready,false);await assert.rejects(run('submit'));assert.equal(await p.locator('output').innerText(),'0');
   await p.locator('[role="progressbar"]').evaluate(e=>e.remove());assert.equal((await run('check')).ready,false);await p.waitForTimeout(550);assert.equal((await run('check')).ready,true);
   await p.locator('#sales').evaluate(e=>e.insertAdjacentHTML('beforeend','<div class="andes-spinner">Loading again</div>'));await assert.rejects(run('submit'));await p.locator('.andes-spinner').evaluate(e=>e.remove());assert.equal((await run('check')).ready,false);await p.waitForTimeout(550);assert.equal((await run('check')).ready,true);await run('submit');assert.equal(await p.locator('output').innerText(),'1');
  });
  await regression('enabled wholesale Confirm cannot submit while toggle response is loading',async()=>{
   await p.goto(url);await p.locator('#apply-all').evaluate(e=>{e.checked=true;e.setAttribute('aria-labelledby','wholesale-loading');e.parentElement.insertAdjacentHTML('beforeend','<span id="wholesale-loading">批发价格</span>');e.onchange=()=>{document.querySelector('#confirm').disabled=false;document.querySelector('#sales').insertAdjacentHTML('beforeend','<div class="andes-spinner">Loading</div>');};});await run('open');await run('wholesale-off');assert.equal((await run('wholesale-check')).ready,false);await assert.rejects(run('wholesale-submit'));assert.equal(await p.locator('output').innerText(),'0');
   await p.locator('.andes-spinner').evaluate(e=>e.remove());assert.equal((await run('wholesale-check')).ready,false);await p.waitForTimeout(550);assert.equal((await waitReady('wholesale-check')).ready,true);await run('wholesale-submit');assert.equal(await p.locator('output').innerText(),'1');
  });
  await regression('main listing price allows wholesale annotations without using secondary amounts',async()=>{
   await p.goto(manager);await p.locator('.sll-list-cell-price').evaluate(e=>e.innerHTML='<div class="sll-list-cell-generic__line-group"><span class="sll-list-no-wrap">R$ 99</span></div><div class="sll-list-cell-generic__line-group">批发价供应<span hidden>您有包含批发价的变体 / 请逐一检查您设置的价格 R$ 20</span></div>');assert.equal((await run('verify',{url:manager})).reached,true);assert.equal((await run('verify',{url:manager})).priceText,'R$ 99');
   await p.locator('.sll-list-no-wrap').evaluate(e=>e.textContent='R$ 98');assert.equal((await run('verify',{url:manager})).reached,false);
   await p.locator('.sll-list-no-wrap').evaluate(e=>e.textContent='价格待确认');await p.locator('.sll-list-cell-price').evaluate(e=>e.insertAdjacentHTML('beforeend','<span>R$ 99</span>'));assert.equal((await run('verify',{url:manager})).reached,false);
   await p.locator('.sll-list-no-wrap').evaluate(e=>e.remove());assert.equal((await run('verify',{url:manager})).reached,false);
   await p.locator('.sll-list-cell-price').evaluate(e=>e.innerHTML='<span class="sll-list-no-wrap">R$ 99</span><span class="sll-list-no-wrap">R$ 98</span>');await assert.rejects(run('verify',{url:manager}));
  });
  await regression('pending wholesale save reports why without claiming persistence',async()=>{
   await p.goto(url);const state=await run('wholesale-saved');assert.equal(state.ready,false);assert.equal(state.reason,'sales-panel-open');assert.equal(state.expanded,'true');assert.equal(state.url,url);
  });
  await regression('checked disabled wholesale is still enabled',async()=>{
   await p.goto(url);await p.locator('#apply-all').evaluate(e=>{e.checked=true;e.disabled=true;e.setAttribute('aria-labelledby','wholesale-disabled');e.parentElement.insertAdjacentHTML('beforeend','<span id="wholesale-disabled">批发价格</span>');});assert.equal((await run('open')).wholesaleEnabled,true);assert.equal((await run('wholesale-state')).wholesaleEnabled,true);await assert.rejects(run('draft'));
  });
  await regression('slow opening clicks the sales toggle once per document',async()=>{
   await p.goto(url);await p.locator('button[aria-label]').evaluate(e=>{e.setAttribute('aria-expanded','false');e.onclick=()=>e.dataset.clicks=String(Number(e.dataset.clicks||0)+1);});await run('open');await run('open');assert.equal(await p.locator('button[aria-label]').getAttribute('data-clicks'),'1');
   await p.goto(url);await p.locator('button[aria-label]').evaluate(e=>{e.setAttribute('aria-expanded','false');e.onclick=()=>e.dataset.clicks=String(Number(e.dataset.clicks||0)+1);});await run('open');assert.equal(await p.locator('button[aria-label]').getAttribute('data-clicks'),'1');
  });
  await regression('a sibling-owned confirm cannot save the target card',async()=>{
   await p.goto(url);await p.locator('#sales').evaluate(e=>{e.innerHTML='<button aria-label="销售条件" aria-expanded="true"></button><section class="syi-task-desktop--prices_task_MLB456"><span>#456</span><input id="price__input-target" aria-label="设置价格 BRL" value="100"></section><section class="syi-task-desktop--prices_task_MLB789"><span>#789</span><input id="price__input-sibling" aria-label="设置价格 BRL" value="200"><button onclick="document.querySelector(\'output\').textContent=\'wrong\'">确认</button></section>';});
   assert.equal((await run('open')).ready,false);await assert.rejects(run('draft'));assert.equal(await p.locator('output').innerText(),'0');
  });
  await regression('authorized shipping quote is confirmed exactly once',async()=>{
   await p.goto(url);await run('open');await run('draft');await waitReady('check');
   await p.locator('#confirm').evaluate(e=>e.onclick=()=>{document.querySelector('output').textContent='1';document.body.insertAdjacentHTML('beforeend','<div class="andes-modal consequences-modal" role="dialog"><h2>进行这些更改后，其他更新将应用于商品信息</h2><div class="andes-modal__content"><span>你提供包邮和大幅折扣，需支付<del>R$ 42,90</del> <strong>R$ 12,87</strong>运费</span></div><button aria-label="Confirmar" onclick="this.parentElement.remove();document.querySelector(\'output\').dataset.consequence=\'confirmed\'">Confirmar</button><button aria-label="Cancelar">Cancelar</button></div>');});
   await run('submit');assert.equal(await p.locator('output').getAttribute('data-consequence'),'confirmed');assert.equal(await p.locator('.consequences-modal').count(),0);await assert.rejects(run('submit'));
  });
  await regression('shipping modal on blur is confirmed without clicking the main save twice',async()=>{
   await p.goto(url);await run('open');await run('draft');
   await p.locator('body').evaluate(e=>e.insertAdjacentHTML('beforeend','<div class="andes-modal consequences-modal"><span>运费<del>R$ 12,87</del><strong>R$ 42,90</strong></span><button onclick="this.parentElement.remove();document.querySelector(\'output\').textContent=\'shipping-confirmed\'">确认</button></div>'));
   assert.equal((await run('check')).ready,true);await run('submit');assert.equal(await p.locator('output').innerText(),'shipping-confirmed');await assert.rejects(run('submit'));
  });
  for(const quote of ['需支付R$12,95或运费。','Você pagará R$ 12,95 de frete.','需支付<strong>R$ 0,00</strong>运费'])await regression('single shipping quote '+quote,async()=>{
   await p.goto(url);await run('open');await run('draft');await waitReady('check');
   await p.locator('#confirm').evaluate((e,quote)=>e.onclick=()=>{document.querySelector('output').textContent='1';document.body.insertAdjacentHTML('beforeend','<div class="andes-modal consequences-modal"><h2>进行这些更改后，其他更新将应用于商品信息</h2><span>'+quote+'</span><button onclick="this.parentElement.remove();document.querySelector(\'output\').dataset.consequence=\'confirmed\'">Confirmar</button><button>Cancelar</button></div>');},quote);
   await run('submit');assert.equal(await p.locator('output').getAttribute('data-consequence'),'confirmed');await assert.rejects(run('submit'));
  });
  await regression('unrecognized cost remains guarded',async()=>{
   await p.goto(url);await run('open');await run('draft');await waitReady('check');
   await p.locator('#confirm').evaluate(e=>e.onclick=()=>{document.querySelector('output').textContent='1';document.body.insertAdjacentHTML('beforeend','<div class="andes-modal consequences-modal" role="dialog"><h2>进行这些更改后，其他更新将应用于商品信息</h2><div class="andes-modal__content"><span>未知服务变更<del>R$ 12,87</del> <strong>R$ 42,90</strong></span></div><button aria-label="Confirmar">Confirmar</button><button aria-label="Cancelar">Cancelar</button></div>');});
   await assert.rejects(run('submit'));assert.equal(await p.locator('.consequences-modal').count(),1);assert.equal(await p.locator('button[aria-label="Confirmar"]').last().evaluate(e=>e.dataset.clicked||''),'');
  });
  assert.deepEqual(regressions,[]);
  console.log('PASS: base target, exact SKU/item, active-discount refusal, no whole-group changes, durable single-submit binding and precise manager link');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
