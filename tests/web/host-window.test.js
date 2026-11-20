'use strict';
// Native state is synthetic; real production Shell, Dockview, lifecycle and chat instances.
const assert = require('node:assert/strict'), path = require('node:path'), fs = require('node:fs');
const {spawn} = require('node:child_process'), {chromium} = require('playwright');
function assertPlacement(actual, expected) {
  function compareSizes(node, saved) {
    if (Number.isFinite(saved?.size)) {
      assert.ok(Math.abs(node.size - saved.size) <= 1, 'Dockview placement: ' + node.size + ' px instead of ' + saved.size + ' px');
      node.size = saved.size;
    }
    if (node.type === 'branch' && saved?.type === 'branch') node.data.forEach((child, index) => compareSizes(child, saved.data[index]));
  }
  compareSizes(actual.layout.grid.root, expected.layout.grid.root);
  assert.deepEqual(actual, expected, 'full placement and active panel are retained');
}
(async () => {
  const server = spawn('python3', ['-B', 'prototypes/serve_ui.py', '--port', '0'], {cwd: path.resolve(__dirname, '../..')});
  server.stderr.resume(); let browser;
  try {
    const address = await new Promise((resolve, reject) => {
      server.once('error', reject); server.stdout.on('data', bytes => { const match = /http:\/\/127\.0\.0\.1:\d+\//.exec(String(bytes)); if (match) resolve(match[0]); });
    });
    browser = await chromium.launch({headless: true, ...(process.env.BROWSER_EXECUTABLE ? {executablePath: process.env.BROWSER_EXECUTABLE} : {})});
    const page = await browser.newPage({viewport: {width: 1280, height: 900}, reducedMotion: 'reduce'}), errors = [];
    page.setDefaultTimeout(10000); page.on('pageerror', e => errors.push(e.message));
    await page.goto(address + 'preview/web/?scenario=ready');
    await page.waitForFunction(() => !state.initializePromise && RNAssistantConversationPanel.office('preview-chat'));
    assert.equal(await page.locator('#hostWindowControls').isVisible(), false, 'unsupported hosts keep native controls hidden');
    assert.ok(await page.locator('#workspaceLayout').isVisible(), 'other hosts retain the layout entry');
    assert.equal(await page.locator('#workspaceLayout').textContent(), '', 'layout uses an accessible icon without a long toolbar label');
    await page.evaluate(() => {
      const chat = RNAssistantConversationPanel.office('preview-chat');
      RNAssistantConversationPanel.open(chat.route);
      window.windowProbe = {chat, input: chat.node('chatInput'), messages: chat.node('messages'), disposed: 0, activates: 0, deactivates: 0, calls: [], frameLoads: 0};
      RNAssistantDockWorkspace.registerView('window-probe', {perObject: true, maximumInstances: 1, accepts: t => t.view === 'window-probe',
        key: () => 'window-probe', titleFor: () => 'Retained probe', create: () => {
          const root = document.createElement('div'), surface = document.createElement('div'), frame = document.createElement('iframe');
          root.style.height = '100%'; frame.title = 'Retained input'; frame.id = 'windowProbeFrame'; frame.sandbox = 'allow-scripts';
          frame.addEventListener('load', () => windowProbe.frameLoads++); frame.srcdoc = '<input aria-label="Retained input" value="initial">'; surface.appendChild(frame);
          const viewer = RNAssistantViewerRegistry.mount('browser', root, {surface, portalHost: document.querySelector('.shell')});
          return {element: root, layout: viewer.layout,
            activate() { windowProbe.activates++; viewer.activate(); }, deactivate() { windowProbe.deactivates++; viewer.deactivate(); },
            dispose() { windowProbe.disposed++; RNAssistantViewerRegistry.unmount(root); }};
        }});
      RNAssistantDockWorkspace.openTarget({view: 'window-probe'}, {beside: true});
      windowProbe.object = RNAssistantDockWorkspace.getView('window-probe');
      const original = send;
      const current = {revision: 1, presentation: 'FullNormal', visibility: 'Visible', pinned: false, autoCollapse: true,
        chromeReady: false, anchorAvailable: true, canRestore: false, canMaximize: true, canCompact: true};
      windowProbe.state = current;
      windowProbe.set = (presentation, visibility = 'Visible') => {
        Object.assign(current, {revision: current.revision + 1, presentation, visibility, canRestore: presentation === 'FullMaximized'});
        RNAssistantWindow.apply({...current});
      };
      window.send = async (type, payload, ...rest) => {
        if (type === 'getHostWindowState') return {state: {...current}};
        if (type !== 'hostWindowCommand') return original(type, payload, ...rest);
        windowProbe.calls.push({...payload});
        if (payload.command === 'ChromeReady') current.chromeReady = true;
        if (payload.command === 'ShowFull') current.presentation = 'FullNormal';
        if (payload.command === 'ShowCompact') current.presentation = 'Compact';
        if (payload.command === 'Maximize') { current.presentation = 'FullMaximized'; current.canRestore = true; current.canMaximize = false; }
        if (payload.command === 'Restore') { current.presentation = 'FullNormal'; current.canRestore = false; current.canMaximize = true; }
        if (payload.command === 'Shutdown' || payload.command === 'SwitchDocument') current.visibility = 'ShuttingDown';
        if (payload.command === 'SetPinned') current.pinned = payload.value;
        if (payload.command === 'SetAutoCollapse') current.autoCollapse = payload.value;
        current.revision++; return {accepted: true, state: {...current}};
      };
      RNAssistantWindow.bootstrap({capabilities: {integratedChrome: true, nativeDragRegions: true, compactPresentation: true, companion: true, pin: true}, state: {...current}});
    });
    await page.waitForFunction(() => document.documentElement.classList.contains('integrated-window-chrome'));
    assert.ok(await page.evaluate(() => document.documentElement.classList.contains('native-window-drag')));
    assert.equal(await page.locator('#workspaceLayout').isVisible(), false, 'native layout is exposed once through the window menu');
    assert.deepEqual(await page.locator('.workspace-topbar button:visible').evaluateAll(buttons => buttons.map(button => button.id)), [
      'productMobileMenuButton','toggleChatInspectorButton','hostWindowMenu','hostWindowPin','hostWindowPresentation','hostWindowMaximize','hostWindowClose'
    ]);
    assert.ok(await page.locator('#productMobileMenuButton + .workspace-context').count(), 'navigation sits before the title');
    for (const id of ['productMobileMenuButton', 'toggleChatInspectorButton', 'hostWindowPin', 'hostWindowPresentation', 'hostWindowMaximize'])
      assert.ok(await page.locator('#' + id).isVisible(), 'direct native toolbar control is visible: ' + id);
    const presentationButton = page.locator('#hostWindowPresentation');
    const compactIcon = await presentationButton.innerHTML();
    assert.equal(await presentationButton.getAttribute('aria-label'), 'Компактное окно');
    await page.locator('#hostWindowMaximize').click();
    await page.waitForFunction(() => windowProbe.state.presentation === 'FullMaximized');
    assert.equal(await page.locator('#hostWindowMaximize').getAttribute('aria-label'), 'Восстановить размер окна');
    await page.locator('#hostWindowMaximize').click();
    await page.waitForFunction(() => windowProbe.state.presentation === 'FullNormal');
    await page.locator('#hostWindowPin').click();
    await page.waitForFunction(() => windowProbe.state.pinned === true);
    assert.equal(await page.locator('#hostWindowPin').getAttribute('aria-pressed'), 'true');
    await page.locator('#hostWindowPin').click();
    await page.waitForFunction(() => windowProbe.state.pinned === false);
    await page.locator('#hostWindowMenu').click();
    for (const name of ['Навигация', 'Инспектор', 'Полное окно', 'Развернуть окно', 'Оставлять окно открытым', 'Открыть…'])
      assert.equal(await page.getByRole('menuitem', {name, exact: true}).count(), 0, 'wide menu omits direct action: ' + name);
    assert.equal(await page.locator('.host-window-menu .rna-action-menu-info, .host-window-menu small').count(), 0, 'no raw status or second detail rows');
    assert.equal(await page.locator('#hostWindowMenu').getAttribute('title'), 'Вид и окно');
    await page.keyboard.press('Escape');
    assert.equal(await page.evaluate(() => document.activeElement.id), 'hostWindowMenu');
    // Older WebView2 still gets the same controls; only header dragging uses the bridge.
    await page.evaluate(() => {
      windowProbe.state.revision++;
      RNAssistantWindow.bootstrap({capabilities: {integratedChrome: true, nativeDragRegions: false,
        compactPresentation: true, companion: true, pin: true}, state: {...windowProbe.state}});
    });
    assert.ok(await page.locator('#hostWindowClose').isVisible());
    assert.equal(await page.evaluate(() => document.documentElement.classList.contains('native-window-drag')), false);
    const header = await page.locator('.workspace-context').boundingBox();
    await page.mouse.move(header.x + 12, header.y + header.height / 2);
    await page.mouse.down(); await page.mouse.move(header.x + 30, header.y + header.height / 2);
    await page.waitForFunction(() => windowProbe.calls.filter(call => call.command === 'BeginMove').length === 1);
    await page.mouse.move(header.x + 40, header.y + header.height / 2); await page.mouse.up();
    await page.locator('#hostWindowClose').dispatchEvent('pointerdown', {button: 0, buttons: 1, clientX: 10, clientY: 10});
    await page.locator('#hostWindowClose').dispatchEvent('pointermove', {buttons: 1, clientX: 30, clientY: 10});
    assert.equal(await page.evaluate(() => windowProbe.calls.filter(call => call.command === 'BeginMove').length), 1, 'one drag request, buttons never initiate moving');
    await page.evaluate(() => {
      windowProbe.state.revision++;
      RNAssistantWindow.bootstrap({capabilities: {integratedChrome: true, nativeDragRegions: true,
        compactPresentation: true, companion: true, pin: true}, state: {...windowProbe.state}});
    });
    await page.evaluate(() => RNAssistantDockWorkspace.show('inspector'));
    await page.locator('#chatInspectorPanelPin').click();
    await page.evaluate(() => RNAssistantDockWorkspace.openTarget({view: 'window-probe'}));
    await page.frameLocator('#windowProbeFrame').getByRole('textbox').fill('iframe retained');
    await page.waitForTimeout(200); // existing layout persistence debounce
    const before = await page.evaluate(() => ({layout: localStorage.getItem('rnassistant.ui.dock.s1.v1'), panels: document.querySelectorAll('[data-tab-panel-id]').length,
      selected: RNAssistantConversationPanel.selected() === windowProbe.chat}));
    assert.ok(before.selected, 'non-chat active panel retains selected exact chat');
    await page.evaluate(() => {
      windowProbe.input.value = 'Черновик сохраняется'; windowProbe.input.dispatchEvent(new Event('input', {bubbles: true}));
      windowProbe.edit = windowProbe.chat.composer.startEdit(windowProbe.chat.target, {messageId: 'edit-probe'});
    });
    await page.locator('#hostWindowPresentation').click();
    await page.waitForFunction(() => document.documentElement.dataset.hostPresentation === 'Compact');
    await page.setViewportSize({width: 420, height: 520});
    await page.evaluate(() => document.documentElement.style.zoom = '1.25');
    await page.waitForTimeout(100);
    const compact = await page.evaluate(() => ({same: RNAssistantConversationPanel.office('preview-chat') === windowProbe.chat,
      input: windowProbe.input.isConnected, value: windowProbe.input.value, disposed: windowProbe.disposed,
      object: RNAssistantDockWorkspace.getView('window-probe') === windowProbe.object,
      layout: localStorage.getItem('rnassistant.ui.dock.s1.v1'), visible: RNAssistantDockWorkspace.isVisible('window-probe'),
      overflow: document.documentElement.scrollWidth > innerWidth}));
    assert.ok(compact.same && compact.input && compact.object && !compact.visible && !compact.overflow);
    assert.equal(compact.value, 'Черновик сохраняется'); assert.equal(compact.disposed, 0);
    assert.ok(await page.evaluate(() => windowProbe.edit && windowProbe.chat.composer.edit(windowProbe.chat.target) === windowProbe.edit), 'edit state remains owned by the same composer');
    assert.equal(compact.layout, before.layout, 'transient placement never overwrites full layout');
    for (const id of ['productMobileMenuButton', 'toggleChatInspectorButton', 'hostWindowPin', 'hostWindowMaximize',
      'hostWindowMenu', 'hostWindowPresentation', 'hostWindowClose']) assert.ok(await page.locator('#' + id).isVisible(), 'Compact keeps direct control: ' + id);
    assert.equal(await page.locator('#hostWindowControls button').count(), 4);
    assert.equal(await presentationButton.getAttribute('aria-label'), 'Вернуть полное окно');
    assert.notEqual(await presentationButton.innerHTML(), compactIcon, 'Compact uses a different restore icon');
    await page.locator('#hostWindowMenu').click();
    for (const name of ['Полное окно', 'Навигация', 'Инспектор', 'Развернуть окно', 'Оставлять окно открытым'])
      assert.equal(await page.getByRole('menuitem', {name, exact: true}).count(), 0, 'Compact menu omits direct action: ' + name);
    assert.ok(await page.getByRole('menuitem', {name: 'Открыть…', exact: true}).isVisible());
    await page.keyboard.press('Escape');
    await page.locator('#hostWindowPin').click();
    await page.waitForFunction(() => windowProbe.state.pinned === true);
    assert.equal(await page.locator('#hostWindowPin').getAttribute('aria-pressed'), 'true', 'pin is stateful on the toolbar');
    await page.locator('#hostWindowMenu').click();
    await page.getByRole('menuitem', {name: 'Настройки окна…', exact: true}).click();
    const autoCollapse = page.getByRole('menuitemcheckbox', {name: 'Сворачивать при смене фокуса', exact: true});
    assert.ok(await autoCollapse.isDisabled());
    assert.equal(await autoCollapse.getAttribute('aria-checked'), 'false', 'pin suppresses the effective auto-collapse state');
    assert.equal(await page.evaluate(() => windowProbe.state.autoCollapse), true, 'pin preserves the saved auto-collapse preference');
    await page.getByRole('menuitem', {name: 'Назад', exact: true}).click();
    await page.keyboard.press('Escape');
    await page.locator('#hostWindowMenu').focus(); await page.keyboard.press('Tab');
    assert.equal(await page.evaluate(() => document.activeElement.id), 'hostWindowPin', 'keyboard follows the visual order');
    await page.evaluate(() => RNAssistantWindow.apply({...windowProbe.state, revision: 1, pinned: false}));
    assert.equal(await page.evaluate(() => windowProbe.state.pinned), true, 'stale push cannot undo a command');
    if (process.env.RNA_WINDOW_REVIEW) await page.screenshot({path: process.env.RNA_WINDOW_REVIEW + '-compact.png'});
    await page.evaluate(() => windowProbe.set('Companion', 'ContextHidden'));
    assert.equal(await page.evaluate(() => windowProbe.chat.view.canRender()), false, 'native hiding deactivates chat');
    await page.evaluate(() => windowProbe.set('Compact'));
    await page.setViewportSize({width: 1280, height: 900});
    await page.evaluate(() => document.documentElement.style.zoom = '1');
    await presentationButton.click();
    await page.waitForFunction(() => document.documentElement.dataset.hostPresentation === 'FullNormal');
    assert.equal(await presentationButton.innerHTML(), compactIcon);
    assert.equal(await page.evaluate(() => windowProbe.calls.some(call => call.command === 'Park')), false);
    await page.waitForTimeout(250);
    assertPlacement(await page.evaluate(() => JSON.parse(localStorage.getItem('rnassistant.ui.dock.s1.v1'))), JSON.parse(before.layout));
    assert.equal(await page.evaluate(() => windowProbe.disposed), 0);
    assert.equal(await page.evaluate(() => windowProbe.frameLoads), 1, 'retained Browser iframe is not reloaded');
    assert.equal(await page.frameLocator('#windowProbeFrame').getByRole('textbox').inputValue(), 'iframe retained');
    // Native state and WM_SIZE are independent deliveries: Full may arrive before the new width.
    await page.evaluate(() => windowProbe.set('Compact'));
    await page.setViewportSize({width: 420, height: 520});
    await page.evaluate(() => windowProbe.set('FullNormal'));
    assert.ok(await page.locator('#productMobileMenuButton').isVisible(), 'narrow Full keeps navigation in the toolbar');
    assert.ok(await page.locator('#toggleChatInspectorButton').isVisible(), 'narrow Full keeps Inspector in the toolbar');
    await page.setViewportSize({width: 1280, height: 900});
    await page.waitForTimeout(250);
    assertPlacement(await page.evaluate(() => JSON.parse(localStorage.getItem('rnassistant.ui.dock.s1.v1'))), JSON.parse(before.layout));
    await page.evaluate(() => windowProbe.set('Compact'));
    await page.locator('#hostWindowMenu').click();
    await page.getByRole('menuitem', {name: 'Открыть…', exact: true}).click();
    await page.waitForFunction(() => document.documentElement.dataset.hostPresentation === 'FullNormal');
    await page.locator('.dock-screen-chooser input').waitFor();
    await page.locator('[data-tab-panel-id^="choose-"]').getByRole('button').click();
    await page.locator('#hostWindowMenu').click();
    await page.getByRole('menuitem', {name: 'Расположение панелей…', exact: true}).click();
    await page.getByRole('menuitem', {name: 'Сохранить раскладку', exact: true}).waitFor();
    await page.keyboard.press('Escape');
    // Opening a complex tool from Compact goes through the native Full command first.
    await page.evaluate(() => windowProbe.set('Compact'));
    await page.evaluate(() => RNAssistantDockWorkspace.show('settings'));
    await page.waitForFunction(() => document.documentElement.dataset.hostPresentation === 'FullNormal');
    assert.ok(await page.locator('[data-tab-panel-id="settings"]').isVisible());
    await page.evaluate(() => windowProbe.set('FullMaximized'));
    assert.equal(await page.locator('#hostWindowMaximize').getAttribute('aria-label'), 'Восстановить размер окна');
    await page.evaluate(() => windowProbe.set('FullNormal'));
    await page.evaluate(() => { RNAssistantDockWorkspace.close('settings'); RNAssistantDockWorkspace.close('window-probe'); });
    if (process.env.RNA_WINDOW_REVIEW) await page.screenshot({path: process.env.RNA_WINDOW_REVIEW + '-full.png'});
    const demo = await browser.newPage({viewport: {width: 1280, height: 900}, reducedMotion: 'reduce'});
    await demo.goto(address);
    const reference = await demo.evaluate(() => ({header: document.querySelector('.topbar').getBoundingClientRect().height,
      control: document.querySelector('#maximize-toggle').getBoundingClientRect().height}));
    const measured = await page.evaluate(() => ({header: document.querySelector('.workspace-topbar').getBoundingClientRect().height,
      control: document.querySelector('#hostWindowClose').getBoundingClientRect().height}));
    assert.deepEqual(measured, reference, 'window controls use Demo header/control geometry at the same viewport');
    if (process.env.RNA_WINDOW_REVIEW) fs.writeFileSync(process.env.RNA_WINDOW_REVIEW + '.json', JSON.stringify({viewport: '1280x900 / 100%', reference, measured}, null, 2));
    await demo.close();
    await page.locator('#hostWindowClose').click();
    await page.getByRole('dialog').getByRole('button', {name: 'Закрыть RNAssistant', exact: true}).click();
    await page.getByRole('alert').filter({hasText: 'Сначала завершите или отмените редактирование'}).waitFor();
    assert.equal(await page.evaluate(() => windowProbe.calls.some(call => call.command === 'Shutdown')), false, 'dirty message edit blocks destructive shutdown');
    await page.getByRole('dialog').getByRole('button', {name: 'Закрыть', exact: true}).click();
    await page.evaluate(() => {
      windowProbe.chat.composer.cancelEdit(windowProbe.chat.target);
      windowProbe.flushOffice = RNAssistantChatDrafts.flushOffice;
      RNAssistantChatDrafts.flushOffice = async () => { throw new Error('Draft save failed'); };
      RNAssistantWindow.switchDocument();
    });
    await page.getByRole('dialog').getByRole('button', {name: 'Переключить', exact: true}).click();
    await page.getByRole('alert').filter({hasText: 'Draft save failed'}).waitFor();
    assert.equal(await page.evaluate(() => windowProbe.calls.some(call => call.command === 'SwitchDocument')), false, 'failed draft write never switches document session');
    await page.getByRole('dialog').getByRole('button', {name: 'Закрыть', exact: true}).click();
    await page.evaluate(() => { RNAssistantChatDrafts.flushOffice = windowProbe.flushOffice; });
    assert.equal(await page.evaluate(() => windowProbe.chat === RNAssistantConversationPanel.office('preview-chat')), true);
    await page.evaluate(() => {
      windowProbe.set('Compact');
      RNAssistantConversationPanel.closeDeleted(windowProbe.chat.target);
    });
    await page.waitForTimeout(50);
    assert.ok(await page.locator('.host-compact-empty').isVisible(), 'deleted/absent exact chat has a Full entry, no automatic creation');
    await page.evaluate(() => windowProbe.set('FullNormal'));
    assert.ok(await page.evaluate(() => !RNAssistantConversationPanel.office('preview-chat')), 'Full cannot resurrect a deleted identity');
    const cold = await browser.newPage({viewport: {width: 420, height: 520}});
    await cold.goto(address + 'preview/web/?scenario=ready');
    await cold.waitForFunction(() => !state.initializePromise && RNAssistantConversationPanel.office('preview-chat'));
    await cold.evaluate(() => {
      state.initializePromise = new Promise(resolve => { window.finishWindowInit = resolve; });
      const original = send, current = {revision: 1, presentation: 'Compact', visibility: 'Visible', chromeReady: false};
      window.send = async (type, payload, ...rest) => {
        if (type !== 'hostWindowCommand') return original(type, payload, ...rest);
        current.chromeReady = payload.command === 'ChromeReady'; current.revision++;
        return {accepted: true, state: {...current}};
      };
      RNAssistantWindow.bootstrap({capabilities: {integratedChrome: true, nativeDragRegions: false, compactPresentation: true, companion: true, pin: true}, state: {...current}});
    });
    assert.equal(await cold.evaluate(() => document.documentElement.dataset.hostPresentation), 'FullNormal', 'saved Compact waits for existing initial chat selection');
    await cold.waitForFunction(() => document.documentElement.classList.contains('integrated-window-chrome'));
    assert.ok(await cold.locator('#hostWindowClose').isVisible(), 'unsupported drag regions do not suppress controls on startup');
    await cold.evaluate(() => { state.initializePromise = null; finishWindowInit(); });
    await cold.waitForFunction(() => document.documentElement.dataset.hostPresentation === 'Compact');
    assert.ok(await cold.evaluate(() => RNAssistantConversationPanel.office('preview-chat').view.canRender()));
    await cold.setViewportSize({width: 360, height: 320});
    await cold.evaluate(() => { document.documentElement.style.zoom = '1.25'; });
    await cold.waitForTimeout(50);
    assert.equal(await cold.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, 'minimum Compact viewport has no horizontal overflow');
    await cold.close();
    await page.evaluate(() => RNAssistantWindow.switchDocument());
    await page.getByRole('dialog').getByRole('button', {name: 'Переключить', exact: true}).click();
    await page.waitForFunction(() => windowProbe.calls.some(call => call.command === 'SwitchDocument'));
    await page.evaluate(() => windowProbe.set('FullNormal', 'Visible')); // Synthetic replacement host.
    await page.locator('#hostWindowClose').click();
    await page.getByRole('dialog').getByRole('button', {name: 'Закрыть RNAssistant', exact: true}).click();
    await page.waitForFunction(() => windowProbe.calls.some(call => call.command === 'Shutdown'));
    assert.deepEqual(errors, []);
    console.log('PASS host window: capability, same-instance compact/full, native visibility, stale push, keyboard and 420px/125%');
  } finally { if (browser) await browser.close(); server.kill(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
