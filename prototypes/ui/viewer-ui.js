(function () {
  "use strict";
  var P = window.Prototype;
  // Presentation only. Parsing, completeness, resource leases and navigation stay in web/.
  P.viewerUI = {
    mount: function (root, kind, options, controller) {
      var events = new AbortController(), queued = false;
      function icon(control, name, only) {
        if (control.dataset.uiIcon) return;
        control.dataset.uiIcon = name;
        var label = control.getAttribute("aria-label") || control.title || control.textContent;
        if (only) { control.setAttribute("aria-label", label); control.title = label; control.innerHTML = P.icon(name); }
        else control.insertAdjacentHTML("afterbegin", P.icon(name));
      }
      function decorate() {
        root.querySelectorAll('button.secondary, button.rn-json-more, button.rn-json-copy').forEach(function (button) {
          button.classList.add("button", "compact", "ghost");
        });
        root.querySelectorAll('.rn-text-viewer-search, .rn-sequence-input').forEach(function (input) { input.classList.add("control-input"); });
        root.querySelectorAll('.rn-pdf-viewer-tabs, .rn-markdown-viewer-tabs').forEach(function (tabs) {
          tabs.classList.add("view-switch");
          tabs.querySelectorAll("button").forEach(function (button) {
            button.classList.remove("ghost");
            button.setAttribute("aria-pressed", String(button.classList.contains("active")));
          });
        });
        root.querySelectorAll('.rn-text-viewer').forEach(function (reader) {
          var full = kind === "pdf" ? options.textComplete === true && options.sourceComplete === true : options.complete === true && options.sourceComplete !== false;
          var status = reader.querySelector('.rn-text-viewer-status');
          status.hidden = full && !options.draft;
          if (options.draft) status.textContent = "Есть несохранённые изменения";
          else if (!full) status.textContent = status.textContent.replace("exact source", "текст").replace("viewer bound", "лимит просмотра");
          var search = reader.querySelector('.rn-text-viewer-search');
          search.placeholder = "Найти в тексте"; search.setAttribute("aria-label", "Найти в тексте");
          reader.querySelector('.rn-text-viewer-source').setAttribute("role", "document");
          reader.querySelector('.rn-text-viewer-source').setAttribute("aria-label", "Текст документа · только чтение");
          reader.querySelectorAll('.rn-text-viewer-toolbar button').forEach(function (button) {
            var label = button.textContent;
            if (label === "Найти") icon(button, "search", true);
            if (label.startsWith("Копировать")) icon(button, "copy", true);
            if (label === "Скачать") icon(button, "download", true);
          });
        });
        root.querySelectorAll('.rn-resource-viewer-toolbar button').forEach(function (button) { icon(button, "download", false); });
        root.querySelectorAll('.rn-preview-nav').forEach(function (button) {
          button.classList.add("icon-btn"); icon(button, button.classList.contains("rn-preview-nav-next") ? "right" : "left", true);
        });
        root.querySelectorAll('.rn-pdf-thumbnail-rail .rn-sequence-title').forEach(function (title) { if (title.textContent !== "Стр.") title.textContent = "Стр."; });
        var icons = { "zoom-in": "plus", "zoom-out": "minus", reset: "fit", "rotate-left": "rotateLeft", "rotate-right": "rotateRight" };
        Object.keys(icons).forEach(function (key) { root.querySelectorAll('.viewer-' + key).forEach(function (button) { icon(button, icons[key], true); }); });
        root.querySelectorAll('.viewer-one-to-one').forEach(function (button) {
          if (!button.dataset.uiIcon) { button.dataset.uiIcon = "actual"; button.textContent = "1:1"; }
        });
        if (kind === "json") {
          var doc = controller.getDocument();
          root.classList.toggle("json-clean", options.completeness === "full" && doc.ok && !doc.duplicateKeyCount);
          if (!doc.ok && doc.error) {
            var status = root.querySelector('.rn-json-status'), limited = doc.error.code.indexOf("limit.") === 0;
            status.textContent = limited ? "Дерево превышает лимит просмотра. Показан исходный текст." : "Ошибка JSON · символ " + (doc.error.position + 1) + ". Показан исходный текст.";
            status.title = doc.error.message;
          }
        }
      }
      root.addEventListener("keydown", function (event) {
        if (event.key === "Enter" && event.target.matches('.rn-text-viewer-search')) {
          event.preventDefault(); event.target.parentElement.querySelector('button').click();
        }
      }, { signal: events.signal });
      var observer = new MutationObserver(function (records) {
        if (queued || !records.some(function (record) { return Array.from(record.addedNodes).some(function (node) { return node.nodeType === 1; }); })) return;
        queued = true; queueMicrotask(function () { queued = false; if (root.isConnected) decorate(); });
      });
      decorate(); observer.observe(root, { childList: true, subtree: true });
      return { destroy: function () { observer.disconnect(); events.abort(); } };
    }
  };
}());
