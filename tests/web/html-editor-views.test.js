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
      window.htmlTest = { calls: [], versions: {}, contents: {}, uploads: {}, pages: new Map() };
      state.chats.push(...['html-a','html-b'].map(Id=>({Id,DocumentAuthorityId:'html-document'})));
      const original = send;
      function response(id) {
        const version = htmlTest.versions[id] || 1, artifact = id + '-r' + version, uri = 'rna://chat/' + id + '/html/file1';
        const content = htmlTest.contents[id] || '<main><h1>' + id + '</h1><input value="initial"></main>';
        return { activeChatId: id, sessionRevision: version, activeHtmlArtifactId: artifact,
          browserTarget: {kind:'HtmlWorkspace',state:'Ready',chatId:id,title:id,logicalId:id,immutable:false,displayLocator:'/resources/'+id,ownerScope:{kind:'Document',scopeId:'html-document',chatId:id},documentId:htmlTest.wrongDocument?'foreign':'html-document',resourceUri:'rna://chat/'+id+'/artifact/'+artifact+'/revision/'+version,artifactId:artifact,revision:version,capabilities:{preview:false,source:true,edit:!htmlTest.readOnly,versions:true,saveVersion:!htmlTest.readOnly,restore:!htmlTest.readOnly,exportProject:true,createProject:false,annotations:false}},
          artifacts: [{ Id: artifact, Kind: 'html_workspace', Title: id, Revision: version }], workspace: {
            revisionArtifactId: artifact, activeFileId: 'file1', files: [{ id: 'file1', path: 'index.html', kind: 'html', content,
              source: { uri, revision: 'r' + version }, sourceReadKey: uri + '@r' + version, sha256: 'a'.repeat(64), byteLength: content.length, characters: content.length }],
            dataSources: [], history: [], redoHistory: [], redoBranches: [], recovery: { status: 'empty', canMutate: true, candidates: [] } } };
      }
      RNAssistantResourceUpload.write = async (lease, blob) => { htmlTest.uploads[lease.leaseId] = await blob.text(); };
      send = function (command, payload) {
        htmlTest.calls.push({ command, payload });
        if (command === 'browserCommand') {
          let value=htmlTest.pages.get(payload.browserTabId);
          if(payload.action==='Open') { value={browserTabId:payload.browserTabId,sessionId:'session-'+payload.browserTabId,ownerScope:payload.ownerScope,revision:1,documentGeneration:1,
            status:'Unavailable',url:payload.locator,title:payload.ownerScope.chatId,target:response(payload.ownerScope.chatId).browserTarget};htmlTest.pages.set(payload.browserTabId,value); }
          else if(payload.action==='Reload') {value.target=response(value.ownerScope.chatId).browserTarget;value.documentGeneration++;}
          else if(payload.action==='Close')value.status='Closed';value.revision++;return Promise.resolve(structuredClone(value));
        }
        if(command==='resolveLocalLocator')return Promise.resolve({locator:payload.locator,target:response(payload.ownerScope.chatId).browserTarget});
        if(command==='readBrowserSource') {const text=htmlTest.contents[payload.ownerScope.chatId]||'<h1>'+payload.ownerScope.chatId+'</h1>';return Promise.resolve({files:['index.html'],path:'index.html',text,offset:0,totalCharacters:text.length,revision:'sha',requestGeneration:payload.requestGeneration});}
        if(command==='getBrowserVersionState')return Promise.resolve({target:payload.target,capabilities:payload.target.capabilities,contentRevision:payload.target.resourceUri,stateRevision:'catalog'});
        if(command==='getBrowserVersions')return Promise.resolve({items:[],stateRevision:'catalog'});
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
        return original(command, payload);
      };
      window.editorView = id => RNAssistantDockWorkspace.getView(RNAssistantHtmlWorkspaceView.key({ chatId: id }));
      RNAssistantHtmlWorkspaceSurface.open('html-a', { mode: 'edit' });
    });
    const root = id => page.locator('[data-html-node="htmlWorkspaceEditor"][data-chat-id="' + id + '"]');
    const a = root('html-a');
    await a.locator('.CodeMirror').waitFor();
    await page.evaluate(() => RNAssistantHtmlWorkspaceSurface.open('html-a', { mode: 'preview' }));
    const browserSource=page.locator('.browser-object-view:visible .browser-document-source');
    await browserSource.getByText('html-a',{exact:false}).waitFor();
    assert.equal(await page.locator('.browser-object-view iframe').count(),0,'source-only Browser never creates an authored iframe');
    assert.equal(await a.locator('.preview-surface-toolbar').count(),0,'editor no longer has its own Browser chrome');
    await page.evaluate(() => RNAssistantHtmlWorkspaceSurface.open('html-b', { mode: 'edit' }));
    await root('html-b').locator('.CodeMirror').evaluate(node=>{node.CodeMirror.execCommand('selectAll');node.CodeMirror.replaceSelection('<main>draft B</main>','end','+input');});
    const hiddenReads=await page.evaluate(()=>htmlTest.calls.filter(c=>c.command==='readBrowserSource').length);
    await page.evaluate(()=>{htmlTest.versions['html-a']=2;htmlTest.contents['html-a']='<h1>agent v2</h1>';handleResourceAuthorityChanged({scope:'document:html-document',generation:2,resources:[],allInScope:true});});
    assert.equal(await page.evaluate(()=>htmlTest.calls.filter(c=>c.command==='readBrowserSource').length),hiddenReads,'hidden Browser defers source refresh');
    assert.equal(await root('html-b').locator('.CodeMirror').evaluate(n=>n.CodeMirror.getValue()),'<main>draft B</main>');
    await page.evaluate(()=>RNAssistantHtmlWorkspaceSurface.open('html-a',{mode:'preview'}));
    await browserSource.getByText('agent v2',{exact:false}).waitFor();
    const reads=await page.evaluate(()=>htmlTest.calls.filter(c=>c.command==='readBrowserSource').length);
    await page.evaluate(()=>{handleResourceAuthorityChanged({scope:'document:other',generation:3,resources:[],allInScope:true});handleResourceAuthorityChanged({scope:'document:html-document',generation:2,resources:[],allInScope:true});});
    assert.equal(await page.evaluate(()=>htmlTest.calls.filter(c=>c.command==='readBrowserSource').length),reads,'other scope and duplicate generation do not read');
    await page.evaluate(()=>{htmlTest.versions['html-a']=3;htmlTest.contents['html-a']='<h1>agent v3</h1>';handleResourceAuthorityChanged({scope:'document:html-document',generation:3,resources:[],allInScope:true});});
    await browserSource.getByText('agent v3',{exact:false}).waitFor();
    await page.evaluate(()=>RNAssistantHtmlWorkspaceSurface.open('html-b',{mode:'edit'}));
    page.once('dialog',dialog=>dialog.dismiss());await page.evaluate(()=>RNAssistantDockWorkspace.close(RNAssistantHtmlWorkspaceView.key({chatId:'html-b'})));
    assert.equal(await page.evaluate(()=>!!editorView('html-b')),true,'dirty close is cancelled');
    await root('html-b').locator('[data-html-node="saveHtmlWorkspaceButton"]').click();
    await page.waitForFunction(()=>!editorView('html-b').dirty()&&!editorView('html-b').busy());
    assert.deepEqual(await page.evaluate(()=>{const p=htmlTest.calls.find(c=>c.command==='saveHtmlWorkspaceFile').payload;return [p.chatId,p.expectedActiveHtmlArtifactId,htmlTest.contents[p.chatId]];}),['html-b','html-b-r1','<main>draft B</main>']);
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
    await page.evaluate(async()=>{htmlTest.wrongDocument=true;await editorView('html-a').ready(true);});
    await root('html-a').getByText(/Цель Browser не подтверждена/).waitFor();
    await page.evaluate(()=>RNAssistantDockWorkspace.close(RNAssistantHtmlWorkspaceView.key({chatId:'html-a'})));
    assert.equal(await root('html-a').count(),0,'clean close releases the editor without closing Browser');
    assert.ok(await page.locator('.browser-object-view').count()>0);
    assert.deepEqual(errors, []);
    console.log('PASS HTML editor: exact dirty buffers, shared source-only Browser, scoped current refresh, hidden deferral, narrow bounds, stale reads and independent close');
  } finally { if (browser) await browser.close(); server.kill(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
