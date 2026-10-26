const fs=require('fs'),assert=require('assert/strict'),{chromium}=require('../../web/node_modules/playwright');

(async()=>{
  const browser=await chromium.launch({executablePath:'C:/Program Files/Google/Chrome/Application/chrome.exe',headless:true});
  try{
    const page=await browser.newPage();
    const script=fs.readFileSync('server/Execution/ProductIdentityPage.js','utf8');
    await page.route('https://vendedores.mercadolivre.com.br/**',route=>route.fulfill({status:200,contentType:'text/html',body:`
      <meta charset="utf-8"><body><main id="task-content-variations_task"><h1>#4890587967</h1><table class="syi-variations-table"><tbody>
        ${['G','M/G','M','P'].map((label,index)=>`<tr class="syi-variations-table__row"><td data-title="characteristics"><button class="andes-table__expandable--trigger" aria-expanded="false">${label}</button></td><td data-title="nestedStock">总库存${[9,16,31,31][index]}</td><td data-title="nestedAttribute">泳装脚蹼尺码${label}</td></tr>`).join('')}
      </tbody></table></main><script>
        document.querySelectorAll('.syi-variations-table__row button').forEach((button,index)=>button.addEventListener('click',()=>{
          button.setAttribute('aria-expanded','true');
          const sku=['NW260494NY','NW260493NY','NW260492NY','NW260491NY'][index];
          const detail=document.createElement('tr');detail.innerHTML='<td><div data-andes-textfield="true"><label for="sku-'+index+'"><span>识别码(SKU)</span></label><input id="sku-'+index+'" maxlength="50" value="'+sku+'"></div><span data-testid="characteristic-text">泳装脚蹼尺码'+['G','M/G','M','P'][index]+'</span></td>';
          button.closest('tr').after(detail);
        }));
      </script></body>`}));
    await page.goto('https://vendedores.mercadolivre.com.br/anuncios/MLB4890587967/modificar/bomni/edit/item_detail_form');
    const args={action:'legacy',itemId:'4890587967',sku:'NW260493NY'};
    const read=()=>page.evaluate(`(${script})(${JSON.stringify(args)})`);
    const first=await read();
    assert.equal(first.ready,false,'collapsed legacy variant rows should be expanded before reading SKUs');
    await page.waitForFunction(()=>[...document.querySelectorAll('.syi-variations-table__row button')].every(button=>button.getAttribute('aria-expanded')==='true'));
    const result=await read();
    assert.equal(result.ready,true,'expanded legacy variant details should expose seller SKU inputs');
    assert.deepEqual(result.skus,['NW260494NY','NW260493NY','NW260492NY','NW260491NY']);
    assert.equal(result.variants.find(x=>x.sku==='NW260493NY').label,'泳装脚蹼尺码M/G');
    assert.equal(result.variants.find(x=>x.sku==='NW260494NY').stock,'总库存9');
    console.log('PASS: expands legacy variant rows and reads SKU, variant label, and stock from detail rows');
  }finally{await browser.close()}
})().catch(error=>{console.error(error);process.exitCode=1});
