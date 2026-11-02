'use strict';
// Same synthetic iframe and Dockview restore workload; only the presentation host differs.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{spawn}=require('node:child_process');
const {chromium}=require('playwright');
(async()=>{const server=spawn('python3',['-B','prototypes/serve_ui.py','--port','0'],{cwd:path.resolve(__dirname,'../..')});server.stderr.resume();let browser;
try{const address=await new Promise((resolve,reject)=>{server.once('error',reject);server.stdout.on('data',d=>{const m=/RNAssistant design: (http:\/\/127\.0\.0\.1:\d+\/)/.exec(String(d));if(m)resolve(m[1]);});});
browser=await chromium.launch({headless:true,...(process.env.BROWSER_EXECUTABLE?{executablePath:process.env.BROWSER_EXECUTABLE}:{})});const results=[];
for(const mode of ['inline','browser']){const page=await browser.newPage({viewport:{width:1280,height:900},reducedMotion:'reduce'});page.on('pageerror',e=>{throw e;});
await page.goto(address+'preview/web/?scenario=ready');await page.waitForFunction(()=>state.activeChatId==='preview-chat'&&!state.initializePromise);
await page.evaluate(mode=>{window.frameLoads=0;RNAssistantDockWorkspace.registerView('frame-probe',{perObject:true,accepts:t=>t.view==='frame-probe',key:()=> 'frame-probe',maximumInstances:1,titleFor:()=> 'Frame probe',create:()=>{
const root=document.createElement('div'),surface=document.createElement('div'),frame=document.createElement('iframe');root.style.height='100%';frame.id='probeFrame';frame.title='Synthetic input';frame.sandbox='allow-scripts';frame.addEventListener('load',()=>frameLoads++);frame.srcdoc='<input aria-label="Input" value="initial">';surface.appendChild(frame);
if(mode==='browser'){const viewer=RNAssistantViewerRegistry.mount('browser',root,{surface,portalHost:document.querySelector('.shell')});return {element:root,activate:viewer.activate,deactivate:viewer.deactivate,layout:viewer.layout,dispose:()=>RNAssistantViewerRegistry.unmount(root)};}
root.appendChild(surface);return {element:root,activate(){},deactivate(){},dispose(){root.remove();}};
}});RNAssistantDockWorkspace.openTarget({view:'frame-probe'});},mode);
const input=page.frameLocator('#probeFrame').getByRole('textbox');await input.fill('retained',{timeout:5000});const initialLoads=await page.evaluate(()=>frameLoads);
async function layout(name){await page.locator('#workspaceLayout').click();await page.getByRole('menuitem',{name,exact:true}).click();}
await layout('Сохранить раскладку');const elapsed=[];
for(let i=0;i<5;i++){const start=performance.now();await layout('Вернуть сохранённую');await input.waitFor();await page.waitForFunction(()=>document.getElementById('probeFrame').getBoundingClientRect().width>0);elapsed.push(+(performance.now()-start).toFixed(1));}
const result={mode,initialLoads,afterLoads:await page.evaluate(()=>frameLoads),inputAfter:await input.inputValue(),restoreActionMs:elapsed};
assert.equal(result.afterLoads,mode==='browser'?initialLoads:initialLoads+5);assert.equal(result.inputAfter,mode==='browser'?'retained':'initial');results.push(result);await page.close();}
const report={fixture:'same <input> iframe, 1280x900, five Dockview saved-layout restores',note:'Inline reproduces the replaced embedding mechanism on the same current Dockview. Timings include automation; not a global product speed claim.',results};
if(process.env.RNA_BROWSER_MEASUREMENT)fs.writeFileSync(process.env.RNA_BROWSER_MEASUREMENT,JSON.stringify(report,null,2)+'\n');console.log(JSON.stringify(report));
}finally{if(browser)await browser.close();server.kill();}})().catch(error=>{console.error(error);process.exitCode=1;});
