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
            RunId:'preview-run',Kind:'tool',Title:'core.read',ToolId:'core.read',
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
    await page.evaluate(() => { chatTest.fail = false; });
    await page.locator('#chatInput').press('Enter');
    await page.waitForFunction(() => document.querySelector('#chatInput').value === '');
    assert.equal(await page.evaluate(() => chatTest.requests.length), 2,
      'the same project composer retries once and clears only after its receipt');
    await page.evaluate(() => RNAssistantPlugins.select('data-templates'));
    const pluginToggle=page.locator('#workspaceInspectorToggle');
    await page.waitForFunction(() => RNAssistantWorkspaceChat.owner()==='plugin' &&
      document.querySelector('#workspaceChatView').parentElement.id==='projectChatLayout');
    assert.equal(await page.locator('#tab-chat').getAttribute('class').then(value=>value.includes('active')),true,
      'plugin opens its source project in the common chat screen');
    assert.equal(await page.evaluate(() => RNAssistantShellLayout.isNavigationOpen()),true,
      'the project and chat tree remains open');
    assert.equal(await page.locator('#pluginRail [data-plugin-id="data-templates"]').getAttribute('aria-current'),'page',
      'the plugin icon identifies the selected project shell');
    assert.equal(await pluginToggle.getAttribute('aria-controls'),'pluginPreviewMount');
    assert.equal(await pluginToggle.getAttribute('aria-expanded'),'true');
    assert.equal(await pluginToggle.evaluate(node=>node.parentElement.classList.contains('workspace-chat-head')),true,
      'on a wide screen the panel button stays at the right edge of the central chat header');
    const sourceWorkspace='preview-plugin-workspace';
    await page.locator('[data-tree-key="project:'+sourceWorkspace+'"]').waitFor();
    assert.equal(await page.locator('#projectChatTree').isVisible(),true);
    assert.deepEqual(await page.evaluate(() => [document.querySelector('#messages').parentElement.id,
      document.querySelector('#messages').dataset.testIdentity]), ['workspaceChatTranscriptMount',transcriptIdentity],
      'plugin uses the same physical transcript as Office and project chats');
    assert.deepEqual(await dimensions('#chatForm','#messages'),office,'plugin uses shared message and composer metrics');
    await page.frameLocator('#pluginPreviewMount iframe').locator('#dtViewSources h3').waitFor();
    const frame=page.locator('#pluginPreviewMount iframe');
    await frame.evaluate(node=>{node.dataset.routeProbe='same-instance';});
    await page.locator('#pluginNavigationToggle').click();
    assert.equal(await page.locator('#pluginNavigationPane').isVisible(),true,'plugin sections open inside the preview');
    await page.locator('#pluginNavigationToggle').click();
    await page.screenshot({animations:'disabled',path:path.join(captures,'chat-plugin.png')});
    const split=page.locator('#pluginSurfaceSplitter'), before=Number(await split.getAttribute('aria-valuenow'));
    await split.press('ArrowLeft');
    assert.ok(Number(await split.getAttribute('aria-valuenow'))>before,'the right preview width can be changed');
    await page.setViewportSize({width:1080,height:900});
    await page.waitForFunction(()=>!document.querySelector('#projectChatLayout').classList.contains('plugin-surface-overlay') &&
      Number(document.querySelector('#pluginSurfaceSplitter').getAttribute('aria-valuemax'))<500);
    await page.waitForFunction(()=>!document.querySelector('#projectChatLayout').classList.contains('inspector-transition'));
    assert.equal(await split.isVisible(),true,'the preview divider stays available when chat and preview both fit');
    const paneBefore=await page.locator('#pluginWorkbench').evaluate(node=>node.getBoundingClientRect().width);
    const dragStart=Number(await split.getAttribute('aria-valuenow')), dragBox=await split.boundingBox();
    await page.mouse.move(dragBox.x+dragBox.width/2,dragBox.y+dragBox.height/2);
    await page.mouse.down();
    await page.mouse.move(dragBox.x+dragBox.width/2+40,dragBox.y+dragBox.height/2,{steps:4});
    await page.mouse.up();
    await page.waitForFunction(value=>Number(document.querySelector('#pluginSurfaceSplitter').getAttribute('aria-valuenow'))<value,dragStart);
    await page.waitForFunction(width=>document.querySelector('#pluginWorkbench').getBoundingClientRect().width<width-25,paneBefore);
    await page.setViewportSize({width:1920,height:900});
    await page.waitForFunction(()=>Number(document.querySelector('#pluginSurfaceSplitter').getAttribute('aria-valuemax'))>780);
    await split.press('End');
    await page.waitForFunction(()=>document.querySelector('#pluginWorkbench').getBoundingClientRect().width>780);
    assert.ok(await page.locator('#workspaceChatView').evaluate(node=>node.getBoundingClientRect().width)>=280,
      'wide previews can stretch past the former 780px cap while keeping the chat usable');
    await split.press('Enter');
    await page.setViewportSize({width:1360,height:900});
    await page.evaluate(()=>RNAssistantShellLayout.setNavigationOpen(false));
    assert.equal(await split.isVisible(),true,'preview divider remains usable with the project tree collapsed');
    await page.evaluate(()=>RNAssistantShellLayout.setNavigationOpen(true));
    await pluginToggle.click();
    assert.equal(await page.locator('#pluginWorkbench').isVisible(),false,'the common panel button closes the preview');
    assert.equal(await page.locator('#workspaceChatView').isVisible(),true,'closing preview keeps the chat');
    const selectedSource=await page.evaluate(id=>RNAssistantWorkspaceChat.selectedSession(id),sourceWorkspace);
    await page.locator('[data-tree-key="project-chat:'+sourceWorkspace+':'+selectedSource+'"] .navigation-tree-label').click();
    await page.waitForFunction(()=>RNAssistantWorkspaceChat.owner()==='plugin');
    assert.equal(await page.locator('#pluginWorkbench').isVisible(),false,
      'selecting a source chat does not reopen a closed preview');
    await pluginToggle.click();
    assert.equal(await frame.getAttribute('data-route-probe'),'same-instance','reopening keeps the live plugin frame');
    const firstSession=await page.evaluate(id=>RNAssistantWorkspaceChat.selectedSession(id),sourceWorkspace);
    await page.locator('#chatInput').fill('Черновик первой беседы');
    await page.locator('#projectChatNew').click();
    await page.locator('#projectChatTitle').getByText('Новый чат').waitFor();
    const secondSession=await page.evaluate(id=>RNAssistantWorkspaceChat.selectedSession(id),sourceWorkspace);
    assert.notEqual(secondSession,firstSession,'the common project button creates a separate source chat');
    assert.equal(await page.locator('#chatInput').inputValue(),'');
    await page.locator('[data-tree-key="project-chat:'+sourceWorkspace+':'+firstSession+'"] .navigation-tree-label').click();
    await page.waitForFunction(session=>RNAssistantWorkspaceChat.selectedSession('preview-plugin-workspace')===session,firstSession);
    assert.equal(await page.locator('#chatInput').inputValue(),'Черновик первой беседы');
    assert.equal(await frame.getAttribute('data-route-probe'),'same-instance','switching source chats keeps the preview');
    const deletedRow=page.locator('[data-tree-key="project-chat:'+sourceWorkspace+':'+firstSession+'"]');
    page.once('dialog',dialog=>dialog.accept());
    await deletedRow.hover();
    await deletedRow.getByRole('button',{name:/Удалить чат/}).click();
    await page.waitForFunction(session=>RNAssistantWorkspaceChat.selectedSession('preview-plugin-workspace')===session,secondSession);
    assert.equal(await frame.getAttribute('data-route-probe'),'same-instance',
      'deleting the selected chat opens another source chat without replacing the preview');
    await page.locator('#projectChatTree [data-tree-key="project:preview-plan-workspace"] > .navigation-tree-row > .navigation-tree-label').click();
    await page.waitForFunction(()=>RNAssistantWorkspaceChat.owner()==='project');
    assert.equal(await page.locator('#pluginWorkbench').isVisible(),false,'another project hides the plugin preview');
    assert.equal(await page.locator('.section-rail [data-section="chat"]').getAttribute('aria-current'),'page',
      'the ordinary project restores the workspace rail selection');
    await page.locator('[data-tree-key="project-chat:'+sourceWorkspace+':'+secondSession+'"] .navigation-tree-label').click();
    await page.waitForFunction(()=>RNAssistantWorkspaceChat.owner()==='plugin');
    assert.equal(await page.locator('#pluginWorkbench').isVisible(),true,
      'opening a plugin source chat from Projects restores its HTML preview without the plugin icon');
    assert.equal(await page.locator('#projectChatTree').isVisible(),true);
    await page.setViewportSize({width:390,height:844});
    await page.waitForFunction(()=>document.querySelector('#pluginWorkbench').getBoundingClientRect().width===innerWidth);
    assert.equal(await pluginToggle.evaluate(node=>node.parentElement.classList.contains('preview-surface-panel-head')),true,
      'the same button remains reachable when the preview covers the narrow chat');
    await page.screenshot({animations:'disabled',path:path.join(captures,'chat-plugin-narrow.png')});
    assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true,'plugin preview fits the narrow viewport');
    await page.setViewportSize({width:1360,height:900});
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
