'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const {spawn} = require('node:child_process');
const {chromium} = require('playwright');
const root = path.resolve(__dirname, '../..');
const captures = process.env.RNA_CHAT_SCREENSHOTS || path.join(os.tmpdir(), 'rnassistant-shared-chat');
fs.mkdirSync(captures, {recursive:true});
(async () => {
  const server = spawn('python3', ['-B', 'prototypes/serve_ui.py', '--port', '0'], {cwd:root});
  const address = await new Promise((resolve,reject) => {
    server.stdout.on('data', chunk => { const match = /RNAssistant design: (http:\/\/127\.0\.0\.1:\d+\/)/.exec(String(chunk)); if(match)resolve(match[1]); });
    server.once('exit', code => reject(Error('Preview server exited: '+code)));
  });
  const browser = await chromium.launch({headless:true, ...(process.env.BROWSER_EXECUTABLE ? {executablePath:process.env.BROWSER_EXECUTABLE} : {})});
  try {
    const page = await browser.newPage({viewport:{width:1360,height:900}}), errors=[];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(address+'preview/web/?scenario=ready');
    await page.waitForFunction(() => state.activeChatId==='preview-chat' && !state.initializePromise);
    const checkToggleFocus = async selector => {
      const button=page.locator(selector);
      for(const key of ['Enter','Space']){
        await button.press(key);
        assert.equal(await button.evaluate(node=>node.matches(':focus-visible')),true,selector+' shows keyboard focus for '+key);
        await button.click();
        assert.deepEqual(await button.evaluate(node=>[node===document.activeElement,node.matches(':focus-visible'),getComputedStyle(node).outlineStyle]),
          [true,false,'none'],selector+' keeps pointer focus without a stale keyboard outline');
      }
    };
    await checkToggleFocus('#productMobileMenuButton');
    await page.waitForFunction(() => !document.querySelector('.shell').classList.contains('navigation-transition'));
    await page.locator('#chatLayout .chatbar').screenshot({animations:'disabled',path:path.join(captures,'panel-focus-mouse.png')});
    await page.screenshot({animations:'disabled',path:path.join(captures,'chat-office.png')});
    const dimensions = async (form, list) => page.evaluate(([form,list]) => {
      const styles = node => { const value=getComputedStyle(node); return ['fontSize','lineHeight','paddingTop','paddingBottom','borderRadius'].map(key=>value[key]); };
      return {composer:styles(document.querySelector(form)), input:styles(document.querySelector(form+' textarea')),
        messages:styles(document.querySelector(list))};
    },[form,list]);
    const office = await dimensions('#chatForm','#messages');
    await page.locator('#chatInput').fill('Черновик Office до перехода');
    const composerIdentity = await page.evaluate(() => {
      const form = document.querySelector('#chatForm'); form.dataset.testIdentity = crypto.randomUUID();
      return form.dataset.testIdentity;
    });
    const transcriptIdentity = await page.evaluate(() => {
      const list = document.querySelector('#messages'); list.dataset.testIdentity = crypto.randomUUID();
      return list.dataset.testIdentity;
    });
    await page.evaluate(() => {
      state.messages = Array.from({length:30}, (_,index) => ({Id:'office-scroll-'+index,
        Role:'user', Content:'Сообщение Office '+index+' — '+('Проверка прокрутки. '.repeat(8))}));
      state.messageStartIndex = 0; state.messageTotalCount = state.messages.length;
      renderMessages({fullReset:true,forceScroll:true});
      const list = document.querySelector('#messages');
      list.scrollTop = Math.floor((list.scrollHeight-list.clientHeight)/3);
      list.dispatchEvent(new Event('scroll'));
    });
    const toggleMetrics = selector => page.locator(selector).evaluate(node => {
      const css=getComputedStyle(node),svg=node.querySelector('svg');
      return [node.tagName,css.width,css.height,css.borderRadius,css.color,css.backgroundColor,
        getComputedStyle(svg).width,svg.innerHTML];
    });
    const officeToggle=await toggleMetrics('#toggleChatInspectorButton');
    await checkToggleFocus('#toggleChatInspectorButton');
    await page.locator('#toggleChatInspectorButton').click();
    const inspector=page.locator('#chatInspectorSplitter');
    const inspectorBefore=Number(await inspector.getAttribute('aria-valuenow'));
    await inspector.press('ArrowLeft');
    assert.equal(Number(await inspector.getAttribute('aria-valuenow')),inspectorBefore+12,'shared splitter grows a right-hand pane to the left');
    await inspector.press('Enter');
    await page.locator('#toggleChatInspectorButton').click();
    // Exercise the actual bridge consumers with the same readable transcript, not separate HTML fixtures.
    await page.evaluate(() => {
      const original=send;
      window.chatTest = {requests:[], fail:false, transcript:true};
      window.send=async function(type,payload){
        if(type==='submitProjectChat'){
          chatTest.requests.push({type,payload});
          if(chatTest.fail)throw Error('Проверка ошибки отправки');
        }
        const result=await original(type,payload);
        if(type==='submitProjectChat'&&chatTest.hold)await new Promise(resolve=>{chatTest.release=resolve;});
        if(chatTest.transcript&&(type==='getProjectChat'||type==='getPluginChat'))result.messages=[
          {Id:'user',Role:'user',Content:'Проверь структуру проекта и подготовь краткую сводку.'},
          {Id:'step',Role:'assistant',Content:'Проверяю состав исходников.'},
          {Id:'read',Role:'assistant',Content:'',RunId:'preview-run',Activity:{
            RunId:'preview-run',Kind:'tool',Title:'common.resources_read',ToolId:'common.resources_read',
            ToolCallId:'preview-call',Status:'completed',ResultMessage:'Исходник прочитан.'}},
          {Id:'finish',Role:'assistant',Content:'Исходники доступны.\n\n| Файл | Назначение |\n| --- | --- |\n| plugin.json | Описание плагина |\n| index.html | Интерфейс |\n\nЭто пример для проверки интерфейса; модель не запускалась.'}
        ];
        return result;
      };
    });
    const officeScroll = await page.evaluate(() => {
      const list = document.querySelector('#messages');
      list.scrollTop = Math.floor((list.scrollHeight-list.clientHeight)/3);
      list.dispatchEvent(new Event('scroll'));
      const top = list.scrollTop;
      RNAssistantProjectChats.setScope('projects');
      return {top:top,viewport:suspendedOfficeTranscript.viewport};
    });
    assert.ok(officeScroll.top>0,'Office transcript is scrolled before navigation');
    await page.locator('#messages .message.user').first().waitFor();
    assert.deepEqual(await page.evaluate(() => [document.querySelectorAll('#messages').length,
      document.querySelector('#messages').parentElement.id, document.querySelector('#messages').dataset.testIdentity]),
      [1,'workspaceChatTranscriptMount',transcriptIdentity],'project mounts the same physical transcript');
    assert.deepEqual(await page.evaluate(() => [document.querySelectorAll('form#chatForm').length,
      document.querySelector('#chatForm').parentElement.id, document.querySelector('#chatForm').dataset.testIdentity]),
      [1,'workspaceChatView',composerIdentity],'project mounts the same physical composer');
    assert.equal(await page.locator('#chatInput').inputValue(),'', 'Office draft is absent from the project input');
    await page.evaluate(() => { renderSendControls(); setChatInputText('Поздний черновик Office', false, true); });
    assert.equal(await page.locator('#chatInput').inputValue(),'', 'background Office state cannot replace the project input');
    const projectTranscript = await page.locator('#messages').textContent();
    await page.evaluate(() => { renderMessages({fullReset:true}); renderStreamingMessages(true); });
    assert.equal(await page.locator('#messages').textContent(),projectTranscript,
      'background Office render cannot replace the project transcript');
    assert.deepEqual(await dimensions('#chatForm','#messages'), office, 'workspace uses the same message/composer metrics');
    await page.evaluate(() => RNAssistantProjectChats.setScope('office'));
    await page.locator('#messages .message.user').first().waitFor();
    assert.deepEqual(await page.evaluate(() => [document.querySelector('#messages').parentElement.id,
      document.querySelector('#messages').dataset.testIdentity]), ['officeChatTranscriptMount',transcriptIdentity],
      'return to Office restores its history in the same transcript');
    assert.deepEqual(await page.evaluate(() => [document.querySelector('#chatForm').parentElement.id,
      document.querySelector('#chatForm').dataset.testIdentity]), ['chatDropZone',composerIdentity],
      'return to Office restores the same composer');
    assert.equal(await page.locator('#chatInput').inputValue(), 'Поздний черновик Office',
      'returning to Office restores its updated draft');
    const restoredOfficeAnchor = await page.evaluate(key => {
      const list=document.querySelector('#messages'),unit=renderedMessageUnits[key];
      return unit&&unit.node.parentNode===list ? unit.node.getBoundingClientRect().top-list.getBoundingClientRect().top : null;
    },officeScroll.viewport.key);
    assert.ok(restoredOfficeAnchor!==null && Math.abs(restoredOfficeAnchor-officeScroll.viewport.offset)<4,
      'return to Office restores the anchored reading position');
    await page.evaluate(() => startMessageEdit(state.messages[0], 0));
    await page.locator('#chatInput').fill('Правка сообщения Office');
    await page.evaluate(() => RNAssistantProjectChats.setScope('projects'));
    await page.evaluate(() => RNAssistantProjectChats.setScope('office'));
    assert.equal(await page.locator('#chatInput').inputValue(), 'Правка сообщения Office',
      'switching surfaces preserves the Office edit in the shared composer');
    await page.locator('#cancelMessageEditButton').click();
    assert.equal(await page.locator('#chatInput').inputValue(), 'Поздний черновик Office',
      'cancelling the edit restores the Office draft');
    await page.evaluate(() => RNAssistantProjectChats.setScope('projects'));
    await page.locator('#messages .message.user').first().waitFor();
    await page.screenshot({animations:'disabled',path:path.join(captures,'chat-workspace.png')});
    await page.locator('#chatInput').fill('Черновик рабочего проекта');
    await page.locator('#projectChatTree [data-tree-key="project:preview-plan-workspace"] > .navigation-tree-row > .navigation-tree-label').click();
    await page.locator('#projectChatTitle').getByText('План запуска').waitFor();
    assert.equal(await page.locator('#chatInput').inputValue(),'','another workspace has its own draft');
    await page.locator('#projectChatTree [data-tree-key="project:preview-data-workspace"] > .navigation-tree-row > .navigation-tree-label').click();
    await page.locator('#projectChatTitle').getByText('Подготовка отчёта').waitFor();
    assert.equal(await page.locator('#chatInput').inputValue(),'Черновик рабочего проекта','returning restores the workspace draft');
    assert.equal(await page.locator('#sendButton').isEnabled(),true,'restored draft can be sent');
    await page.evaluate(() => {chatTest.fail=true;});
    await page.locator('#chatInput').press('Enter');
    await page.locator('#workspaceChatNotice').getByText('Проверка ошибки отправки').waitFor();
    assert.equal(await page.locator('#chatInput').inputValue(),'Черновик рабочего проекта','failed delivery preserves the draft');
    await page.evaluate(() => RNAssistantPlugins.select('data-templates'));
    const pluginToggle=page.locator('#pluginSurfaceToggle');
    assert.equal(await pluginToggle.getAttribute('aria-expanded'),'true','plugin opens with the right preview');
    assert.equal(await pluginToggle.getAttribute('aria-controls'),'pluginPreviewMount');
    assert.equal(await page.evaluate(() => RNAssistantShellLayout.isNavigationOpen()),false,'plugin opens with its tree collapsed');
    // Office was closed; the plugin preview is open, so active foreground/background differ.
    assert.deepEqual((await toggleMetrics('#pluginSurfaceToggle')).filter((_,index)=>index!==4&&index!==5),
      officeToggle.filter((_,index)=>index!==4&&index!==5),'plugin reuses the same right-panel button geometry and icon');
    await checkToggleFocus('#pluginSurfaceToggle');
    await pluginToggle.click();
    await page.locator('.plugin-toolbar').screenshot({animations:'disabled',path:path.join(captures,'plugin-panel-closed.png')});
    await pluginToggle.press('Enter');
    assert.equal(await pluginToggle.getAttribute('aria-expanded'),'true','Enter opens the right preview once');
    await page.waitForFunction(() => RNAssistantWorkspaceChat.owner()==='plugin' && document.querySelector('#workspaceChatView').parentElement.id==='pluginChat');
    assert.equal(await page.locator('#pluginChat').isVisible(),true,'the shared chat stays visible');
    await page.locator('#messages .message.user').first().waitFor();
    assert.deepEqual(await page.evaluate(() => [document.querySelector('#messages').parentElement.id,
      document.querySelector('#messages').dataset.testIdentity]), ['workspaceChatTranscriptMount',transcriptIdentity],
      'plugin mounts the same physical transcript');
    await page.frameLocator('#pluginPreviewMount iframe').locator('#dtViewSources h3').waitFor({state:'visible'});
    assert.equal(await page.locator('#pluginPreview').evaluate(node=>node.parentElement.classList.contains('preview-surface-actions')),true,
      'the existing refresh action belongs to the right preview');
    assert.match(await page.locator('#pluginPreviewMount .preview-surface-location').textContent(),/Данные и шаблоны/,
      'the right preview identifies its resource');
    await page.waitForFunction(() => !document.querySelector('.shell').classList.contains('navigation-transition'));
    await page.evaluate(() => switchTab('chat'));
    assert.equal(await page.evaluate(() => RNAssistantShellLayout.isNavigationOpen()),true,
      'leaving the plugin restores the prior tree preference');
    await page.evaluate(() => switchTab('plugins'));
    assert.equal(await page.evaluate(() => RNAssistantShellLayout.isNavigationOpen()),false,
      'returning to the plugin collapses its tree again');
    assert.ok((await page.locator('#pluginChat').boundingBox()).x < (await page.locator('#pluginPreviewMount').boundingBox()).x,
      'the shared chat is centered before the right preview');
    assert.deepEqual(await dimensions('#chatForm','#messages'), office, 'plugin uses the same message/composer metrics');
    const pluginTitleInset = () => page.locator('#pluginChatTitle').evaluate(node => node.getBoundingClientRect().left-node.parentElement.getBoundingClientRect().left);
    assert.equal(await pluginTitleInset(),12,'nested chat header has no navigation-toggle reservation on desktop');
    await page.locator('.plugin-toolbar').screenshot({animations:'disabled',path:path.join(captures,'plugin-panel-open.png')});
    await page.screenshot({animations:'disabled',path:path.join(captures,'chat-plugin.png')});
    const splitter=page.locator('#pluginSurfaceSplitter'), splitBox=await splitter.boundingBox();
    const seam=await splitter.evaluate(node=>{
      const handle=node.getBoundingClientRect(),toolbar=document.querySelector('.plugin-toolbar').getBoundingClientRect();
      const body=document.querySelector('#pluginBody').getBoundingClientRect();
      const preview=document.querySelector('#pluginPreviewMount .preview-surface-bar').getBoundingClientRect();
      return {top:handle.top,headerTop:toolbar.top,previewTop:preview.top,
        previewHeight:preview.height,bottom:handle.bottom,bodyBottom:body.bottom};
    });
    assert.ok(Math.abs(seam.top-seam.headerTop)<1 && Math.abs(seam.top-seam.previewTop)<1 &&
      Math.abs(seam.bottom-seam.bodyBottom)<1 && Math.abs(seam.previewHeight-52)<1,
      'plugin chat and preview have aligned top headers and one full-height divider');
    await page.mouse.move(splitBox.x-4,seam.headerTop+26);
    assert.equal(await splitter.evaluate(node=>node.matches(':hover')),true,
      'the header continuation keeps the same transparent resize target');
    const navWidth=(await page.locator('#pluginNavigationPane').boundingBox()).width;
    assert.equal(await splitter.getAttribute('class'),'splitter','plugin uses the common splitter element');
    assert.equal(splitBox.width,1,'development divider is one visible pixel');
    assert.deepEqual(await splitter.evaluate(node=>{
      const line=getComputedStyle(node),hit=getComputedStyle(node,'::before');
      return [line.boxShadow,hit.backgroundColor,hit.left,hit.right];
    }),['none','rgba(0, 0, 0, 0)','-5px','-5px'],'wide hit target adds no background or glow');
    await page.mouse.move(splitBox.x-4,splitBox.y+120);
    assert.equal(await splitter.evaluate(node=>node.matches(':hover')),true,'transparent area catches the pointer over the preview');
    await page.screenshot({animations:'disabled',path:path.join(captures,'chat-plugin-splitter-hover.png')});
    const splitBefore=Number(await splitter.getAttribute('aria-valuenow'));
    await page.mouse.down();
    await page.mouse.move(splitBox.x-70,splitBox.y+120,{steps:4});
    await page.mouse.up();
    assert.ok(Number(await splitter.getAttribute('aria-valuenow'))<splitBefore,'transparent hit target still resizes the panes');
    assert.equal((await page.locator('#pluginNavigationPane').boundingBox()).width,navWidth,'local split does not change the shared navigation width');
    await splitter.press('Enter');
    await page.locator('#productMobileMenuButton').click();
    await page.waitForFunction(() => !document.querySelector('.shell').classList.contains('navigation-transition'));
    assert.equal(await splitter.isVisible(),true,'collapsing navigation keeps the local splitter visible');
    assert.equal((await splitter.boundingBox()).width,1);
    await pluginToggle.click();
    await page.evaluate(() => refreshSplitPanes());
    assert.equal(await splitter.isVisible(),false,'common layout refresh respects a closed preview');
    assert.equal(await page.locator('#pluginChat').isVisible(),true,'closing the preview keeps the chat');
    assert.equal(await pluginToggle.getAttribute('aria-expanded'),'false');
    await pluginToggle.press('Space');
    assert.equal(await pluginToggle.getAttribute('aria-expanded'),'true','Space opens the right preview once');
    await page.locator('#productMobileMenuButton').click();
    await page.waitForFunction(() => !document.querySelector('.shell').classList.contains('navigation-transition'));
    const history=page.locator('#messages .is-activity-message');
    assert.match(await history.textContent(),/common.resources_read/,'saved tool activity uses the common renderer');
    await page.locator('#chatInput').fill('Черновик плагина');
    await page.locator('#chatInput').press('Escape');
    assert.equal(await page.locator('#pluginChat').isVisible(),true,'Escape does not close the central chat');
    await page.locator('#pluginPreviewMount iframe').evaluate(frame => { frame.dataset.toggleProbe = 'same-instance'; });
    await pluginToggle.click();
    assert.equal(await page.locator('#chatInput').inputValue(),'Черновик плагина','closing the preview keeps the chat draft');
    await pluginToggle.press('Enter');
    assert.equal(await page.locator('#chatInput').inputValue(),'Черновик плагина','reopening the preview keeps the chat draft');
    assert.equal(await page.locator('#pluginPreviewMount iframe').getAttribute('data-toggle-probe'),'same-instance',
      'reopening the right preview keeps its live iframe');
    await page.locator('#chatInput').press('Shift+Enter');
    assert.match(await page.locator('#chatInput').inputValue(),/\n$/,'Shift+Enter inserts a line break');
    await page.locator('#chatInput').press('Enter');
    await page.locator('#workspaceChatNotice').getByText('Проверка ошибки отправки').waitFor();
    assert.equal((await page.locator('#chatInput').inputValue()).trim(),'Черновик плагина','plugin failure preserves input');
    assert.equal(await history.count(),1,'failure keeps the saved tool activity visible');
    const firstRequest=await page.evaluate(() => chatTest.requests.at(-1).payload.input.requestId);
    await page.locator('#chatInput').press('Enter');
    await page.waitForFunction(id => chatTest.requests.filter(item => item.payload.input.requestId===id).length===2,firstRequest);
    await page.locator('#sendButton').waitFor({state:'visible'});
    await page.locator('#workspaceComposerPicker .composer-add-trigger').click();
    await page.locator('#workspacePlanButton').click();
    await page.locator('#workspaceReasoningToggle').click();
    await page.locator('#chatInput').press('Enter');
    await page.waitForFunction(() => chatTest.requests.at(-1).payload.input.Mode==='plan');
    assert.equal(await page.evaluate(() => chatTest.requests.at(-1).payload.input.ReasoningEnabled),true,'workspace preferences reach the runtime');
    assert.notEqual(await page.evaluate(() => chatTest.requests.at(-1).payload.input.requestId),firstRequest,'changed preferences get a new request identity');
    await page.setViewportSize({width:390,height:844});
    await page.locator('#chatInput').scrollIntoViewIfNeeded();
    assert.ok((await page.locator('#pluginChat').boundingBox()).y < (await page.locator('#pluginPreviewMount').boundingBox()).y,'narrow plugin keeps chat before preview');
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth<=innerWidth),true,'plugin chat fits a narrow viewport');
    assert.equal(await pluginTitleInset(),12,'narrow chat title keeps its normal inset');
    await page.screenshot({animations:'disabled',path:path.join(captures,'chat-plugin-narrow.png')});
    await page.setViewportSize({width:750,height:900});
    await page.waitForFunction(() => document.querySelector('.shell').classList.contains('navigation-compact'));
    assert.equal(await pluginTitleInset(),12,'compact chat title keeps its normal inset');
    await page.setViewportSize({width:1360,height:900});
    await page.evaluate(() => {chatTest.fail=false;chatTest.transcript=false;});
    await page.locator('#chatInput').fill('Первое обсуждение плагина');
    await page.locator('#chatInput').press('Enter');
    await page.locator('#messages').getByText('Изменение исходников смоделировано в preview. Файлы плагина не менялись.',{exact:true}).waitFor();
    const sourceWorkspace='preview-plugin-workspace';
    const firstSession=await page.evaluate(id=>RNAssistantWorkspaceChat.selectedSession(id),sourceWorkspace);
    await page.locator('#chatInput').fill('Неотправленный черновик первой беседы');
    await page.locator('#pluginChatNew').click();
    await page.locator('#messages .chat-empty').waitFor();
    const secondSession=await page.evaluate(id=>RNAssistantWorkspaceChat.selectedSession(id),sourceWorkspace);
    assert.notEqual(secondSession,firstSession,'plus creates a separate workspace session');
    assert.equal(await page.locator('#chatInput').inputValue(),'','new chat starts with an empty draft');
    assert.equal(await page.locator('#pluginChatTitle').textContent(),'Новый чат');
    assert.equal(await pluginTitleInset(),12,'new chat title keeps its normal inset');
    assert.equal(await page.locator('#workspaceReasoningToggle').isEnabled(),true,'new chat releases composer controls');
    await page.screenshot({animations:'disabled',path:path.join(captures,'plugin-new-chat.png')});
    await page.evaluate(() => {chatTest.hold=true;});
    await page.locator('#chatInput').fill('Второе обсуждение плагина');
    await page.locator('#chatInput').press('Enter');
    await page.waitForFunction(() => typeof chatTest.release==='function');
    const submitted=await page.evaluate(() => chatTest.requests.at(-1).payload);
    assert.equal(submitted.sessionId,secondSession,'send targets the newly created session');
    assert.equal(submitted.input.sessionId,secondSession,'runtime input pins the same session');
    await page.locator('#pluginChatHistory').click();
    await page.locator('#pluginChatHistoryList > button').first().waitFor();
    assert.equal(await page.locator('#pluginChatHistoryList > button').count(),2,'history remains available during a run');
    await page.locator('#pluginPreviewMount iframe').evaluate(frame => { frame.dataset.routeProbe = 'same-instance'; });
    await page.evaluate(([workspace,session])=>RNAssistantProjectChats.open(workspace,session),[sourceWorkspace,secondSession]);
    await page.locator('#messages').getByText('Второе обсуждение плагина',{exact:true}).waitFor();
    const sourceTree=page.locator('[data-tree-key="project:'+sourceWorkspace+'"]');
    await page.waitForFunction(workspace=>document.querySelectorAll('[data-tree-key^="project-chat:'+workspace+':"]').length===2,sourceWorkspace);
    assert.equal(await sourceTree.locator('[data-tree-key^="project-chat:"]').count(),2,'main tree lists both source chats');
    if(!await page.evaluate(() => RNAssistantShellLayout.isNavigationOpen()))await page.locator('#productMobileMenuButton').click();
    await page.screenshot({animations:'disabled',path:path.join(captures,'plugin-chat-history.png')});
    const firstRow=page.locator('[data-tree-key="project-chat:'+sourceWorkspace+':'+firstSession+'"]');
    await firstRow.locator('> .navigation-tree-row > .navigation-tree-label').click();
    await page.locator('#messages').getByText('Первое обсуждение плагина',{exact:true}).waitFor();
    assert.equal(await page.locator('#messages').getByText('Второе обсуждение плагина',{exact:true}).count(),0,'older chat has its own history');
    await page.evaluate(() => {chatTest.hold=false;chatTest.release();});
    assert.equal(await page.evaluate(id=>RNAssistantWorkspaceChat.selectedSession(id),sourceWorkspace),firstSession,'late reply cannot replace the selected history session');
    await firstRow.hover();
    await firstRow.getByRole('button',{name:'Открыть чат в плагине',exact:true}).click();
    await page.locator('#messages').getByText('Первое обсуждение плагина',{exact:true}).waitFor();
    assert.equal(await page.locator('#chatInput').inputValue(),'Неотправленный черновик первой беседы','returning to a source chat restores its own draft');
    assert.equal(await page.locator('#pluginPreviewMount iframe').getAttribute('data-route-probe'),'same-instance',
      'opening another source chat keeps the live plugin frame');
    await page.locator('#pluginChatHistory').click();
    await page.locator('#messages').getByText('Первое обсуждение плагина',{exact:true}).waitFor();
    await page.locator('[data-plugin-id="data-templates"]').click();
    await page.locator('#messages').getByText('Первое обсуждение плагина',{exact:true}).waitFor();
    assert.equal(await page.locator('#pluginPreviewMount iframe').getAttribute('data-route-probe'),'same-instance',
      'reselecting the current plugin does not reopen its frame');
    // Reopening the same session must also restore the transcript after its DOM was cleared.
    await page.mouse.move(680,500);
    await page.locator('[data-plugin-id="data-templates"]').hover();
    await page.getByRole('button',{name:'Редактировать',exact:true}).waitFor();
    await page.screenshot({animations:'disabled',path:path.join(captures,'plugin-hover-menu.png')});
    await page.getByRole('button',{name:'Редактировать',exact:true}).click();
    await page.locator('#pluginCreateForm').waitFor({state:'visible'});
    assert.equal(await page.locator('#pluginTask').isVisible(),false,'appearance editor contains only relevant fields');
    await page.locator('#pluginName').fill('Отчёты команды');
    await page.locator('#pluginIconColorMode').selectOption('custom');
    await page.locator('#pluginIconColor').fill('#db593d');
    await page.locator('#pluginIconTrigger').click();
    await page.locator('#pluginBuiltinIcons [data-icon-id="chart"]').click();
    await page.locator('#pluginIconTrigger').click();
    await page.screenshot({animations:'disabled',path:path.join(captures,'plugin-edit.png')});
    await page.locator('#pluginIconTrigger').click();
    await page.locator('#pluginCreateSubmit').click();
    await page.locator('[data-plugin-id="data-templates"][aria-label="Отчёты команды"]').waitFor();
    const saved=await page.evaluate(async () => (await send('getPlugins',{})).items.find(item=>item.pluginId==='data-templates'));
    assert.equal(saved.name,'Отчёты команды'); assert.equal(saved.icon,'chart'); assert.equal(saved.iconColor,'#db593d');
    assert.equal(await page.locator('[data-plugin-id="data-templates"] svg').evaluate(node=>getComputedStyle(node).color),'rgb(219, 89, 61)');
    assert.deepEqual(await page.locator('[data-plugin-id="data-templates"] svg path').getAttribute('d'),await page.evaluate(()=>{const node=document.createElement('div');node.innerHTML=RNAssistantIcons.render('chart');return node.querySelector('path').getAttribute('d');}),'built-in Data Templates also respects its edited icon');
    await page.locator('#pluginAdd').click();
    await page.locator('#pluginName').fill('Цветной SVG');
    assert.equal(await page.locator('#pluginIconColorMode').inputValue(),'theme','new plugins default to a neutral theme color');
    assert.equal(await page.locator('#pluginIconPreview svg').evaluate(node=>getComputedStyle(node).color),
      await page.locator('.plugin-create-eyebrow').evaluate(node=>getComputedStyle(node).color),'built-in icon defaults to the neutral theme color too');
    await page.locator('#pluginIconTrigger').click();
    await page.locator('.plugin-icon-import summary').click();
    await page.locator('#pluginIconImportName').fill('Два цвета');
    const artwork='<svg xmlns="http://www.w3.org/2000/svg" width="480" height="240" viewBox="0 0 48 24"><g transform="translate(0 0)"><rect width="24" height="24" fill="#ed3322"/><rect x="24" width="24" height="24" fill="#2266dd"/></g>'+ '<circle cx="1" cy="1" r=".2" fill="#ed3322"/>'.repeat(24)+' '.repeat(5000)+'</svg>';
    await page.locator('#pluginIconImportSvg').fill(artwork);
    await page.locator('#pluginIconImportButton').click();
    await page.locator('#pluginIconPreview .plugin-icon-tint').waitFor();
    await page.screenshot({animations:'disabled',path:path.join(captures,'plugin-color-theme.png')});
    await page.locator('#pluginIconColorMode').selectOption('original');
    const picture=page.locator('#pluginIconPreview img');
    await picture.waitFor();
    const pixels=await picture.evaluate(async image=>{await image.decode();const canvas=document.createElement('canvas');canvas.width=canvas.height=24;const context=canvas.getContext('2d');context.drawImage(image,0,0,24,24);return [Array.from(context.getImageData(6,12,1,1).data),Array.from(context.getImageData(18,12,1,1).data)];});
    assert.deepEqual(pixels,[[237,51,34,255],[34,102,221,255]],'SVG image preserves both source colors and fits a non-square viewBox');
    assert.equal(await picture.evaluate(node=>node.getBoundingClientRect().width),32,'header icon preview uses a common 32px size');
    await page.screenshot({animations:'disabled',path:path.join(captures,'plugin-color-original.png')});
    await page.locator('#pluginIconColorMode').selectOption('custom');
    await page.locator('#pluginIconColor').fill('#7149c6');
    assert.equal(await page.locator('#pluginIconPreview .plugin-icon-tint').evaluate(node=>getComputedStyle(node).backgroundColor),'rgb(113, 73, 198)');
    await page.screenshot({animations:'disabled',path:path.join(captures,'plugin-color-picker.png')});
    await page.locator('#pluginCreateSubmit').click();
    const newRail=page.locator('#pluginRail [aria-label="Цветной SVG"]');await newRail.waitFor();
    assert.equal(await newRail.locator('.plugin-icon-tint').evaluate(node=>getComputedStyle(node).backgroundColor),'rgb(113, 73, 198)','creation retains color in the rail');
    await newRail.hover();await page.getByRole('button',{name:'Редактировать',exact:true}).click();
    assert.equal(await page.locator('#pluginIconColor').inputValue(),'#7149c6','editing restores the chosen color');
    await page.locator('#pluginIconColorMode').selectOption('original');await page.locator('#pluginCreateSubmit').click();
    await newRail.locator('img').waitFor();
    const restoredIcon=await page.evaluate(async()=> (await send('getPlugins',{})).items.find(item=>item.name==='Цветной SVG'));
    assert.equal(restoredIcon.iconColor,'original','original SVG colors can be restored without redrawing');
    assert.match(restoredIcon.iconSvg,/#ed3322/);assert.match(restoredIcon.iconSvg,/#2266dd/);
    await newRail.hover();await page.getByRole('button',{name:'Редактировать',exact:true}).click();
    assert.equal(await page.locator('#pluginIconColorMode').inputValue(),'original','editing restores the original palette mode');
    await page.locator('#pluginIconColorMode').selectOption('theme');await page.locator('#pluginCreateSubmit').click();
    await newRail.locator('.plugin-icon-tint').waitFor();
    const themedIcon=await page.evaluate(async()=> (await send('getPlugins',{})).items.find(item=>item.name==='Цветной SVG'));
    assert.equal(themedIcon.iconColor,null,'theme color is the default stored appearance');
    const previousTheme=await page.locator('html').getAttribute('data-theme'), themeColors=[];
    for(const theme of ['light','dark']){
      await page.evaluate(theme=>{document.documentElement.dataset.theme=theme;},theme);
      const colors=await newRail.locator('.plugin-icon-tint').evaluate(node=>{
        const probe=document.createElement('span');probe.style.color='var(--ink-muted)';document.body.appendChild(probe);
        const colors=[getComputedStyle(node).backgroundColor,getComputedStyle(probe).color];probe.remove();return colors;
      });
      assert.equal(colors[0],colors[1],'neutral SVG follows the current theme');themeColors.push(colors[0]);
    }
    assert.notEqual(themeColors[0],themeColors[1],'light and dark themes use their respective neutral colors');
    await page.evaluate(theme=>{document.documentElement.dataset.theme=theme;},previousTheme);
    assert.deepEqual(errors,[],'no browser errors');
    console.log('PASS shared chat: message/composer parity, drafts, failed-send retry, mode/reasoning, narrow layout, plugin session history/navigation, plugin appearance editing');
    console.log('Screenshots: '+captures);
  } finally { await browser.close(); server.kill(); }
})().catch(error=>{console.error(error);process.exitCode=1;});
