'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const { chromium } = require('playwright');
(async () => {
  const browser = await chromium.launch({ headless:true, ...(process.env.BROWSER_EXECUTABLE ? {executablePath:process.env.BROWSER_EXECUTABLE} : {}) });
  try {
    const host = await browser.newPage(), content = await browser.newPage(), errors=[];
    host.on('pageerror',error=>errors.push(error.message)); content.on('pageerror',error=>errors.push(error.message));
    await host.setContent('<button id="toggle" disabled>Выбрать элемент</button><span id="status"></span><div id="viewport" style="width:600px;height:320px"></div>');
    await content.setContent('<body style="height:1800px"><section id="card" data-ui-id="card" style="padding:20px"><span>Visible text</span><input value="private-password"><div contenteditable>private editable text</div><span data-private>private descendant</span><button id="action">Run</button></section><output id="count">0</output></body>');
    await content.evaluate(()=>document.getElementById('action').onclick=()=>document.getElementById('count').textContent=String(Number(document.getElementById('count').textContent)+1));
    const cdp=await content.context().newCDPSession(content), tree=await cdp.send('Page.getFrameTree');
    const {executionContextId}=await cdp.send('Page.createIsolatedWorld',{frameId:tree.frameTree.frame.id,worldName:'RNAssistantLocal'});
    async function evaluate(expression) { const r=await cdp.send('Runtime.evaluate',{expression,contextId:executionContextId,returnByValue:true,awaitPromise:true}); if(r.exceptionDetails)throw new Error(JSON.stringify(r.exceptionDetails));return r.result.value; }
    await cdp.send('Runtime.addBinding',{name:'__rnaVisualEvent',executionContextName:'RNAssistantLocal'});
    cdp.on('Runtime.bindingCalled',event=>{ if(event.executionContextId===executionContextId && event.name==='__rnaVisualEvent')host.evaluate(value=>inspector.receive(value),JSON.parse(event.payload)).catch(()=>{}); });
    await host.exposeFunction('nativeControl',message=>evaluate('__rnaVisualControl('+JSON.stringify(message)+');true'));
    await host.addScriptTag({path:'web/js/app-visual-inspector.js'});
    await host.evaluate(()=>{ document.documentElement.style.setProperty('--accent','#3A63F2');document.documentElement.style.setProperty('--accent-ink','#FFFFFF');
      window.inspector=RNAssistantVisualInspector.createHost({frame:document.getElementById('viewport'),button:document.getElementById('toggle'),status:document.getElementById('status'),send:nativeControl,
        onSelectionChanged:value=>window.selected=value,onLocations:values=>window.locations=values,onReveal:value=>window.revealed=value}); });
    await evaluate(await fs.readFile('web/js/app-visual-inspector.js','utf8'));
    await evaluate('RNAssistantVisualInspector.install(1,value=>__rnaVisualEvent(JSON.stringify(value)));true');
    await host.evaluate(()=>inspector.bind(1)); await host.locator('#toggle:not([disabled])').waitFor();
    await content.locator('#action').click();assert.equal(await content.locator('#count').textContent(),'1');
    await host.locator('#toggle').click(); await host.waitForFunction(()=>document.getElementById('toggle').getAttribute('aria-pressed')==='true');
    await content.locator('#card').hover({position:{x:2,y:2}});
    const highlight=await content.locator('[data-rna-visual-highlight]').evaluate(node=>({pointer:getComputedStyle(node).pointerEvents,color:getComputedStyle(node).borderTopColor}));
    assert.deepEqual(highlight,{pointer:'none',color:'rgb(58, 99, 242)'});
    await content.locator('#card').click({position:{x:2,y:2}});await host.waitForFunction(()=>selected===true);
    const selection=await host.evaluate(()=>inspector.selectionRecord());
    assert.equal(selection.generation,1);assert.equal(selection.selection.stableUiId,'card');assert.doesNotMatch(JSON.stringify(selection),/private-password|private editable|private descendant/);
    await content.locator('#action').click();await host.waitForFunction(()=>inspector.selection().tag==='button');assert.equal(await content.locator('#count').textContent(),'1','inspection never clicks the application');
    await host.evaluate(()=>inspector.watch([{id:'draft',selector:'#action',tag:'button',stableUiId:''}]));await host.waitForFunction(()=>window.locations?.[0]?.status==='visible');
    await content.evaluate(()=>scrollTo(0,700));await host.waitForFunction(()=>window.locations?.[0]?.status==='outside');
    await host.evaluate(()=>inspector.reveal('draft'));await host.waitForFunction(()=>window.revealed?.found===true && window.locations?.[0]?.status==='visible');
    await evaluate('__rnaVisualControl({generation:1,operation:"dispose"});RNAssistantVisualInspector.install(2,value=>__rnaVisualEvent(JSON.stringify(value)));true');
    await host.evaluate(()=>{inspector.bind(2);inspector.receive({generation:1,type:'rnassistant-visual-inspector-selected',selection:{tag:'button'}});});
    await host.locator('#toggle:not([disabled])').waitFor();assert.equal(await host.evaluate(()=>inspector.selectionRecord()),null,'navigation cannot carry selection forward');
    await host.evaluate(()=>inspector.dispose());await content.locator('#action').click();assert.equal(await content.locator('#count').textContent(),'2');
    assert.deepEqual(errors,[]);
    console.log('PASS native visual inspector: typed isolated transport, element selection, privacy, hover/watch/reveal, generation refusal, cleanup; no iframe');
  } finally { await browser.close(); }
})().catch(error=>{console.error(error);process.exitCode=1;});
