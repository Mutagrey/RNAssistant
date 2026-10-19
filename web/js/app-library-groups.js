(function () {
  'use strict';
  var namespaces = {
    excel: 'Excel', word: 'Word', powerpoint: 'PowerPoint', outlook: 'Outlook',
    plugins: 'Плагины', workspace: 'Проекты'
  };
  var common = {
    capabilities: 'Возможности', resources: 'Ресурсы', skills: 'Навыки', skill: 'Навыки',
    tools: 'Инструменты', tool: 'Инструменты', html: 'HTML', plan: 'Планы',
    task: 'Задачи', prompts: 'Инструкции', prompt: 'Инструкции',
    vba: 'VBA', file: 'Файлы', files: 'Файлы', chat: 'Чаты',
    questions: 'Вопросы', markdown: 'Markdown'
  };
  function category(id, host) {
    var parts = String(id || '').toLowerCase().split('.');
    var namespace = parts[0];
    if (namespaces[namespace]) return { key: namespace, title: namespaces[namespace] };
    if (namespace === 'common') {
      var family = (parts[1] || '').split('_')[0];
      if (common[family]) return { key: 'common:' + family, title: common[family] };
      return { key: 'common', title: 'Общие' };
    }
    var normalizedHost = String(host || '').toLowerCase();
    if (namespaces[normalizedHost]) return { key: normalizedHost, title: namespaces[normalizedHost] };
    return { key: 'other', title: 'Прочее' };
  }
  window.RNAssistantLibraryGroups = { category: category };
}());
