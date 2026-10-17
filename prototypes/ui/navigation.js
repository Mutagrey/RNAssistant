(function () {
  "use strict";
  var P = window.Prototype, E = P.escape, I = P.icon;
  P.sections = [
    { id: "workspace", title: "Рабочая область", short: "Область", icon: "home", route: "chat/main" },
    { id: "materials", title: "Материалы", short: "Материалы", icon: "files", route: "materials/all" },
    { id: "library", title: "Библиотека", short: "Библиотека", icon: "book", route: "library/all" },
    { id: "vba", title: "VBA", short: "VBA", icon: "code", route: "vba/code" },
    { id: "projects", title: "Данные и шаблоны", short: "Данные", icon: "chart", route: "projects/sources" },
    { id: "settings", title: "Настройки", short: "Настройки", icon: "settings", route: "settings/connection" }
  ];
  P.activeSection = function () { var group = P.state.route.split("/")[0]; return group === "chat" ? "workspace" : group; };
  P.initNavigation = function (s) { s.sectionRoutes = {}; s.navSearch = {}; s.navSearchOpen = {}; s.navCollapsed = {}; };
  P.sidebarSearchButton = function (kind, open, query) {
    var label = kind === "tree" ? "Поиск по дереву" : "Поиск в разделе", active = !!query.trim();
    return '<button class="icon-btn sidebar-search-toggle' + (active ? ' has-search-query' : '') + '" data-action="toggle-' + kind + '-search" aria-label="' + label + '" title="' + E(active ? 'Фильтр: ' + query + ' · изменить поиск' : label) + '" aria-description="' + E(active ? 'Активен фильтр: ' + query : '') + '" aria-expanded="' + open + '">' + I("search") + '</button>';
  };
  P.renderSectionRail = function (labels) {
    return P.sections.map(function (section) {
      return (!labels && section.id === "projects" ? '<span class="rail-divider" role="separator"></span>' : '') + '<button class="rail-button' + (section.id === "settings" ? ' rail-settings' : '') + '" data-section="' + section.id + '" aria-label="' + section.title + '" title="' + section.title + '"' + (P.activeSection() === section.id ? ' aria-current="page"' : '') + '>' + I(section.icon) + (labels ? '<span>' + section.short + '</span>' : '') + '</button>';
    }).join("");
  };
  function leaf(label, route, detail, icon, selected, module) { return { label: label, route: route, detail: detail, icon: icon, selected: selected, module: module }; }
  function group(id, label, children) { return { id: id, label: label, children: children }; }
  P.sectionNodes = function (section) {
    var s = P.state, sub = s.route.split("/")[1];
    if (section === "library") return [
      group("skills", "Навыки", [leaf("Анализ продаж", "library/skill", "Навык · Excel", "spark", ["all", "skill", "references"].includes(sub))]),
      group("tools", "Инструменты", [leaf("Форматирование таблицы", "library/tool", "Excel · VBA", "code", ["tool", "schema", "implementation", "test", "docs", "manage"].includes(sub))]),
      group("prompts", "Инструкции", [leaf("Основная инструкция", "library/prompt", "Общая инструкция агента", "book")])
    ];
    if (section === "vba") return [
      group("modules", "Модули", [leaf("FormatSales", "vba/code", "Стандартный модуль", "code", s.vbaModule === "FormatSales", "FormatSales")]),
      group("objects", "Объекты Excel", [leaf("ThisWorkbook", "vba/code", "Книга", "file", s.vbaModule === "ThisWorkbook", "ThisWorkbook"), leaf("Sheet1", "vba/code", "Лист «Продажи»", "file", s.vbaModule === "Sheet1", "Sheet1")])
    ];
    if (section === "materials") return [leaf("Все материалы", "materials/all", "Каталог и фильтры", "files")].concat(["Этот чат", "В документе", "Все документы"].map(function (owner, index) {
      return group("owner-" + index, owner, P.materials().filter(function (item) { return item.owner === owner; }).map(function (item) { var node = leaf(item.name, "materials/" + item.id, item.type + " · " + item.owner, item.icon, item.id === s.materialId && !["all", "create"].includes(sub));
        if (item.files && item.id === s.materialId && sub !== "all") { node.id = "html-files:" + item.id; node.children = Object.keys(item.files).map(function (path) { var file = leaf(path, "materials/code", path.startsWith("data/") ? "Подключённый источник · только чтение" : "Файл проекта", path.endsWith(".json") ? "braces" : "code", sub === "code" && path === P.materialFile()); file.materialFile = path; file.materialId = item.id; return file; }); node.selected = sub === item.id; }
        return node; }));
    }));
    if (section === "projects") return [group("project", P.fixtures.project, [
      leaf("Источники", "projects/sources", "Файлы проекта", "files"),
      group("templates", "Шаблоны", [leaf("Ежемесячные продажи", "projects/templates", "JSON · черновик", "file"), leaf("Снимки черновиков", "projects/drafts", "Сохранённые версии", "clock")]),
      group("dictionary", "Словарь", [leaf("Параметры", "projects/parameters", "Определения полей", "braces"), leaf("Соответствия", "projects/mapping", "Значения источников", "link")]),
      leaf("Обработка", "projects/processing", "Очередь файлов", "play"), leaf("Результаты", "projects/results", "Готовые данные", "chart"), leaf("Форматы и ограничения", "projects/format", "Возможности обработки", "file")
    ])];
    return [["Подключение", "connection", "link"], ["Модель", "model", "spark"], ["Контекст и вложения", "context", "paperclip"], ["Агент", "agent", "chat"], ["Интерфейс", "interface", "panel"], ["Защита", "protection", "shield"], ["Помощь и обслуживание", "diagnostics", "settings"]].map(function (item) { return leaf(item[0], "settings/" + item[1], "", item[2]); });
  };
  function matches(node, query) { return (node.label + " " + (node.detail || "")).toLocaleLowerCase("ru").includes(query) || node.children && node.children.some(function (child) { return matches(child, query); }); }
  P.sectionTreeNodes = function () {
    var section = P.activeSection(), s = P.state, query = (s.navSearch[section] || "").trim().toLocaleLowerCase("ru");
    function convert(nodes, filter, prefix) {
      return nodes.filter(function (node) { return !filter || matches(node, filter); }).map(function (node, index) {
        var key = section + ":" + (node.id || node.materialFile || node.module || node.route || prefix + index);
        return { key: key, title: node.label, tooltip: node.label + (node.detail ? " · " + node.detail : ""), kind: node.children ? (node.route ? "parent" : "group") : "item",
          folder: !!node.children, expanded: !!filter || !s.navCollapsed[key], groupKey: key,
          iconHtml: node.icon ? I(node.icon) : "", route: node.route, module: node.module, materialFile: node.materialFile, materialId: node.materialId,
          selected: node.selected == null ? s.route === node.route : node.selected,
          children: node.children ? convert(node.children, node.label.toLocaleLowerCase("ru").includes(filter) ? "" : filter, key) : undefined };
      });
    }
    return convert(P.sectionNodes(section), query, "");
  };
  P.renderSectionTree = function () { return '<div class="product-tree" data-product-tree="section" aria-label="Дерево раздела"></div>'; };
  P.renderNavigation = function () {
    var s = P.state, section = P.activeSection();
    if (section === "workspace") return P.renderWorkspaceNavigation();
    var title = P.sections.find(function (item) { return item.id === section; }).title;
    var open = !!s.navSearchOpen[section], action = { library: ["Создать в библиотеке", "new-library", "plus"], materials: ["Добавить материал", "create-material", "plus"], projects: ["Открыть проект", "open-project-dialog", "folder"], vba: ["Обновить модули", "refresh-vba", "refresh"] }[section];
    return '<div class="workspace-heading' + (open ? ' searching' : '') + '"><div class="nav-label" title="' + title + '">' + title + '</div><div class="tree-toolbar">' + P.sidebarSearchButton("nav", open, s.navSearch[section] || "") + '<input class="tree-search-input" type="search" data-nav-search="' + section + '" value="' + E(s.navSearch[section] || "") + '" placeholder="Найти…" aria-label="Поиск: ' + title + '"' + (open ? '' : ' hidden') + '>' + (action ? P.iconButton(action[0], action[1], action[2]) : '') + '</div></div><div class="section-tree-scroll">' + P.renderSectionTree() + '</div>';
  };
}());
