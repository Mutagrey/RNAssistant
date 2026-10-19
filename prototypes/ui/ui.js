(function () {
  "use strict";
  var P = window.Prototype = { fixtures: window.PrototypeFixtures, map: window.PrototypeMap };
  P.escape = function (value) { return String(value == null ? "" : value).replace(/[&<>"']/g, function (c) { return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]; }); };
  P.icon = function (name) { return window.RNAssistantNavigationTree.icon(name); };
  P.officeIcon = function (host) {
    var letters = { Excel: "X", Word: "W", PowerPoint: "P", Outlook: "O" };
    return letters[host] ? '<span class="office-app-mark is-' + host.toLowerCase() + '" aria-hidden="true"><span class="office-app-page"></span><span class="office-app-tile">' + letters[host] + '</span></span>' : P.icon("file");
  };
  P.button = function (label, action, icon, kind) { return '<button type="button" class="button ' + (kind || "") + '" data-action="' + P.escape(action) + '">' + (icon ? P.icon(icon) : "") + P.escape(label) + '</button>'; };
  P.go = function (label, route, icon, kind) { return '<button type="button" class="button ' + (kind || "") + '" data-go="' + route + '">' + (icon ? P.icon(icon) : "") + P.escape(label) + '</button>'; };
  P.iconButton = function (label, action, name, extra) { return '<button type="button" class="icon-btn ' + (extra || "") + '" data-action="' + action + '" aria-label="' + P.escape(label) + '" title="' + P.escape(label) + '">' + P.icon(name) + '</button>'; };
  P.iconGo = function (label, route, name) { return '<button type="button" class="icon-btn" data-go="' + P.escape(route) + '" aria-label="' + P.escape(label) + '" title="' + P.escape(label) + '">' + P.icon(name || "right") + '</button>'; };
  P.panelMenu = function (title, label, icon, className, items) {
    return '<details class="popover panel-switcher ' + className + '"><summary aria-label="' + P.escape(label + ": " + title) + '" title="' + P.escape(title + " · сменить раздел") + '" aria-haspopup="menu">' + P.icon(icon) + '<span>' + P.escape(title) + '</span>' + P.icon("down") + '</summary><div class="popover-menu" role="menu" aria-label="' + P.escape(label) + '">' + items + '</div></details>';
  };
  P.badge = function (label, kind) { return '<span class="badge ' + (kind || "") + '"><span class="badge-dot"></span>' + P.escape(label) + '</span>'; };
  P.notice = function (title, text, kind, actions) { return '<div class="notice ' + (kind || "") + '" role="' + (kind === "danger" ? "alert" : "status") + '">' + P.icon(kind === "success" ? "check" : kind === "warning" || kind === "danger" ? "warning" : "info") + '<div class="row-copy"><strong>' + P.escape(title) + '</strong><p>' + P.escape(text) + '</p>' + (actions ? '<div class="actions">' + actions + '</div>' : "") + '</div></div>'; };
  P.search = function (placeholder, name) { return '<label class="search">' + P.icon("search") + '<input type="search" placeholder="' + placeholder + '" aria-label="' + placeholder + '" data-search="' + name + '"></label>'; };
  P.header = function (title, breadcrumb, actions) { return '<header class="page-header"><div class="page-heading"><div class="breadcrumb">' + P.escape(breadcrumb) + '</div><h1 title="' + P.escape(title) + '">' + P.escape(title) + '</h1></div><div class="header-actions">' + (actions || "") + '</div></header>'; };
  P.tabs = function (items, current) { return window.RNAssistantEditorTabs.render(items.map(function (item) {
    return { label: item[0], value: item[1] };
  }), { className: "tabs", selected: current }); };
  P.menu = function (items, label) { return window.RNAssistantActionMenu.render(items.map(function (item) {
    return item === "-" ? item : { label: item[0], action: item[1], route: item[1].indexOf("/") >= 0,
      icon: item[2], className: item[3] };
  }), { label: label }); };
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
  P.table = function (headers, rows) { return '<div class="rna-table-wrap"><table class="rna-table"><thead><tr>' + headers.map(function (v) { return '<th scope="col">' + v + '</th>'; }).join("") + '</tr></thead><tbody>' + rows.map(function (row) { return '<tr data-filter-row>' + row.map(function (v) { return '<td>' + v + '</td>'; }).join("") + '</tr>'; }).join("") + '</tbody></table></div>'; };
  P.info = function (title, subtitle, icon, action) { return '<div class="info-row">' + P.icon(icon || "file") + '<div class="row-copy"><strong>' + P.escape(title) + '</strong><small>' + P.escape(subtitle) + '</small></div>' + (action || "") + '</div>'; };
  P.chart = function () { return '<div class="mini-chart" role="img" aria-label="Демонстрационная выручка: июнь 9,6, июль 10,2, август 10,8, сентябрь 12,8 миллиона рублей.">' + [["Июн", "9,6", 60], ["Июл", "10,2", 68], ["Авг", "10,8", 76], ["Сен", "12,8", 94]].map(function (v) { return '<div class="chart-bar" style="height:' + v[2] + '%"><b>' + v[1] + '</b><span>' + v[0] + '</span></div>'; }).join("") + '</div>'; };
  P.dirtyBar = function (key) { return '<div class="editor-status"><span data-dirty-status="' + key + '">' + (P.state.dirty[key] ? "Есть изменения" : "") + '</span><span data-editor-position>Строка 1, столбец 1</span></div>'; };
}());
