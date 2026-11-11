'use strict';
const assert = require('node:assert/strict'), fs = require('node:fs'), path = require('node:path');
const {chromium} = require('playwright');
const source = name => fs.readFileSync(path.join(__dirname, '../../src/RNAssistant.Office/WebView/', name), 'utf8');
(async () => {
  const browser = await chromium.launch({headless:true, ...(process.env.BROWSER_EXECUTABLE ? {executablePath:process.env.BROWSER_EXECUTABLE} : {})});
  try {
    const page = await browser.newPage();
    await page.route('https://navigation.example/**', route => route.fulfill({contentType:'text/html',body:'<h1>Loaded page</h1><label>Name <input></label>'}));
    const cdp = await page.context().newCDPSession(page);
    await cdp.send('Page.enable'); await cdp.send('Runtime.enable');
    await cdp.send('Page.addScriptToEvaluateOnNewDocument', {source:source('ExternalBrowserControl.js'), worldName:'rna-semantic-read'});
    let contextId, observation;
    async function evaluate(expression) {
      const result = await cdp.send('Runtime.evaluate', {expression,contextId,returnByValue:true,timeout:1000});
      assert.ok(!result.exceptionDetails, JSON.stringify(result.exceptionDetails)); return result.result.value;
    }
    async function read() { observation = await evaluate(source('ExternalBrowserSemanticRead.js')); }
    async function bind() {
      const {frameTree} = await cdp.send('Page.getFrameTree');
      ({executionContextId:contextId} = await cdp.send('Page.createIsolatedWorld', {frameId:frameTree.frame.id,worldName:'rna-semantic-read'}));
      await read();
    }
    const navigate = (url, id = observation.observationId) => evaluate('globalThis.__rnaBrowser.navigate('+JSON.stringify(id)+','+JSON.stringify(url)+')');
    await page.goto('https://navigation.example/start'); await bind();
    assert.equal(await page.evaluate(() => typeof globalThis.__rnaBrowser), 'undefined', 'main-world page cannot access the navigation API');
    assert.equal((await navigate('javascript:alert(1)')).code, 'browser_action_invalid');
    assert.equal((await navigate('https://user:secret@navigation.example/')).code, 'browser_action_invalid');
    assert.equal((await navigate('https://navigation.example/next', 'stale')).code, 'browser_observation_stale');
    assert.equal((await navigate(page.url())).unchanged, true, 'same address is an observed no-op');
    assert.equal((await navigate('https://navigation.example/next')).code, 'browser_observation_stale', 'one observation authorizes at most one dispatch');
    await read();
    const [dispatched] = await Promise.all([navigate('https://navigation.example/next'), page.waitForURL('https://navigation.example/next')]);
    assert.equal(dispatched.started, true); await bind();
    assert.ok(observation.nodes.some(node => node.name === 'Loaded page'), 'new isolated world can capture the loaded document');
    const fragment = await navigate('https://navigation.example/next#part');
    assert.equal(fragment.started, true); await page.waitForURL('https://navigation.example/next#part');
    assert.equal(await evaluate('globalThis.__rnaBrowser.completeNavigation()'), true, 'native read-back can release the same-document dispatch latch');
    await read();
    assert.equal((await navigate(page.url())).unchanged, true, 'fragment navigation allows a fresh subsequent action');
    await page.goto('https://navigation.example/manual'); await bind();
    await page.getByRole('textbox', {name:'Name'}).fill('user input');
    assert.equal((await navigate('https://navigation.example/replaced')).code, 'browser_user_control');
    assert.equal(page.url(), 'https://navigation.example/manual');
    await read();
    assert.equal((await navigate('https://navigation.example/replaced')).code, 'browser_user_control', 'new capture never releases manual control');
    console.log('PASS browser navigation: isolated fixed API, URL guards, fresh observation, load/fragment, trusted user takeover');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
