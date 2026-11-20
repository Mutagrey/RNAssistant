(function () {
  "use strict";
  var maximumNodes = 400, maximumEdges = 4096;
  var vendorPromise = null;
  function visLibrary() {
    if (window.vis && typeof window.vis.Network === "function") return Promise.resolve(window.vis);
    if (vendorPromise) return vendorPromise;
    var runtime = window.RNAssistantHtmlVendorRuntime;
    if (!runtime) return Promise.reject(new Error("Локальная библиотека графа недоступна."));
    vendorPromise = runtime.loadMainGraphSource().then(function (source) {
      return new Promise(function (resolve, reject) {
        var url = URL.createObjectURL(new Blob([source], { type: "text/javascript" }));
        var script = document.createElement("script");
        function finish() {
          script.remove();
          URL.revokeObjectURL(url);
        }
        script.src = url;
        script.onload = function () {
          finish();
          if (window.vis && typeof window.vis.Network === "function") resolve(window.vis);
          else reject(new Error("Не удалось инициализировать vis.Network."));
        };
        script.onerror = function () {
          finish();
          reject(new Error("Не удалось запустить локальную библиотеку графа."));
        };
        document.head.appendChild(script);
      });
    }).catch(function (error) { vendorPromise = null; throw error; });
    return vendorPromise;
  }

  function node(tag, className, text) {
    var result = document.createElement(tag);
    if (className) result.className = className;
    if (text !== undefined) result.textContent = String(text);
    return result;
  }
  function button(label, title, action, variant) {
    var result = node("button", "button compact" + (variant ? " " + variant : ""), label);
    result.type = "button";
    if (title) result.title = title;
    result.addEventListener("click", action);
    return result;
  }
  function iconButton(label, icon, action) {
    var result = node("button", "icon-btn compact");
    result.type = "button";
    result.title = label;
    result.setAttribute("aria-label", label);
    result.innerHTML = window.RNAssistantIcons.render(icon);
    result.addEventListener("click", action);
    return result;
  }
  function clipped(value, length) {
    return value.length > length ? Array.from(value).slice(0, length - 1).join("") + "…" : value;
  }
  function stateLabel(state) {
    return { resolved: "Найден", unavailable: "Исходник недоступен", missing: "Не найден",
      pending: "Не проверен", ambiguous: "Неоднозначная ссылка", outside: "Вне области",
      external: "Внешняя ссылка", "missing-section": "Раздел отсутствует" }[state] || state;
  }
  function localSnapshot(analysis, focus, title) {
    var graph = { scope: "local", focus: focus, complete: false, scanned: 1,
      notice: "Показаны ссылки открытого документа.", nodes: [
        { id: focus, title: title || "Документ", kind: "document", state: "resolved" }
      ], edges: [] };
    var found = Object.create(null);
    for (var link of (analysis && analysis.links || [])) {
      if (graph.edges.length === maximumEdges) break;
      if (link.isImage || !link.destination) continue;
      var destination = clipped(link.destination, 512);
      if (!found[destination]) {
        if (graph.nodes.length === maximumNodes) continue;
        found[destination] = "ref:" + graph.nodes.length;
        graph.nodes.push({ id: found[destination], title: destination, kind: "reference", state: "pending" });
      }
      graph.edges.push({ from: focus, to: found[destination], kind: "reference", status: "pending",
        line: link.line, start: link.start, destination: destination, label: clipped(link.text || "", 512) });
    }
    return graph;
  }
  function validate(snapshot) {
    if (!snapshot || !Array.isArray(snapshot.nodes) || !Array.isArray(snapshot.edges) ||
        snapshot.nodes.length > maximumNodes || snapshot.edges.length > maximumEdges || typeof snapshot.complete !== "boolean")
      throw new Error("Получен некорректный или слишком большой граф Markdown.");
    var nodes = new Set();
    snapshot.nodes.forEach(function (item) {
      if (!item || typeof item.id !== "string" || !item.id || nodes.has(item.id) ||
          typeof item.title !== "string" || item.title.length > 4096) throw new Error("Некорректный узел графа.");
      nodes.add(item.id);
    });
    snapshot.edges.forEach(function (edge) {
      if (!edge || !nodes.has(edge.from) || !nodes.has(edge.to) || typeof edge.destination !== "string")
        throw new Error("Граф содержит связь с неизвестной целью.");
    });
    return snapshot;
  }

  function mount(host, options) {
    var destroyed = false, request = null, network = null, graph = null, byId = null, visible = null;
    var presentation = options.presentation || {};
    var scope = presentation.graphScope === "all" ? "all" : "local";
    var layout = presentation.graphLayout === "hierarchy" ? "hierarchy" : "network";
    var focus = options.focus || "", selected = presentation.graphSelection || focus;
    var scoped = typeof options.onGraphData === "function";
    var root = node("section", "rna-md-graph");
    root.setAttribute("aria-label", "Граф связей Markdown");
    var toolbar = node("div", "rna-md-graph-toolbar");
    var scopePicker = node("div", "view-switch");
    scopePicker.setAttribute("aria-label", "Область графа");
    var local = button("Рядом", "Текущий документ и его непосредственные связи", function () { changeScope("local"); });
    var all = button("Все", "Все просмотренные документы области", function () { changeScope("all"); });
    var hierarchy = iconButton("Иерархия", "fork", function () {
      layout = presentation.graphLayout = layout === "hierarchy" ? "network" : "hierarchy";
      draw();
    });
    var fit = iconButton("Вписать", "fit", function () { if (network) network.fit(); });
    var refresh = iconButton("Обновить", "refresh", function () { fetchGraph(true); });
    scopePicker.append(local, all);
    if (scoped) toolbar.appendChild(scopePicker);
    var controls = node("div", "rna-md-graph-actions");
    controls.append(hierarchy, fit);
    if (scoped) controls.appendChild(refresh);
    toolbar.appendChild(controls);
    var stage = node("div", "rna-md-graph-stage");
    var canvas = node("div", "rna-md-graph-canvas");
    canvas.setAttribute("aria-label", "Интерактивная схема документов");
    var info = node("aside", "rna-md-graph-info");
    info.setAttribute("aria-label", "Связи выбранного документа");
    stage.append(canvas, info);
    root.append(toolbar, stage);
    host.replaceChildren(root);

    function reportStatus(text, error) {
      if (options.onStatus) options.onStatus(text, !!error);
    }
    function changeScope(value) {
      scope = presentation.graphScope = value;
      draw();
    }
    function select(id, fromCanvas) {
      selected = presentation.graphSelection = visible && visible.has(id) ? id : focus;
      if (!fromCanvas && network) network.selectNodes(selected ? [selected] : []);
      showInfo();
    }
    function open(item) {
      if (item && item.kind === "document" && item.state === "resolved" && item.id !== focus && options.onOpen)
        options.onOpen(item);
    }
    function showInfo() {
      info.replaceChildren();
      var item = byId.get(selected);
      if (!item) {
        info.appendChild(node("p", "", graph.complete ? "В папке нет файлов Markdown." : "Документы Markdown не прочитаны."));
        return;
      }
      info.appendChild(node("strong", "rna-md-graph-info-title", item.title));
      if (item.path) info.appendChild(node("small", "rna-md-graph-meta", item.path));
      if (item.state !== "resolved" || item.historical) {
        var state = node("small", "rna-md-graph-meta", item.historical ? "Историческая версия" : stateLabel(item.state));
        if (item.issue) state.title = item.issue;
        info.appendChild(state);
      }
      if (item.revision && graph.scope === "document")
        info.appendChild(node("small", "rna-md-graph-meta", "Версия " + item.revision));
      if (item.kind === "document" && item.state === "resolved" && options.onOpen && item.id !== focus)
        info.appendChild(button("Открыть материал", null, function () { open(item); }));
      function group(label, list, incoming) {
        info.appendChild(node("h4", "", label + " · " + list.length));
        if (!list.length) {
          info.appendChild(node("small", "rna-md-graph-meta", graph.complete ? "Связей нет" : "Нет в проверенной части"));
          return;
        }
        var collection = node("div", "rna-md-graph-mentions");
        list.slice(0, 70).forEach(function (edge) {
          var other = byId.get(incoming ? edge.from : edge.to);
          var caption = clipped(other.title, 112) + " · строка " + edge.line;
          var hint = edge.destination + (edge.status === "resolved" ? "" : " · " + stateLabel(edge.status));
          var row = node("button", "rna-nav-button");
          row.type = "button";
          row.title = hint;
          var label = node("span", "", caption);
          if (edge.status !== "resolved") label.appendChild(node("small", "rna-md-graph-mention-state", stateLabel(edge.status)));
          row.appendChild(label);
          row.addEventListener("click", function () {
            if (edge.from === focus && options.onReveal) options.onReveal(edge.line);
            else if (incoming && options.onOpen) open(other);
            else select(other.id, false);
          });
          collection.appendChild(row);
        });
        if (list.length > 70) collection.appendChild(node("small", "", "Показаны первые 70 упоминаний"));
        info.appendChild(collection);
      }
      group("Исходящие", graph.edges.filter(function (edge) { return edge.from === item.id; }), false);
      group("Обратные ссылки", graph.edges.filter(function (edge) { return edge.to === item.id; }), true);
    }
    function stopNetwork() {
      if (network) network.destroy();
      network = null;
    }
    function draw(keepCamera) {
      if (destroyed || !graph || !window.vis || !window.vis.Network) return;
      var camera = keepCamera && network ? { position: network.getViewPosition(), scale: network.getScale() } : null;
      var positions = camera ? network.getPositions() : null;
      stopNetwork();
      var hierarchical = layout === "hierarchy";
      visible = scope === "all" ? new Set(byId.keys()) : new Set(focus ? [focus] : []);
      if (scope === "local") graph.edges.forEach(function (edge) {
        if (edge.from === focus || edge.to === focus) { visible.add(edge.from); visible.add(edge.to); }
      });
      var styles = getComputedStyle(document.documentElement);
      function color(token, fallback) { return styles.getPropertyValue(token).trim() || fallback; }
      var accent = color("--accent", "#35684b"), ink = color("--ink", "#263238");
      var line = color("--ink-faint", "#7a837e"), warning = color("--warning", "#86571b");
      var background = color("--surface", "#fff"), subtle = color("--surface-subtle", "#f7f8f9");
      var font = color("--font", "system-ui, sans-serif");
      var visNodes = graph.nodes.filter(function (item) { return visible.has(item.id); }).map(function (item) {
        var reference = item.kind === "reference", isFocus = item.id === focus, unavailable = item.state !== "resolved";
        var border = isFocus ? accent : unavailable ? warning : line;
        var pos = positions && positions[item.id];
        return { id: item.id, label: clipped(item.title, 32),
          title: node("span", "", item.title + (item.path ? " · " + item.path : "") + (unavailable ? " · " + stateLabel(item.state) : "")),
          x: pos ? pos.x : undefined, y: pos ? pos.y : undefined,
          shape: reference ? "box" : "dot", size: isFocus ? 17 : 11, borderWidth: isFocus ? 3 : 1.4,
          font: { color: ink, size: isFocus ? 15 : 13, face: font, multi: false },
          color: { border: border, background: isFocus ? accent : reference ? subtle : background,
            highlight: { border: accent, background: isFocus ? accent : subtle },
            hover: { border: accent, background: isFocus ? accent : subtle } },
          margin: reference ? 8 : undefined
        };
      });
      var relations = new Map();
      graph.edges.forEach(function (edge) {
        if (!visible.has(edge.from) || !visible.has(edge.to)) return;
        var key = JSON.stringify([edge.from, edge.to]);
        if (!relations.has(key)) relations.set(key, { first: edge, count: 0, warning: false });
        var relation = relations.get(key);
        relation.count++;
        if (edge.status !== "resolved") relation.warning = true;
      });
      var visEdges = Array.from(relations.values(), function (relation, index) {
        return { id: "edge:" + index, from: relation.first.from, to: relation.first.to,
          arrows: { to: { enabled: true, scaleFactor: 0.65 } },
          dashes: relation.warning, color: { color: relation.warning ? warning : line, opacity: 0.72 },
          width: Math.min(2.6, 1 + relation.count * 0.18),
          label: relation.count > 1 ? String(relation.count) : undefined,
          font: { size: 12, color: ink, face: font, strokeColor: background },
          smooth: hierarchical ? { type: "cubicBezier", forceDirection: "horizontal", roundness: 0.32 } : { type: "continuous" },
          title: node("span", "", relation.first.destination + " · упоминаний: " + relation.count)
        };
      });
      if (visNodes.length) {
        var instance = new window.vis.Network(canvas, { nodes: visNodes, edges: visEdges }, {
          autoResize: true, width: "100%", height: "100%",
          interaction: { hover: true, dragNodes: !hierarchical, keyboard: { enabled: true, bindToWindow: false }, tooltipDelay: 350 },
          layout: hierarchical ? { hierarchical: { direction: "LR", levelSeparation: 200, nodeSpacing: 92,
            treeSpacing: 160, sortMethod: "directed" }, improvedLayout: false } : { randomSeed: 26 },
          physics: hierarchical || camera ? { enabled: false } : { enabled: true,
            stabilization: { enabled: true, iterations: 120, fit: true },
            barnesHut: { gravitationalConstant: -4200, centralGravity: 0.12, springLength: 126, springConstant: 0.035 },
            minVelocity: 0.9, maxVelocity: 20 }
        });
        network = instance;
        if (!hierarchical && !camera) instance.once("stabilizationIterationsDone", function () {
          if (network === instance) instance.setOptions({ physics: { enabled: false } });
        });
        if (camera) instance.moveTo(camera);
        instance.on("click", function (event) { select(event.nodes && event.nodes[0], true); });
        instance.on("doubleClick", function (event) { open(byId.get(event.nodes && event.nodes[0])); });
      }
      local.setAttribute("aria-pressed", String(scope === "local"));
      all.setAttribute("aria-pressed", String(scope === "all"));
      hierarchy.setAttribute("aria-pressed", String(hierarchical));
      select(selected, false);
      var summary = "Узлы " + visNodes.length + " · Связи " + visEdges.length;
      if (graph.scope !== "local" && !graph.complete) summary = "Частично · " + summary;
      if (options.scopeLabel) summary = options.scopeLabel + " · " + summary;
      reportStatus(summary + (graph.notice ? " · " + graph.notice : ""));
    }
    function busy(value) {
      local.disabled = all.disabled = hierarchy.disabled = fit.disabled = value || !graph || !graph.nodes.length;
      refresh.disabled = value;
      root.setAttribute("aria-busy", String(value));
    }
    function fetchGraph(fresh) {
      if (request) request.abort();
      var controller = new AbortController(), signal = controller.signal;
      request = controller;
      busy(true);
      reportStatus("Проверка связей…");
      var source = scoped
        ? Promise.resolve().then(function () { if (!signal.aborted) return options.onGraphData(!!fresh, signal); })
        : Promise.resolve(localSnapshot(options.analysis, focus || (focus = "document"), options.title));
      source.then(function (result) {
        if (signal.aborted) return;
        var next = validate(result);
        return visLibrary().then(function () {
          if (signal.aborted) return;
          graph = next;
          byId = new Map(graph.nodes.map(function (item) { return [item.id, item]; }));
          if (!byId.has(focus)) focus = graph.focus || "";
          draw();
        });
      }).catch(function (error) {
        if (signal.aborted) return;
        stopNetwork();
        graph = null;
        info.replaceChildren();
        reportStatus(error.detail || error.message || "Не удалось проверить связи.", true);
      }).finally(function () { if (request === controller && !destroyed) busy(false); });
    }
    var themeObserver = window.MutationObserver ? new window.MutationObserver(function () { draw(true); }) : null;
    if (themeObserver) themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme", "style"] });
    fetchGraph(false);
    return { element: root, destroy: function () {
      destroyed = true;
      if (request) request.abort();
      if (themeObserver) themeObserver.disconnect();
      stopNetwork();
      host.replaceChildren();
    } };
  }

  window.RNAssistantMarkdownGraph = Object.freeze({ mount: mount, validate: validate, localSnapshot: localSnapshot });
}());
