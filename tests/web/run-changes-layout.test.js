const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('playwright');
const read = file => fs.readFileSync(path.join(__dirname, '../../web', file), 'utf8');
(async () => {
  const browser = await chromium.launch({ headless: true, ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
  try {
    const page = await browser.newPage({ viewport: { width: 390, height: 844 } });
    const css = [...read('index.html').matchAll(/<link rel="stylesheet" href="([^"?]+)[^"]*">/g)].map(m => read(m[1])).join('\n');
    await page.setContent(`<html data-theme="dark"><style>${css}</style><body><main style="padding:18px;max-width:680px;margin:auto"><p>Обновлены расчёт VBA и HTML-отчёт, добавлена документация.</p><div id="output"></div></main></body></html>`);
    await page.evaluate(() => {
      window.state = {activeChatId: 'chat', chatProjectionRevisions: {chat: 1}};
      window.calls = 0;
      const middle = Array.from({length: 16}, (_, i) => '    value' + i + ' = ' + i).join('\n');
      const before = 'Sub Report()\n    title = "До"\n' + middle + '\n    SaveReport\nEnd Sub\n';
      window.fixture = {chatId: 'chat', runId: 'run', complete: true, items: [
        {id:'vba',title:'ReportModule',scope:'VBA',beforeExists:true,afterExists:true,availability:'available',before,after:before.replace('До','После').replace('SaveReport','ExportReport')},
        {id:'html',title:'reports/quarterly/index.html',scope:'HTML-проект',beforeExists:true,afterExists:true,availability:'available',before:'<h1>Продажи</h1>\n',after:'<h1>Продажи по регионам</h1>\n'},
        {id:'md',title:'README.md',scope:'HTML-проект',beforeExists:false,afterExists:true,availability:'available',before:'',after:'# Отчёт\n\nДанные обновляются из Excel.\n'},
        {id:'js',title:'src/a-long-folder-name/renamed.js',beforeTitle:'src/old.js',scope:'HTML-проект',beforeExists:true,afterExists:true,availability:'available',before:'alert(1);\n',after:'alert(1);\n'},
        {id:'unknown',title:'UnknownModule',scope:'VBA',availability:'unverified'},
        {id:'inert',title:'<img src=x onerror=alert(1)>.md',scope:'Текст',beforeExists:true,afterExists:true,availability:'available',before:'<script>bad()</script>',after:'<script>stillInert()</script>'}
      ]};
      window.send = async () => { window.calls++; return window.fixture; };
    });
    for (const file of ['js/app-text-diff.js', 'js/app-run-changes.js']) await page.addScriptTag({content: read(file)});
    await page.evaluate(() => {
      window.$ = id => document.getElementById(id);
      window.state.messages = [];
      window.RNAssistantAgentApproval = { create: () => ({ pendingActivity: () => null }) };
      window.currentActiveSend = () => null;
      window.hasActiveMessageEdit = () => false;
      window.canEditMessage = () => false;
      window.smallIconButton = title => { const button = document.createElement('button'); button.textContent = title; return button; };
      window.enhanceActivity = () => {};
      window.enhanceMarkdown = () => {};
      window.markdown = text => text;
    });
    for (const file of ['app-utils.js', 'app-run-view-state.js', 'app-agent-model.js', 'app-agent.js', 'app-messages.js', 'app-agent-data.js', 'app-agent-activity.js'])
      await page.addScriptTag({content: read('js/' + file)});
    await page.evaluate(() => {
      const final = { Role: 'assistant', Id: 'final', Content: 'Готово.', RunId: 'run', RunViewState: {
        RunId: 'run', TurnId: 'run', Lifecycle: 'completed', ExecutionHealth: 'clean', Narrative: 'Готово.' } };
      document.getElementById('output').appendChild(renderAgentRunArticle({items: [], finalMessage: {message: final, index: 0}, live: false}));
    });
    await page.locator('.run-changes-summary').waitFor();
    assert.equal(await page.locator('.run-change-file:visible').count(), 3);
    assert.equal(await page.locator('.run-changes-summary .run-change-added').textContent(), '+7');
    assert.equal(await page.locator('.run-changes-summary .run-change-removed').textContent(), '−4');
    await page.locator('.run-change-file-summary').first().click();
    await page.locator('.run-change-diff .vba-diff-line').first().waitFor();
    assert.equal(await page.locator('.run-change-diff .vba-diff-line.add').count(), 2);
    await page.locator('.run-changes-more').click();
    assert.equal(await page.locator('.run-change-file:visible').count(), 6);
    assert.equal(await page.locator('.run-change-file').nth(4).locator('.run-change-counts').count(), 0);
    await page.locator('.run-change-file-summary').last().click();
    await page.waitForFunction(() => document.querySelectorAll('.run-change-diff code').length > 4);
    assert.equal(await page.locator('#output img, #output script').count(), 0);
    for (const width of [390, 320, 740]) {
      await page.setViewportSize({width, height: 844});
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'no page horizontal overflow at ' + width);
    }
    await page.setViewportSize({width: 390, height: 844});
    if (process.env.LAYOUT_SCREENSHOT) await page.screenshot({path: process.env.LAYOUT_SCREENSHOT, fullPage:true});
    // A late result must not attach to a different chat after navigation.
    await page.evaluate(() => {
      window.state.chatProjectionRevisions.chat = 2;
      window.send = () => new Promise(resolve => window.resolveChanges = resolve);
      document.getElementById('output').textContent = '';
      window.appendRunChanges(document.getElementById('output'), 'chat', 'run');
    });
    await page.waitForFunction(() => !!window.resolveChanges);
    await page.evaluate(() => { window.state.activeChatId = 'other'; window.resolveChanges(window.fixture); });
    await page.evaluate(() => new Promise(resolve => setTimeout(resolve, 30)));
    assert.equal(await page.locator('.run-changes').count(), 0);
    console.log('PASS run changes: summary, exact counts, lazy details, inert source, narrow/wide layout, stale chat');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
