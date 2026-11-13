'use strict';
const assert = require('node:assert/strict'), fs = require('node:fs'), path = require('node:path');
const {chromium} = require('playwright');
const asset = name => fs.readFileSync(path.join(__dirname, '../../src/RNAssistant.Office/WebView', name), 'utf8');
const body = '<h1>Research page</h1><label>Search <input id="query"></label><input type="password" value="secret">'+
  '<button onclick="window.clicks++;setTimeout(()=>document.querySelector(\'output\').textContent=\'Results ready\',window.delay)">Find</button>'+
  '<output></output><a id="help" href="/article?token=private" target="_blank">Help</a><script>window.clicks=0;window.delay=150;</script>';
(async () => {
  const browser = await chromium.launch({headless:true,...(process.env.BROWSER_EXECUTABLE ? {executablePath:process.env.BROWSER_EXECUTABLE} : {})});
  try {
    const page = await browser.newPage();
    await page.route('https://research.example/**', route => route.fulfill({contentType:'text/html',body}));
    const cdp = await page.context().newCDPSession(page);
    await cdp.send('Page.enable'); await cdp.send('Runtime.enable');
    await cdp.send('Page.addScriptToEvaluateOnNewDocument', {source:asset('ExternalBrowserControl.js'),worldName:'rna-semantic-read'});
    let contextId, observed, sequence = 0;
    async function evaluate(expression) {
      const result = await cdp.send('Runtime.evaluate', {expression,contextId,returnByValue:true,awaitPromise:true,timeout:2000});
      assert.ok(!result.exceptionDetails, JSON.stringify(result.exceptionDetails)); return result.result.value;
    }
    async function read() { observed = await evaluate(asset('ExternalBrowserSemanticRead.js')); return observed; }
    async function reset() {
      await page.goto('https://research.example/start');
      const {frameTree} = await cdp.send('Page.getFrameTree');
      ({executionContextId:contextId} = await cdp.send('Page.createIsolatedWorld', {frameId:frameTree.frame.id,worldName:'rna-semantic-read'}));
      await evaluate('globalThis.__rnaBrowserScope=Object.freeze({interact:true,navigate:true})');
      await read();
    }
    async function prepare(request) {
      const id = 'prepared-'+(++sequence);
      const result = await evaluate('globalThis.__rnaBrowser.prepare('+[observed.observationId,id,request].map(x=>JSON.stringify(x)).join(',')+')');
      return {id,...result};
    }
    const act = plan => evaluate('globalThis.__rnaBrowser.act('+JSON.stringify(plan.id)+')');
    async function link() {
      const id = 'link-'+(++sequence);
      return {id,...await evaluate('globalThis.__rnaBrowser.prepareLink('+[observed.observationId,id,'Help'].map(x=>JSON.stringify(x)).join(',')+')')};
    }
    const follow = plan => evaluate('globalThis.__rnaBrowser.follow('+[plan.id,plan.url].map(x=>JSON.stringify(x)).join(',')+')');
    const fill = {action:'fill',name:'Search',value:'Browser evidence'}, click = {action:'click',name:'Find',expectedText:'Results ready'};
    await reset();
    assert.ok(!JSON.stringify(observed).includes('token=private') && !JSON.stringify(observed).includes('secret'), 'href and private values stay outside model observation');
    assert.equal((await act(await prepare(fill))).effect,'VerifiedChange');
    assert.equal(await page.locator('#query').inputValue(),'Browser evidence');
    await read(); assert.equal((await act(await prepare(click))).effect,'VerifiedChange','bounded async read-back observes SPA update');
    assert.equal(await page.evaluate(()=>window.clicks),1);
    await reset(); await page.evaluate(()=>{window.delay=300;}); const pending = act(await prepare(click));
    await page.waitForFunction(()=>window.clicks===1); await page.locator('#query').fill('user takes over');
    assert.equal((await pending).effect,'Unknown','takeover during read-back cannot paint success');
    await read(); assert.equal((await prepare(fill)).code,'browser_user_control');
    await reset(); await page.evaluate(()=>{window.delay=2500;});
    assert.equal((await act(await prepare(click))).effect,'Unknown','missing bounded read-back does not claim failure without effect');
    await read(); assert.equal((await prepare(click)).code,'browser_effect_unknown','fresh read cannot replay an unknown click');
    await reset(); let plan = await link(); assert.equal(plan.prepared,true);
    await page.locator('#help').evaluate(node=>{node.href='/replaced';});
    assert.equal((await follow(plan)).code,'browser_element_stale'); assert.equal(page.url(),'https://research.example/start');
    await reset(); await page.locator('#help').evaluate(node=>{node.href='/changed-before-prepare';});
    assert.equal((await link()).code,'browser_element_stale');
    await reset(); await page.locator('#help').evaluate(node=>node.after(node.cloneNode(true)));
    assert.equal((await link()).code,'browser_element_ambiguous');
    await reset(); await page.locator('#help').evaluate(node=>{node.href='javascript:alert(1)';}); await read();
    assert.equal((await link()).code,'browser_action_invalid');
    await reset(); await page.locator('#help').evaluate(node=>{node.href='/'+'x'.repeat(9000);}); await read();
    assert.equal((await link()).code,'browser_element_not_interactable','oversized href never becomes a prepared navigation');
    await reset(); await page.locator('#help').evaluate(node=>node.setAttribute('download','report')); await read();
    assert.equal((await link()).code,'browser_element_not_interactable');
    await reset(); await page.evaluate(()=>{for(let i=0;i<200;i++){const p=document.createElement('p');p.textContent='row '+i;document.body.append(p);}}); await read();
    assert.equal(observed.truncated,true); assert.equal((await prepare(fill)).code,'browser_observation_incomplete');
    await reset(); plan=await link();
    const [result]=await Promise.all([follow(plan),page.waitForURL('https://research.example/article?token=private')]);
    assert.equal(result.started,true); assert.equal(page.context().pages().length,1,'semantic follow uses the existing tab, including target=_blank');
    console.log('PASS external sites: fill, async click, takeover, unknown/no replay, exact hidden href, stale/ambiguous/private/bounded targets');
  } finally { await browser.close(); }
})().catch(error=>{console.error(error);process.exitCode=1;});
