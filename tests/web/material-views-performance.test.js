'use strict';
// Comparable synthetic routes. MATERIAL_BENCH_ROOT can point to an archived baseline.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const {spawn}=require('node:child_process'),{chromium}=require('playwright');
(async()=>{
  const cwd=process.env.MATERIAL_BENCH_ROOT||path.resolve(__dirname,'../..');
  const server=spawn('python3',['-B','prototypes/serve_ui.py','--port','0'],{cwd});server.stderr.resume();let browser;
  try{
    const address=await new Promise((resolve,reject)=>{server.once('error',reject);server.once('exit',code=>reject(Error('preview exit '+code)));server.stdout.on('data',data=>{const m=/RNAssistant design: (http:\/\/127\.0\.0\.1:\d+\/)/.exec(String(data));if(m)resolve(m[1]);});});
    browser=await chromium.launch({headless:true,...(process.env.BROWSER_EXECUTABLE?{executablePath:process.env.BROWSER_EXECUTABLE}:{})});
    const page=await browser.newPage({viewport:{width:1280,height:900}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
    await page.route('**/preview/fixtures.js',async route=>{const r=await route.fetch();await route.fulfill({response:r,body:await r.text()+'\nPrototypeFixtures.report += "\\n\\n" + "Проверка сохранения прокрутки.\\n\\n".repeat(300);\nPrototypeFixtures.materialSources.text = "Строка большого исходника\\n".repeat(30000);'});});
    await page.goto(address+'preview/web/?scenario=ready');await page.waitForFunction(()=>state.activeChatId==='preview-chat'&&!state.initializePromise);
    await page.evaluate(()=>{RNAssistantChatInspector.setOpen(false);window.perfMaterial={calls:[]};const original=send;send=function(command,payload){perfMaterial.calls.push({command,payload});return original(command,payload);};});
    await page.locator('.rail-button[data-tab="artifacts"]').click();await page.locator('#artifactCatalogOverviewRows tr').first().waitFor();
    const independent=await page.evaluate(()=>!!window.RNAssistantMaterialView),samples=[];
    for(let i=0;i<20;i++){
      const name=i%2?'json':'report';
      const sample=await page.evaluate(async ({name,independent})=>{
        const start=performance.now(),uri='rna://chat/preview-chat/artifact/'+name+'/revision/1';
        RNAssistantArtifactCatalog.select(uri);
        await new Promise((resolve,reject)=>{const deadline=performance.now()+10000;function ready(){
          const root=independent?Array.from(document.querySelectorAll('.material-object-view')).find(n=>n.dataset.resourceUri===uri):document.getElementById('artifactCatalogPreviewBody');
          if(root&&root.querySelector(name==='json'?'.rn-json-viewer':'.rn-markdown-viewer'))return requestAnimationFrame(()=>requestAnimationFrame(resolve));
          if(performance.now()>deadline)return reject(Error('body unavailable '+name));requestAnimationFrame(ready);
        }ready();});return performance.now()-start;
      },{name,independent});samples.push(sample);
    }
    const repeated=await page.evaluate(()=>({bodyReads:perfMaterial.calls.filter(c=>/^readArtifact|^exportArtifact/.test(c.command)).length,
      retainedViewerNodes:document.querySelectorAll('.material-object-view *, #artifactCatalogPreviewBody *').length}));
    const before=await page.evaluate(()=>performance.now());await page.evaluate(()=>RNAssistantArtifactCatalog.select('rna://chat/preview-chat/artifact/text/revision/1'));
    const root=page.locator(independent?'.material-object-view[data-resource-uri$="/text/revision/1"]':'#artifactCatalogPreviewBody');
    await root.locator('.rn-text-viewer-source').waitFor();
    const large=await root.evaluate((root,before)=>({readyMs:performance.now()-before,renderedTextCharacters:root.querySelector('.rn-text-viewer-source').textContent.length,domNodes:root.querySelectorAll('*').length}),before);
    assert.deepEqual(errors,[]);if(independent){assert.equal(repeated.bodyReads,2);assert.ok(large.renderedTextCharacters<40000);}
    samples.sort((a,b)=>a-b);
    const result={independent,fixture:{mdParagraphs:300,largeTextCharacters:780000,alternatingSelections:20},route:'select → viewer mounted → two animation frames',repeated:{...repeated,p50Ms:Math.round(samples[10]*10)/10,p95Ms:Math.round(samples[18]*10)/10},large};
    console.log(JSON.stringify(result,null,2));if(process.env.MATERIAL_BENCH_OUTPUT)fs.writeFileSync(process.env.MATERIAL_BENCH_OUTPUT,JSON.stringify(result,null,2)+'\n');
  }finally{if(browser)await browser.close();server.kill();}
})().catch(e=>{console.error(e);process.exitCode=1;});
