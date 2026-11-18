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

  function occurrenceOffsets(text, query) {
    var offsets = [], needle = String(query || "").toLowerCase(), source = String(text || "").toLowerCase();
    if (!needle) return offsets;
    var offset = 0;
    while (offset <= source.length - needle.length) {
      var found = source.indexOf(needle, offset);
      if (found < 0) break;
      offsets.push(found);
      offset = found + Math.max(1, needle.length);
    }
    return offsets;
  }

  function completenessText(options, showingFull) {
    if (options.draft) return "Несохранённый Markdown draft";
    if (showingFull) return (options.sourceLabel || "Полный exact source") + " · " + options.fullText.length + " символов";
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
    var statusListeners = [];
    root.appendChild(status);

    var toolbar = element("div", "rn-text-viewer-toolbar");
    var searchGroup = element("div", "rn-text-viewer-find");
    var actions = element("div", "rn-text-viewer-actions");
    var search = element("input", "rn-text-viewer-search");
    search.type = "search";
    search.placeholder = "Поиск в " + (hasFull ? "полном source" : "текущей странице");
    var searchResult = element("span", "rn-text-viewer-search-result", "");
    searchResult.setAttribute("role", "status");
    search.addEventListener("input", function () {
      var query = String(search.value || "");
      searchResult.textContent = query ? "Совпадений: " + occurrences(displayed, query) : "";
    });
    searchGroup.appendChild(search);
    searchGroup.appendChild(searchResult);
    toolbar.appendChild(searchGroup);
    var actionStatus = element("span", "rn-text-viewer-action-status", "");

    function actionButton(label, action, successText, failureText) {
      var button = element("button", "secondary compact", label);
      button.type = "button";
      button.disabled = typeof action !== "function";
      button.addEventListener("click", function () {
        if (button.disabled) return;
        asPromise(function () { return action(); }, actionStatus, successText, failureText);
      });
      actions.appendChild(button);
      return button;
    }

    if (options.showCopy !== false) {
      actionButton(hasFull ? "Копировать всё" : "Копировать страницу",
        typeof options.onCopy === "function" ? function () { return options.onCopy(displayed); } : null,
        "Скопировано", "Не удалось скопировать");
    }
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
    actions.appendChild(actionStatus);
    toolbar.appendChild(actions);
    if (options.toolbar !== false) root.appendChild(toolbar);

    var source = element("div", "rn-text-viewer-source"), editor = null;
    if (options.code && window.RNAssistantCodeEditor && window.CodeMirror) {
      root.classList.add("has-code-editor");
      root.appendChild(source);
      editor = RNAssistantCodeEditor.mount(source, { text: displayed, startLine: displayedStartLine,
        path: options.path, mode: options.mode });
    } else {
    var gutter = element("pre", "rn-text-viewer-lines");
    var content = element("pre", "rn-text-viewer-content");
    gutter.setAttribute("aria-hidden", "true");
    gutter.textContent = lineNumbers(displayedStartLine, lineCount(displayed));
    content.textContent = displayed;
    source.appendChild(gutter);
    source.appendChild(content);
    root.appendChild(source);
    }
    function textStatus() {
      var totalLines = lineCount(displayed);
      if (editor && editor.editor && typeof editor.editor.getCursor === "function") {
        var cursor = editor.editor.getCursor();
        return "Стр. " + (displayedStartLine + Number(cursor.line || 0)) + ", стлб. " + (Number(cursor.ch || 0) + 1) +
          " · " + totalLines + " строк · " + displayed.length + " символов";
      }
      return totalLines + " строк · " + displayed.length + " символов";
    }
    function emitStatus() {
      var value = textStatus();
      statusListeners.slice().forEach(function (listener) { listener(value); });
    }
    if (editor && editor.editor && typeof editor.editor.on === "function") editor.editor.on("cursorActivity", emitStatus);
    var searchIndex = -1, previousQuery = "";
    function searchText(query, direction) {
      query = String(query || "").trim();
      var offsets = occurrenceOffsets(displayed, query);
      if (!query || !offsets.length) { searchIndex = -1; previousQuery = query; return { count: 0, index: 0 }; }
      if (query !== previousQuery || direction === 0) searchIndex = direction < 0 ? offsets.length - 1 : 0;
      else searchIndex = (searchIndex + (direction < 0 ? -1 : 1) + offsets.length) % offsets.length;
      previousQuery = query;
      var offset = offsets[searchIndex];
      if (editor && editor.editor && typeof editor.editor.posFromIndex === "function") {
        var from = editor.editor.posFromIndex(offset), to = editor.editor.posFromIndex(offset + query.length);
        editor.editor.setSelection(from, to);
        editor.editor.scrollIntoView({ from: from, to: to }, 60);
      } else if (content && content.firstChild && window.getSelection && document.createRange) {
        var range = document.createRange();
        range.setStart(content.firstChild, offset); range.setEnd(content.firstChild, offset + query.length);
        var selection = window.getSelection(); selection.removeAllRanges(); selection.addRange(range);
      }
      return { count: offsets.length, index: searchIndex };
    }
    return {
      element: root,
      search: searchText,
      subscribeStatus: function (listener) {
        if (typeof listener !== "function") return function () {};
        statusListeners.push(listener); listener(textStatus());
        return function () { var index = statusListeners.indexOf(listener); if (index >= 0) statusListeners.splice(index, 1); };
      },
      revealLine: function (line) {
        var index = Math.max(0, Number(line || 1) - displayedStartLine);
        if (editor && editor.editor) {
          editor.editor.setCursor({ line: index, ch: 0 });
          editor.editor.scrollIntoView({ line: index, ch: 0 }, 40);
          editor.focus();
        } else if (content && window.getComputedStyle) {
          var height = parseFloat(window.getComputedStyle(content).lineHeight);
          if (Number.isFinite(height)) source.scrollTop = index * height;
        }
      },
      destroy: function () { if (editor) editor.destroy(); root.replaceChildren(); }
    };
  }

  function createMarkdown(options) {
    options = options || {};
    var root = element("div", "rn-markdown-viewer");
    var toolbar = element("div", "rn-markdown-viewer-toolbar");
    var tabs = element("div", "view-switch rn-markdown-viewer-tabs");
    tabs.setAttribute("role", "group");
    tabs.setAttribute("aria-label", "Представление Markdown");
    var renderButton = element("button", "icon-btn compact rn-markdown-viewer-mode");
    var sourceButton = element("button", "icon-btn compact rn-markdown-viewer-mode");
    var graphButton = element("button", "icon-btn compact rn-markdown-viewer-mode");
    renderButton.title = "Просмотр Markdown";
    renderButton.setAttribute("aria-label", "Просмотр Markdown");
    sourceButton.title = "Исходник Markdown — только чтение";
    graphButton.title = "Граф заголовков и явных ссылок";
    sourceButton.setAttribute("aria-label", "Исходник Markdown");
    graphButton.setAttribute("aria-label", "Граф связей Markdown");
    if (window.RNAssistantIcons) renderButton.innerHTML = window.RNAssistantIcons.render("play");
    else renderButton.textContent = "▶";
    if (window.RNAssistantIcons) sourceButton.innerHTML = window.RNAssistantIcons.render("code");
    else sourceButton.textContent = "</>";
    if (window.RNAssistantIcons) graphButton.innerHTML = window.RNAssistantIcons.render("graph");
    else graphButton.textContent = "◎";
    renderButton.type = sourceButton.type = graphButton.type = "button";
    var layout = element("div", "rn-markdown-viewer-layout");
    var navigation = element("aside", "rn-markdown-viewer-navigation");
    navigation.setAttribute("aria-label", "Оглавление и ссылки");
    var tree = element("div", "rn-markdown-viewer-tree");
    navigation.appendChild(tree);
    var body = element("div", "rn-markdown-viewer-body");
    var note = element("div", "rn-markdown-viewer-note");
    var noteText = element("span", "rn-markdown-viewer-note-text");
    note.appendChild(noteText);
    note.hidden = true;
    note.setAttribute("role", "status");
    var presentation = options.presentation || {};
    var child = null, rendered = null, analysis = null, pending = false, disposed = false, generation = 0;
    var compactLayout = null, compactAutoCollapsed = false;
    var navigationExternal = false, navigationAvailabilityListeners = [];
    var statusListeners = [], childStatusUnsubscribe = null, navigationQuery = "";
    var full = typeof options.fullText === "string" && options.complete === true && options.sourceComplete !== false;
    var sourceText = full ? options.fullText : String(options.text || "");
    var mode = ["md", "source", "graph"].indexOf(presentation.mode) >= 0 ? presentation.mode : "md";
    var needsAnalysis = options.requireAnalysis === true || typeof options.onAnalyze === "function";
    var expanded = presentation.expanded || (presentation.expanded = {});
    var navigationButton = element("button", "icon-btn compact rn-markdown-viewer-navigation-toggle");
    navigationButton.type = "button";
    navigationButton.title = "Оглавление и ссылки";
    navigationButton.setAttribute("aria-label", "Оглавление и ссылки");
    if (window.RNAssistantIcons) navigationButton.innerHTML = window.RNAssistantIcons.render("menu");
    else navigationButton.textContent = "≡";
    var actions = element("div", "rn-markdown-viewer-actions");
    var retry = element("button", "button compact rn-markdown-viewer-retry", "Повторить анализ");
    retry.type = "button";
    retry.hidden = true;
    retry.addEventListener("click", function () { analyze(); });
    note.appendChild(retry);
    function syncNote() { note.hidden = !noteText.textContent && retry.hidden; note.dataset.error = String(retry.hidden === false); }
    function setNote(text) { noteText.textContent = text || ""; syncNote(); }
    if (typeof options.onCopy === "function") {
      var copy = element("button", "icon-btn compact rn-markdown-viewer-copy");
      copy.title = full ? "Копировать содержимое" : "Копировать фрагмент";
      copy.setAttribute("aria-label", copy.title);
      if (window.RNAssistantIcons) copy.innerHTML = window.RNAssistantIcons.render("copy");
      else copy.textContent = "Копировать";
      var copyStatus = element("span", "rn-text-viewer-action-status", "");
      copy.type = "button";
      copy.addEventListener("click", function () {
        asPromise(function () { return options.onCopy(sourceText); },
          copyStatus, "Скопировано", "Не удалось скопировать");
      });
      actions.appendChild(copy);
      actions.appendChild(copyStatus);
    }
    function clear() {
      if (childStatusUnsubscribe) childStatusUnsubscribe();
      childStatusUnsubscribe = null;
      if (child && typeof child.destroy === "function") child.destroy();
      child = null;
      rendered = null;
      if (window.clearMarkdownEnhancements) window.clearMarkdownEnhancements(body);
      body.replaceChildren();
    }

    function showSource() {
      child = createText(Object.assign({}, options, { showCopy: false, code: true, mode: "markdown" }));
      if (child && typeof child.subscribeStatus === "function") childStatusUnsubscribe = child.subscribeStatus(function (value) {
        if (mode === "source") publishStatus(value);
      });
      body.appendChild(child.element);
      emitStatus();
    }

    function scrollToAnchor(anchor) {
      if (!rendered) return;
      var nodes = rendered.querySelectorAll("[id]");
      for (var index = 0; index < nodes.length; index += 1) {
        if (nodes[index].getAttribute("id") === anchor) {
          nodes[index].scrollIntoView({ block: "start" });
          break;
        }
      }
    }

    function showRendered() {
      rendered = element("div", "markdown rn-markdown-viewer-rendered");
      // Resource documents use the shared host analysis. Message-only viewers keep
      // the existing streaming renderer; it is never a fallback for saved documents.
      var html = analysis ? analysis.html : window.markdown(String(options.fullText));
      if (needsAnalysis) {
        if (!window.DOMPurify) throw new Error("Безопасный просмотр Markdown недоступен.");
        html = window.DOMPurify.sanitize(html, { USE_PROFILES: { html: true } });
      }
      rendered.innerHTML = html;
      if (needsAnalysis) {
        Array.prototype.forEach.call(rendered.querySelectorAll("a[href]"), function (link) {
          var destination = link.getAttribute("href");
          link.removeAttribute("href");
          link.setAttribute("role", "link");
          link.setAttribute("tabindex", "0");
          link.title = destination;
          function follow(event) {
            if (event.type === "keydown" && event.key !== "Enter") return;
            event.preventDefault();
            if (destination.charAt(0) === "#") {
              try { scrollToAnchor(decodeURIComponent(destination.slice(1))); } catch (ignore) {}
            } else setNote("Ссылка: " + destination);
          }
          link.addEventListener("click", follow);
          link.addEventListener("keydown", follow);
        });
      }
      body.appendChild(rendered);
      if (typeof window.enhanceMarkdown === "function") {
        window.enhanceMarkdown(rendered, { sourceText: String(options.fullText), enableJsonViewer: true });
      }
    }

    function renderGraph() {
      var graph = element("div", "rn-markdown-graph");
      var sections = analysis.sections.slice(0, 60), links = analysis.links.filter(function (link) { return !link.isImage; }).slice(0, 120);
      var destinations = [], destinationIndex = Object.create(null);
      links.forEach(function (link) {
        if (!link.destination || destinationIndex[link.destination] != null || destinations.length >= 24) return;
        destinationIndex[link.destination] = destinations.length; destinations.push(link.destination);
      });
      var height = Math.max(420, (Math.max(sections.length, destinations.length) + 2) * 44);
      var svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
      svg.classList.add("rn-markdown-graph-svg"); svg.setAttribute("viewBox", "0 0 1000 " + height);
      svg.setAttribute("role", "img"); svg.setAttribute("aria-label", "Граф заголовков и явных ссылок Markdown");
      var sectionPosition = Object.create(null), nodes = [];
      var documentNode = { x: 30, y: 30, w: 150, h: 34, title: options.path || "Документ", kind: "document" };
      nodes.push(documentNode);
      sections.forEach(function (section, index) {
        var position = { x: 235 + Math.max(0, Number(section.level || 1) - 1) * 105, y: 30 + index * 44,
          w: 150, h: 32, title: section.title || "Без названия", section: section };
        sectionPosition[section.id] = position; nodes.push(position);
      });
      var destinationPosition = Object.create(null);
      destinations.forEach(function (destination, index) {
        var position = { x: 850, y: 30 + index * 44, w: 130, h: 32, title: destination };
        destinationPosition[destination] = position; nodes.push(position);
      });
      function line(from, to) {
        if (!from || !to) return;
        var value = document.createElementNS("http://www.w3.org/2000/svg", "line");
        value.setAttribute("x1", from.x + from.w); value.setAttribute("y1", from.y + from.h / 2);
        value.setAttribute("x2", to.x); value.setAttribute("y2", to.y + to.h / 2); value.classList.add("rn-markdown-graph-edge"); svg.appendChild(value);
      }
      sections.forEach(function (section) {
        line(section.parentId && sectionPosition[section.parentId] || documentNode, sectionPosition[section.id]);
      });
      var edgeCount = sections.length;
      links.forEach(function (link) {
        var target = destinationPosition[link.destination]; if (!target) return;
        var source = documentNode;
        sections.forEach(function (section) {
          if (link.start >= section.start && link.start < section.end && (!source.section || section.level > source.section.level))
            source = sectionPosition[section.id];
        });
        line(source, target); edgeCount++;
      });
      nodes.forEach(function (position) {
        var group = document.createElementNS("http://www.w3.org/2000/svg", "g");
        group.classList.add("rn-markdown-graph-node"); group.setAttribute("transform", "translate(" + position.x + " " + position.y + ")");
        var rect = document.createElementNS("http://www.w3.org/2000/svg", "rect"); rect.setAttribute("width", position.w); rect.setAttribute("height", position.h); rect.setAttribute("rx", "7");
        var text = document.createElementNS("http://www.w3.org/2000/svg", "text"); text.setAttribute("x", "9"); text.setAttribute("y", "21");
        text.textContent = String(position.title || "").length > 24 ? String(position.title).slice(0, 23) + "…" : String(position.title || "");
        group.append(rect, text); svg.appendChild(group);
      });
      graph.appendChild(svg);
      var bounded = analysis.sections.length > sections.length || analysis.links.filter(function (link) { return !link.isImage; }).length > links.length || destinations.length === 24;
      if (bounded || !links.length) {
        var note = element("div", "rn-markdown-graph-note", !links.length ? "В документе нет явных ссылок; показана структура заголовков." :
          "Граф ограничен первыми 60 заголовками, 24 адресами и 120 ссылками.");
        graph.appendChild(note);
      }
      body.appendChild(graph);
    }

    function show(nextMode) {
      if (disposed) return;
      mode = nextMode;
      if (["md", "source", "graph"].indexOf(mode) < 0) mode = "md";
      if (!full || needsAnalysis && !analysis) mode = "source";
      if (mode === "graph" && !analysis) mode = "source";
      presentation.mode = mode;
      clear();
      [[renderButton, "md"], [sourceButton, "source"], [graphButton, "graph"]].forEach(function (item) {
        item[0].classList.toggle("active", item[1] === mode);
        item[0].setAttribute("aria-pressed", String(item[1] === mode));
      });
      try {
        if (mode === "md") showRendered();
        else if (mode === "graph") renderGraph();
        else showSource();
      } catch (error) {
        setNote(error.message || "Не удалось открыть представление.");
        if (mode !== "source") show("source");
      }
      emitStatus();
    }

    function setNavigationVisibility() {
      var available = !!analysis && !!window.RNAssistantNavigationTree;
      var hidden = !available || presentation.navigationCollapsed === true;
      navigation.hidden = hidden;
      root.classList.toggle("has-markdown-navigation", !navigationExternal && !hidden);
      navigationButton.disabled = !available;
      navigationButton.setAttribute("aria-expanded", String(!hidden));
    }

    function syncCompactLayout() {
      if (navigationExternal) return;
      var compact = root.clientWidth > 0 && root.clientWidth <= 640;
      if (compactLayout === null && compact && presentation.navigationCollapsed === undefined) {
        presentation.navigationCollapsed = true;
        compactAutoCollapsed = true;
      } else if (compactLayout === false && compact) {
        if (presentation.navigationCollapsed !== true) {
          presentation.navigationCollapsed = true;
          compactAutoCollapsed = true;
        }
      } else if (compactLayout === true && !compact && compactAutoCollapsed) {
        presentation.navigationCollapsed = false;
        compactAutoCollapsed = false;
      }
      compactLayout = compact;
      setNavigationVisibility();
    }

    function renderNavigation() {
      if (!window.RNAssistantNavigationTree || !analysis) { setNavigationVisibility(); return; }
      var sections = [], byId = Object.create(null);
      analysis.sections.forEach(function (section) {
        var node = { key: "section:" + section.id, title: section.title || "Без названия",
          kind: "item", section: section, children: [], selectable: true,
          expanded: expanded[section.id] !== false };
        byId[section.id] = node;
        var parent = section.parentId && byId[section.parentId];
        if (parent) { parent.kind = "parent"; parent.children.push(node); }
        else sections.push(node);
      });
      var groups = [{ key: "contents", title: "Оглавление", kind: "group", expanded: expanded.contents !== false,
        badge: String(analysis.sections.length), children: sections }];
      var targets = [], linksByTarget = Object.create(null);
      analysis.links.forEach(function (link, index) {
        if (link.isImage) return;
        var target = linksByTarget[link.destination];
        if (!target) {
          target = { key: "target:" + index, title: link.destination || "Пустая ссылка", kind: "group",
            expanded: !!expanded["target:" + index], children: [] };
          linksByTarget[link.destination] = target; targets.push(target);
        }
        target.children.push({ key: "link:" + index, title: (link.text || link.destination) + " · строка " + link.line,
          kind: "item", link: link });
      });
      if (targets.length) groups.push({ key: "links", title: "Ссылки в документе", kind: "group",
        badge: String(targets.length), expanded: !!expanded.links, children: targets });
      var query = navigationQuery.toLowerCase();
      function filtered(node) {
        var children = (node.children || []).map(filtered).filter(Boolean);
        var own = node.key === "contents" || node.key === "links" ? "" :
          [node.title, node.link && node.link.destination].filter(Boolean).join(" ").toLowerCase();
        if (query && own.indexOf(query) < 0 && !children.length) return null;
        node.children = children;
        if (children.length) node.kind = "parent";
        return node;
      }
      if (query) groups = groups.map(filtered).filter(Boolean);
      window.RNAssistantNavigationTree.render(tree, groups, {
        label: "Оглавление и ссылки", reserveToggleSpace: true, lazyChildren: true,
        onToggle: function (node, value) { expanded[node.section ? node.section.id : node.key] = value; },
        onSelect: function (node) {
          if (node.section) { show("md"); scrollToAnchor(node.section.anchor); }
          else if (node.link) { show("source"); if (child && child.revealLine) child.revealLine(node.link.line); }
          if (root.clientWidth > 0 && root.clientWidth <= 640) {
            presentation.navigationCollapsed = true; setNavigationVisibility();
          }
          return true;
        }
      });
      setNavigationVisibility();
    }

    function acceptAnalysis(result) {
      if (!result || result.characterCount !== sourceText.length || typeof result.profile !== "string" ||
          typeof result.html !== "string" || !Array.isArray(result.sections) || !Array.isArray(result.links)) {
        throw new Error("Структура Markdown не соответствует открытому тексту.");
      }
      analysis = result;
      renderButton.disabled = graphButton.disabled = false;
      renderNavigation();
      navigationAvailabilityListeners.slice().forEach(function (listener) { listener(true); });
      syncCompactLayout();
      emitStatus();
    }

    function searchRendered(query, direction) {
      if (!rendered) return { count: 0, index: 0 };
      rendered.querySelectorAll("mark.rn-markdown-search-match").forEach(function (mark) {
        var parent = mark.parentNode; if (!parent) return;
        parent.replaceChild(document.createTextNode(mark.textContent || ""), mark); parent.normalize();
      });
      query = String(query || "").trim();
      if (!query) return { count: 0, index: 0 };
      var walker = document.createTreeWalker(rendered, NodeFilter.SHOW_TEXT), matches = [], current;
      while ((current = walker.nextNode())) {
        if (!current.nodeValue || current.parentElement && /^(SCRIPT|STYLE)$/.test(current.parentElement.tagName)) continue;
        occurrenceOffsets(current.nodeValue, query).forEach(function (offset) { matches.push({ node: current, offset: offset }); });
      }
      var marks = [];
      matches.slice().reverse().forEach(function (match) {
        var target = match.node, after = target.splitText(match.offset); after.splitText(query.length);
        var mark = document.createElement("mark"); mark.className = "rn-markdown-search-match";
        after.parentNode.replaceChild(mark, after); mark.appendChild(after); marks.unshift(mark);
      });
      if (!marks.length) return { count: 0, index: 0 };
      var index = direction < 0 ? marks.length - 1 : 0;
      if (direction && previousRenderedQuery === query.toLowerCase()) {
        var currentIndex = marks.findIndex(function (mark) { return mark.classList.contains("active"); });
        index = currentIndex < 0 ? 0 : (currentIndex + (direction < 0 ? -1 : 1) + marks.length) % marks.length;
      }
      previousRenderedQuery = query.toLowerCase();
      marks.forEach(function (mark) { mark.classList.remove("active"); });
      marks[index].classList.add("active"); marks[index].scrollIntoView({ block: "center" });
      return { count: marks.length, index: index };
    }
    var previousRenderedQuery = "";

    function markdownStatus() {
      if (mode === "source" && child && typeof child.subscribeStatus === "function") return null;
      if (mode === "graph" && analysis) return "Граф · разделы: " + analysis.sections.length + " · явные ссылки: " + analysis.links.length;
      return (analysis ? "Разделы: " + analysis.sections.length + " · ссылки: " + analysis.links.length : "Markdown") +
        " · " + sourceText.length + " символов";
    }
    function emitStatus() {
      var value = markdownStatus();
      if (value == null) return;
      publishStatus(value);
    }
    function publishStatus(value) {
      statusListeners.slice().forEach(function (listener) { listener(value); });
    }

    function analyze() {
      if (!full || pending || disposed) return;
      if (typeof options.onAnalyze !== "function") {
        setNote("Просмотр Markdown недоступен. Исходник можно прочитать ниже.");
        return;
      }
      pending = true; retry.hidden = true;
      var serial = ++generation, desiredMode = ["md", "source", "graph"].indexOf(presentation.requestedMode) >= 0 ? presentation.requestedMode : "md";
      setNote("Подготовка Markdown…");
      Promise.resolve().then(function () { return options.onAnalyze(sourceText); }).then(function (result) {
        if (disposed || serial !== generation) return;
        acceptAnalysis(result); setNote("");
        show(presentation.requestedMode || desiredMode);
      }).catch(function (error) {
        if (disposed || serial !== generation) return;
        retry.hidden = false; setNote(error.message || "Не удалось подготовить Markdown.");
      }).finally(function () { if (serial === generation) pending = false; });
    }

    renderButton.disabled = graphButton.disabled = !full || needsAnalysis;
    renderButton.addEventListener("click", function () { presentation.requestedMode = "md"; show("md"); });
    sourceButton.addEventListener("click", function () { presentation.requestedMode = "source"; show("source"); });
    graphButton.addEventListener("click", function () { presentation.requestedMode = "graph"; show("graph"); });
    navigationButton.addEventListener("click", function () {
      presentation.navigationCollapsed = !presentation.navigationCollapsed;
      compactAutoCollapsed = false;
      setNavigationVisibility();
    });
    tabs.appendChild(renderButton);
    tabs.appendChild(sourceButton);
    tabs.appendChild(graphButton);
    if (needsAnalysis) toolbar.appendChild(navigationButton);
    toolbar.appendChild(tabs);
    toolbar.appendChild(actions);
    if (options.toolbar !== false) root.appendChild(toolbar);
    root.appendChild(note);
    layout.appendChild(navigation); layout.appendChild(body); root.appendChild(layout);
    if (!full) setNote("Просмотр и оглавление доступны после полного чтения документа.");
    setNavigationVisibility();
    var compactObserver = typeof window.ResizeObserver === "function" ? new window.ResizeObserver(syncCompactLayout) : null;
    if (compactObserver) compactObserver.observe(root);
    else window.addEventListener("resize", syncCompactLayout);
    if (full && needsAnalysis && options.analysis) {
      try { acceptAnalysis(options.analysis); } catch (error) { setNote(error.message); }
    }
    show(mode);
    if (full && needsAnalysis && !analysis) analyze();
    return {
      element: root,
      search: function (query, direction) {
        navigationQuery = String(query || "").trim();
        renderNavigation();
        if (mode === "source" && child && typeof child.search === "function") return child.search(query, direction);
        return searchRendered(query, direction);
      },
      subscribeStatus: function (listener) {
        if (typeof listener !== "function") return function () {};
        statusListeners.push(listener); emitStatus();
        return function () { var index = statusListeners.indexOf(listener); if (index >= 0) statusListeners.splice(index, 1); };
      },
      chrome: {
        navigation: {
          element: navigation,
          available: function () { return !!analysis; },
          subscribeAvailability: function (listener) {
            if (typeof listener !== "function") return function () {};
            navigationAvailabilityListeners.push(listener); listener(!!analysis);
            return function () { var index = navigationAvailabilityListeners.indexOf(listener); if (index >= 0) navigationAvailabilityListeners.splice(index, 1); };
          },
          setCollapsed: function (collapsed) {
            presentation.navigationCollapsed = !!collapsed;
            compactAutoCollapsed = false;
            setNavigationVisibility();
          },
          attach: function (host) {
            navigationExternal = true;
            if (host && navigation.parentElement !== host) host.appendChild(navigation);
            root.classList.remove("has-markdown-navigation");
            setNavigationVisibility();
          }
        }
      },
      destroy: function () {
        disposed = true; generation++;
        if (compactObserver) compactObserver.disconnect();
        else window.removeEventListener("resize", syncCompactLayout);
        if (window.RNAssistantNavigationTree) window.RNAssistantNavigationTree.unmount(tree);
        clear(); root.replaceChildren();
      }
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
