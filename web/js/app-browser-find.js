(function () {
  "use strict";
  // Presentation of the current native semantic capture, never a second search index.
  function create(options) {
    var current = null, session = null, generation = 0, sequence = 0, request = 0, timer = null, disposed = false;
    var bar = document.createElement('div'); bar.className = 'preview-surface-find'; bar.hidden = true;
    var input = document.createElement('input'); input.type = 'search'; input.maxLength = 160; input.className = 'rna-form-control';
    input.placeholder = 'Найти на странице'; input.setAttribute('aria-label', input.placeholder);
    var status = document.createElement('span'); status.setAttribute('role', 'status'); status.className = 'preview-surface-status';
    function button(label, icon, action) { var node = document.createElement('button'); node.type = 'button'; node.className = 'icon-btn compact';
      node.setAttribute('aria-label', label); node.title = label; node.innerHTML = RNAssistantIcons.render(icon); node.onclick = action; return node; }
    var previous = button('Предыдущее совпадение', 'left', function () { find(-1); });
    var next = button('Следующее совпадение', 'right', function () { find(1); });
    var close = button('Закрыть поиск', 'close', function () { hide(true); options.focusPage(); });
    bar.append(input, previous, next, close, status); options.root.insertBefore(bar, options.content);
    function enabled() { return !disposed && options.isCurrent() && current && current.status === 'Ready' && current.target.capabilities.preview; }
    function find(direction, clear) {
      clearTimeout(timer); if (!enabled() || bar.hidden && !clear) return;
      var id = ++request, query = clear ? '' : input.value.trim(), page = generation;
      previous.disabled = next.disabled = true; if (!clear) status.textContent = query ? 'Поиск…' : '';
      options.command('VisualControl', { visual: { generation: generation, operation: 'find', find: { query: query, direction: direction, requestId: id } } })
        .catch(function (error) { if (!disposed && !bar.hidden && id === request && page === generation) status.textContent = error.detail || error.message; });
    }
    function hide(clear) { clearTimeout(timer); if (clear && enabled()) find(0, true); ++request; bar.hidden = true; status.textContent = ''; options.layout(); }
    function open() { if (!enabled()) return; bar.hidden = false; options.selectionOff(); input.focus(); input.select(); options.layout(); if (input.value) find(0); }
    input.oninput = function () { ++request; clearTimeout(timer); previous.disabled = next.disabled = true; timer = setTimeout(function () { find(0); }, 180); };
    input.onkeydown = function (event) { if (event.key === 'Enter') { event.preventDefault(); event.stopPropagation(); find(event.shiftKey ? -1 : 1); }
      if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); hide(true); options.focusPage(); } };
    function shortcut(event) { if ((event.ctrlKey || event.metaKey) && !event.altKey && event.key.toLowerCase() === 'f' && enabled()) { event.preventDefault(); event.stopPropagation(); open(); } }
    options.root.addEventListener('keydown', shortcut);
    return { open: open, enabled: enabled,
      update: function (value) {
        current = value;
        if (session !== value.sessionId || generation !== value.documentGeneration) { hide(false); session = value.sessionId; generation = value.documentGeneration; sequence = 0; }
        if (!enabled()) { hide(false); return; }
        var visual = value.visual; if (!visual || visual.generation !== generation || visual.sequence <= sequence) return; sequence = visual.sequence;
        if (visual.type === 'rnassistant-visual-inspector-find-open') open();
        var result = visual.find;
        if (bar.hidden || !result || result.requestId !== request || result.query !== input.value.trim()) return;
        status.textContent = result.query ? (result.count ? result.index + ' из ' + result.count : 'Нет совпадений') + ' в доступном тексте' + (result.truncated ? ' · чтение ограничено' : '') : '';
        previous.disabled = next.disabled = !result.count;
      }, deactivate: function () { hide(true); },
      dispose: function () { disposed = true; clearTimeout(timer); ++request; options.root.removeEventListener('keydown', shortcut); bar.remove(); }
    };
  }
  window.RNAssistantBrowserFind = { create: create };
}());
