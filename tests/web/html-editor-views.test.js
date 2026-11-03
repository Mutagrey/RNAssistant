'use strict';
// Production scoped editors, synthetic bounded bridge data; no Office qualification.
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
    await page.evaluate(() => {
      window.htmlTest = { calls: [], versions: {}, contents: {}, uploads: {} };
      const original = send;
      function response(id) {
        const version = htmlTest.versions[id] || 1, artifact = id + '-r' + version, uri = 'rna://chat/' + id + '/html/file1';
        const content = htmlTest.contents[id] || '<main><h1>' + id + '</h1><input value="initial"></main>';
        return { activeChatId: id, sessionRevision: version, activeHtmlArtifactId: artifact,
          artifacts: [{ Id: artifact, Kind: 'html_workspace', Title: id, Revision: version }], workspace: {
            revisionArtifactId: artifact, activeFileId: 'file1', files: [{ id: 'file1', path: 'index.html', kind: 'html', content,
              source: { uri, revision: 'r' + version }, sourceReadKey: uri + '@r' + version, sha256: 'a'.repeat(64), byteLength: content.length, characters: content.length }],
            dataSources: [], history: [], redoHistory: [], redoBranches: [], recovery: { status: 'empty', canMutate: true, candidates: [] } } };
      }
      RNAssistantResourceUpload.write = async (lease, blob) => { htmlTest.uploads[lease.leaseId] = await blob.text(); };
      send = function (command, payload) {
        htmlTest.calls.push({ command, payload });
        if (command === 'getHtmlWorkspace') {
          if (payload.chatId === 'late') return new Promise(resolve => { htmlTest.release = () => resolve(response('late')); });
          return Promise.resolve(response(payload.chatId));
        }
        if (command === 'beginHtmlWorkspaceMutationUpload') return Promise.resolve({ leaseId: 'b'.repeat(64) });
        if (command === 'cancelHtmlWorkspaceMutationUpload') return Promise.resolve({});
        if (command === 'saveHtmlWorkspaceFile') {
          htmlTest.contents[payload.chatId] = htmlTest.uploads[payload.uploadLeaseId];
          htmlTest.versions[payload.chatId] = (htmlTest.versions[payload.chatId] || 1) + 1;
          return Promise.resolve(response(payload.chatId));
        }
        if (command === 'stageHtmlVisualSelection') return Promise.resolve({ resource: { Id: 'annotation-' + payload.chatId, FileName: 'selection.json', Kind: 'file', Size: 180, Status: 'ready' } });
        return original(command, payload);
      };
      window.editorView = id => RNAssistantDockWorkspace.getView(RNAssistantHtmlWorkspaceView.key({ chatId: id }));
      RNAssistantHtmlWorkspaceSurface.open('html-a', { mode: 'preview' });
    });
    const root = id => page.locator('[data-html-node="htmlWorkspaceEditor"][data-chat-id="' + id + '"]');
    // Browser surfaces live in a stable portal; anchors/editors live in Dockview.
    await page.waitForFunction(() => [...document.querySelectorAll('[data-html-node="htmlWorkspacePreviewFrame"]')].some(f => f.srcdoc.includes('html-a')));
    const a = page.frameLocator('[data-html-node="htmlWorkspacePreviewFrame"][data-chat-id="html-a"]');
    await a.getByRole('heading', { name: 'html-a', exact: true }).waitFor();
    await a.getByRole('textbox').fill('retained');
    await page.evaluate(() => { htmlTest.frame = document.querySelector('[data-html-node="htmlWorkspacePreviewFrame"]'); htmlTest.document = htmlTest.frame.contentDocument; RNAssistantHtmlWorkspaceSurface.open('html-b', { mode: 'edit' }); });
    await root('html-b').locator('.CodeMirror').waitFor();
    await root('html-b').locator('.CodeMirror').evaluate(node => { node.CodeMirror.execCommand('selectAll'); node.CodeMirror.replaceSelection('<main>draft B</main>', 'end', '+input'); });
    await page.evaluate(() => RNAssistantHtmlWorkspaceSurface.open('html-a', { mode: 'preview' }));
    assert.equal(await a.getByRole('textbox').inputValue(), 'retained');
    async function layout(name) { await page.locator('#workspaceLayout').click(); await page.getByRole('menuitem', { name, exact: true }).click(); }
    await layout('Сохранить раскладку'); await layout('Вернуть сохранённую');
    assert.equal(await page.evaluate(() => htmlTest.frame.contentDocument === htmlTest.document), true, 'restore retains live iframe');
    assert.equal(await a.getByRole('textbox').inputValue(), 'retained');
    // Annotate A while the global selected chat is another chat. No shell interception.
    await page.locator('[data-html-node="inspectHtmlWorkspaceButton"]:visible').click();
    await a.getByRole('heading', { name: 'html-a', exact: true }).click();
    await page.locator('.visual-annotation-editor:visible textarea').fill('Комментарий A');
    await page.locator('.visual-annotation-editor:visible .primary').click();
    await page.waitForFunction(() => (readOfficeDraft('html-a')?.attachments || []).some(item => item.Id === 'annotation-html-a'));
    assert.deepEqual(await page.evaluate(() => { const p = htmlTest.calls.find(c => c.command === 'stageHtmlVisualSelection').payload; return [p.chatId, p.artifactId, p.artifactRevision]; }), ['html-a', 'html-a-r1', 1]);
    assert.equal(await page.evaluate(() => state.activeChatId), 'preview-chat');
    assert.equal(await page.evaluate(() => (readOfficeDraft('preview-chat')?.attachments || []).length), 0);
    await page.evaluate(() => RNAssistantHtmlWorkspaceSurface.open('html-b', { mode: 'edit' }));
    assert.equal(await root('html-b').locator('.CodeMirror').evaluate(node => node.CodeMirror.getValue()), '<main>draft B</main>');
    page.once('dialog', dialog => dialog.dismiss());
    await page.evaluate(() => RNAssistantDockWorkspace.close(RNAssistantHtmlWorkspaceView.key({ chatId: 'html-b' })));
    assert.equal(await page.evaluate(() => !!editorView('html-b')), true, 'dirty close is cancelled');
    await root('html-b').locator('[data-html-node="saveHtmlWorkspaceButton"]').click();
    await page.waitForFunction(() => !editorView('html-b').dirty() && !editorView('html-b').busy());
    assert.deepEqual(await page.evaluate(() => { const p = htmlTest.calls.find(c => c.command === 'saveHtmlWorkspaceFile').payload; return [p.chatId, p.expectedActiveHtmlArtifactId, htmlTest.contents[p.chatId]]; }), ['html-b', 'html-b-r1', '<main>draft B</main>']);
    // A changed source cannot replace an unsaved editor buffer or accept its stale write.
    await root('html-b').locator('.CodeMirror').evaluate(node => { node.CodeMirror.execCommand('selectAll'); node.CodeMirror.replaceSelection('unsaved B', 'end', '+input'); });
    await page.evaluate(() => editorView('html-b').onSnapshot({ activeChatId: 'html-b', activeHtmlArtifactId: 'html-b-r3', sessionRevision: 3 }));
    assert.equal(await root('html-b').locator('.CodeMirror').evaluate(node => node.CodeMirror.getValue()), 'unsaved B');
    assert.equal(await root('html-b').locator('[data-html-node="saveHtmlWorkspaceButton"]').isDisabled(), true);
    await page.setViewportSize({ width: 420, height: 900 });
    await page.evaluate(() => { document.documentElement.dataset.theme = 'dark'; document.body.style.zoom = '1.25'; RNAssistantShellLayout.setNavigationOpen(false); });
    const geometry = await root('html-b').locator('.CodeMirror').evaluate(node => { const r = node.getBoundingClientRect(); return { left: r.left, right: r.right, bottom: r.bottom, width: innerWidth, height: innerHeight }; });
    assert.ok(geometry.left >= 0 && geometry.right <= geometry.width + 1 && geometry.bottom <= geometry.height + 1, JSON.stringify(geometry));
    await page.evaluate(() => { RNAssistantHtmlWorkspaceSurface.open('late'); });
    await page.waitForFunction(() => !!htmlTest.release);
    await page.evaluate(() => { RNAssistantDockWorkspace.close(RNAssistantHtmlWorkspaceView.key({ chatId: 'late' })); htmlTest.release(); });
    await page.waitForFunction(() => !editorView('late'));
    assert.equal(await root('late').count(), 0);
    assert.deepEqual(errors, []);
    console.log('HTML editor views: independent buffers, source chat, annotation, restore, close guard, stale source, narrow/zoom and late close OK');
  } finally { if (browser) await browser.close(); server.kill(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
