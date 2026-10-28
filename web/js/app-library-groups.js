(function () {
  'use strict';
  var names = { core: 'Core', resources: 'Ресурсы', skills: 'Навыки', tools: 'Инструменты',
    excel: 'Excel', word: 'Word', powerpoint: 'PowerPoint', outlook: 'Outlook', html: 'HTML',
    plugins: 'Плагины', workspace: 'Проекты', web: 'Web', office: 'Office',
    plan: 'Планы', task: 'Задачи', prompts: 'Инструкции', chat: 'Чаты' };
  function path(id, kind) {
    var parts = String(id || '').split('.').filter(Boolean);
    if (kind === 'skills' && parts[0] === 'skills') parts.shift();
    parts.pop();
    return parts.length ? parts : ['other'];
  }
  function category(id) {
    var key = path(id)[0]; return { key: key, title: names[key] || key };
  }
  function tree(rows, kind, leaf, expanded) {
    var root = { children: [], groups: Object.create(null) };
    rows.forEach(function (row) {
      var node = root, prefix = 'library:' + kind;
      path((row.value || {}).Id, kind).forEach(function (part) {
        prefix += ':' + part;
        if (!node.groups[part]) {
          var group = { key: prefix, kind: 'group', title: names[part] || part,
            expanded: expanded(prefix), children: [], groups: Object.create(null) };
          node.groups[part] = group; node.children.push(group);
        }
        node = node.groups[part];
      });
      node.children.push(leaf(row));
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
  window.RNAssistantLibraryGroups = { category: category, path: path, tree: tree };
}());
