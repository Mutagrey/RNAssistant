'use strict';
// The source workbench must not substitute an active release after a failed check.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
const {withPreviewPage}=require('./preview-session');
withPreviewPage(async({page,errors})=>{
  page.setDefaultTimeout(8000);
  await page.setViewportSize({width:1280,height:900});
  await page.waitForFunction(()=>state.activeChatId==='preview-chat'&&!state.initializePromise);
  await page.evaluate(async()=>{
    window.sourceFailure={calls:[],fail:true,stamp:'source-1'};const original=send;
    send=function(command,payload){sourceFailure.calls.push({command,payload});
      if(command==='getPluginSourceState')return Promise.resolve({pluginId:payload.pluginId,stamp:sourceFailure.stamp});
      if(command==='checkPlugin'&&sourceFailure.fail)return Promise.reject(new Error('Некорректные исходники плагина'));
      return original(command,payload);};
    RNAssistantPlugins.select('data-templates');
    await RNAssistantDockWorkspace.getView(RNAssistantPluginView.key({pluginId:'data-templates'})).ready();
  });
  const workbench=page.locator('.plugin-workbench[data-plugin-id="data-templates"]');
  const opens=()=>page.evaluate(()=>sourceFailure.calls.filter(c=>c.command==='openPlugin'));
  assert.equal((await opens()).length,0,'failed initial source check must not open the active release');
  await workbench.getByRole('alert').filter({hasText:'Некорректные исходники плагина'}).waitFor();
  assert.ok(!(await workbench.getByRole('alert').textContent()).includes('Открытый экран'),'initial error does not claim an existing page');
  assert.equal(await workbench.locator('iframe').count(),0);
  const captures=process.env.RNA_PLUGIN_FAILURE_SCREENSHOTS;
  for(const width of [1280,420]){
    await page.setViewportSize({width,height:900});
    await page.evaluate(zoom=>{document.documentElement.style.zoom=zoom;},width===420?'1.25':'');
    await workbench.getByRole('alert').filter({hasText:'Некорректные исходники плагина'}).waitFor();
    assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1));
    if(captures){fs.mkdirSync(captures,{recursive:true});await page.screenshot({path:path.join(captures,'browser-p3a-error-'+width+'.png')});}
  }
  // Polling still notices file changes, but does not repeat a failed check for unchanged source.
  await page.waitForFunction(()=>sourceFailure.calls.filter(c=>c.command==='getPluginSourceState').length>=2);
  assert.equal(await page.evaluate(()=>sourceFailure.calls.filter(c=>c.command==='checkPlugin').length),1);
  assert.equal((await opens()).length,0);
  await page.setViewportSize({width:1280,height:900});
  await page.evaluate(()=>{document.documentElement.style.zoom='';sourceFailure.fail=false;sourceFailure.stamp='source-2';});
  await workbench.locator('iframe').waitFor();
  assert.ok((await opens()).at(-1).payload.candidateId,'corrected source opens a checked candidate');
  const frame=page.frameLocator('.plugin-workbench[data-plugin-id="data-templates"] iframe');
  await frame.locator('body').evaluate(body=>{const input=document.createElement('input');input.id='retained';input.value='keep';body.appendChild(input);});
  const beforeRetry=(await opens()).length;
  await page.evaluate(()=>{sourceFailure.fail=true;});
  await workbench.locator('[data-plugin-node="Preview"]').click();
  await workbench.getByRole('alert').filter({hasText:'Некорректные исходники плагина'}).waitFor();
  assert.equal(await frame.locator('#retained').inputValue(),'keep','failed refresh retains the previous page');
  assert.equal((await opens()).length,beforeRetry);
  await page.evaluate(()=>{sourceFailure.fail=false;});
  await workbench.locator('[data-plugin-node="Preview"]').click();
  await page.waitForFunction(before=>sourceFailure.calls.filter(c=>c.command==='openPlugin').length===before+1,beforeRetry);
  assert.ok((await opens()).every(call=>call.payload.candidateId),'automatic opening and retry never switch to published code');
  await page.waitForFunction(()=>!RNAssistantDockWorkspace.getView(RNAssistantPluginView.key({pluginId:'data-templates'})).isBusy());
  await workbench.getByRole('button',{name:'Действия с плагином',exact:true}).press('Enter');
  await page.getByRole('menuitem',{name:'Открыть активную версию',exact:true}).press('Enter');
  await page.waitForFunction(()=>{const call=sourceFailure.calls.filter(c=>c.command==='openPlugin').at(-1);return call&&!call.payload.candidateId;});
  assert.equal((await opens()).length,beforeRetry+2,'published code is still available by explicit command');
  assert.equal(await page.evaluate(()=>sourceFailure.calls.some(c=>/^(applyPlugin|restorePluginVersion|submit.*Chat|cancelChatRun|cancelProjectChat)$/.test(c.command))),false);
  assert.deepEqual(errors,[]);
  console.log('PASS plugin-source-failure: initial rejection, no release fallback/retry, watch recovery, retained previous page, explicit retry/release, narrow/zoom');
}).catch(error=>{console.error(error);process.exitCode=1;});
