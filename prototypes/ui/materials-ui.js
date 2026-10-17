(function () {
  "use strict";
  var P = window.Prototype, E = P.escape, I = P.icon, B = P.button, G = P.go;
  var sources = {
    report: P.fixtures.report,
    html: '<!doctype html>\n<html lang="ru"><meta charset="utf-8">\n<style>body{font:16px system-ui;color:#21392a;padding:32px}h1{font-size:28px}.chart{display:flex;align-items:end;gap:24px;height:180px;margin:40px 0}.bar{flex:1;background:#d7e7dc;text-align:center;border-radius:6px 6px 0 0}p{line-height:1.7}</style>\n<h1>Динамика продаж</h1>\n<p>Выручка выросла на 18,4%. Москва и Казань дали основной прирост.</p>\n<div class="chart"><div class="bar" style="height:60%">Июнь · 9,6</div><div class="bar" style="height:68%">Июль · 10,2</div><div class="bar" style="height:76%">Август · 10,8</div><div class="bar" style="height:94%">Сентябрь · 12,8</div></div>\n<p>Миллионы рублей · демонстрационные данные</p></html>',
    json: JSON.stringify({ period: "2026-09", regions: [{ name: "Москва", revenue: 7420000 }, { name: "Казань", revenue: 5380000 }, { name: "Самара", revenue: null }] }, null, 2)
  };
  sources.plan = "# План подготовки отчёта\n\n1. Сверить источники и периоды.\n2. Объяснить изменения показателей.\n3. Подготовить отчёт и проверить выводы.\n";
  sources.tasks = JSON.stringify({ goal: "Подготовить отчёт за сентябрь", status: "active", steps: [{ id: "read", text: "Проверить источники", status: "completed" }, { id: "compare", text: "Сравнить периоды", status: "in_progress", note: "Москва и Казань включены; Самара требует уточнения." }, { id: "report", text: "Собрать итоговый отчёт", status: "pending" }] }, null, 2);
  sources.text = "Заметки к отчёту\n\nСравнить сентябрь с августом.\nУказать состав источников и пропуски.\nНе включать Самару без поля «Выручка».";
  P.initMaterials = function (state) { state.materialId = "report"; state.materialFile = "index.html"; state.materials = P.fixtures.materials.map(function (item) {
      var versions = sources[item.id] == null ? [] : [{ body: sources[item.id], note: "Исходная версия" }];
      if (item.id === "report") versions.unshift({ body: "# Итоги сентября\n\nПервый вариант отчёта.\n", note: "Первый вариант" });
      return Object.assign({ page: 1, versions: versions }, item);
    });
    state.materials.push({ id: "plan", name: "План отчёта.md", type: "План", icon: "layers", owner: "Этот чат", time: "12:38", page: 1, versions: [{ body: sources.plan, note: "Исходный план" }] },
      { id: "tasks", name: "Подготовка отчёта", type: "Задачи", icon: "check", owner: "Этот чат", time: "12:38", page: 1, versions: [] },
      { id: "text", name: "Заметки к отчёту.txt", type: "Текст", icon: "file", owner: "В документе", time: "Вчера", page: 1, versions: [{ body: sources.text, note: "Импортирован из файла" }] });
    var html = state.materials.find(function (item) { return item.id === "html"; });
    html.files = { "index.html": sources.html, "styles.css": "/* Оформление проекта */\nbody { max-width: 900px; margin: auto; }\nh1 { letter-spacing: -.03em; }", "app.js": "// Подключение данных выполняет sandbox preview.\n// В прототипе пример остаётся статичным.\nconst period = '2026-09';", "data/regions.json": sources.json };
    html.fileVersions = {};
    Object.keys(html.files).forEach(function (path) { html.fileVersions[path] = [{ body: html.files[path], note: "Исходная версия" }]; });
  };
  P.materials = function () { return P.state.materials.filter(function (item) { return !P.state.removedMaterials.includes(item.id); }); };
  P.selectedMaterial = function () { return P.state.materials.find(function (item) { return item.id === P.state.materialId; }); };
  P.materialFile = function () { var item = P.selectedMaterial(); return item.files ? (item.files[P.state.materialFile] != null ? P.state.materialFile : "index.html") : ""; };
  P.materialKey = function () { return "material:" + P.state.materialId + (P.materialFile() ? ":" + P.materialFile() : ""); };
  P.materialEditable = function (item) { return item.owner === "Этот чат" && ["Markdown", "План", "Текст", "HTML", "JSON", "CSS", "JavaScript"].includes(item.type) && !(item.files && P.materialFile().startsWith("data/")); };
  P.materialFileSource = function (item, path) { var key = "material:" + item.id + (path ? ":" + path : ""); return P.state.drafts[key] != null ? P.state.drafts[key] : P.state.saved[key] != null ? P.state.saved[key] : path ? item.files[path] : sources[item.id] || item.source || ""; };
  P.materialSource = function () { return P.materialFileSource(P.selectedMaterial(), P.materialFile()); };
  P.materialVersions = function () { var item = P.selectedMaterial(); return item.files ? item.fileVersions[P.materialFile()] : item.versions; };
  P.materialRoute = function () { return "materials/" + P.state.materialId; };
  function viewButton(label, route, selected) { return '<button class="button" data-go="' + route + '"' + (selected ? ' aria-current="page"' : '') + '>' + label + '</button>'; }
  function preview(item) {
    if (item.type === "HTML") {
      var source = P.materialFileSource(item, "index.html"), style = P.materialFileSource(item, "styles.css");
      source = '<meta http-equiv="Content-Security-Policy" content="default-src \'none\'; style-src \'unsafe-inline\'; img-src data:"><style>' + style.replace(/<\/style/gi, "< /style") + '</style>' + source;
      return '<iframe class="material-frame" title="Просмотр HTML" sandbox="" srcdoc="' + E(source) + '"></iframe>';
    }
    var kind = { Markdown: "markdown", "План": "markdown", "Текст": "text", JSON: "json", "Задачи": "task_list", "Изображение": "image", PDF: "pdf", "Коллекция": "collection" }[item.type] || "text";
    return '<div class="product-viewer" data-product-viewer="' + kind + '"></div>';
  }
  P.renderMaterial = function (notice) {
    var s = P.state, sub = s.route.split("/")[1], item = P.selectedMaterial(), editable = P.materialEditable(item), key = P.materialKey(), route = P.materialRoute(), code = sub === "code";
    var menu = [["Обсудить в чате", "discuss", "chat"], [item.owner === "Этот чат" ? "Включить в контекст" : "Добавить в этот чат", "attach-material", "paperclip"], ["Создать независимую копию", "copy-material", "copy"], ["Сведения о материале", "material-info", "file"], ["Экспорт", "materials/export", "download"]];
    if (editable) menu.push(["История версий", "materials/versions", "clock"], ["Скачать файл", "download-material", "download"]);
    if (item.owner === "Этот чат") menu.push("-", ["Убрать из чата", "detach-material", "close"]);
    if (item.owner !== "Все документы") menu.push("-", ["Удалить из документа…", "materials/remove", "trash", "danger-text"]);
    var head = P.header(item.name, "Материалы / " + item.owner, (code && editable ? B("Сохранить", "save-editor", "check", "primary") : "") + P.menu(menu));
    var start = '', end = '', body;
    if (["Markdown", "План", "Текст", "HTML", "JSON", "CSS", "JavaScript", "Задачи"].includes(item.type)) {
      start = '<nav class="view-switch" aria-label="Представление материала">' + viewButton(item.type === "JSON" ? "Данные" : "Просмотр", route, sub === item.id) + viewButton(item.type === "JSON" ? "Редактирование" : "Исходник", "materials/code", code) + '</nav>';
      if (code && editable) end = P.iconButton("Отменить изменения", "discard-editor", "refresh");
      else if (item.type === "HTML" && sub === item.id) end = P.iconButton("Обновить просмотр", "refresh-preview", "refresh");
    }
    if (!start) start = '<span class="muted small">' + E(item.type) + '</span>';
    if (code) body = editable ? P.code(key, P.materialSource()) : '<div class="product-viewer" data-product-viewer="' + (P.materialFile().endsWith('.json') ? 'json' : 'text') + '"></div>';
    else if (sub === "versions") {
      body = '<div class="section-head"><div><h2>История версий</h2><p>Восстановление создаёт новую версию. Существующая история сохраняется.</p></div></div>' + (P.materialVersions() || []).map(function (version, index) {
        return P.info("Версия " + (index + 1), version.note, "clock", index === P.materialVersions().length - 1 ? P.badge("Текущая", "success") : B("Восстановить", "restore-material:" + index, "refresh"));
      }).reverse().join("");
      if (!P.materialVersions().length) body += '<p class="muted small">Сохраните первый вариант, чтобы начать историю.</p>';
    } else if (sub === "remove") body = P.notice("Удалить «" + item.name + "»?", "Удаление затрагивает все ссылки на материал в чатах этого документа. Это отдельное действие от «Убрать из чата». В прототипе меняются только демоданные.", "warning", B("Удалить", "delete-material", "trash", "danger") + G("Отмена", route));
    else if (sub === "export") body = '<div class="form-stack"><h2>Экспорт · ' + E(item.type) + '</h2>' + (item.files ? B("Скачать автономный HTML", "download-html-preview", "download", "primary") + '<button class="button" disabled title="Недоступно в демо">Редактируемый пакет .rna-html.zip</button><p class="small muted">Пакет сохраняет файлы и связи с данными. Его сборка относится к существующему экспорту RNAssistant и будет подключена вместе с хранилищем.</p>' : editable ? B("Скачать файл", "download-material", "download", "primary") : '<p class="small muted">Оригинал файла в демо не загружен. В просмотрщике доступны страницы и текст демонстрационного документа.</p>') + '</div>';
    else body = preview(item);
    if (item.files && code) start = '<details class="popover material-file-picker"><summary class="button ghost" aria-label="Выбрать файл проекта">' + I("code") + '<span class="truncate">' + E(P.materialFile()) + '</span>' + I("down") + '</summary><div class="popover-menu" role="menu" aria-label="Файлы проекта">' + Object.keys(item.files).map(function (path) {
      return '<button type="button" role="menuitemradio" aria-checked="' + (path === P.materialFile()) + '" data-action="material-file:' + encodeURIComponent(path) + '">' + I(path.endsWith(".json") ? "braces" : "code") + '<span>' + E(path) + '</span>' + (path === P.materialFile() ? I("check") : '') + '</button>';
    }).join("") + '</div></details>';
    var readonly = code && !editable ? '<div class="material-readonly">Только чтение. ' + (item.files && P.materialFile().startsWith("data/") ? 'Данные подключены из материала; изменяйте исходный JSON.' : 'Добавьте материал в этот чат или создайте независимую копию.') + '</div>' : '';
    var meta = item.files ? 'HTML-проект · 3 файла и источник данных' : item.type;
    var bar = ["PDF", "Изображение", "Коллекция"].includes(item.type) ? "" : '<div class="editor-bar"><div class="actions">' + start + '</div><div class="actions">' + end + '</div></div>';
    return head + '<section class="editor">' + bar + '<div class="editor-body">'  + notice + readonly + body + '</div>' + (editable ? P.dirtyBar(key) : '<div class="editor-status"><span>' + E(meta) + '</span><span>' + E(item.owner) + ' · только чтение</span></div>') + '</section>';
  };
}());
