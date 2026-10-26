// Read-only identity and independent price recovery. All seller requests are intercepted.
const fs=require('node:fs'),assert=require('node:assert/strict'),{chromium}=require('../../web/node_modules/playwright');
(async()=>{const browser=await chromium.launch({executablePath:'C:/Program Files/Google/Chrome/Application/chrome.exe',headless:true});try{
 const p=await browser.newPage(),source=fs.readFileSync('server/Execution/ProductIdentityPage.js','utf8');let content='';await p.route('**/*',r=>r.fulfill({contentType:'text/html',body:content}));
 const manager='https://vendedores.mercadolivre.com.br/anuncios?page=1&sort=DEFAULT&search=SKU-A',editor='https://vendedores.mercadolivre.com.br/anuncios/MLB456/modificar/bomni/session/item_detail_form';
 const run=(action,extra={})=>p.evaluate('('+source+')('+JSON.stringify({action,url:manager,itemId:'456',sku:'SKU-A',...extra})+')');
 content='<meta charset="UTF-8"><div class="sll-list-grid-row sll-list-grid-row--item" id="MLB456"><span class="sc-copyable-element__text">#456</span><span class="sc-copyable-element__text">SKU SKU-A</span><div class="sll-list-cell-price">R$ 100<p class="sll-list-price-participation">正以 R$ 75,00 促销</p></div><div class="sll-list-cell-status">需审核未激活</div><a href="https://www.mercadolivre.com.br/syi/core/modify?itemId=MLB456">编辑</a></div>';
 await p.goto(manager);let r=await run('manager');assert.equal(r.ready,true);assert.equal(r.products[0].managedPriceText,'R$ 75,00');assert.equal(r.products[0].sku,'SKU-A');
 await p.locator('.sll-list-price-participation').evaluate(e=>e.innerText='正以 R$ 70 至 R$ 80 促销');r=await run('manager');assert.equal(r.products[0].managedPriceText,null);
 await p.locator('.sc-copyable-element__text').last().evaluate(e=>e.innerText='SKU SKU-AB');r=await run('manager');assert.equal(r.products[0].sku,'SKU-AB');
 content='<meta charset="UTF-8"><button aria-label="展开变体" aria-expanded="false" onclick="this.setAttribute(\'aria-expanded\',\'true\')">展开</button><div class="sll-list-grid-row--item" id="MLB456"><div class="sll-list-cell-price">R$ 282</div><div class="sll-list-grid-row-lite"><span class="sc-copyable-element__text">SKU SKU-A</span></div><div class="sll-list-grid-row-lite"><span class="sc-copyable-element__text">SKU SKU-B</span></div></div>';
 await p.goto(manager);assert.equal((await run('manager')).ready,false);r=await run('manager');assert.equal(r.products[0].sku,'');assert.equal(r.products[0].basePriceText,'R$ 282');
 content='<meta charset="UTF-8"><h1>商品 #456</h1><div id="task-content-variations_task" hidden><table class="syi-variations-table"><tbody><tr><td data-title="sellerSku"><span>SKU</span><p class="syi-variations-table__cell--values">SKU-A</p></td></tr><tr><td data-title="sellerSku"><span>SKU</span><p class="syi-variations-table__cell--values">SKU-B</p></td></tr></tbody></table></div>';
 await p.goto(editor);r=await run('legacy');assert.deepEqual(r.skus,['SKU-A','SKU-B']);assert.equal(r.ready,true);assert.equal((await run('legacy',{itemId:'457'})).ready,false);
 await p.locator('h1').evaluate(e=>e.innerText='商品 #457');assert.equal((await run('legacy')).ready,false);
 console.log('PASS: exact legacy identity, shared siblings, mismatched IDs, independent managed price and ranged-price rejection. No live network.');
 }finally{await browser.close();}})().catch(e=>{console.error(e);process.exitCode=1});
