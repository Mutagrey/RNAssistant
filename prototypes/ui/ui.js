(function () {
  "use strict";
  var P = window.Prototype = { fixtures: window.PrototypeFixtures, map: window.PrototypeMap };
  P.escape = function (value) { return String(value == null ? "" : value).replace(/[&<>"']/g, function (c) { return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]; }); };
  var paths = {
    home: '<path d="m3 10 9-7 9 7M5 8.5V20a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1V8.5M9 21v-8h6v8"/>',
    fork: '<circle cx="6" cy="5" r="2"/><circle cx="18" cy="5" r="2"/><circle cx="6" cy="19" r="2"/><path d="M6 7v10M18 7v2a5 5 0 0 1-5 5H6"/>',
    paperclip: '<path d="m21 11-9 9a6 6 0 0 1-8.5-8.5L13 2a4 4 0 0 1 5.7 5.7L9.2 17.2a2 2 0 0 1-2.8-2.8l9-9"/>',
    selection: '<path d="M8 3H3v5M16 3h5v5M21 16v5h-5M8 21H3v-5M8 8h8v8H8Z"/>',
    compress: '<path d="m4 4 5 5M4 9h5V4m11 16-5-5m0 5v-5h5M4 20l5-5m-5 0h5v5M20 4l-5 5m0-5v5h5"/>',
    chat: '<path d="M21 11.5a8.5 8.5 0 0 1-8.5 8.5H4l-1 1V11.5a9 9 0 0 1 18 0Z"/><path d="M7 9h10M7 13h7"/>',
    folder: '<path d="M3 7V5h6l2 2h10v12H3Z"/>',
    files: '<path d="M7 3h9l4 4v13H7Z"/><path d="M16 3v5h4M3 7v14h12"/>',
    file: '<path d="M5 3h10l4 4v14H5Z"/><path d="M14 3v5h5M9 12h6M9 16h6"/>',
    office: '<path fill="#d83b01" stroke="none" d="m4 6 10-4 7 3v14l-7 3-10-4V6Zm4 2v8l6 2V6L8 8Z"/><path fill="#f16c28" stroke="none" d="m14 2 7 3v14l-7 3v-4l4-1V7l-4-1V2Z"/>',
    bulb: '<path d="M9 18h6M10 21h4M8.4 14.7a6 6 0 1 1 7.2 0c-.9.7-1.4 1.6-1.6 2.3h-4c-.2-.7-.7-1.6-1.6-2.3Z"/><path d="M12 1V0m-7.1 4.9-.8-.8m15.8 0-.8.8"/>',
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
    menu: '<path d="M4 6h16M4 12h16M4 18h16"/>',
    link: '<path d="m10 14 4-4M8 16l-1 1a4 4 0 0 1-6-6l4-4a4 4 0 0 1 6 0m2 1 1-1a4 4 0 0 1 6 6l-4 4a4 4 0 0 1-6 0"/>',
    download: '<path d="M12 3v12m-5-5 5 5 5-5M4 16v5h16v-5"/>',
    upload: '<path d="M12 16V4m-5 5 5-5 5 5M4 16v5h16v-5"/>',
    copy: '<rect x="8" y="8" width="12" height="13" rx="2"/><path d="M15 8V3H3v13h5"/>',
    sun: '<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M2 12h2M20 12h2M5 5l1 1m12 12 1 1M5 19l1-1M18 6l1-1"/>',
    moon: '<path d="M20 14A8 8 0 0 1 10 4a8.5 8.5 0 1 0 10 10Z"/>',
    shield: '<path d="m12 3 8 3v6c0 5-8 9-8 9S4 17 4 12V6Z"/><path d="m8 12 3 3 5-6"/>',
    chart: '<path d="M3 3v18h18M7 16v-5M12 16V7M17 16V4"/>',
    braces: '<path d="M8 3H6v7l-3 2 3 2v7h2M16 3h2v7l3 2-3 2v7h-2"/>',
    image: '<rect x="3" y="3" width="18" height="18" rx="2"/><circle cx="8" cy="8" r="1"/><path d="m3 17 6-6 4 4 3-3 5 5"/>',
    layers: '<path d="m12 3 10 5-10 5L2 8Zm-9 10 9 5 9-5M3 18l9 5 9-5"/>',
    warning: '<path d="m12 3 10 18H2Z"/><path d="M12 9v5M12 17v1"/>',
    play: '<path d="m8 4 12 8-12 8Z"/>',
    stop: '<rect x="6" y="6" width="12" height="12" rx="1"/>',
    trash: '<path d="M3 6h18M9 6V3h6v3M5 6l1 15h12l1-15M10 10v7M14 10v7"/>',
    edit: '<path d="m4 16-1 5 5-1L20 8l-4-4ZM13 7l4 4"/>',
    refresh: '<path d="M20 7V3m0 4h-4M4 17v4m0-4h4M4 10a8 8 0 0 1 14-5l2 2M4 17l2 2a8 8 0 0 0 14-5"/>',
    spark: '<path d="m12 3 2.4 6.6L21 12l-6.6 2.4L12 21l-2.4-6.6L3 12l6.6-2.4Z"/>',
    pin: '<path d="m9 3-1 6-4 4h7v8m0-8h7l-4-4-1-6Z"/>'
  };
  P.icon = function (name) { return '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.55" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' + (paths[name] || paths.file) + '</svg>'; };
  P.officeIcon = function (host) {
    var letters = { Excel: "X", Word: "W", PowerPoint: "P", Outlook: "O" };
    return letters[host] ? '<span class="office-app-mark is-' + host.toLowerCase() + '" aria-hidden="true"><span class="office-app-page"></span><span class="office-app-tile">' + letters[host] + '</span></span>' : P.icon("file");
  };
  P.button = function (label, action, icon, kind) { return '<button type="button" class="button ' + (kind || "") + '" data-action="' + P.escape(action) + '">' + (icon ? P.icon(icon) : "") + P.escape(label) + '</button>'; };
  P.go = function (label, route, icon, kind) { return '<button type="button" class="button ' + (kind || "") + '" data-go="' + route + '">' + (icon ? P.icon(icon) : "") + P.escape(label) + '</button>'; };
  P.iconButton = function (label, action, name, extra) { return '<button type="button" class="icon-btn ' + (extra || "") + '" data-action="' + action + '" aria-label="' + P.escape(label) + '" title="' + P.escape(label) + '">' + P.icon(name) + '</button>'; };
  P.iconGo = function (label, route, name) { return '<button type="button" class="icon-btn" data-go="' + P.escape(route) + '" aria-label="' + P.escape(label) + '" title="' + P.escape(label) + '">' + P.icon(name || "right") + '</button>'; };
  P.badge = function (label, kind) { return '<span class="badge ' + (kind || "") + '"><span class="badge-dot"></span>' + P.escape(label) + '</span>'; };
  P.notice = function (title, text, kind, actions) { return '<div class="notice ' + (kind || "") + '" role="' + (kind === "danger" ? "alert" : "status") + '">' + P.icon(kind === "success" ? "check" : "warning") + '<div class="row-copy"><strong>' + P.escape(title) + '</strong><p>' + P.escape(text) + '</p>' + (actions ? '<div class="actions">' + actions + '</div>' : "") + '</div></div>'; };
  P.search = function (placeholder, name) { return '<label class="search">' + P.icon("search") + '<input type="search" placeholder="' + placeholder + '" aria-label="' + placeholder + '" data-search="' + name + '"></label>'; };
  P.header = function (title, breadcrumb, actions) { return '<header class="page-header">' + (P.state.standalone ? "" : P.iconButton("Открыть навигацию", "navigation", "menu", "menu-btn")) + '<div class="page-heading"><div class="breadcrumb">' + P.escape(breadcrumb) + '</div><h1 title="' + P.escape(title) + '">' + P.escape(title) + '</h1></div><div class="header-actions">' + (actions || "") + '</div></header>'; };
  P.tabs = function (items, current) { return '<nav class="tabs" aria-label="Разделы экрана">' + items.map(function (v) { return '<button data-go="' + v[1] + '"' + (current === v[1] ? ' aria-current="page"' : "") + '>' + P.escape(v[0]) + '</button>'; }).join("") + '</nav>'; };
  P.menu = function (items, label) { return '<details class="popover"><summary class="icon-btn" aria-label="' + P.escape(label || "Дополнительные действия") + '" title="' + P.escape(label || "Дополнительные действия") + '">' + P.icon("more") + '</summary><div class="popover-menu">' + items.map(function (v) { return v === "-" ? '<hr>' : '<button data-' + (v[1].indexOf("/") >= 0 ? "go" : "action") + '="' + v[1] + '" class="' + (v[3] || "") + '">' + P.icon(v[2] || "right") + P.escape(v[0]) + '</button>'; }).join("") + '</div></details>'; };
  P.empty = function (title, description, actions, icon) { return '<div class="empty">' + P.icon(icon || "files") + '<h2>' + P.escape(title) + '</h2><p>' + P.escape(description) + '</p><div class="actions">' + (actions || "") + '</div></div>'; };
  P.field = function (label, value, key, hint, type) { return '<label class="field">' + P.escape(label) + '<input type="' + (type || "text") + '" value="' + P.escape(value) + '" data-field="' + key + '">' + (hint ? '<small>' + P.escape(hint) + '</small>' : "") + '</label>'; };
  P.code = function (key, initial) { P.edits.rememberCode(key, initial); return '<textarea class="code-editor" spellcheck="false" aria-label="Редактор исходного текста" data-draft="' + key + '">' + P.escape(P.state.drafts[key] == null ? initial : P.state.drafts[key]) + '</textarea>'; };
  P.markdown = function (text) {
    // Deliberately small, inert preview. User text is always escaped before markup.
    var lines = text.split(/\r?\n/), skipTo = -1;
    function cells(line) { return line.trim().replace(/^\||\|$/g, "").split("|").map(function (cell) { return P.escape(cell.trim()); }); }
    return lines.map(function (line, index) {
      if (index <= skipTo) return "";
      if (/^\s*\|/.test(line) && /^\s*\|(?:\s*:?-{3,}:?\s*\|)+\s*$/.test(lines[index + 1] || "")) {
        var rows = [], cursor = index + 2, headers = cells(line);
        while (cursor < lines.length && /^\s*\|/.test(lines[cursor])) {
          var row = cells(lines[cursor]);
          rows.push(headers.map(function (_, column) { return row[column] || ""; })); cursor++;
        }
        skipTo = cursor - 1;
        return P.table(headers, rows);
      }
      if (line.indexOf("# ") === 0) return '<h1>' + P.escape(line.slice(2)) + '</h1>';
      if (line.indexOf("## ") === 0) return '<h2>' + P.escape(line.slice(3)) + '</h2>';
      if (line.indexOf("- ") === 0) return '<p>• ' + P.escape(line.slice(2)) + '</p>';
      return line ? '<p>' + P.escape(line) + '</p>' : '';
    }).join("");
  };
  P.table = function (headers, rows) { return '<div class="table-wrap"><table><thead><tr>' + headers.map(function (v) { return '<th scope="col">' + v + '</th>'; }).join("") + '</tr></thead><tbody>' + rows.map(function (row) { return '<tr data-filter-row>' + row.map(function (v) { return '<td>' + v + '</td>'; }).join("") + '</tr>'; }).join("") + '</tbody></table></div>'; };
  P.info = function (title, subtitle, icon, action) { return '<div class="info-row">' + P.icon(icon || "file") + '<div class="row-copy"><strong>' + P.escape(title) + '</strong><small>' + P.escape(subtitle) + '</small></div>' + (action || "") + '</div>'; };
  P.chart = function () { return '<div class="mini-chart" role="img" aria-label="Демонстрационная выручка: июнь 9,6, июль 10,2, август 10,8, сентябрь 12,8 миллиона рублей.">' + [["Июн", "9,6", 60], ["Июл", "10,2", 68], ["Авг", "10,8", 76], ["Сен", "12,8", 94]].map(function (v) { return '<div class="chart-bar" style="height:' + v[2] + '%"><b>' + v[1] + '</b><span>' + v[0] + '</span></div>'; }).join("") + '</div>'; };
  P.dirtyBar = function (key) { return '<div class="editor-status"><span data-dirty-status="' + key + '">' + (P.state.dirty[key] ? "Есть изменения" : "") + '</span><span data-editor-position>Строка 1, столбец 1</span></div>'; };
}());
