'use strict';
// Existing Library/VBA owners in production Dockview; no Office execution.
const assert = require('node:assert/strict'), path = require('node:path'), { spawn } = require('node:child_process');
const { chromium } = require('playwright');
(async () => {
  const server = spawn('python3', ['-B', 'prototypes/serve_ui.py', '--port', '0'], { cwd: path.resolve(__dirname, '../..') });
  server.stderr.resume(); let browser;
  try {
    const base = await new Promise((resolve, reject) => {
      server.once('error', reject); server.once('exit', code => reject(Error('preview exit ' + code)));
      server.stdout.on('data', data => { const m = /RNAssistant design: (http:\/\/127\.0\.0\.1:\d+\/)/.exec(String(data)); if (m) resolve(m[1]); });
    });
    browser = await chromium.launch({ headless: true, ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
    const page = await browser.newPage({ viewport: { width: 1280, height: 900 }, reducedMotion: 'reduce' }), errors = [];
    page.setDefaultTimeout(8000); page.on('pageerror', error => errors.push(error.stack || error.message));
    await page.goto(base + 'preview/web/?scenario=ready');
    await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
    await page.evaluate(() => switchTab('instructions'));
    await page.locator('#instructionsList').getByText('Агент · общие', { exact: true }).click();
    const prompt = page.locator('#libraryEditorView .rn-code-editor-promptEditInput');
    await prompt.waitFor();
    await page.waitForFunction(() => !document.querySelector('.rn-code-editor-promptEditInput').CodeMirror.getOption('readOnly'));
    await prompt.evaluate(node => node.CodeMirror.replaceRange('Local prompt draft\n', { line: 0, ch: 0 }, null, '+input'));
    await page.evaluate(() => { window.editorTest = { library: document.getElementById('libraryEditorView'), prompt: getCodeEditorValue('promptEditInput') }; switchTab('vba'); });
    await page.locator('#refreshVbaEmptyButton').click();
    await page.locator('#vbaModuleList').getByText('FormatSales', { exact: true }).click();
    const vba = page.locator('#vbaEditorView .CodeMirror'); await vba.waitFor();
    await vba.evaluate(node => node.CodeMirror.replaceRange("' local VBA draft\n", { line: 0, ch: 0 }, null, '+input'));
    await page.evaluate(() => { editorTest.vba = document.getElementById('vbaEditorView'); editorTest.code = getCodeEditorValue('vbaCodeInput'); });
    async function layout(name) { await page.locator('#workspaceLayout').click(); await page.getByRole('menuitem', { name, exact: true }).click(); }
    await layout('Сохранить раскладку'); await layout('Вернуть сохранённую');
    await page.evaluate(() => { switchTab('chat'); RNAssistantDockWorkspace.show('library'); });
    assert.equal(await prompt.evaluate(node => node.CodeMirror.getValue()), await page.evaluate(() => editorTest.prompt));
    assert.equal(await page.evaluate(() => document.getElementById('libraryEditorView') === editorTest.library), true);
    await page.evaluate(() => { RNAssistantDockWorkspace.close('library'); RNAssistantDockWorkspace.show('library'); });
    assert.equal(await prompt.evaluate(node => node.CodeMirror.getValue()), await page.evaluate(() => editorTest.prompt), 'closing retained editor preserves its draft');
    await page.evaluate(() => RNAssistantDockWorkspace.show('vba'));
    assert.equal(await vba.evaluate(node => node.CodeMirror.getValue()), await page.evaluate(() => editorTest.code));
    assert.equal(await page.evaluate(() => document.getElementById('vbaEditorView') === editorTest.vba), true);
    for (const width of [1280, 420]) {
      await page.setViewportSize({ width, height: 900 });
      await page.evaluate(() => { RNAssistantShellLayout.setNavigationOpen(false); document.documentElement.dataset.theme = 'dark'; document.body.style.zoom = '1.25'; });
      await page.waitForFunction(() => document.querySelector('.chat-dock').getBoundingClientRect().right <= innerWidth + 1);
      for (const id of ['library', 'vba']) {
        await page.evaluate(id => RNAssistantDockWorkspace.show(id), id);
        const bounds = await (id === 'library' ? prompt : vba).evaluate(node => { const r = node.getBoundingClientRect(), p = node.parentElement.getBoundingClientRect(); return { left: r.left, right: r.right, height: r.height, parent: p.height, width: innerWidth }; });
        assert.ok(bounds.left >= 0 && bounds.right <= bounds.width + 1 && bounds.height > 200 && Math.abs(bounds.height - bounds.parent) <= 1, JSON.stringify({ id, bounds }));
        if (process.env.RNA_EDITOR_SCREENSHOTS) await page.screenshot({ path: path.join(process.env.RNA_EDITOR_SCREENSHOTS, id + '-' + width + '.png') });
      }
    }
    assert.deepEqual(errors, []);
    console.log('Editor Dock: shared placement, retained roots/drafts, close/reopen, restore, narrow/dark/zoom and full-height code OK');
  } finally { if (browser) await browser.close(); server.kill(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
