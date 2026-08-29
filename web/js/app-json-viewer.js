(function () {
  "use strict";

  var HARD_LIMITS = {
    maxChars: 2000000,
    maxNodes: 50000,
    maxDepth: 128,
    childPageSize: 200,
    maxDomRows: 5000,
    maxInlineStringChars: 2000,
    maxRawRenderChars: 500000,
    maxPrettyChars: 4000000
  };
  var DEFAULT_LIMITS = {
    maxChars: 750000,
    maxNodes: 20000,
    maxDepth: 64,
    childPageSize: 50,
    maxDomRows: 1200,
    maxInlineStringChars: 320,
    maxRawRenderChars: 250000,
    maxPrettyChars: 1500000
  };

  function boundedInteger(value, fallback, minimum, maximum) {
    var number = Number(value);
    if (!isFinite(number)) number = fallback;
    number = Math.floor(number);
    return Math.max(minimum, Math.min(maximum, number));
  }

  function normalizeLimits(input) {
    input = input || {};
    return {
      maxChars: boundedInteger(input.maxChars, DEFAULT_LIMITS.maxChars, 1, HARD_LIMITS.maxChars),
      maxNodes: boundedInteger(input.maxNodes, DEFAULT_LIMITS.maxNodes, 1, HARD_LIMITS.maxNodes),
      maxDepth: boundedInteger(input.maxDepth, DEFAULT_LIMITS.maxDepth, 1, HARD_LIMITS.maxDepth),
      childPageSize: boundedInteger(input.childPageSize, DEFAULT_LIMITS.childPageSize, 1, HARD_LIMITS.childPageSize),
      maxDomRows: boundedInteger(input.maxDomRows, DEFAULT_LIMITS.maxDomRows, 1, HARD_LIMITS.maxDomRows),
      maxInlineStringChars: boundedInteger(input.maxInlineStringChars, DEFAULT_LIMITS.maxInlineStringChars, 16, HARD_LIMITS.maxInlineStringChars),
      maxRawRenderChars: boundedInteger(input.maxRawRenderChars, DEFAULT_LIMITS.maxRawRenderChars, 128, HARD_LIMITS.maxRawRenderChars),
      maxPrettyChars: boundedInteger(input.maxPrettyChars, DEFAULT_LIMITS.maxPrettyChars, 128, HARD_LIMITS.maxPrettyChars),
      shouldCancel: typeof input.shouldCancel === "function" ? input.shouldCancel : null
    };
  }

  function parserError(code, message, position) {
    return { rnJsonError: true, code: code, message: message, position: Math.max(0, position || 0) };
  }

  function parse(source, requestedLimits) {
    source = source === null || source === undefined ? "" : String(source);
    var limits = normalizeLimits(requestedLimits);
    if (source.length > limits.maxChars) {
      return failure(source, parserError("limit.chars", "JSON exceeds the configured character limit.", limits.maxChars), limits, 0);
    }

    var state = { source: source, index: 0, nodes: 0, steps: 0, limits: limits };
    try {
      skipWhitespace(state);
      if (state.index >= source.length) fail(state, "syntax.empty", "JSON is empty.");
      var root = parseValue(state, 0);
      skipWhitespace(state);
      if (state.index !== source.length) fail(state, "syntax.trailing", "Unexpected content after the JSON value.");
      var duplicates = assignPaths(root, "$");
      return {
        ok: true,
        source: source,
        root: root,
        nodeCount: state.nodes,
        duplicateKeyCount: duplicates,
        limits: limits,
        error: null
      };
    } catch (error) {
      if (!error || !error.rnJsonError) throw error;
      return failure(source, error, limits, state.nodes);
    }
  }

  function failure(source, error, limits, nodeCount) {
    return {
      ok: false,
      source: source,
      root: null,
      nodeCount: nodeCount || 0,
      duplicateKeyCount: 0,
      limits: limits,
      error: { code: error.code, message: error.message, position: error.position }
    };
  }

  function checkBudget(state) {
    state.steps += 1;
    if ((state.steps & 1023) === 0 && state.limits.shouldCancel && state.limits.shouldCancel()) {
      throw parserError("cancelled", "JSON parsing was cancelled.", state.index);
    }
  }

  function createNode(state, type, start) {
    state.nodes += 1;
    if (state.nodes > state.limits.maxNodes) {
      throw parserError("limit.nodes", "JSON exceeds the configured node limit.", start);
    }
    return { id: state.nodes, type: type, start: start, end: start, path: "$" };
  }

  function fail(state, code, message) {
    throw parserError(code, message, state.index);
  }

  function skipWhitespace(state) {
    while (state.index < state.source.length) {
      var code = state.source.charCodeAt(state.index);
      if (code !== 0x20 && code !== 0x09 && code !== 0x0a && code !== 0x0d) return;
      state.index += 1;
      checkBudget(state);
    }
  }

  function parseValue(state, depth) {
    if (depth > state.limits.maxDepth) {
      throw parserError("limit.depth", "JSON exceeds the configured depth limit.", state.index);
    }
    checkBudget(state);
    var character = state.source.charAt(state.index);
    if (character === "{") return parseObject(state, depth);
    if (character === "[") return parseArray(state, depth);
    if (character === "\"") return parseString(state);
    if (character === "-" || character >= "0" && character <= "9") return parseNumber(state);
    if (state.source.substr(state.index, 4) === "true") return parseLiteral(state, "true", true, "boolean");
    if (state.source.substr(state.index, 5) === "false") return parseLiteral(state, "false", false, "boolean");
    if (state.source.substr(state.index, 4) === "null") return parseLiteral(state, "null", null, "null");
    fail(state, "syntax.value", "Expected a JSON value.");
  }

  function parseObject(state, depth) {
    var node = createNode(state, "object", state.index);
    node.entries = [];
    state.index += 1;
    skipWhitespace(state);
    if (state.source.charAt(state.index) === "}") {
      state.index += 1;
      node.end = state.index;
      return node;
    }
    while (state.index < state.source.length) {
      checkBudget(state);
      if (state.source.charAt(state.index) !== "\"") fail(state, "syntax.object-key", "Expected a quoted object key.");
      var key = parseString(state);
      skipWhitespace(state);
      if (state.source.charAt(state.index) !== ":") fail(state, "syntax.colon", "Expected ':' after the object key.");
      state.index += 1;
      skipWhitespace(state);
      var value = parseValue(state, depth + 1);
      node.entries.push({ key: key, value: value, occurrence: 1, duplicateCount: 1 });
      skipWhitespace(state);
      var separator = state.source.charAt(state.index);
      if (separator === "}") {
        state.index += 1;
        node.end = state.index;
        return node;
      }
      if (separator !== ",") fail(state, "syntax.object-separator", "Expected ',' or '}' in the object.");
      state.index += 1;
      skipWhitespace(state);
    }
    fail(state, "syntax.object-end", "Unterminated object.");
  }

  function parseArray(state, depth) {
    var node = createNode(state, "array", state.index);
    node.items = [];
    state.index += 1;
    skipWhitespace(state);
    if (state.source.charAt(state.index) === "]") {
      state.index += 1;
      node.end = state.index;
      return node;
    }
    while (state.index < state.source.length) {
      checkBudget(state);
      node.items.push(parseValue(state, depth + 1));
      skipWhitespace(state);
      var separator = state.source.charAt(state.index);
      if (separator === "]") {
        state.index += 1;
        node.end = state.index;
        return node;
      }
      if (separator !== ",") fail(state, "syntax.array-separator", "Expected ',' or ']' in the array.");
      state.index += 1;
      skipWhitespace(state);
    }
    fail(state, "syntax.array-end", "Unterminated array.");
  }

  function parseString(state) {
    var start = state.index;
    var node = createNode(state, "string", start);
    state.index += 1;
    while (state.index < state.source.length) {
      checkBudget(state);
      var code = state.source.charCodeAt(state.index);
      if (code === 0x22) {
        state.index += 1;
        node.end = state.index;
        try {
          // Decode exactly one quoted string token. Containers and numbers never
          // pass through JSON.parse, so duplicate keys and numeric lexemes survive.
          node.value = JSON.parse(state.source.slice(start, node.end));
        } catch (error) {
          throw parserError("syntax.string", "Invalid JSON string.", start);
        }
        return node;
      }
      if (code < 0x20) fail(state, "syntax.string-control", "Unescaped control character in a JSON string.");
      if (code === 0x5c) {
        state.index += 1;
        if (state.index >= state.source.length) fail(state, "syntax.string-escape", "Unterminated JSON escape.");
        var escape = state.source.charAt(state.index);
        if (escape === "u") {
          if (!/^[0-9a-fA-F]{4}$/.test(state.source.substr(state.index + 1, 4))) {
            fail(state, "syntax.string-unicode", "Invalid Unicode escape in a JSON string.");
          }
          state.index += 5;
          continue;
        }
        if ('\"\\/bfnrt'.indexOf(escape) < 0) fail(state, "syntax.string-escape", "Invalid JSON escape.");
      }
      state.index += 1;
    }
    fail(state, "syntax.string-end", "Unterminated JSON string.");
  }

  function parseNumber(state) {
    var start = state.index;
    var source = state.source;
    if (source.charAt(state.index) === "-") state.index += 1;
    if (source.charAt(state.index) === "0") {
      state.index += 1;
      if (isDigit(source.charAt(state.index))) fail(state, "syntax.number-leading-zero", "Leading zero is not allowed in a JSON number.");
    } else {
      if (!isNonZeroDigit(source.charAt(state.index))) fail(state, "syntax.number-integer", "Expected the integer part of a JSON number.");
      while (isDigit(source.charAt(state.index))) { state.index += 1; checkBudget(state); }
    }
    if (source.charAt(state.index) === ".") {
      state.index += 1;
      if (!isDigit(source.charAt(state.index))) fail(state, "syntax.number-fraction", "Expected digits after the decimal point.");
      while (isDigit(source.charAt(state.index))) { state.index += 1; checkBudget(state); }
    }
    var exponent = source.charAt(state.index);
    if (exponent === "e" || exponent === "E") {
      state.index += 1;
      var sign = source.charAt(state.index);
      if (sign === "+" || sign === "-") state.index += 1;
      if (!isDigit(source.charAt(state.index))) fail(state, "syntax.number-exponent", "Expected exponent digits.");
      while (isDigit(source.charAt(state.index))) { state.index += 1; checkBudget(state); }
    }
    var node = createNode(state, "number", start);
    node.end = state.index;
    return node;
  }

  function parseLiteral(state, text, value, type) {
    var node = createNode(state, type, state.index);
    state.index += text.length;
    node.end = state.index;
    node.value = value;
    return node;
  }

  function isDigit(value) { return value >= "0" && value <= "9"; }
  function isNonZeroDigit(value) { return value >= "1" && value <= "9"; }

  function assignPaths(node, path) {
    node.path = path;
    var duplicates = 0;
    if (node.type === "array") {
      node.items.forEach(function (item, index) {
        duplicates += assignPaths(item, path + "[" + index + "]");
      });
      return duplicates;
    }
    if (node.type !== "object") return 0;
    var totals = Object.create(null);
    node.entries.forEach(function (entry) {
      var key = "$" + entry.key.value;
      totals[key] = (totals[key] || 0) + 1;
    });
    var seen = Object.create(null);
    node.entries.forEach(function (entry) {
      var key = "$" + entry.key.value;
      seen[key] = (seen[key] || 0) + 1;
      entry.occurrence = seen[key];
      entry.duplicateCount = totals[key];
      entry.key.path = path + ".<key:" + entry.occurrence + ">";
      var suffix = totals[key] > 1 ? "#" + entry.occurrence : "";
      duplicates += totals[key] > 1 ? 1 : 0;
      duplicates += assignPaths(entry.value, path + "[" + JSON.stringify(entry.key.value) + "]" + suffix);
    });
    return duplicates;
  }

  function raw(document, node) {
    if (!document || !node) return "";
    return document.source.slice(node.start, node.end);
  }

  function format(document, requestedMaxChars) {
    if (!document || !document.ok) {
      return { ok: false, text: "", error: document && document.error ? document.error : { code: "invalid", message: "JSON is invalid.", position: 0 } };
    }
    var maximum = boundedInteger(requestedMaxChars, document.limits.maxPrettyChars, 128, HARD_LIMITS.maxPrettyChars);
    var chunks = [];
    var length = 0;
    function append(value) {
      value = String(value);
      length += value.length;
      if (length > maximum) throw parserError("limit.pretty", "Formatted JSON exceeds the configured output limit.", length);
      chunks.push(value);
    }
    function write(node, depth) {
      if (node.type === "array") {
        if (!node.items.length) { append("[]"); return; }
        append("[\n");
        node.items.forEach(function (item, index) {
          append(repeat("  ", depth + 1)); write(item, depth + 1);
          append(index + 1 < node.items.length ? ",\n" : "\n");
        });
        append(repeat("  ", depth) + "]");
        return;
      }
      if (node.type === "object") {
        if (!node.entries.length) { append("{}"); return; }
        append("{\n");
        node.entries.forEach(function (entry, index) {
          append(repeat("  ", depth + 1)); append(raw(document, entry.key)); append(": ");
          write(entry.value, depth + 1);
          append(index + 1 < node.entries.length ? ",\n" : "\n");
        });
        append(repeat("  ", depth) + "}");
        return;
      }
      append(raw(document, node));
    }
    try {
      write(document.root, 0);
      return { ok: true, text: chunks.join(""), error: null };
    } catch (error) {
      if (!error || !error.rnJsonError) throw error;
      return { ok: false, text: "", error: error };
    }
  }

  function repeat(value, count) {
    var result = "";
    while (count-- > 0) result += value;
    return result;
  }

  function element(tag, className, text) {
    var node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined && text !== null) node.textContent = String(text);
    return node;
  }

  function button(label, className, handler) {
    var node = element("button", className, label);
    node.type = "button";
    node.addEventListener("click", function (event) {
      if (event && event.preventDefault) event.preventDefault();
      if (event && event.stopPropagation) event.stopPropagation();
      handler();
    });
    return node;
  }

  function create(options) {
    options = options || {};
    var limits = normalizeLimits(options.limits);
    var state = {
      source: options.text === null || options.text === undefined ? "" : String(options.text),
      completeness: options.completeness || "full",
      document: null,
      formatted: null,
      mode: options.mode || "tree",
      domRows: 0,
      copyVersion: 0
    };
    var root = element("section", "rn-json-viewer");
    root.setAttribute("data-completeness", state.completeness);
    var toolbar = element("div", "rn-json-toolbar");
    toolbar.setAttribute("role", "toolbar");
    toolbar.setAttribute("aria-label", "JSON viewer");
    var modes = element("div", "rn-json-modes");
    var treeButton = button("Дерево", "rn-json-mode", function () { setMode("tree"); });
    var prettyButton = button("Форматированный", "rn-json-mode", function () { setMode("pretty"); });
    var rawButton = button("Исходный", "rn-json-mode", function () { setMode("raw"); });
    modes.appendChild(treeButton); modes.appendChild(prettyButton); modes.appendChild(rawButton);
    var actions = element("div", "rn-json-actions");
    var collapseButton = button("Свернуть", "rn-json-action", collapseAll);
    var copyButton = button(state.completeness === "full" ? "Копировать всё" : "Копировать preview", "rn-json-action", function () {
      requestCopy(state.source, "source", null);
    });
    actions.appendChild(collapseButton); actions.appendChild(copyButton);
    toolbar.appendChild(modes); toolbar.appendChild(actions);
    var status = element("div", "rn-json-status");
    status.setAttribute("aria-live", "polite");
    var body = element("div", "rn-json-body");
    root.appendChild(toolbar); root.appendChild(status); root.appendChild(body);

    function completenessText() {
      var labels = {
        full: "Полный payload",
        preview: "Ограниченный preview",
        redacted: "Данные скрыты владельцем",
        loading: "Загрузка",
        unloaded: "Не загружено",
        unavailable: "Недоступно",
        corrupt: "Повреждено"
      };
      return labels[state.completeness] || state.completeness;
    }

    function setStatus(message, kind) {
      status.className = "rn-json-status" + (kind ? " " + kind : "");
      status.textContent = completenessText() + (message ? " · " + message : "");
      if (typeof options.onStatus === "function") options.onStatus(message || "", kind || "");
    }

    function parseCurrent() {
      state.document = parse(state.source, limits);
      state.formatted = state.document.ok ? format(state.document, limits.maxPrettyChars) : null;
      if (!state.document.ok && state.mode !== "raw") state.mode = "raw";
      if (state.document.ok && state.mode === "pretty" && !state.formatted.ok) state.mode = "raw";
    }

    function setMode(mode) {
      if (mode === "tree" && !state.document.ok) mode = "raw";
      if (mode === "pretty" && (!state.formatted || !state.formatted.ok)) mode = "raw";
      state.mode = mode;
      render();
    }

    function modeState(node, mode, disabled) {
      node.disabled = !!disabled;
      node.classList.toggle("active", state.mode === mode);
      node.setAttribute("aria-pressed", state.mode === mode ? "true" : "false");
    }

    function render() {
      body.replaceChildren();
      state.domRows = 0;
      modeState(treeButton, "tree", !state.document.ok);
      modeState(prettyButton, "pretty", !state.formatted || !state.formatted.ok);
      modeState(rawButton, "raw", false);
      collapseButton.classList.toggle("hidden", state.mode !== "tree");
      if (state.mode === "tree" && state.document.ok) {
        var tree = element("div", "rn-json-tree");
        tree.setAttribute("role", "tree");
        tree.appendChild(renderNode(state.document.root, null, 1, true));
        body.appendChild(tree);
        setStatus(state.document.nodeCount + " узлов" + (state.document.duplicateKeyCount ? ", повторов ключей: " + state.document.duplicateKeyCount : ""), "ok");
      } else {
        var pre = element("pre", "rn-json-text");
        var shown;
        if (state.mode === "pretty" && state.formatted && state.formatted.ok) {
          shown = state.formatted.text;
          pre.setAttribute("data-json-view", "pretty");
          setStatus("Форматирование сохраняет исходные scalar tokens", "ok");
        } else {
          shown = state.source.slice(0, limits.maxRawRenderChars);
          pre.setAttribute("data-json-view", "raw");
          if (shown.length < state.source.length) shown += "\n\n[display limited to " + limits.maxRawRenderChars + " characters]";
          if (state.document.ok) setStatus("Исходный текст без изменений", "ok");
          else setStatus(errorText(state.document.error), "error");
        }
        pre.textContent = shown;
        body.appendChild(pre);
      }
    }

    function errorText(error) {
      if (!error) return "JSON недоступен";
      return error.message + " Позиция: " + error.position + " (" + error.code + ")";
    }

    function renderNode(node, entry, level, open) {
      if (node.type !== "object" && node.type !== "array") return renderScalar(node, entry, level);
      var details = element("details", "rn-json-node rn-json-container");
      details.open = !!open;
      var summary = element("summary", "rn-json-row");
      summary.setAttribute("role", "treeitem");
      summary.setAttribute("aria-level", level);
      summary.setAttribute("aria-expanded", details.open ? "true" : "false");
      appendKey(summary, entry);
      var bracket = node.type === "object" ? "{" : "[";
      var close = node.type === "object" ? "}" : "]";
      var count = node.type === "object" ? node.entries.length : node.items.length;
      summary.appendChild(element("span", "rn-json-punctuation", bracket));
      summary.appendChild(element("span", "rn-json-count", count + (count === 1 ? " элемент" : " элементов")));
      summary.appendChild(element("span", "rn-json-punctuation", close));
      appendNodeActions(summary, node);
      details.appendChild(summary);
      var children = element("div", "rn-json-children");
      details.appendChild(children);
      var loaded = 0;
      var moreButton = null;
      function loadPage() {
        var list = childEntries(node);
        var target = Math.min(list.length, loaded + limits.childPageSize);
        while (loaded < target && state.domRows < limits.maxDomRows) {
          var child = list[loaded++];
          children.appendChild(renderNode(child.node, child.entry, level + 1, false));
          state.domRows += 1;
        }
        if (moreButton && moreButton.parentNode) moreButton.parentNode.removeChild(moreButton);
        moreButton = null;
        if (loaded < list.length) {
          var remaining = list.length - loaded;
          moreButton = button(state.domRows >= limits.maxDomRows ? "Лимит DOM: " + limits.maxDomRows : "Показать ещё " + Math.min(remaining, limits.childPageSize), "rn-json-more", loadPage);
          moreButton.disabled = state.domRows >= limits.maxDomRows;
          children.appendChild(moreButton);
        }
      }
      details.addEventListener("toggle", function () {
        summary.setAttribute("aria-expanded", details.open ? "true" : "false");
        if (details.open && loaded === 0) loadPage();
      });
      if (details.open) loadPage();
      return details;
    }

    function childEntries(node) {
      if (node.type === "array") {
        return node.items.map(function (item, index) { return { node: item, entry: { index: index } }; });
      }
      return node.entries.map(function (entry) { return { node: entry.value, entry: entry }; });
    }

    function renderScalar(node, entry, level) {
      var row = element("div", "rn-json-row rn-json-scalar-row");
      row.setAttribute("role", "treeitem");
      row.setAttribute("aria-level", level);
      appendKey(row, entry);
      var exact = raw(state.document, node);
      var visible = exact;
      if (visible.length > limits.maxInlineStringChars) {
        visible = visible.slice(0, limits.maxInlineStringChars) + "… [" + exact.length + " chars]";
      }
      row.appendChild(element("span", "rn-json-value rn-json-" + node.type, visible));
      appendNodeActions(row, node);
      return row;
    }

    function appendKey(row, entry) {
      if (!entry) return;
      if (entry.key) {
        row.appendChild(element("span", "rn-json-key", raw(state.document, entry.key)));
        if (entry.duplicateCount > 1) {
          row.appendChild(element("span", "rn-json-duplicate", "повтор " + entry.occurrence + "/" + entry.duplicateCount));
        }
      } else if (entry.index !== undefined) {
        row.appendChild(element("span", "rn-json-index", String(entry.index)));
      }
      row.appendChild(element("span", "rn-json-punctuation", ":"));
    }

    function appendNodeActions(row, node) {
      var actions = element("span", "rn-json-node-actions");
      actions.appendChild(button("Узел", "rn-json-copy", function () { requestCopy(raw(state.document, node), "node", node); }));
      actions.appendChild(button("Путь", "rn-json-copy", function () { requestCopy(node.path, "path", node); }));
      if (node.type === "string") {
        actions.appendChild(button("Текст", "rn-json-copy", function () { requestCopy(node.value, "string-value", node); }));
      }
      row.appendChild(actions);
    }

    function requestCopy(text, kind, node) {
      var version = ++state.copyVersion;
      if (typeof options.onCopy !== "function") {
        setStatus("Копирование недоступно: владелец экрана не передал callback", "error");
        return;
      }
      try {
        var result = options.onCopy(String(text), { kind: kind, path: node ? node.path : "$", completeness: state.completeness });
        if (result && typeof result.then === "function") {
          result.then(function () {
            if (version === state.copyVersion) setStatus("Скопировано", "ok");
          }, function () {
            if (version === state.copyVersion) setStatus("Не удалось скопировать", "error");
          });
        } else {
          setStatus("Скопировано", "ok");
        }
      } catch (error) {
        setStatus("Не удалось скопировать", "error");
      }
    }

    function collapseAll() {
      if (!root.querySelectorAll) return;
      Array.prototype.slice.call(root.querySelectorAll("details[open]")).forEach(function (details) {
        details.open = false;
      });
    }

    function setSource(text, metadata) {
      metadata = metadata || {};
      state.source = text === null || text === undefined ? "" : String(text);
      state.completeness = metadata.completeness || state.completeness || "full";
      root.setAttribute("data-completeness", state.completeness);
      copyButton.textContent = state.completeness === "full" ? "Копировать всё" : "Копировать preview";
      parseCurrent();
      render();
    }

    parseCurrent();
    render();
    return {
      element: root,
      setSource: setSource,
      setMode: setMode,
      collapseAll: collapseAll,
      getDocument: function () { return state.document; },
      destroy: function () { state.copyVersion += 1; root.replaceChildren(); }
    };
  }

  window.RNAssistantJsonViewer = {
    create: create,
    parse: parse,
    format: format,
    raw: raw,
    normalizeLimits: normalizeLimits,
    defaults: {
      maxChars: DEFAULT_LIMITS.maxChars,
      maxNodes: DEFAULT_LIMITS.maxNodes,
      maxDepth: DEFAULT_LIMITS.maxDepth,
      childPageSize: DEFAULT_LIMITS.childPageSize,
      maxDomRows: DEFAULT_LIMITS.maxDomRows
    }
  };
  if (window.RNAssistantViewerRegistry) {
    window.RNAssistantViewerRegistry.register("json", create);
  }
}());
