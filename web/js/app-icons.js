(function () {
  "use strict";
  var iconPaths = {
    home: '<path d="m3 10 9-7 9 7M5 8.5V20a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1V8.5M9 21v-8h6v8"/>',
    fork: '<circle cx="6" cy="5" r="2"/><circle cx="18" cy="5" r="2"/><circle cx="6" cy="19" r="2"/><path d="M6 7v10M18 7v2a5 5 0 0 1-5 5H6"/>',
    paperclip: '<path d="m21 11-9 9a6 6 0 0 1-8.5-8.5L13 2a4 4 0 0 1 5.7 5.7L9.2 17.2a2 2 0 0 1-2.8-2.8l9-9"/>',
    selection: '<path d="M8 3H3v5M16 3h5v5M21 16v5h-5M8 21H3v-5M8 8h8v8H8Z"/>',
    compress: '<path d="m4 4 5 5M4 9h5V4m11 16-5-5m0 5v-5h5M4 20l5-5m-5 0h5v5M20 4l-5 5m0-5v5h5"/>',
    compaction: '<path d="M5 8h14M8 12h8M10 16h4"/>',
    chat: '<path d="M21 11.5a8.5 8.5 0 0 1-8.5 8.5H4l-1 1V11.5a9 9 0 0 1 18 0Z"/><path d="M7 9h10M7 13h7"/>',
    folder: '<path d="M3 7V5h6l2 2h10v12H3Z"/>',
    files: '<path d="M7 3h9l4 4v13H7Z"/><path d="M16 3v5h4M3 7v14h12"/>',
    file: '<path d="M5 3h10l4 4v14H5Z"/><path d="M14 3v5h5M9 12h6M9 16h6"/>',
    office: '<path fill="#d83b01" stroke="none" d="m4 6 10-4 7 3v14l-7 3-10-4V6Zm4 2v8l6 2V6L8 8Z"/><path fill="#f16c28" stroke="none" d="m14 2 7 3v14l-7 3v-4l4-1V7l-4-1V2Z"/>',
    bulb: '<path d="M9 18h6M10 21h4M8.4 14.7a6 6 0 1 1 7.2 0c-.9.7-1.4 1.6-1.6 2.3h-4c-.2-.7-.7-1.6-1.6-2.3Z"/><path d="M12 2V1m-7.1 4.9-.8-.8m15.8 0-.8.8"/>',
    book: '<path d="M4 4h6a3 3 0 0 1 3 3v14a3 3 0 0 0-3-3H4ZM13 7a3 3 0 0 1 3-3h5v14h-5a3 3 0 0 0-3 3"/>',
    code: '<path d="m8 6-6 6 6 6m8-12 6 6-6 6M14 3l-4 18"/>',
    settings: '<path d="m9.7 3-.5 2.2-1.4.8-2.2-.7-2.3 4 1.7 1.5v1.6L3.3 14l2.3 4 2.2-.7 1.4.8.5 2.2h4.6l.5-2.2 1.4-.8 2.2.7 2.3-4-1.7-1.6v-1.6l1.7-1.5-2.3-4-2.2.7-1.4-.8-.5-2.2Z"/><circle cx="12" cy="11.7" r="3.2"/>',
    search: '<circle cx="10.5" cy="10.5" r="6.5"/><path d="m16 16 4 4"/>',
    pause: '<path d="M8 5v14M16 5v14"/>',
    arrowDown: '<path d="M12 5v14m-6-6 6 6 6-6"/>',
    plus: '<path d="M12 5v14M5 12h14"/>',
    minus: '<path d="M5 12h14"/>',
    left: '<path d="m15 6-6 6 6 6"/>',
    fit: '<path d="M8 3H3v5M16 3h5v5M21 16v5h-5M8 21H3v-5"/>',
    rotateLeft: '<path d="M3 10a9 9 0 1 1 3 9M3 4v6h6"/>',
    rotateRight: '<path d="M21 10a9 9 0 1 0-3 9M21 4v6h-6"/>',
    more: '<circle cx="5" cy="12" r="1"/><circle cx="12" cy="12" r="1"/><circle cx="19" cy="12" r="1"/>',
    down: '<path d="m6 9 6 6 6-6"/>',
    right: '<path d="m9 6 6 6-6 6"/>',
    external: '<path d="M14 4h6v6m0-6L10 14M10 5H4v15h15v-6"/>',
    back: '<path d="m10 5-7 7 7 7M3 12h18"/>',
    arrow: '<path d="M12 19V5m-6 6 6-6 6 6"/>',
    close: '<path d="m6 6 12 12M18 6 6 18"/>',
    check: '<path d="m5 12 4 4L19 6"/>',
    clock: '<circle cx="12" cy="12" r="9"/><path d="M12 6v6l4 2"/>',
    panel: '<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M15 4v16"/>',
    panelLeft: '<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M9 4v16"/>',
    menu: '<path d="M4 6h16M4 12h16M4 18h16"/>',
    link: '<path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.72-1.71"/>',
    download: '<path d="M12 3v12m-5-5 5 5 5-5M4 16v5h16v-5"/>',
    upload: '<path d="M12 16V4m-5 5 5-5 5 5M4 16v5h16v-5"/>',
    copy: '<rect x="8" y="8" width="12" height="13" rx="2"/><path d="M15 8V3H3v13h5"/>',
    collapse: '<path d="m6 14 6-6 6 6M6 20l6-6 6 6"/>',
    sun: '<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M2 12h2M20 12h2M5 5l1 1m12 12 1 1M5 19l1-1M18 6l1-1"/>',
    moon: '<path d="M20 14A8 8 0 0 1 10 4a8.5 8.5 0 1 0 10 10Z"/>',
    shield: '<path d="m12 3 8 3v6c0 5-8 9-8 9S4 17 4 12V6Z"/><path d="m8 12 3 3 5-6"/>',
    chart: '<path d="M3 3v18h18M7 16v-5M12 16V7M17 16V4"/>',
    table: '<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M3 10h18M3 15h18M10 4v16M16 4v16"/>',
    puzzle: '<path d="M9 3H3v6h2a2 2 0 1 1 0 4H3v8h7v-2a2 2 0 1 1 4 0v2h7v-7h-2a2 2 0 1 1 0-4h2V3h-8v2a2 2 0 1 1-4 0Z"/>',
    braces: '<path d="M8 3H6v7l-3 2 3 2v7h2M16 3h2v7l3 2-3 2v7h-2"/>',
    image: '<rect x="3" y="3" width="18" height="18" rx="2"/><circle cx="8" cy="8" r="1"/><path d="m3 17 6-6 4 4 3-3 5 5"/>',
    layers: '<path d="m12 3 10 5-10 5L2 8Zm-9 10 9 5 9-5M3 18l9 5 9-5"/>',
    info: '<circle cx="12" cy="12" r="9"/><path d="M12 11v6M12 7v.1"/>',
    warning: '<path d="m12 3 10 18H2Z"/><path d="M12 9v5M12 17v1"/>',
    play: '<path d="m8 4 12 8-12 8Z"/>',
    stop: '<rect x="6" y="6" width="12" height="12" rx="1"/>',
    trash: '<path d="M3 6h18M9 6V3h6v3M5 6l1 15h12l1-15M10 10v7M14 10v7"/>',
    edit: '<path d="m4 16-1 5 5-1L20 8l-4-4ZM13 7l4 4"/>',
    refresh: '<path d="M20 7V3m0 4h-4M4 17v4m0-4h4M4 10a8 8 0 0 1 14-5l2 2M4 17l2 2a8 8 0 0 0 14-5"/>',
    spark: '<path d="m12 3 2.4 6.6L21 12l-6.6 2.4L12 21l-2.4-6.6L3 12l6.6-2.4Z"/>',
    pin: '<path d="m9 3-1 6-4 4h7v8m0-8h7l-4-4-1-6Z"/>',
    prompt: '<path d="M12 6c-2-1.5-5-2-9-1v14c4-1 7-.5 9 1 2-1.5 5-2 9-1V5c-4-1-7-.5-9 1zM12 6v14"/>',
    skill: '<path d="m10 3 1.8 5.2L17 10l-5.2 1.8L10 17l-1.8-5.2L3 10l5.2-1.8Z"/><path d="m19 15 .8 2.2L22 18l-2.2.8L19 21l-.8-2.2L16 18l2.2-.8Z"/>',
    tool: '<path d="m8 8-4 4 4 4M16 8l4 4-4 4M14 5l-4 14"/>',
    module: '<path d="m8 8-4 4 4 4M16 8l4 4-4 4"/>',
    document: '<path d="M6 3h8l4 4v14H6zM14 3v5h4M9 12h6M9 16h6"/>',
    class: '<path d="M5 5h14v14H5zM8 9h8M8 12h8M8 15h5"/>',
    form: '<rect x="4" y="4" width="16" height="16" rx="2"/><path d="M4 9h16M8 13h8M8 16h5"/>',
    html: '<path d="m5 4 2 16h10l2-16zM8 9l4 3 4-3M12 12v5"/>',
    css: '<path d="M5 4h14l-1 15-6 2-6-2zM9 9h7l-.5 3H10l.3 3 2 .6 2-.6"/>',
    js: '<path d="M5 3h14v18H5zM13 10v6c0 2-3 2-4 0M16 14c2-1 3 2 1 3"/>',
    json: '<path d="M8 4H6v6l-2 2 2 2v6h2M16 4h2v6l2 2-2 2v6h-2"/>',
    markdown: '<rect x="3" y="5" width="18" height="14" rx="2"/><path d="M6 15V9l3 3 3-3v6m3-3 2 3 2-3"/>',
    plan: '<rect x="4" y="3" width="16" height="18" rx="2"/><path d="m8 9 1.5 1.5L12 8M14 9h3m-9 6 1.5 1.5L12 14m2 1h3"/>',
    open: '<path d="M14 5h5v5m0-5-8 8M18 13v5a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5"/>',
    resources: '<rect x="3" y="3" width="13" height="13" rx="2"/><path d="M8 21h11a2 2 0 0 0 2-2V8"/>',
    contextAdd: '<path d="M4 7h16"/><path d="M4 12h10"/><path d="M4 17h7"/><path d="M17 15v6"/><path d="M14 18h6"/>',
    selectionAdd: '<rect x="4" y="4" width="16" height="16" rx="2" stroke-dasharray="3 3"/><path d="M12 8v8M8 12h8"/>',
    planMode: '<circle cx="5" cy="6" r="2"/><circle cx="5" cy="18" r="2"/><path d="M11 6h9M11 12h9M11 18h9M3 12h4"/>',
    contextMeter: '<circle class="context-meter-track" cx="12" cy="12" r="9"/>                    <circle class="context-meter-fill" cx="12" cy="12" r="9" pathLength="100"/>',
    send: '<path d="M12 4.75a1 1 0 0 1 .7.29l5.25 5.25a1 1 0 1 1-1.41 1.42L13 8.16V18a1 1 0 1 1-2 0V8.16l-3.54 3.55a1 1 0 0 1-1.41-1.42l5.25-5.25a1 1 0 0 1 .7-.29Z"/>',
    eye: '<path d="M2 12s3.5-6 10-6 10 6 10 6-3.5 6-10 6-10-6-10-6Z"/><circle cx="12" cy="12" r="3"/>',
    eyeOff: '<path d="M3 3l18 18M10.6 10.6A3 3 0 0 0 13.4 13.4M9.9 5.2A10.8 10.8 0 0 1 12 5c6.5 0 10 7 10 7a17.9 17.9 0 0 1-3.2 4.2M6.1 6.6C3.4 8.4 2 12 2 12s3.5 7 10 7a10.6 10.6 0 0 0 4.1-.8"/>',
    terminal: '<rect x="3" y="4" width="18" height="16" rx="2"/><path d="m7 9 3 3-3 3M13 15h4"/>',
    question: '<path d="M4 4h16v13H9l-5 4Z"/><path d="M9 8a3 3 0 0 1 6 0c0 2-3 2-3 4M12 14v.1"/>',
    package: '<path d="m3 7 9-4 9 4v10l-9 4-9-4Zm0 0 9 4 9-4M12 11v10M8 5l9 4"/>',
    learn: '<path d="M4 4h10l4 4v12H4Z"/><path d="M14 4v5h5M8 12h6M8 16h4m9-2-3 4-2-2"/>',
    status: '<circle cx="12" cy="12" r="8"/><path d="m8.5 12 2.3 2.3 4.8-5"/>',
    audio: '<path d="M4 9v6h4l5 4V5L8 9H4ZM17 9a4 4 0 0 1 0 6M19 6a8 8 0 0 1 0 12"/>'
  };
  var iconAliases = { dataTemplates: "chart", connection: "link", model: "spark", context: "paperclip", agent: "chat", interface: "panel", protection: "shield", service: "settings", system: "settings",
    branch: "fork", retry: "refresh", html_workspace: "html", tool_result: "terminal", compaction: "compress", attachment: "file", script: "js", data: "json", task_list: "check", sequence: "layers" };

  function resolve(name) { return iconPaths[iconAliases[name] || name] || iconPaths.file; }
  function plugin(name, paths) {
    if (Array.isArray(paths) && paths.length > 0 && paths.length <= 6 &&
        paths.every(function (path) { return typeof path === 'string' && path.length <= 1024 &&
          /^[MmLlHhVvCcSsQqTtAaZz0-9\s,.+\-]+$/.test(path); })) {
      return '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.55" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' +
        paths.map(function (path) { return '<path d="' + path + '"/>'; }).join('') + '</svg>';
    }
    return render(name && iconPaths[iconAliases[name] || name] ? name : 'puzzle');
  }
  function render(name, className) {
    var cssClass = className && /^[a-z0-9_ -]+$/i.test(className) ? ' class="' + className + '"' : '';
    return '<svg' + cssClass + ' viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.55" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' + resolve(name) + '</svg>';
  }
  function hydrate(root) {
    (root || document).querySelectorAll('svg[data-rna-icon]').forEach(function (node) {
      node.innerHTML = resolve(node.getAttribute('data-rna-icon'));
      node.setAttribute('viewBox', '0 0 24 24');
      node.setAttribute('fill', 'none'); node.setAttribute('stroke', 'currentColor');
      node.setAttribute('stroke-width', '1.55'); node.setAttribute('stroke-linecap', 'round'); node.setAttribute('stroke-linejoin', 'round');
    });
  }
  var pluginChoices = [
    ['dataTemplates', 'Данные и шаблоны'], ['puzzle', 'Плагин'], ['chart', 'График'],
    ['files', 'Файлы'], ['folder', 'Папка'], ['table', 'Таблица'], ['plan', 'План'],
    ['code', 'Код'], ['braces', 'Данные'], ['image', 'Изображение'], ['spark', 'Идея'],
    ['tool', 'Инструмент'], ['book', 'Справочник'], ['layers', 'Слои'], ['search', 'Поиск'],
    ['settings', 'Настройки'], ['package', 'Пакет'], ['resources', 'Ресурсы']
  ].filter(function (choice) { return !!iconPaths[iconAliases[choice[0]] || choice[0]]; });
  window.RNAssistantIcons = { render: render, renderPlugin: plugin, hydrate: hydrate,
    pluginChoices: pluginChoices,
    has: function (name) { return !!iconPaths[iconAliases[name] || name]; } };
  if (typeof document !== 'undefined' && typeof document.querySelectorAll === 'function') hydrate(document);
}());
