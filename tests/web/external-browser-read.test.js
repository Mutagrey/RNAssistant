'use strict';
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
const {chromium}=require('playwright');
(async()=>{
  const browser=await chromium.launch({headless:true,...(process.env.BROWSER_EXECUTABLE?{executablePath:process.env.BROWSER_EXECUTABLE}:{})});
  try{
    const page=await browser.newPage();
    await page.setContent(`<title>Contact research</title><h1>Contact form</h1><p>${'Useful research fact. '.repeat(30)}</p><label>Name <input value="PRIVATE_INPUT"></label>
      <input type="password" value="PRIVATE_PASSWORD"><textarea>PRIVATE_TEXTAREA</textarea>
      <select><option selected>PRIVATE_OPTION</option></select><div contenteditable>PRIVATE_EDITABLE</div>
      <section data-private>PRIVATE_SECTION<button>PRIVATE_BUTTON</button></section>
      <button disabled>Save <input value="PRIVATE_NESTED"></button><a href="https://example.test/?token=PRIVATE_URL">Help</a>
      <p style="display:none">PRIVATE_HIDDEN</p><iframe srcdoc="PRIVATE_FRAME"></iframe>
      <label><input type="checkbox" checked>Accepted</label><div role="checkbox" aria-label="Mixed" aria-checked="mixed"></div><div id="shadow"></div>`);
    await page.evaluate(()=>{
      document.querySelector('#shadow').attachShadow({mode:'open'}).innerHTML='<p>PRIVATE_SHADOW</p>';
      document.body.addEventListener('click',()=>{window.mutations=(window.mutations||0)+1;});
      // Page-owned monkey patches cannot replace the native capture's DOM methods.
      document.createTreeWalker=()=>{throw new Error('page override must not run');};
    });
    const cdp=await page.context().newCDPSession(page),script=fs.readFileSync(path.join(__dirname,'../../src/RNAssistant.Office/WebView/ExternalBrowserSemanticRead.js'),'utf8');
    async function capture(){
      const {frameTree}=await cdp.send('Page.getFrameTree');
      const {executionContextId}=await cdp.send('Page.createIsolatedWorld',{frameId:frameTree.frame.id,worldName:'rna-semantic-read'});
      const result=await cdp.send('Runtime.evaluate',{expression:script,contextId:executionContextId,returnByValue:true,silent:true,timeout:1000});
      assert.ok(!result.exceptionDetails,JSON.stringify(result.exceptionDetails));return result.result.value;
    }
    const result=await capture(),wire=JSON.stringify(result);
    assert.equal(result.title,'Contact research');
    assert.ok(result.nodes.some(n=>n.role==='text'&&n.name.length>160),'research paragraphs retain more than an element name');
    assert.ok(result.nodes.some(n=>n.role==='heading'&&n.name==='Contact form'));
    assert.ok(result.nodes.some(n=>n.role==='textbox'&&n.name==='Name'));
    assert.ok(result.nodes.some(n=>n.role==='button'&&n.name==='Save'&&n.disabled));
    assert.ok(result.nodes.some(n=>n.role==='checkbox'&&n.checked));
    assert.ok(result.nodes.some(n=>n.role==='checkbox'&&n.name==='Mixed'&&n.checked===null),'mixed is never reported as unchecked');
    assert.ok(result.nodes.some(n=>n.role==='link'&&n.name==='Help'));
    assert.ok(!wire.includes('PRIVATE_'),'values, private regions, hidden text, URLs, frames and shadow DOM are absent');
    assert.equal(await page.evaluate(()=>window.mutations||0),0,'capture performs no user action');
    await page.evaluate(()=>{document.querySelector('h1').textContent='Updated form';});
    assert.ok((await capture()).nodes.some(n=>n.name==='Updated form'),'next capture observes changed DOM');
    await page.setContent('<p>'+('long text '.repeat(400000))+'</p>'+('<button>Action</button>'.repeat(4000)));
    const bounded=await capture();
    assert.ok(bounded.truncated);assert.ok(bounded.nodes.length<=160&&bounded.visited<=3000);
    assert.ok(bounded.nodes.every(n=>n.name.length<=(n.role==='text'?2000:160)));assert.ok(bounded.nodes.reduce((n,x)=>n+x.name.length,0)<=16000);
    console.log('PASS external Browser read: isolated-world semantics, privacy omissions, fresh DOM, bounded capture, no mutation');
  }finally{await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
