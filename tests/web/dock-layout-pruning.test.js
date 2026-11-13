'use strict';
const assert=require('node:assert/strict');
const {withPreviewPage}=require('./preview-session');
(async()=>withPreviewPage(async({page,errors})=>{
  const key='rnassistant.ui.dock.s1.v1';
  await page.waitForFunction(()=>state.activeChatId==='preview-chat'&&!state.initializePromise);
  await page.waitForFunction(()=>!!localStorage.getItem('rnassistant.ui.dock.s1.v1'));
  const prior=await page.evaluate(key=>{
    const value=JSON.parse(localStorage.getItem(key)), layout=value.layout;
    function leaves(node){return node.type==='leaf'?[node]:node.data.flatMap(leaves);}
    const groups=leaves(layout.grid.root), first=groups[0];
    const valid=first.data.views.slice(), active=layout.activeGroup;
    first.data.views.splice(1,0,'plugin:old','browser:external');first.data.activeView='plugin:old';
    layout.panels['plugin:old']={id:'plugin:old',contentComponent:'plugin',title:'Old plugin',params:{target:{view:'plugin',pluginId:'old',title:'Old plugin'}}};
    layout.panels['browser:external']={id:'browser:external',contentComponent:'browser',title:'Old address',params:{target:{view:'browser',browserTabId:'external',ownerScope:{kind:'User',scopeId:'local'},locator:'https://external.invalid'}}};
    localStorage.setItem(key,JSON.stringify({version:1,layout}));
    return {valid,active,group:first.data.id,count:groups.length};
  },key);
  // pagehide normally persists a live layout. Freeze that one writer while reloading this historical fixture.
  await page.evaluate(()=>{Storage.prototype.setItem=function(){};});
  await page.reload();await page.waitForFunction(()=>state.activeChatId==='preview-chat'&&!state.initializePromise);
  const restored=await page.evaluate(key=>JSON.parse(localStorage.getItem(key)),key);
  assert.equal(restored.version,2);assert.equal(restored.layout.panels['plugin:old'],undefined);assert.equal(restored.layout.panels['browser:external'],undefined);
  function leaves(node){return node.type==='leaf'?[node]:node.data.flatMap(leaves);}
  const groups=leaves(restored.layout.grid.root);
  assert.equal(groups.length,prior.count);assert.deepEqual(groups.find(g=>g.data.id===prior.group).data.views,prior.valid);assert.equal(restored.layout.activeGroup,prior.active);
  const corrupt=JSON.stringify({version:1,layout:{grid:{root:{type:'branch',data:'invalid'}},panels:restored.layout.panels}});
  await page.evaluate(({key,corrupt})=>{localStorage.setItem(key,corrupt);Storage.prototype.setItem=function(){};},{key,corrupt});
  await page.reload();await page.waitForFunction(()=>state.activeChatId==='preview-chat'&&!state.initializePromise);await page.waitForTimeout(250);
  assert.equal(await page.evaluate(key=>localStorage.getItem(key),key),corrupt,'failed migration preserves the original preference');
  assert.deepEqual(errors,[]);console.log('PASS selective Dock restore preserves order, groups and invalid input');
}))().catch(error=>{console.error(error);process.exitCode=1;});
