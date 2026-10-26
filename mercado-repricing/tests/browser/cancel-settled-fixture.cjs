const fs=require('fs'),assert=require('assert/strict'),{chromium}=require('../../web/node_modules/playwright');
(async()=>{
 const source=fs.readFileSync('server/Execution/ReadOnlyCdp.cs','utf8').split('public async Task CancelAsync()')[1].split('public async ValueTask DisposeAsync()')[0];
 const match=source.match(/await Evaluate\("((?:\\.|[^"\\])*)",cleanup.Token\)/);
 assert.ok(match,'CancelAsync browser expression must be present');
 const expression=JSON.parse('"'+match[1]+'"');
 const browser=await chromium.launch({executablePath:'C:/Program Files/Google/Chrome/Application/chrome.exe',headless:true});
 try{
  const page=await browser.newPage();
  await page.setContent(`<div class="promotion-modal-cdp2026"><button data-testid="cdp--promotion-modal--cancel" onclick="setTimeout(()=>this.parentElement.remove(),600)">取消</button></div>`);
  await page.evaluate(expression);
  assert.equal(await page.locator('.promotion-modal-cdp2026').count(),0,'cancel must wait for delayed modal removal');
  await page.evaluate(expression);
  console.log('PASS: cancellation waits for modal removal and tolerates an already closed dialog');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
