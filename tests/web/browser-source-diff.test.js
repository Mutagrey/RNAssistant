'use strict';
const assert = require('node:assert/strict');
const {withPreviewPage} = require('./preview-session');

withPreviewPage(async ({page, errors}) => {
  page.setDefaultTimeout(8000);
  await page.setViewportSize({width:1280, height:900});
  await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
  await page.evaluate(() => {
    const original = send, read = RNAssistantResourceDownload.read;
    window.diffTest = {calls:[], uri:'rna://chat/preview-chat/artifact/saved-comparison/revision/2'};
    diffTest.target = RNAssistantMaterialView.target({title:'Отчёт продаж', kind:'html_workspace', resourceUri:diffTest.uri,
      documentId:'preview-document', revision:2, mimeType:'application/json'}, {chatId:'preview-chat'});
    const html = '<h1>Отчёт продаж</h1><label>Комментарий <input value="initial"></label>';
    RNAssistantResourceDownload.read = (data, options) => data.diffFixture
      ? Promise.resolve(new TextEncoder().encode(JSON.stringify({Project:{EntryPath:'index.html',Files:[{Path:'index.html',Kind:'html',Content:html}],Data:[],ExternalDependencies:[]},Views:[]})))
      : read(data, options);
    send = (type, payload) => {
      diffTest.calls.push({type, payload});
      if (type === 'exportArtifact' && payload.resourceUri === diffTest.uri) return Promise.resolve({
        browserTarget:{kind:'HtmlArtifact',state:'Ready',chatId:payload.chatId,documentId:payload.documentId,resourceUri:payload.resourceUri,
          artifactId:'saved-comparison',revision:2,capabilities:{preview:true,source:true,edit:false,versions:true,restore:false,exportProject:true,createProject:true,annotations:false}},
        contentType:'application/json',fileName:'project.rna',data:{diffFixture:true,leaseId:'comparison-fixture',payload:{byteLength:1000}}});
      if (type === 'closeArtifactTransfer') return Promise.resolve({});
      if (type === 'getSavedHtmlHistory') return Promise.resolve({chatId:payload.chatId,documentId:payload.documentId,resourceUri:payload.resourceUri,
        headArtifactId:'saved-head',items:(payload.cursor ? [0] : [2,1]).map(n => ({resourceUri:n === 2 ? diffTest.uri : 'saved-version-'+n,revision:n})),
        nextCursor:payload.cursor ? null : 'older'});
      if (type === 'getPluginVersions') return Promise.resolve({items:[{releaseId:'release-2',version:'2.0.0'},{releaseId:'release-1',version:'1.0.0'}]});
      if (type === 'openPlugin' && payload.releaseId) return Promise.reject(Error('Предпросмотр плагина недоступен.'));
      if (type === 'compareHtmlSources' || type === 'comparePluginSources') {
        const files = [{path:'index.html',change:'Changed'},{path:'empty.txt',change:'Added'},
          {path:'large.txt',change:'Changed'},{path:'styles.css',change:'Changed'}];
        const result = {beforeId:payload.beforeId,afterId:payload.afterId,beforeLabel:'v1',afterLabel:'v2',path:payload.path,files,
          complete:payload.path !== 'large.txt',message:'Файл превышает лимит. Частичный diff не показан.',
          beforeText:payload.path === 'empty.txt' ? '' : '<h1>Отчёт продаж</h1>\n<p>Выручка: 120 000 ₽</p>\n',
          afterText:payload.path === 'empty.txt' ? '' : '<h1>Отчёт продаж</h1>\n<p>Выручка: 150 000 ₽</p>\n<p>Рост: 25%</p>\n'};
        if (diffTest.delay && payload.path) { diffTest.delay = false; return new Promise(resolve => { diffTest.finish = () => resolve(result); }); }
        return Promise.resolve(result);
      }
      return original(type, payload);
    };
    RNAssistantChatInspector.setOpen(false);
    RNAssistantDockWorkspace.openTarget(diffTest.target);
  });
  const view = page.locator('.browser-object-view[aria-label="Отчёт продаж"]');
  const action = async (root, name) => {
    const button = root.getByRole('button', {name, exact:true, includeHidden:true});
    if (!await button.isVisible()) await root.getByRole('button', {name:'Инструменты страницы',exact:true}).click();
    await button.click();
  };
  const choose = async (root, version = 'v1') => {
    await root.getByRole('button', {name:'Сравнить с версией',exact:true}).click();
    await page.getByRole('menuitem', {name:version,exact:true}).click();
    await root.locator('.vba-diff-summary').waitFor();
  };
  const shot = async suffix => { if (process.env.BROWSER_SCREENSHOT) await page.screenshot({path:process.env.BROWSER_SCREENSHOT+'-'+suffix+'.png'}); };
  await view.locator('iframe').waitFor();
  await view.frameLocator('iframe').getByRole('textbox').fill('Сохранить мой ввод');
  await shot('before1280');
  await action(view, 'Сравнение');
  await view.getByRole('button', {name:'Сравнить с версией',exact:true}).click();
  assert.equal(await page.getByRole('menuitem', {name:'v2',exact:true}).isDisabled(), true);
  await page.getByRole('menuitem', {name:'Следующая страница версий',exact:true}).click();
  await page.getByRole('menuitem', {name:'v0',exact:true}).waitFor();
  await page.getByRole('menuitem', {name:'В начало истории',exact:true}).click();
  await page.getByRole('menuitem', {name:'v1',exact:true}).click();
  await view.locator('.vba-diff-summary').waitFor();
  assert.match(await view.locator('.vba-diff-summary').innerText(), /\+2 −1/);
  assert.equal(await view.locator('.vba-diff-line.add').count(), 2);
  assert.equal(await view.locator('.browser-source-diff-output h1').count(), 0, 'source stays inert');
  await shot('diff1280');
  const files = view.getByRole('combobox', {name:'Файл для сравнения'});
  await files.selectOption('empty.txt');
  await page.waitForFunction(() => document.querySelector('.browser-source-diff-output').textContent.includes('Добавлен пустой файл'));
  await files.selectOption('large.txt');
  await view.getByText('Файл превышает лимит. Частичный diff не показан.', {exact:true}).waitFor();
  assert.equal(await view.locator('.vba-diff-line').count(), 0);
  await files.selectOption('index.html'); await view.locator('.vba-diff-summary').waitFor();
  await action(view, 'Исходник'); await action(view, 'Страница');
  assert.equal(await view.frameLocator('iframe').getByRole('textbox').inputValue(), 'Сохранить мой ввод');
  assert.equal(await page.evaluate(() => diffTest.calls.filter(c => c.type === 'exportArtifact').length), 1);
  await action(view, 'Сравнение');
  await page.setViewportSize({width:400,height:800});
  await page.evaluate(() => { document.documentElement.style.zoom = '1.25'; document.documentElement.dataset.theme = 'dark'; });
  await view.locator('.browser-source-diff-output').focus();
  await page.keyboard.press('ArrowDown');
  assert.ok(await view.locator('.browser-source-diff').evaluate(node => node.scrollWidth <= node.clientWidth + 1), 'narrow diff scroll stays inside output');
  await shot('diff400-125');
  await page.setViewportSize({width:1280,height:900});
  await page.evaluate(() => { document.documentElement.style.zoom = ''; document.documentElement.dataset.theme = 'light'; diffTest.delay = true; });
  await files.selectOption('styles.css');
  await page.waitForFunction(() => diffTest.finish);
  await action(view, 'Страница');
  await page.evaluate(() => { diffTest.finish(); delete diffTest.finish; });
  assert.equal(await view.locator('.vba-diff-summary').count(), 0, 'hidden comparison discards late response');
  await action(view, 'Сравнение'); await view.locator('.vba-diff-summary').waitFor();
  assert.equal(await files.inputValue(), 'styles.css', 'activation retries only the selected interrupted read');
  await page.evaluate(() => { diffTest.delay = true; }); await files.selectOption('index.html');
  await page.waitForFunction(() => diffTest.finish);
  await page.evaluate(() => { RNAssistantDockWorkspace.close(RNAssistantMaterialView.key(diffTest.target)); diffTest.finish(); });
  assert.equal(await view.count(), 0, 'close removes comparison and frame');
  await page.evaluate(() => RNAssistantDockWorkspace.openTarget({view:'plugin-release',pluginId:'data-templates',releaseId:'release-2',title:'Плагин · v2'}));
  const plugin = page.locator('.browser-object-view[aria-label="Плагин · v2"]');
  await plugin.getByText('Предпросмотр плагина недоступен.', {exact:true}).waitFor();
  await action(plugin, 'Сравнение'); await choose(plugin, 'v1.0.0');
  await shot('plugin1280');
  assert.ok(await page.evaluate(() => diffTest.calls.some(c => c.type === 'comparePluginSources' && c.payload.beforeId === 'release-1' && c.payload.afterId === 'release-2')));
  assert.equal(await page.evaluate(() => diffTest.calls.some(c => /^(restore|apply|save)/i.test(c.type))), false, 'read-only comparison never publishes or restores');
  assert.deepEqual(errors, []);
  console.log('PASS Browser source comparison: exact pair, paging, bounds, inert text, input, stale/close, failed preview, narrow/zoom');
}).catch(error => { console.error(error); process.exitCode = 1; });
