(function () {
  'use strict';
  var names = { core: 'Основные', common: 'Общие', resources: 'Ресурсы', skills: 'Навыки', tools: 'Инструменты',
    excel: 'Excel', word: 'Word', powerpoint: 'PowerPoint', outlook: 'Outlook', html: 'HTML',
    plugins: 'Плагины', workspace: 'Проекты', web: 'Web', office: 'Office',
    plan: 'Планы', task: 'Задачи', prompts: 'Инструкции', chat: 'Чаты' };
  function category(id) {
    var parts = String(id || '').split('.').filter(Boolean);
    var key = parts.length > 1 ? parts[0] : 'other';
    return { key: key, title: names[key] || key };
  }
  function tree(rows, kind, leaf, expanded) {
    var root = { children: [], groups: Object.create(null) };
    rows.forEach(function (row) {
      var value = row.value || {};
      var host = value.Host || "Common";
      var part = kind === 'skills' ? host.toLowerCase() : category(value.Id).key;
      var key = 'library:' + kind + ':' + part;
      if (!root.groups[part]) {
        var group = { key: key, kind: 'group', title: names[part] || (kind === 'skills' ? host : part),
          expanded: expanded(key), children: [], groups: Object.create(null) };
        root.groups[part] = group; root.children.push(group);
      }
      root.groups[part].children.push(leaf(row));
    });
    function sort(node) {
      node.children.sort(function (a, b) {
        if (a.kind !== b.kind) return a.kind === 'group' ? -1 : 1;
        if (a.key === 'library:tools:core') return -1;
        if (b.key === 'library:tools:core') return 1;
        return a.title.localeCompare(b.title, 'ru');
      });
      node.children.forEach(function (child) { if (child.children) sort(child); });
      delete node.groups;
    }
    sort(root); return root.children;
  }
  window.RNAssistantLibraryGroups = { category: category, tree: tree };
}());
