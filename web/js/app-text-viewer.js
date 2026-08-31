(function () {
  "use strict";

  function element(tag, className, text) {
    var node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  }

  function asPromise(action, status, successText, failureText) {
    try {
      return Promise.resolve(action()).then(function () {
        status.textContent = successText;
        return true;
      }).catch(function () {
        status.textContent = failureText;
        return false;
      });
    } catch (error) {
      status.textContent = failureText;
      return Promise.resolve(false);
    }
  }

  function lineCount(text) {
    if (!text) return 1;
    var count = 1;
    for (var index = 0; index < text.length; index += 1) {
      if (text.charAt(index) === "\n") count += 1;
    }
    return count;
  }

  function lineNumbers(start, count) {
    var values = [];
    for (var index = 0; index < count; index += 1) values.push(String(start + index));
    return values.join("\n");
  }

  function occurrences(text, query) {
    query = String(query || "").toLowerCase();
    if (!query) return 0;
    text = String(text || "").toLowerCase();
    var count = 0;
    var offset = 0;
    while (offset <= text.length - query.length) {
      var found = text.indexOf(query, offset);
      if (found < 0) break;
      count += 1;
      offset = found + Math.max(1, query.length);
    }
    return count;
  }

  function completenessText(options, showingFull) {
    if (options.draft) return "Несохранённый Markdown draft";
    if (showingFull) return "Полный exact source · " + options.fullText.length + " символов";
    var offset = Number(options.offset || 0);
    var returned = String(options.text || "").length;
    var total = Number(options.totalCharacters || 0);
    var range = returned ? (offset + 1) + "–" + (offset + returned) : "0";
    var suffix = options.sourceComplete === false
      ? " · исходное извлечение обрезано"
      : options.viewerLimitReached ? " · достигнут viewer bound" : "";
    return "Страница " + range + (total ? " из " + total : "") + " символов" + suffix;
  }

  function createText(options) {
    options = options || {};
    var root = element("div", "rn-text-viewer");
    var status = element("div", "rn-text-viewer-status");
    var hasFull = typeof options.fullText === "string" && options.complete === true;
    var displayed = hasFull ? options.fullText : String(options.text || "");
    var displayedStartLine = hasFull ? 1 : Math.max(1, Number(options.startLine || 1));
    status.textContent = completenessText(options, hasFull);
    root.appendChild(status);

    var toolbar = element("div", "rn-text-viewer-toolbar");
    var search = element("input", "rn-text-viewer-search");
    search.type = "search";
    search.placeholder = "Поиск в " + (hasFull ? "полном source" : "текущей странице");
    var searchButton = element("button", "secondary compact", "Найти");
    searchButton.type = "button";
    var searchResult = element("span", "rn-text-viewer-search-result", "");
    searchButton.addEventListener("click", function () {
      var query = String(search.value || "");
      searchResult.textContent = query ? "Совпадений: " + occurrences(displayed, query) : "Введите текст";
    });
    toolbar.appendChild(search);
    toolbar.appendChild(searchButton);
    toolbar.appendChild(searchResult);
    var actionStatus = element("span", "rn-text-viewer-action-status", "");

    function actionButton(label, action, successText, failureText) {
      var button = element("button", "secondary compact", label);
      button.type = "button";
      button.disabled = typeof action !== "function";
      button.addEventListener("click", function () {
        if (button.disabled) return;
        asPromise(function () { return action(); }, actionStatus, successText, failureText);
      });
      toolbar.appendChild(button);
      return button;
    }

    actionButton(hasFull ? "Копировать всё" : "Копировать страницу",
      typeof options.onCopy === "function" ? function () { return options.onCopy(displayed); } : null,
      "Скопировано", "Не удалось скопировать");
    if (!hasFull && options.fullReadAllowed && typeof options.onLoadFull === "function") {
      actionButton("Загрузить полностью", options.onLoadFull, "Полный source загружен", "Полный source недоступен");
    }
    if (hasFull) {
      actionButton("Скачать", typeof options.onDownload === "function"
        ? function () { return options.onDownload(displayed); }
        : null, "Файл подготовлен", "Не удалось скачать");
    }
    if (!hasFull) {
      actionButton("←", options.hasPrevious ? options.onPrevious : null, "Предыдущая страница", "Страница недоступна");
      actionButton("→", options.hasNext ? options.onNext : null, "Следующая страница", "Страница недоступна");
    }
    toolbar.appendChild(actionStatus);
    root.appendChild(toolbar);

    var source = element("div", "rn-text-viewer-source");
    var gutter = element("pre", "rn-text-viewer-lines");
    var content = element("pre", "rn-text-viewer-content");
    gutter.setAttribute("aria-hidden", "true");
    gutter.textContent = lineNumbers(displayedStartLine, lineCount(displayed));
    content.textContent = displayed;
    source.appendChild(gutter);
    source.appendChild(content);
    root.appendChild(source);
    return {
      element: root,
      destroy: function () { root.replaceChildren(); }
    };
  }

  function createMarkdown(options) {
    options = options || {};
    var root = element("div", "rn-markdown-viewer");
    var tabs = element("div", "rn-markdown-viewer-tabs");
    var renderedButton = element("button", "secondary compact", "Просмотр");
    var sourceButton = element("button", "secondary compact", "Источник");
    renderedButton.type = sourceButton.type = "button";
    var body = element("div", "rn-markdown-viewer-body");
    var child = null;
    var canRender = typeof options.fullText === "string" && options.complete === true;
    renderedButton.disabled = !canRender;

    function clear() {
      if (child && typeof child.destroy === "function") child.destroy();
      child = null;
      if (window.clearMarkdownEnhancements) window.clearMarkdownEnhancements(body);
      body.replaceChildren();
    }

    function showSource() {
      clear();
      renderedButton.classList.remove("active");
      sourceButton.classList.add("active");
      child = createText(options);
      body.appendChild(child.element);
    }

    function showRendered() {
      if (!canRender) return showSource();
      clear();
      renderedButton.classList.add("active");
      sourceButton.classList.remove("active");
      var rendered = element("div", "markdown rn-markdown-viewer-rendered");
      rendered.innerHTML = window.markdown(String(options.fullText));
      body.appendChild(rendered);
      if (typeof window.enhanceMarkdown === "function") {
        window.enhanceMarkdown(rendered, { sourceText: String(options.fullText), enableJsonViewer: true });
      }
    }

    renderedButton.addEventListener("click", showRendered);
    sourceButton.addEventListener("click", showSource);
    tabs.appendChild(renderedButton);
    tabs.appendChild(sourceButton);
    root.appendChild(tabs);
    if (!canRender) {
      var note = element("div", "rn-markdown-viewer-note",
        "Markdown preview отключён до полного exact read; сокращённый source не рендерится.");
      root.appendChild(note);
    }
    root.appendChild(body);
    if (canRender && !options.showSourceFirst) showRendered(); else showSource();
    return {
      element: root,
      destroy: function () { clear(); root.replaceChildren(); }
    };
  }

  if (!window.RNAssistantViewerRegistry) throw new Error("Viewer registry is unavailable.");
  window.RNAssistantViewerRegistry.register("text", createText);
  window.RNAssistantViewerRegistry.register("markdown", createMarkdown);
  window.RNAssistantArtifactTextViewers = {
    createText: createText,
    createMarkdown: createMarkdown
  };
}());
