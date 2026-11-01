(function () {
  'use strict';
  const D = window.RnaDemo, frames = new Set();
  // Demo-only adapter: existing product preview owns the chat; Dockview owns its tab.
  function appearance(frame) {
    const view = frame.contentWindow, root = document.documentElement;
    if (!view?.applyUiTheme) return;
    view.applyUiTheme({ UiTheme: root.dataset.theme }, false);
    view.RNAssistantAccents.apply(root.dataset.accent === 'default' ? 'green' : root.dataset.accent, false);
  }
  new MutationObserver(() => frames.forEach(appearance)).observe(document.documentElement, {
    attributes: true, attributeFilter: ['data-theme', 'data-accent']
  });
  D.createChatView = function (id) {
    const element = document.createElement('div');
    element.className = 'panel-content product-chat'; element.dataset.view = id;
    if (location.protocol === 'file:') {
      element.innerHTML = '<div class="empty-state"><h2>Чат из локального preview</h2><p>Запустите <code>python3 -B prototypes/serve_ui.py</code> из корня проекта.</p><a href="http://127.0.0.1:8897/">Открыть Demo</a></div>';
      return { element, init() {} };
    }
    const frame = document.createElement('iframe');
    const loading = document.createElement('p'); loading.className = 'chat-preview-loading';
    loading.setAttribute('role', 'status'); loading.textContent = 'Загрузка примера чата…';
    element.appendChild(loading);
    frame.title = 'Production-чат · ' + D.views[id].title;
    frame.dataset.chatPreview = id;
    frame.src = '/preview/web/?dockChat=' + (id === 'summary' ? 'preview-summary' : 'preview-chat') + '&theme=' + document.documentElement.dataset.theme;
    frame.addEventListener('load', () => {
      if (!frame.contentWindow?.applyUiTheme) { loading.textContent = 'Не удалось загрузить чат. Проверьте локальный preview-server.'; return; }
      appearance(frame);
      frame.dataset.ready = 'true'; loading.hidden = true;
      frame.contentDocument.addEventListener('pointerdown', () => D.closeMenus(), true);
      // Only existing fixture links cross into Dockview. No bridge or chat state is copied.
      frame.contentDocument.addEventListener('click', event => {
        const target = event.target.closest('[data-artifact-id="report"], #composerOpenMaterialsButton, #contextMeter');
        if (!target || target.disabled) return;
        event.preventDefault(); event.stopImmediatePropagation();
        const menu = target.closest('details'); if (menu) menu.open = false;
        D.openView(target.id === 'contextMeter' ? 'inspector' : 'report');
      }, true);
    });
    return {
      element,
      init() { frames.add(frame); element.appendChild(frame); },
      dispose() { frames.delete(frame); },
      focus() { frame.contentDocument?.getElementById('chatInput')?.focus({ preventScroll: true }); }
    };
  };
}());
