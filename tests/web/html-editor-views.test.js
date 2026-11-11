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
      state.chats.push(...['html-a','html-b'].map(Id=>({Id,DocumentAuthorityId:'html-document'})));
      const original = send;
      function response(id) {
        const version = htmlTest.versions[id] || 1, artifact = id + '-r' + version, uri = 'rna://chat/' + id + '/html/file1';
        const content = htmlTest.contents[id] || '<main><h1>' + id + '</h1><input value="initial"></main>';
        return { activeChatId: id, sessionRevision: version, activeHtmlArtifactId: artifact,
          browserTarget: {kind:'HtmlWorkspace',state:'Ready',chatId:id,documentId:htmlTest.wrongDocument?'foreign':'html-document',resourceUri:'rna://chat/'+id+'/artifact/'+artifact+'/revision/'+version,artifactId:artifact,revision:version,capabilities:{preview:true,source:true,edit:!htmlTest.readOnly,versions:true,restore:!htmlTest.readOnly,exportProject:true,createProject:false,annotations:!htmlTest.readOnly}},
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
          if (htmlTest.deferNext === payload.chatId) {
            htmlTest.deferNext=null; const value=response(payload.chatId);
            return new Promise(resolve=>{htmlTest.releaseRefresh=()=>resolve(value);});
          }
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
    const toolbar=page.locator('.browser-view-surface:visible .preview-surface-toolbar');
    assert.equal(await toolbar.getByRole('button',{name:'Аннотировать: выбрать элемент',exact:true}).count(),1);
    if(process.env.HTML_EDITOR_SCREENSHOT)await page.screenshot({path:process.env.HTML_EDITOR_SCREENSHOT+'-preview.png'});
    await toolbar.getByRole('button',{name:'Исходник',exact:true}).click();
    await root('html-a').locator('.CodeMirror').waitFor();
    await root('html-a').getByRole('button',{name:'Страница',exact:true}).click();
    assert.equal(await a.getByRole('textbox').inputValue(),'retained','moving the shared mode controls does not reload the page');
    // Annotate A while the global selected chat is another chat. No shell interception.
    await page.locator('[data-html-node="inspectHtmlWorkspaceButton"]:visible').click();
    await a.getByRole('heading', { name: 'html-a', exact: true }).click();
    await page.locator('.visual-annotation-editor:visible textarea').fill('Комментарий A');
    await page.locator('.visual-annotation-editor:visible .primary').click();
    await page.waitForFunction(() => (readOfficeDraft('html-a')?.attachments || []).some(item => item.Id === 'annotation-html-a'));
    assert.deepEqual(await page.evaluate(() => { const p = htmlTest.calls.find(c => c.command === 'stageHtmlVisualSelection').payload; return [p.chatId, p.artifactId, p.artifactRevision]; }), ['html-a', 'html-a-r1', 1]);
    assert.equal(await page.evaluate(() => state.activeChatId), 'preview-chat');
    assert.equal(await page.evaluate(() => (readOfficeDraft('preview-chat')?.attachments || []).length), 0);
    // Reload reads the current publication, even without the source Chat View.
    await page.evaluate(()=>{htmlTest.versions['html-a']=2;htmlTest.contents['html-a']='<h1>agent v2</h1>';});
    await toolbar.getByRole('button',{name:'Обновить HTML',exact:true}).click();
    await a.getByRole('heading',{name:'agent v2',exact:true}).waitFor();
    await page.evaluate(()=>{htmlTest.versions['html-a']=3;htmlTest.contents['html-a']='<h1>agent v3</h1>';
      handleResourceAuthorityChanged({scope:'document:html-document',generation:3,resources:[],allInScope:true});});
    await a.getByRole('heading',{name:'agent v3',exact:true}).waitFor();
    assert.ok(await toolbar.getByRole('button',{name:'Обновить HTML',exact:true}).isEnabled(),'reload stays available after an automatic refresh');
    const readCount=await page.evaluate(()=>htmlTest.calls.filter(c=>c.command==='getHtmlWorkspace').length);
    await page.evaluate(()=>{
      handleResourceAuthorityChanged({scope:'document:other-document',generation:4,resources:[],allInScope:true});
      handleResourceAuthorityChanged({scope:'document:html-document',generation:3,resources:[],allInScope:true});
    });
    assert.equal(await page.evaluate(()=>htmlTest.calls.filter(c=>c.command==='getHtmlWorkspace').length),readCount,'other scopes and duplicate generations do not reload');
    await page.evaluate(()=>RNAssistantHtmlWorkspaceSurface.open('html-b',{mode:'edit'}));
    const hiddenReads=await page.evaluate(()=>htmlTest.calls.filter(c=>c.command==='getHtmlWorkspace'&&c.payload.chatId==='html-a').length);
    await page.evaluate(()=>{htmlTest.versions['html-a']=4;htmlTest.contents['html-a']='<h1>agent v4</h1>';
      handleResourceAuthorityChanged({scope:'document:html-document',generation:4,resources:[],allInScope:true});});
    assert.equal(await page.evaluate(()=>htmlTest.calls.filter(c=>c.command==='getHtmlWorkspace'&&c.payload.chatId==='html-a').length),hiddenReads,'hidden view defers refresh');
    assert.equal(await root('html-b').locator('.CodeMirror').evaluate(n=>n.CodeMirror.getValue()),'<main>draft B</main>','another project publication preserves the dirty buffer');
    await page.evaluate(()=>RNAssistantHtmlWorkspaceSurface.open('html-a',{mode:'preview'}));
    await a.getByRole('heading',{name:'agent v4',exact:true}).waitFor();
    await page.evaluate(()=>{htmlTest.deferNext='html-a';htmlTest.versions['html-a']=5;htmlTest.contents['html-a']='<h1>agent v5</h1>';
      handleResourceAuthorityChanged({scope:'document:html-document',generation:5,resources:[],allInScope:true});});
    await page.waitForFunction(()=>!!htmlTest.releaseRefresh);
    await page.evaluate(()=>{htmlTest.versions['html-a']=6;htmlTest.contents['html-a']='<h1>agent v6</h1>';
      handleResourceAuthorityChanged({scope:'document:html-document',generation:6,resources:[],allInScope:true});htmlTest.releaseRefresh();});
    await a.getByRole('heading',{name:'agent v6',exact:true}).waitFor();
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
    await page.evaluate(() => {htmlTest.versions['html-b']=3;
      handleResourceAuthorityChanged({scope:'document:html-document',generation:7,resources:[],allInScope:true});});
    await page.waitForFunction(()=>document.querySelector('[data-chat-id="html-b"] [data-html-node="saveHtmlWorkspaceButton"]').disabled);
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
    await page.setViewportSize({width:1280,height:900});
    await page.evaluate(async()=>{document.body.style.zoom='';document.documentElement.dataset.theme='light';
      htmlTest.readOnly=true;RNAssistantHtmlWorkspaceSurface.open('html-a',{mode:'edit'});await editorView('html-a').ready(true);});
    assert.equal(await root('html-a').locator('.CodeMirror').evaluate(node=>node.CodeMirror.getOption('readOnly')),true,'revoked edit capability makes the existing editor read-only');
    await page.evaluate(()=>RNAssistantHtmlWorkspaceSurface.open('html-a',{mode:'preview'}));
    await a.getByRole('heading',{name:'agent v6',exact:true}).waitFor();
    const liveSurface=page.locator('[data-html-node="htmlWorkspacePreview"]').filter({has:page.locator('[data-html-node="htmlWorkspacePreviewFrame"][data-chat-id="html-a"]')});
    assert.equal(await liveSurface.locator('[data-html-node="inspectHtmlWorkspaceButton"]').isDisabled(),true,'read-only target cannot start annotations');
    if(process.env.BROWSER_SCREENSHOT)await page.screenshot({path:process.env.BROWSER_SCREENSHOT+'-live-readonly.png'});
    assert.equal(await page.evaluate(async()=>{htmlTest.wrongDocument=true;htmlTest.contents['html-a']='<h1>FOREIGN</h1>';return editorView('html-a').ready(true);}),false,'foreign response is rejected');
    await a.getByRole('heading',{name:'agent v6',exact:true}).waitFor();
    assert.equal(await a.getByRole('heading',{name:'FOREIGN',exact:true}).count(),0,'foreign source never replaces the retained preview');
    assert.deepEqual(errors, []);
    console.log('HTML editor views: buffers, scoped annotation, reload/current publication, hidden and concurrent refresh, dirty guards, restore, narrow/zoom and late close OK');
  } finally { if (browser) await browser.close(); server.kill(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
