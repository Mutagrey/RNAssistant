'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const {withPreviewPage} = require('./preview-session');
(async () => {
  await withPreviewPage(async ({page, errors}) => {
    await page.setViewportSize({width:1280, height:900});
    await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
    await page.evaluate(() => { RNAssistantDockWorkspace.show('settings'); });
    await page.locator('#baseUrlInput').waitFor({state:'visible'});
    assert.equal(await page.locator('#htmlNetworkOriginsInput, [data-deployment-config]').count(), 0);
    const developer = page.locator('.settings-page[data-settings-page="connection"] details').filter({hasText:'Разработчик'});
    await developer.locator('summary').click();
    assert.equal(await developer.locator('input, button, textarea').count(), 0, 'Developer remains empty');
    assert.equal(await page.locator('#baseUrlInput, #modelsConfigUrlInput, #apiKeyInput').count(), 3, 'LLM connection remains');
    if (process.env.LOCAL_BROWSER_REVIEW) await page.screenshot({path:process.env.LOCAL_BROWSER_REVIEW + '-wide.png'});
    await page.setViewportSize({width:420, height:900});
    await page.evaluate(() => { document.documentElement.style.zoom = '1.25'; });
    await developer.scrollIntoViewIfNeeded();
    assert.ok(await developer.evaluate(el => el.scrollWidth <= el.clientWidth + 1));
    if (process.env.LOCAL_BROWSER_REVIEW) await page.screenshot({path:process.env.LOCAL_BROWSER_REVIEW + '-narrow.png'});
    assert.deepEqual(errors, []);
  });
  const preview = fs.readFileSync('web/js/app-html-workspace-preview.js', 'utf8');
  const view = fs.readFileSync('web/js/app-html-workspace-view.js', 'utf8');
  assert.doesNotMatch(preview + view, /rnassistant-html-fetch|allowHtmlNetworkOrigin|htmlFetch/);
  console.log('PASS local Browser settings: no network grants/proxy, empty Developer, LLM intact, narrow 125%');
})().catch(error => { console.error(error); process.exitCode = 1; });
