(function () {
  "use strict";
  const D = window.RnaDemo, E = D.escape, I = D.icon, views = D.views, accents = window.RNAssistantAccents;
  const shell = document.getElementById('shell'), nav = document.getElementById('navigation');
  document.getElementById('workspace-document').textContent = D.fixtures.document;
  document.getElementById('workspace-context').title = 'Документ: ' + D.fixtures.document;
  const storageKey = 'rnassistant.prototype.dockview.p1';
  let preferences = {}, loadNotice = '', saving = false, saveTimer, toastTimer;
  try { const raw = localStorage.getItem(storageKey); if (raw) { const parsed = JSON.parse(raw); if (parsed.version !== 1) throw new Error('version'); preferences = parsed; } }
  catch (_) { loadNotice = 'Сохранённая раскладка недоступна или несовместима. Открыт стартовый вид.'; }
  let section = 'workspace', navOpen = preferences.navOpen !== false, compact = false, narrow = false;
  let navWidth = Math.max(180, Math.min(340, Number(preferences.navWidth) || 224));
  let wideLayout = preferences.wideLayout || null, savedLayout = preferences.savedLayout || null, groupTarget;
  let nextChooser = 1;
  const isChooser = id => /^choose-[1-9]\d*$/.test(id);
  const popup = document.createElement('div');
  popup.id = 'group-menu'; popup.className = 'group-menu'; popup.setAttribute('popover', 'auto');
  popup.innerHTML = '<button class="dock-menu-action" data-group-action="join" title="Перенести вкладку в другую группу">' + I('compress') + '<span>Объединить</span></button><button class="dock-menu-action" data-group-action="close">' + I('close') + '<span>Закрыть вкладку</span></button><hr><button class="dock-menu-action" data-group-action="close-group" title="Закрыть группу вместе со всеми её вкладками">' + I('close') + '<span>Закрыть группу</span></button>';
  document.body.appendChild(popup);

  function toast(message) { const el = document.getElementById('toast'); el.textContent = message; el.hidden = false; clearTimeout(toastTimer); toastTimer = setTimeout(() => { el.hidden = true; }, 4500); }
  function setTheme(value) {
    document.documentElement.dataset.theme = value === 'dark' ? 'dark' : 'light';
    const button = document.getElementById('theme-toggle'), dark = value === 'dark';
    button.innerHTML = I(dark ? 'sun' : 'moon'); button.setAttribute('aria-label', dark ? 'Включить светлую тему' : 'Включить тёмную тему');
    document.querySelectorAll('[data-theme-choice]').forEach(select => { select.value = document.documentElement.dataset.theme; });
    setAccent(document.documentElement.dataset.accent);
  }
  function setAccent(value) {
    const root = document.documentElement, choice = accents.choices.find(item => item.id === value), theme = root.dataset.theme;
    root.dataset.accent = choice ? choice.id : 'default';
    ['--accent', '--accent-text', '--focus', '--surface-selected'].forEach(name => root.style.removeProperty(name));
    if (choice) {
      root.style.setProperty('--accent', choice[theme]);
      root.style.setProperty('--accent-text', accents.foregroundFor(choice[theme]));
      root.style.setProperty('--focus', choice[theme]);
      root.style.setProperty('--surface-selected', 'color-mix(in srgb, var(--accent) 14%, var(--surface))');
    }
    document.querySelectorAll('[data-accent-choice]').forEach(button => {
      const item = accents.choices.find(entry => entry.id === button.dataset.accentChoice);
      button.setAttribute('aria-pressed', String(button.dataset.accentChoice === root.dataset.accent));
      if (item) {
        const label = theme === 'light' && item.lightLabel ? item.lightLabel : item.label;
        button.style.setProperty('--swatch', item[theme]);
        button.querySelector('.accent-label').textContent = label;
        button.setAttribute('aria-label', 'Акцент: ' + label);
      }
    });
  }
  document.documentElement.dataset.accent = preferences.accent || 'default';
  setTheme(preferences.theme);
  function navigationVisibility(focus) {
    shell.classList.toggle('nav-closed', !navOpen);
    nav.inert = !navOpen; nav.setAttribute('aria-hidden', String(!navOpen));
    const button = document.getElementById('navigation-open');
    button.setAttribute('aria-expanded', String(navOpen));
    button.title = navOpen ? 'Свернуть навигацию' : 'Развернуть навигацию'; button.setAttribute('aria-label', button.title);
    document.getElementById('workspace-region').inert = compact && navOpen;
    if (!navOpen && nav.contains(document.activeElement)) button.focus();
    else if (focus) (navOpen ? nav.querySelector('button') : button).focus();
  }
  function closeMenus() { document.querySelectorAll('details.menu[open]').forEach(menu => { menu.open = false; }); popup.hidePopover(); }
  function panelChoices(attribute) {
    return Object.entries(views).map(([id, view]) => '<button ' + attribute + '="' + id + '">' + I(view.icon) + '<span>' + E(view.title) + '</span>' + (api.getPanel(id) ? '<span class="open-dot" aria-label="Открыта">●</span>' : '') + '</button>').join('');
  }
  function routeRow(id, label) { return '<button class="nav-row" data-open="' + id + '">' + I(views[id].icon) + '<span>' + E(label || views[id].title) + '</span></button>'; }
  function treeGroup(title, icon, rows) { return '<details class="tree-group" open><summary>' + I('down') + I(icon) + '<span class="document-name">' + E(title) + '</span></summary><div class="tree-children">' + rows + '</div></details>'; }
  function renderNavigation() {
    const labels = { workspace: 'Рабочая область', files: 'Файлы', materials: 'Материалы', library: 'Библиотека' };
    document.getElementById('navigation-title').textContent = labels[section];
    const contents = {
      workspace: '<div class="nav-section-label">ДОКУМЕНТЫ И ЧАТЫ</div>' + treeGroup(D.fixtures.document, 'table', routeRow('sales', 'Что изменилось за сентябрь') + routeRow('summary')) + '<div class="nav-section-label">В ЭТОМ ПРОЕКТЕ</div>' + routeRow('files') + routeRow('report') + '<p class="nav-note">Открывайте несколько чатов и материалов. Навигация не закрывает вкладки.</p>',
      files: '<div class="nav-section-label">УПРАВЛЕНЧЕСКАЯ ОТЧЁТНОСТЬ</div>' + treeGroup('Исходные данные', 'folder', routeRow('files', 'Москва.xlsx') + routeRow('files', 'Казань.xlsx') + routeRow('files', 'Самара.xlsx')) + treeGroup('Результаты', 'folder', routeRow('report')) + '<p class="nav-note">В P1 исходные файлы открывают общий mock-список.</p>',
      materials: '<div class="nav-section-label">МАТЕРИАЛЫ ЧАТА</div>' + routeRow('report') + routeRow('chart') + '<div class="nav-section-label">СВЯЗАННЫЕ ПРЕДСТАВЛЕНИЯ</div>' + routeRow('browser') + routeRow('inspector'),
      library: '<div class="nav-section-label">КАТАЛОГ</div>' + treeGroup('Навыки', 'spark', routeRow('library')) + '<p class="nav-note">Каталоги инструкций и инструментов появятся при детализации экранов.</p>'
    };
    document.getElementById('navigation-content').innerHTML = contents[section];
    document.querySelectorAll('[data-section]').forEach(el => el.setAttribute('aria-pressed', String(el.dataset.section === section)));
    syncUI();
  }
  function syncUI() {
    if (!api) return;
    const active = api.activePanel && api.activePanel.id;
    document.querySelectorAll('.nav-row').forEach(el => { if (el.dataset.open === active) el.setAttribute('aria-current', 'page'); else el.removeAttribute('aria-current'); });
    document.getElementById('panel-list').innerHTML = '<span class="menu-label">Открыть или перейти к панели</span>' + panelChoices('data-open');
    const count = (n, forms) => n + ' ' + forms[n % 100 >= 11 && n % 100 <= 14 ? 2 : n % 10 === 1 ? 0 : n % 10 >= 2 && n % 10 <= 4 ? 1 : 2];
    document.getElementById('layout-status').textContent = narrow ? 'Узкий режим · вкладки' : count(api.panels.length, ['вкладка', 'вкладки', 'вкладок']) + ' · ' + count(api.groups.filter(g => g.api.isVisible).length, ['группа', 'группы', 'групп']);
    const maxButton = document.getElementById('maximize-toggle'), maximized = api.hasMaximizedGroup();
    maxButton.disabled = !api.activePanel; maxButton.innerHTML = I(maximized ? 'compress' : 'fit');
    maxButton.title = maximized ? 'Восстановить компоновку' : 'Развернуть группу' + (api.activePanel ? ': ' + api.activePanel.title : '');
    maxButton.setAttribute('aria-label', maxButton.title); maxButton.setAttribute('aria-pressed', String(maximized));
    const toolsButton = document.getElementById('tools-toggle'), groups = toolGroups();
    toolsButton.title = narrow || !groups.length ? 'Открыть инспектор' : groups.some(g => g.api.isVisible) ? 'Свернуть инструменты' : 'Развернуть инструменты';
    toolsButton.setAttribute('aria-label', toolsButton.title);
    toolsButton.setAttribute('aria-pressed', String(groups.some(g => g.api.isVisible)));
    document.querySelector('[data-command="restore-layout"]').disabled = !savedLayout;
    document.querySelectorAll('[data-group-split]').forEach(button => {
      const group = api.groups.find(item => item.id === button.closest('.group-actions').dataset.groupId);
      updateSplitButton(button, group);
    });
  }
  function savePreferences() {
    if (saving || !api) return;
    clearTimeout(saveTimer);
    try {
      localStorage.setItem(storageKey, JSON.stringify({ version: 1, layout: api.toJSON(), mode: narrow ? 'narrow' : 'wide', wideLayout, savedLayout,
        navWidth, navOpen: compact ? preferences.navOpen !== false : navOpen,
        theme: document.documentElement.dataset.theme, accent: document.documentElement.dataset.accent }));
    } catch (_) { toast('Браузер не разрешил сохранить раскладку. Она останется до закрытия вкладки.'); }
  }
  function changed() { if (saving) return; syncUI(); clearTimeout(saveTimer); saveTimer = setTimeout(savePreferences, 180); }
  function validLayout(layout) {
    return layout && layout.grid && layout.panels && !layout.floatingGroups?.length && !layout.popoutGroups?.length &&
      Object.entries(layout.panels).every(([id, panel]) => panel.id === id && (views[id] && panel.contentComponent === 'mock' || isChooser(id) && panel.contentComponent === 'chooser'));
  }
  function restore(layout) {
    if (!validLayout(layout)) throw new Error('Unsupported demo layout');
    api.fromJSON(layout);
  }
  function open(id, position) {
    const view = views[id] || (isChooser(id) ? { title: 'Выберите экран', component: 'chooser' } : null);
    if (!view) return;
    let panel = api.getPanel(id);
    if (!panel) {
      if (!position && api.panels.length) {
        const target = view.tool ? api.panels.find(p => views[p.id]?.tool) : api.panels.find(p => !views[p.id]?.tool);
        position = target ? { referencePanel: target.id, direction: 'within' } : { referencePanel: api.panels[0].id, direction: view.tool ? 'right' : 'left' };
      }
      if (narrow && api.panels.length) position = { referencePanel: api.panels[0].id, direction: 'within' };
      panel = api.addPanel({ id, component: view.component || 'mock', title: view.title, renderer: 'always', minimumWidth: narrow ? 140 : 230, minimumHeight: 160, ...(position ? { position } : {}) });
    }
    if (api.hasMaximizedGroup() && !panel.api.isMaximized()) api.exitMaximizedGroup();
    panel.group.api.setVisible(true); panel.api.setActive();
    if (compact) { navOpen = false; navigationVisibility(); }
    return panel;
  }
  function createChooser(id) {
    const element = document.createElement('div'); element.className = 'panel-content screen-chooser'; element.dataset.chooserId = id;
    element.innerHTML = '<div class="chooser-body"><h2>Что открыть?</h2><input type="search" aria-label="Найти экран" placeholder="Найти экран…" autocomplete="off"><div class="chooser-list"></div><p class="chooser-no-results" hidden>Ничего не найдено</p><p class="chooser-note">Открытый экран будет перенесён сюда.</p></div>';
    const input = element.querySelector('input'), list = element.querySelector('.chooser-list');
    function filter() {
      const query = input.value.trim().toLocaleLowerCase('ru'); let matches = 0;
      list.querySelectorAll('button').forEach(button => { button.hidden = !button.textContent.toLocaleLowerCase('ru').includes(query); if (!button.hidden) matches++; });
      element.querySelector('.chooser-no-results').hidden = matches > 0;
    }
    function render() { list.innerHTML = panelChoices('data-choose-view'); filter(); }
    input.addEventListener('input', filter);
    input.addEventListener('keydown', event => { if (event.key === 'Enter') { event.preventDefault(); list.querySelector('button:not([hidden])')?.click(); } });
    element.addEventListener('click', event => {
      const button = event.target.closest('[data-choose-view]'), chooser = api.getPanel(id);
      if (!button || !chooser) return;
      const existing = api.getPanel(button.dataset.chooseView);
      if (existing && existing.group !== chooser.group) existing.api.moveTo({ group: chooser.group, position: 'center' });
      const selected = open(button.dataset.chooseView, { referencePanel: id, direction: 'within' });
      chooser.api.close(); selected.api.setActive();
    });
    return { element, init: render, onShow: render, focus() { input.focus({ preventScroll: true }); } };
  }
  function openChooser(group, direction = 'within') {
    if (!group.activePanel || !['within', 'right', 'below'].includes(direction)) return;
    if (direction !== 'within' && splitUnavailable(group, direction)) return;
    closeMenus();
    if (direction !== 'within') api.exitMaximizedGroup();
    while (api.getPanel('choose-' + nextChooser)) nextChooser++;
    const chooser = open('choose-' + nextChooser++, { referencePanel: group.activePanel.id, direction });
    requestAnimationFrame(() => {
      const input = document.querySelector('[data-chooser-id="' + chooser.id + '"] input');
      if (input && chooser.api.isActive) input.focus({ preventScroll: true });
    });
  }
  function splitUnavailable(group, direction) {
    if (narrow) return 'В узком режиме используйте вкладки одной группы';
    if (!group || (direction === 'right' ? group.api.width < 464 : group.api.height < 444)) return 'Недостаточно места: сначала увеличьте эту область';
    return '';
  }
  function updateSplitButton(button, group) {
    const reason = splitUnavailable(group, button.dataset.groupSplit);
    button.disabled = !!reason; button.title = reason || button.getAttribute('aria-label');
  }
  function singleGroup() {
    api.exitMaximizedGroup();
    api.groups.forEach(group => group.api.setVisible(true));
    const panels = api.panels.slice(), first = panels[0], active = api.activePanel;
    if (first) panels.slice(1).forEach(panel => { if (panel.group !== first.group) panel.api.moveTo({ group: first.group, position: 'center' }); });
    if (active && api.getPanel(active.id)) active.api.setActive();
  }
  function preset(name) {
    saving = true;
    if (name === 'single') singleGroup();
    else {
      api.clear();
      if (name === 'research') { open('report'); open('browser', { referencePanel: 'report', direction: 'right' }); open('files', { referencePanel: 'report', direction: 'within' }); api.getPanel('report').api.setActive(); }
      else { open('sales'); open('summary', { referencePanel: 'sales', direction: 'within' }); open('inspector', { referencePanel: 'sales', direction: 'right' }); api.getPanel('sales').api.setActive(); }
      if (!narrow) { const tool = api.panels.find(p => views[p.id]?.tool); if (tool) tool.api.setSize({ width: 300 }); }
      wideLayout = narrow ? null : api.toJSON();
    }
    saving = false; changed();
  }
  function showGroupMenu(menu, anchor) {
    const rect = anchor.getBoundingClientRect(); closeMenus();
    menu.style.left = Math.max(8, Math.min(window.innerWidth - 248, rect.right - 240)) + 'px';
    menu.style.top = rect.bottom + 4 + 'px'; menu.showPopover();
    menu.style.top = Math.max(8, Math.min(window.innerHeight - menu.offsetHeight - 8, rect.bottom + 4)) + 'px';
  }
  function header(group) {
    const element = document.createElement('div'); element.className = 'group-actions'; element.dataset.groupId = group.id;
    element.innerHTML = D.iconButton('Выбрать экран в новой вкладке', 'data-group-add', 'plus') + D.iconButton('Новая область справа', 'data-group-split="right"', 'splitRight') + D.iconButton('Новая область снизу', 'data-group-split="below"', 'splitBelow') + D.iconButton('Действия с вкладкой', 'data-group-menu aria-haspopup="menu"', 'more');
    const updateButtons = () => element.querySelectorAll('[data-group-split]').forEach(button => updateSplitButton(button, group));
    const sizeListener = group.api.onDidDimensionsChange(updateButtons);
    element.addEventListener('click', event => {
      const add = event.target.closest('[data-group-add]'), actions = event.target.closest('[data-group-menu]'), split = event.target.closest('[data-group-split]');
      if (split) openChooser(group, split.dataset.groupSplit);
      else if (add) openChooser(group);
      else if (actions) {
        groupTarget = group;
        popup.querySelector('[data-group-action="join"]').disabled = api.groups.length < 2;
        showGroupMenu(popup, actions);
      }
    });
    return { element, init: updateButtons, dispose() { sizeListener.dispose(); } };
  }
  function toolGroups() { return api.groups.filter(group => group.panels.length && group.panels.every(panel => views[panel.id]?.tool)); }
  function toggleTools() {
    const groups = toolGroups();
    if (narrow || !groups.length) { open('inspector'); return; }
    const hide = groups.some(group => group.api.isVisible);
    if (hide && !api.groups.some(group => !groups.includes(group) && group.api.isVisible)) { toast('Это единственная рабочая область. Откройте материал или чат рядом.'); return; }
    api.exitMaximizedGroup();
    groups.forEach(group => group.api.setVisible(!hide));
    const focusGroup = hide ? api.groups.find(group => group.api.isVisible) : groups[0];
    if (focusGroup) focusGroup.api.setActive();
    changed();
  }
  let api;
  api = window['dockview-core'].createDockview(document.getElementById('dock'), {
    theme: { name: 'rnassistant', className: 'rna-dock', gap: 4, dndOverlayMounting: 'absolute', dndPanelOverlay: 'content' },
    disableFloatingGroups: true,
    dndStrategy: 'pointer',
    messages: {
      panelOpened: title => 'Открыто: ' + title,
      panelClosed: title => 'Закрыто: ' + title,
      groupMaximized: title => 'Развёрнуто: ' + title,
      groupRestored: title => 'Восстановлено: ' + title,
      closeTab: title => 'Закрыть ' + title,
      closeTabPlain: () => 'Закрыть вкладку'
    },
    createComponent: options => options.name === 'chooser' ? createChooser(options.id) : D.createView(options.id),
    createRightHeaderActionComponent: header,
    createWatermarkComponent() { const element = document.createElement('div'); element.className = 'workspace-empty'; element.innerHTML = '<div class="empty-state">' + I('layers') + '<h2>Ваше рабочее пространство</h2><p>Откройте чат, материал или инструмент. Каждая панель может стать основной.</p>' + D.button('Открыть отчёт', 'data-open="report"', 'file', 'primary') + '</div>'; return { element, init() {} }; }
  });
  popup.addEventListener('click', event => {
    const button = event.target.closest('[data-group-action]'), group = groupTarget;
    if (!button || !group || !group.activePanel) return;
    const action = button.dataset.groupAction, panel = group.activePanel; popup.hidePopover();
    if (action === 'close-group') { closeMenus(); api.exitMaximizedGroup(); group.panels.slice().forEach(item => item.api.close()); changed(); }
    else if (action === 'close') panel.api.close();
    else if (action === 'join') { const target = api.groups.find(g => g !== group); if (target) panel.api.moveTo({ group: target, position: 'center' }); }
  });
  api.onDidLayoutChange(changed);
  api.onDidActivePanelChange(syncUI);
  api.onDidMaximizedGroupChange(syncUI);
  function respond() {
    const width = shell.clientWidth, nextCompact = width <= 1000, nextNarrow = width <= 640;
    if (nextCompact !== compact) { if (!compact) preferences.navOpen = navOpen; compact = nextCompact; navOpen = compact ? false : preferences.navOpen !== false; }
    shell.classList.toggle('compact', compact); shell.classList.toggle('narrow', nextNarrow); navigationVisibility();
    if (nextNarrow !== narrow) {
      saving = true;
      if (nextNarrow) {
        wideLayout = api.toJSON(); narrow = true;
        api.panels.forEach(p => p.api.setConstraints({ minimumWidth: 140 })); singleGroup();
      } else {
        const ids = api.panels.map(p => p.id), active = api.activePanel && api.activePanel.id;
        narrow = false;
        if (validLayout(wideLayout)) {
          restore(wideLayout);
          api.panels.slice().forEach(p => { if (!ids.includes(p.id)) p.api.close(); });
          ids.forEach(id => { if (!api.getPanel(id)) open(id); });
          if (active && api.getPanel(active)) api.getPanel(active).api.setActive();
        }
        api.panels.forEach(p => p.api.setConstraints({ minimumWidth: 230 }));
      }
      api.updateOptions({ disableDnd: narrow }); saving = false; changed();
    }
    syncUI();
  }
  saving = true;
  try {
    if (preferences.layout) { restore(preferences.layout); narrow = preferences.mode === 'narrow'; api.updateOptions({ disableDnd: narrow }); }
    else preset('analysis');
  } catch (_) { loadNotice = 'Сохранённая раскладка несовместима. Открыт стартовый вид.'; preset('analysis'); }
  saving = false;
  shell.style.setProperty('--navigation-width', navWidth + 'px');
  document.getElementById('navigation-resize').setAttribute('aria-valuenow', String(navWidth));
  renderNavigation(); respond();
  new ResizeObserver(respond).observe(shell);
  document.addEventListener('click', event => {
    const accentChoice = event.target.closest('[data-accent-choice]');
    if (accentChoice) { setAccent(accentChoice.dataset.accentChoice); savePreferences(); }
    const opener = event.target.closest('[data-open]'), sectionButton = event.target.closest('[data-section]'), layout = event.target.closest('[data-layout]'), command = event.target.closest('[data-command]');
    if (opener) { open(opener.dataset.open); closeMenus(); }
    if (sectionButton) { const repeated = section === sectionButton.dataset.section; section = sectionButton.dataset.section; navOpen = repeated ? !navOpen : true; renderNavigation(); navigationVisibility(); changed(); }
    if (layout) { preset(layout.dataset.layout); closeMenus(); toast('Компоновка изменена. Черновики сохранены в памяти вкладки.'); }
    if (command) {
      switch (command.dataset.command) {
        case 'toggle-navigation': navOpen = !navOpen; navigationVisibility(true); changed(); break;
        case 'toggle-tools': toggleTools(); break;
        case 'maximize': if (api.hasMaximizedGroup()) api.exitMaximizedGroup(); else if (api.activePanel) api.activePanel.api.maximize(); break;
        case 'save-layout': savedLayout = { layout: api.toJSON(), wideLayout, narrow }; savePreferences(); syncUI(); closeMenus(); toast('Раскладка сохранена в этом браузере.'); break;
        case 'restore-layout':
          if (savedLayout) { saving = true; try { restore(savedLayout.layout); wideLayout = savedLayout.wideLayout; narrow = savedLayout.narrow; } catch (_) { toast('Эта раскладка недоступна.'); } saving = false; respond(); changed(); closeMenus(); }
          break;
      }
    }
    if (!event.target.closest('details.menu')) document.querySelectorAll('details.menu[open]').forEach(menu => { menu.open = false; });
  });
  document.querySelectorAll('details.menu').forEach(menu => menu.addEventListener('toggle', () => { if (menu.open) document.querySelectorAll('details.menu').forEach(other => { if (other !== menu) other.open = false; }); }));
  function toggleTheme() { setTheme(document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark'); savePreferences(); }
  document.getElementById('theme-toggle').addEventListener('click', toggleTheme);
  document.addEventListener('change', event => { if (event.target.matches('[data-theme-choice]')) { setTheme(event.target.value); savePreferences(); } });
  document.getElementById('preview-width').addEventListener('change', event => { shell.style.setProperty('--preview-width', event.target.value === 'auto' ? '100%' : event.target.value + 'px'); closeMenus(); });
  document.addEventListener('keydown', event => {
    if (event.key === 'Escape') { closeMenus(); if (compact && navOpen) { navOpen = false; navigationVisibility(true); } else if (api.hasMaximizedGroup()) api.exitMaximizedGroup(); }
  });
  // This separator only sizes the navigation sidebar. Workspace splitting belongs to Dockview.
  const resize = document.getElementById('navigation-resize');
  let drag;
  function setNavWidth(width) { navWidth = Math.max(180, Math.min(340, width)); shell.style.setProperty('--navigation-width', navWidth + 'px'); resize.setAttribute('aria-valuenow', String(navWidth)); }
  resize.addEventListener('pointerdown', event => { if (event.button !== 0) return; drag = { x: event.clientX, width: navWidth }; resize.setPointerCapture(event.pointerId); event.preventDefault(); });
  resize.addEventListener('pointermove', event => { if (drag) setNavWidth(drag.width + event.clientX - drag.x); });
  resize.addEventListener('lostpointercapture', () => { drag = null; savePreferences(); });
  resize.addEventListener('pointerup', event => { if (resize.hasPointerCapture(event.pointerId)) resize.releasePointerCapture(event.pointerId); });
  resize.addEventListener('keydown', event => { if (['ArrowLeft', 'ArrowRight', 'Home'].includes(event.key)) { event.preventDefault(); setNavWidth(event.key === 'Home' ? 224 : navWidth + (event.key === 'ArrowLeft' ? -16 : 16)); savePreferences(); } });
  resize.addEventListener('dblclick', () => { setNavWidth(224); savePreferences(); });
  window.addEventListener('pagehide', savePreferences);
  function route() {
    const path = location.hash.replace(/^#\//, ''); if (!path) return;
    const exact = { 'chat/main': 'sales', 'chat/summary': 'summary', 'chat/context': 'inspector', 'chat/usage': 'inspector', 'chat/abilities': 'inspector', 'chat/activity': 'diagnostics', 'materials/report': 'report', 'materials/html': 'browser', 'materials/image': 'chart', 'materials/all': 'report' };
    const id = exact[path] || (path.startsWith('library/') ? 'library' : path.startsWith('settings/') ? 'settings' : null);
    if (id) open(id); else toast('Этот экран прежнего Demo не входит в P1. Выберите панель в меню.');
  }
  window.addEventListener('hashchange', route); route();
  if (loadNotice) toast(loadNotice);
}());
