(function () {
  "use strict";
  var current = null, limit = 50 * 1024 * 1024;
  var labels = { Full: "Полностью", Fragment: "Фрагмент", Summary: "Сводка / описание ресурса",
    Excluded: "Не включено", Unknown: "Нет сведений" };
  var tasks = { structure: "modelContextStructureTab", response: "modelContextResponseTab", compare: "modelContextCompareTab" };
  var titles = { summary: "Обзор запроса", parameters: "Параметры и формат", omissions: "Не отправлено",
    resources: "Ресурсы", evidence: "Доказательства", response: "Ответ модели и решение runtime", compare: "Изменения запроса", json: "Точный запрос JSON", group: "Раздел запроса" };
  function task(c) { return c.tab === "response" || c.tab === "compare" ? c.tab : "structure"; }
  function viewKey(c) { return c.tab === "messages" ? [c.tab, c.contents.index, c.contents.format, c.contents.part, c.contents.source].join(":") : c.tab; }
  function requestRecord(c) { return c.detail && (c.detail.events || []).find(function (e) { return e.eventId === c.selected; }); }
  function el(tag, text, cls) {
    var node = document.createElement(tag);
    if (text !== undefined) node.textContent = text;
    if (cls) node.className = cls;
    return node;
  }
  function button(text, action) {
    var node = el("button", text, "button compact"); node.type = "button";
    node.addEventListener("click", action); return node;
  }
  // Split immediate members without reserializing numbers, duplicate keys or escapes.
  function members(text) {
    var start = text.search(/\S/), opening = text[start], closing = opening === "{" ? "}" : "]";
    if (opening !== "{" && opening !== "[") return [];
    var result = [], begin = start + 1, depth = 0, quoted = false, escaped = false, colon = -1;
    for (var i = begin; i < text.length; i++) {
      var c = text[i];
      if (quoted) { if (escaped) escaped = false; else if (c === "\\") escaped = true; else if (c === '"') quoted = false; continue; }
      if (c === '"') { quoted = true; continue; }
      if (c === ":" && depth === 0 && colon < 0) colon = i;
      if ((c === "," && depth === 0) || (c === closing && depth === 0)) {
        if (text.slice(begin, i).trim()) {
          var key = opening === "{" ? JSON.parse(text.slice(begin, colon)) : String(result.length);
          result.push({ key: key, raw: text.slice(opening === "{" ? colon + 1 : begin, i).trim() });
        }
        begin = i + 1; colon = -1;
        if (c === closing) return result;
      } else if (c === "{" || c === "[") depth++;
      else if (c === "}" || c === "]") depth--;
    }
    throw new Error("Незавершённый JSON; откройте исходник.");
  }
  function field(list, key) { var found = list.find(function (m) { return m.key === key; }); return found && found.raw; }
  function decoded(raw) { try { return JSON.parse(raw); } catch (_) { return null; } }
  function snapshotField(value, key) { return value && (value[key] !== undefined ? value[key] : value[key[0].toUpperCase() + key.slice(1)]); }
  function source() { return window.RNAssistantDiagnostics.source(); }
  function matches(c, response) {
    return response && response.chatId === c.chatId && (response.workspaceId || null) === (c.workspaceId || null);
  }
  function payloadOwner(c) { return "model-context" + (c.workspaceId ? ":" + c.workspaceId : ""); }
  function live(c) {
    return current === c && !c.abort.signal.aborted && window.RNAssistantDiagnostics.requestVisible();
  }
  function valid(c, version) { return live(c) && c.version === version; }
  function status(c, text) { if (live(c)) $("modelContextStatus").textContent = text; }
  function sameSource(c) { return RNAssistantDiagnostics.key(c) === RNAssistantDiagnostics.key(source()); }
  function syncTabs(c) {
    Object.keys(tasks).forEach(function (key) {
      var control = $(tasks[key]), selected = task(c) === key;
      control.classList.toggle("active", selected);
      if (selected) control.setAttribute("aria-current", "page"); else control.removeAttribute("aria-current");
      control.disabled = c.mode === "preview" && key !== "structure";
    });
    $("modelContextLayout").classList.toggle("is-response", task(c) !== "structure");
    $("modelContextReaderTitle").textContent = titles[c.tab] || "Сообщение " + (c.contents.index + 1);
    $("modelContextMessagePath").textContent = "";
    $("modelContextMessageActions").hidden = c.tab !== "messages" || !c.document;
    $("modelContextFormats").hidden = c.tab !== "messages" || !c.document;
    $("modelContextJsonMode").hidden = true;
    $("modelContextPart").hidden = true;
    $("modelContextContentBack").hidden = true;
    var record = requestRecord(c), trace = record && record.trace || {};
    $("modelContextScope").textContent = [c.documentLabel, c.label, trace.RunId || c.scope && c.scope.runId,
      record && (record.alias || record.eventId)].filter(Boolean).join(" · ");
    $("modelContextScope").title = $("modelContextScope").textContent;
    if (record && window.RNAssistantDockWorkspace) RNAssistantDockWorkspace.setTitle("request", "Запрос " + (record.alias || "модели"));
  }
  function updateOutline(c, reveal) {
    var record = requestRecord(c), trace = record && record.trace || {};
    var outline = $("modelContextTree"), scroll = outline.scrollTop;
    var result = RNAssistantRequestContents.outline(outline, { state: c.contents, trace: trace, document: c.document,
      tab: c.tab, actual: c.mode === "actual", reveal: reveal, choose: chooseTab });
    outline.scrollTop = scroll;
    $("modelContextCount").textContent = result.count == null ? "Сообщения" : result.count + " сообщений";
    $("modelContextOrder").value = c.contents.order;
    $("modelContextOrder").title = result.notice;
    $("modelContextOutlineNote").textContent = result.structure ? "" : result.notice;
    $("modelContextPage").textContent = result.total ? (result.start + 1) + "–" + result.end + " из " + result.total : "Нет записей";
    $("modelContextPageBefore").disabled = !result.start;
    $("modelContextPageAfter").disabled = result.end >= result.total;
  }
  function dateText(value) {
    return new Date(value).toLocaleString("ru-RU", { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" });
  }
  function cancelReads(c) {
    c.version++; c.abort.abort(); c.abort = new AbortController();
    c.requests.forEach(function (id) { cancelBridgeRequest(id).catch(function () {}); });
    c.pending = {}; c.listRequest = null;
  }
  function discardViews(c) {
    if (!c.viewCache) return;
    c.viewCache.forEach(function (view) { view.mounts.forEach(function (host) { RNAssistantViewerRegistry.unmount(host); }); });
    c.viewCache.clear();
  }
  function disclosureKey(detail, root) {
    var parts = [], current = detail;
    while (current && current !== root) {
      if (current.tagName === "DETAILS") parts.unshift(current.firstElementChild.textContent);
      if (current.dataset && current.dataset.responseEvent) parts.unshift(current.dataset.responseEvent);
      current = current.parentElement;
    }
    return parts.join("\u0000");
  }
  function savePresentation(c) {
    var root = $("modelContextBody");
    if (Object.keys(c.presentation).length > 16) delete c.presentation[Object.keys(c.presentation)[0]];
    c.presentation[c.activeKey || viewKey(c)] = { scroll: root.scrollTop,
      expanded: Array.from(root.querySelectorAll("details[open]")).map(function (detail) { return disclosureKey(detail, root); }) };
  }
  function restorePresentation(c) {
    var saved = c.presentation[c.activeKey || viewKey(c)]; if (!saved) return;
    var root = $("modelContextBody");
    root.querySelectorAll("details").forEach(function (detail) {
      if (saved.expanded.indexOf(disclosureKey(detail, root)) >= 0 && !detail.open) detail.open = true;
    });
    root.scrollTop = saved.scroll;
  }
  function retainView(c) {
    if (c.tab === "group" || c.tab === "omissions" || !c.viewCache || c.loading || c.requests.size || Object.keys(c.pending).length || !$("modelContextBody").firstChild) return;
    savePresentation(c);
    if (c.captureEvidence) c.captureEvidence();
    var root = $("modelContextBody"), fragment = document.createDocumentFragment();
    var view = { fragment: fragment, scroll: root.scrollTop, mounts: c.mounts,
      captureEvidence: c.captureEvidence, viewer: c.viewer, jsonMode: c.contents.jsonMode };
    while (root.firstChild) fragment.appendChild(root.firstChild);
    c.viewCache.set(c.activeKey || viewKey(c), view); c.mounts = []; c.captureEvidence = null;
    while (c.viewCache.size > 2) {
      var oldest = c.viewCache.keys().next().value, removed = c.viewCache.get(oldest);
      removed.mounts.forEach(function (host) { RNAssistantViewerRegistry.unmount(host); }); c.viewCache.delete(oldest);
    }
  }
  function chooseTab(key, messageIndex) {
    var c = current; if (!c || !live(c) || !(titles[key] || key === "messages")) return;
    retainView(c); c.tab = key;
    if (task(c) === "structure") c.structureTab = key;
    if (messageIndex != null && c.contents.index !== messageIndex) { c.contents.index = messageIndex; c.contents.part = 0; c.contents.source = "content"; }
    c.contents.index = c.contents.index || 0;
    $("modelContextLayout").dataset.detail = "true";
    updateOutline(c, messageIndex != null);
    if (key === "evidence" && c.mode === "actual" && c.selected && !(c.detail && c.detail.evidenceReport)) select(c, c.selected, true);
    else render(c);
    $("modelContextReaderTitle").focus({ preventScroll: true });
  }
  function pause() {
    if (!current || current.paused) return;
    clearTimeout(current.timer); current.paused = true;
    current.resumeLoad = current.loading || current.requests.size > 0 || Object.keys(current.pending).length > 0;
    if (current.resumeLoad) { clearView(current); discardViews(current); cancelReads(current); }
  }
  function resume() {
    var c = current; if (!c || !c.paused || !live(c)) return;
    c.paused = false;
    if (c.resumeLoad) { if (c.mode === "preview") preview(c); else if (c.selected) select(c, c.selected, true); else list(c, false); }
    tick(c);
  }
  function clearView(c) {
    if (c.captureEvidence) { c.captureEvidence(); c.captureEvidence = null; }
    c.viewer = null;
    c.viewVersion = (c.viewVersion || 0) + 1;
    c.mounts.forEach(function (node) { window.RNAssistantViewerRegistry.unmount(node); }); c.mounts = [];
    $("modelContextBody").replaceChildren(); $("modelContextBody").classList.remove("product-viewer");
  }
  function notice(c, title, detail, kind) {
    clearView(c);
    var card = el("div", undefined, "model-context-empty"); card.dataset.kind = kind || "empty";
    card.appendChild(el("h3", title));
    if (detail) card.appendChild(el("p", detail));
    $("modelContextBody").appendChild(card);
  }
  function dispose() {
    var c = current; current = null;
    if (c) {
      clearTimeout(c.timer); c.abort.abort();
      c.requests.forEach(function (id) { cancelBridgeRequest(id).catch(function () {}); });
      clearView(c); discardViews(c); c.cache = {}; c.document = null;
    }
    RNAssistantActionMenu.closeWithin($("modelContextDialog"));
    window.RNAssistantDiagnostics.forgetRequest();
    RNAssistantNavigationTree.unmount($("modelContextTree"));
    $("modelContextAttempt").replaceChildren(); $("modelContextAttempt").disabled = true;
    $("modelContextStatus").textContent = ""; $("modelContextMessagePath").textContent = "";
    $("modelContextFormats").hidden = true; $("modelContextMessageActions").hidden = true;
    $("modelContextActions").disabled = true; $("modelContextMode").disabled = true;
    $("modelContextReaderTitle").textContent = "Выберите запрос";
    $("modelContextScope").textContent = ""; $("modelContextTree").replaceChildren();
    $("modelContextBody").textContent = "Выберите сохранённый запрос в чате или журнале запусков.";
  }
  function close(restore) {
    var c = current;
    dispose(); RNAssistantDockWorkspace.close("request");
    if (restore !== false && c) backToRun(c);
  }
  function backToRun(c) {
    if (!c) return;
    if (typeof window.openRunJournal === "function") RNAssistantDiagnostics.returnToJournal({ owner: c.owner, chatId: c.chatId,
      sessionId: c.chatId, workspaceId: c.workspaceId, label: c.label, document: c.documentLabel,
      runId: requestRecord(c) && requestRecord(c).trace.RunId || c.scope && c.scope.runId });
  }
  async function call(c, type, payload) {
    var pending = send(type, Object.assign({ chatId: c.chatId }, c.workspaceId ? { workspaceId: c.workspaceId } : {}, payload || {}));
    if (pending.requestId) c.requests.add(pending.requestId);
    try { return await pending; } finally { c.requests.delete(pending.requestId); }
  }
  async function download(c, response, owner, version) {
    var data = response && (response.data || response.rawData);
    try {
      if (!valid(c, version)) throw new Error("Просмотр закрыт или изменён.");
      if (!data || !data.payload || !matches(c, response)) throw new Error("RESOURCE_DOWNLOAD_INVALID");
      var bytes = await window.RNAssistantResourceDownload.read(data, { fetch: window.fetch.bind(window),
        signal: c.abort.signal, isCurrent: function () { return valid(c, version); }, maxBytes: limit });
      if (!valid(c, version)) throw new Error("Просмотр изменён.");
      return { bytes: bytes, text: new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(bytes),
        contentType: data.payload.contentType };
    } finally {
      if (data && data.leaseId) await send("resourceDataClose", { chatId: c.chatId, workspaceId: owner,
        leaseId: data.leaseId }).catch(function () {});
    }
  }
  async function payload(c, eventId, originalIndex, version) {
    var key = eventId + ":" + (originalIndex === undefined ? "body" : originalIndex);
    if (c.cache[key]) return c.cache[key];
    if (c.pending[key]) return c.pending[key];
    var operation = (async function () {
      var response = await call(c, "getModelContextPayload", { eventId: eventId, originalIndex: originalIndex });
      var value = await download(c, response, payloadOwner(c), version);
      if (response.eventId !== eventId) throw new Error("RESOURCE_DOWNLOAD_INVALID");
      // Only the selected request is retained across tabs. Source/response/SSE bodies are disposable.
      if (valid(c, version) && eventId === c.selected && originalIndex === undefined) c.cache[key] = value;
      return value;
    }());
    c.pending[key] = operation;
    try { return await operation; } finally { if (c.pending[key] === operation) delete c.pending[key]; }
  }
  function save(value, filename) {
    var url = URL.createObjectURL(new Blob([value.bytes], { type: value.contentType || "application/json" }));
    var a = el("a"); a.href = url; a.download = filename; document.body.appendChild(a); a.click(); a.remove();
    setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
  }
  function mount(c, node, text, json, expandedPaths, kindOverride, options) {
    // Viewer bounds limit rendering only; copy/download keep the complete source.
    if (text.length > 2000000) {
      node.appendChild(el("p", "Большой текст: показаны первые 200 000 символов. Копирование и скачивание сохраняют полный исходник."));
      node.appendChild(button("Копировать полностью", function () { copyTextResult(text); }));
      node.appendChild(el("pre", text.slice(0, 200000))); return;
    }
    if (!json && !kindOverride) {
      var newline = text.indexOf("\n"), suffix = newline >= 0 ? text.slice(newline + 1).trim() : "";
      if (newline >= 0 && /^[A-Z_]+/.test(text) && /^[\[{]/.test(suffix) && decoded(suffix) !== null) {
        node.appendChild(el("p", text.slice(0, newline)));
        node.appendChild(button("Копировать исходный текст", function () { copyTextResult(text); }));
        var structured = el("div"); node.appendChild(structured); mount(c, structured, suffix, true); return;
      }
    }
    var kind = kindOverride || (json || /^[\s]*[\[{]/.test(text) && decoded(text) !== null ? "json" : "markdown");
    node.classList.add("product-viewer");
    var controller = window.RNAssistantViewerRegistry.mount(kind, node, Object.assign({ text: text, fullText: text, complete: true,
      completeness: "full", mode: "tree", expandedPaths: expandedPaths, limits: { maxChars: 2000000 }, onCopy: window.copyTextResult }, options || {}));
    c.mounts.push(node); return controller;
  }
  function lazy(c, parent, title, read, json, renderBody) {
    var details = el("details", undefined, "model-context-message");
    details.appendChild(el("summary", title));
    var body = el("div", undefined, "model-context-message-body"); details.appendChild(body); parent.appendChild(details);
    var loaded = false, loading = false, version = c.version;
    details.addEventListener("toggle", async function () {
      if (!details.open || loaded || loading || !valid(c, version)) return;
      loading = true; body.textContent = "Загрузка…";
      try {
        var result = await read();
        if (!valid(c, version) || !body.isConnected) return;
        body.replaceChildren();
        if (renderBody) renderBody(c, body, result);
        else mount(c, body, typeof result === "string" ? result : result.text, json);
        loaded = true; restorePresentation(c);
      } catch (error) { if (valid(c, version)) body.textContent = error.message || String(error); }
      finally { loading = false; }
    });
    return body;
  }
  function note(entry) { return labels[entry && entry.Presentation] || labels.Unknown; }
  function alias(c, group, id) {
    var found = (c.detail && c.detail[group] || []).find(function (item) { return item.canonicalId === id; });
    return found && found.alias;
  }
  function resourceFor(c, link) {
    var reference = link && link.Resource || {}, scope = link && link.ScopeId || {};
    return (c.detail && c.detail.resources || []).find(function (item) {
      var value = item.resource || {}, rowScope = item.scope || {};
      return value.uri === reference.uri && value.revision === reference.revision &&
        rowScope.kind === scope.kind && rowScope.id === scope.id;
    });
  }
  function evidenceNote(c, link) {
    var resource = link.Resource || {}, selected = resourceFor(c, link);
    var details = ["evidence " + (alias(c, "evidence", link.EvidenceId) || link.EvidenceId || "?")];
    if (selected) details.push(selected.alias);
    if (resource.uri) details.push(resource.uri + (resource.revision ? "@" + resource.revision : ""));
    if (link.View) details.push("view " + link.View);
    if (link.ObservedCoverage) details.push("наблюдение " + JSON.stringify(link.ObservedCoverage));
    details.push(link.BodyIncludedInMessage === true ? "тело включено в сообщение" :
      link.BodyIncludedInMessage === false ? "тело не включено в сообщение" : "доставка тела в сообщении не установлена");
    details.push(readMetadataText(link.ReadMetadata));
    return details.join(" · ");
  }
  function readMetadataText(metadata) {
    if (!metadata) return "Запрошенный диапазон и раздельная полнота не сохранены.";
    var requested = metadata.requestedCoverage && (metadata.requestedCoverage.kind === "json-path" ||
        metadata.requestedCoverage.kind === "yaml-path") ?
      (metadata.requestedCoverage.kind === "yaml-path" ? "YAML pointer " : "JSON Pointer ") +
        JSON.stringify(metadata.requestedCoverage.path) +
        (metadata.requestedDepth != null ? ", глубина " + metadata.requestedDepth : "") +
        (metadata.requestedCoverage.start != null ? ", дети [" + metadata.requestedCoverage.start +
          ", " + metadata.requestedCoverage.end + ")" : "") :
      metadata.requestedCoverage ? JSON.stringify(metadata.requestedCoverage) :
      metadata.requestedSection != null ? "раздел " + metadata.requestedSection :
      metadata.requestedProcedure != null ? "процедура " + metadata.requestedProcedure +
        (metadata.requestedProcedureKind ? " (" + metadata.requestedProcedureKind + ")" : "") :
      metadata.requestedPartNumber != null ? "часть " + metadata.requestedPartNumber : "неизвестно";
    function complete(label, value) { return label + ": " + (value === true ? "полностью" : value === false ? "не полностью" : "неизвестно"); }
    return ["Запрошено: " + requested,
      metadata.sourceCoverage ? "Покрытие в источнике: " + JSON.stringify(metadata.sourceCoverage) : "",
      metadata.extractionProfile ? "Профиль извлечения: " + metadata.extractionProfile : "",
      Number.isSafeInteger(metadata.sourcePageCount) && metadata.sourcePageCount > 0 ?
        "Страниц в оригинале: " + metadata.sourcePageCount : "",
      complete("Выбранный фрагмент", metadata.selectionComplete),
      complete("Представление", metadata.representationComplete),
      complete("Источник", metadata.sourceComplete),
      descriptorAtReadText(metadata.descriptorAtRead)].filter(Boolean).join(" · ");
  }
  function descriptorAtReadText(descriptor) {
    if (!descriptor) return "";
    var facts = [];
    if (descriptor.provider) facts.push("провайдер " + descriptor.provider);
    if (descriptor.kind) facts.push("тип " + descriptor.kind);
    if (descriptor.mimeType) facts.push("MIME " + descriptor.mimeType);
    if (descriptor.byteLength != null) facts.push(descriptor.byteLength + " байт");
    if (Array.isArray(descriptor.representations) && descriptor.representations.length)
      facts.push("представления " + descriptor.representations.join(", "));
    if (Array.isArray(descriptor.capabilities) && descriptor.capabilities.length)
      facts.push("операции " + descriptor.capabilities.join(", "));
    if (Array.isArray(descriptor.viewCapabilities) && descriptor.viewCapabilities.length)
      facts.push("параметры views " + descriptor.viewCapabilities.map(function (view) {
        var bounds = [];
        if (view.supportsOffset) bounds.push("смещение");
        if (view.supportsFields) bounds.push("поля");
        if (view.supportsStream) bounds.push("поток");
        if (view.maxItemsPerBatch != null) bounds.push("элементов за раз ≤ " + view.maxItemsPerBatch);
        if (view.maxBatchBytes != null) bounds.push("байт за раз ≤ " + view.maxBatchBytes);
        if (view.maxPayloadBytes != null) bounds.push("байт в view ≤ " + view.maxPayloadBytes);
        return view.view + (bounds.length ? " (" + bounds.join(", ") + ")" : "");
      }).join(", "));
    return facts.length ? "Заявлено провайдером при чтении: " + facts.join("; ") +
      "; текущая доступность не проверена; возможность чтения моделью не подтверждена" : "";
  }
  function sourceDisclosure(parent, title, caption, key, saved) {
    var detail = el("details", undefined, "model-context-source"), summary = el("summary", undefined, "rna-history-summary");
    detail.dataset.contextSource = key;
    summary.appendChild(el("span", title));
    if (caption) summary.appendChild(el("small", caption));
    var arrow = el("span", undefined, "model-context-source-arrow");
    arrow.innerHTML = window.RNAssistantIcons.render("right"); summary.appendChild(arrow);
    detail.appendChild(summary); parent.appendChild(detail);
    if (saved && (saved.sources || []).indexOf(key) >= 0) detail.open = true;
    return detail;
  }
  function sourceFact(parent, label, value) {
    if (value === undefined || value === null || value === "") return;
    parent.appendChild(el("dt", label)); parent.appendChild(el("dd", String(value)));
  }
  function sourceCoverage(coverage) {
    if (!coverage) return "Не сохранено";
    var kinds = { whole: "Ресурс целиком", "line-range": "Строки", "character-range": "Символы", "cell-range": "Ячейки",
      "page-range": "Страницы", "time-range": "Время", "record-range": "Записи", "json-path": "JSON Pointer", "yaml-path": "YAML pointer", "field-set": "Поля" };
    if (!kinds[coverage.kind]) return JSON.stringify(coverage);
    var parts = [kinds[coverage.kind]];
    if (coverage.address) parts.push(coverage.address);
    if (coverage.path != null) parts.push(JSON.stringify(coverage.path));
    // Keep recorded bounds; no provider-specific inclusive/exclusive conversion.
    if (coverage.start != null) parts.push("начало " + coverage.start);
    if (coverage.end != null) parts.push("конец " + coverage.end);
    if (coverage.fields && coverage.fields.length) parts.push(coverage.fields.join(", "));
    return parts.join(" · ");
  }
  function deliveredSource(c, entry, link, selected, content, wireCallId) {
    if (c.mode !== "actual" || link.BodyIncludedInMessage !== true || !selected || !selected.alias ||
        typeof content !== "string" || !window.RNAssistantToolResultPresentation) return null;
    try {
      var data, text, first = content.indexOf("\n");
      if (content.indexOf("CURRENT_RESOURCE_VIEW (") === 0 && first >= 0) {
        var end = content.indexOf("\n", first + 1); if (end < 0) return null;
        data = JSON.parse(content.slice(first + 1, end));
        if (data.bodyIncluded !== true || data.view !== link.View) return null;
        text = content.slice(end + 1);
      } else {
        var result = JSON.parse(content.indexOf("TOOL_RESULT:\n") === 0 ? content.slice(13) : content);
        if (result.name !== "core.read" || result.status !== "ok" ||
            (wireCallId != null ? result.tool_call_id != null || wireCallId !== alias(c, "operations", entry.ToolCallId) :
              result.tool_call_id !== alias(c, "operations", entry.ToolCallId))) return null;
        data = result.data;
        if (!data || data.kind !== "resource-read" || data.representation !== link.View ||
            typeof data.text !== "string" && !data.table && !data.tree) return null;
        text = typeof data.text === "string" ? data.text : null;
      }
      if (data.ref !== selected.alias || typeof data.target !== "string" || !data.coverage || !link.ObservedCoverage) return null;
      if (["kind", "address", "path", "start", "end"].some(function (field) {
        return (data.coverage[field] == null ? null : data.coverage[field]) !==
          (link.ObservedCoverage[field] == null ? null : link.ObservedCoverage[field]);
      }) || JSON.stringify(data.coverage.fields || []) !== JSON.stringify(link.ObservedCoverage.fields || [])) return null;
      var excerpt = text == null ? null : Array.from(text).slice(0, 160).join("");
      return { reference: selected.alias, target: data.target, view: link.View, pageIndex: data.pageIndex,
        coverage: data.sourceCoverage || data.coverage, excerpt: excerpt, excerptTruncated: text != null && excerpt.length < text.length };
    } catch (_) { return null; } // Exact raw message remains available without an inferred label.
  }
  function sourceLinks(c, parent, entry, key, saved, content, wireCallId) {
    (entry.EvidenceLinks || []).filter(Boolean).forEach(function (link, index) {
      var selected = resourceFor(c, link), resource = link.Resource || {}, metadata = link.ReadMetadata;
      var preview = deliveredSource(c, entry, link, selected, content, wireCallId);
      var row = sourceDisclosure(parent, preview ? window.RNAssistantToolResultPresentation.sourceLabel(preview) :
        selected && selected.alias ? "Ресурс " + selected.alias : resource.uri || link.EvidenceId || "Источник",
        link.BodyIncludedInMessage === true ? "Тело включено" : link.BodyIncludedInMessage === false ? "Тело не включено" : "Включение не установлено",
        key + "/resource/" + index, saved);
      var facts = el("dl", undefined, "model-context-source-facts"); row.appendChild(facts);
      sourceFact(facts, "Представление", link.View);
      sourceFact(facts, "Покрытие наблюдения", sourceCoverage(link.ObservedCoverage));
      if (metadata) {
        sourceFact(facts, "Запрошено", metadata.requestedCoverage ? sourceCoverage(metadata.requestedCoverage) :
          metadata.requestedSection != null ? "Раздел: " + metadata.requestedSection :
          metadata.requestedProcedure != null ? "Процедура: " + metadata.requestedProcedure :
          metadata.requestedPartNumber != null ? "Часть: " + metadata.requestedPartNumber : "Не сохранено");
        [["Выбранный фрагмент", metadata.selectionComplete], ["Представление целиком", metadata.representationComplete],
          ["Источник целиком", metadata.sourceComplete]].forEach(function (fact) {
          sourceFact(facts, fact[0], fact[1] === true ? "Да" : fact[1] === false ? "Нет" : "Не установлено");
        });
      } else row.appendChild(el("p", "Запрошенный диапазон и раздельная полнота не сохранены.", "model-context-note"));
      if (selected && selected.alias) row.appendChild(button("Открыть " + selected.alias, function () {
        if (!live(c) || !row.isConnected) return;
        c.focusResource = selected.alias; chooseTab("resources");
      }));
      var technical = sourceDisclosure(row, "Сведения о чтении", "", key + "/resource/" + index + "/details", saved);
      technical.appendChild(el("p", evidenceNote(c, link), "model-context-note"));
    });
  }
  function partTitle(part) {
    var names = { attachment_text: "Текст вложения", attachment_analysis: "Анализ вложения", image_url: "Изображение",
      input_audio: "Аудио", image: "Изображение", pdf: "PDF", text: "Текст", data: "Данные результата", attachments: "Вложения" };
    return (names[part.Kind] || part.Kind || "Часть сообщения") + (part.PageNumber != null ? " · страница " + part.PageNumber :
      part.PartIndex != null ? " · часть " + (part.PartIndex + 1) : "");
  }
  function partSource(parent, part, key, saved) {
    var row = sourceDisclosure(parent, partTitle(part), note(part), key, saved);
    if (part.Reason) row.appendChild(el("p", part.Reason, "model-context-note"));
    var facts = el("dl", undefined, "model-context-source-facts"); row.appendChild(facts);
    sourceFact(facts, "Вложение", part.SourceAttachmentId);
    if (part.PageNumber != null && part.PartIndex != null) sourceFact(facts, "Часть JSON", part.PartIndex + 1);
    sourceFact(facts, "Не выбрано", part.OmittedCount);
    sourceFact(facts, "Символов", part.SelectedCharacters);
    sourceFact(facts, "Байт", part.ByteLength);
    sourceFact(facts, "Тип", part.ContentType);
    if (part.ContentSha256) {
      var technical = sourceDisclosure(row, "Отпечаток содержимого", "", key + "/digest", saved);
      technical.appendChild(el("p", part.ContentSha256, "model-context-note"));
    }
    return row;
  }
  function messageSources(c, parent, entry, key, saved, ambiguous, content, wireCallId) {
    var counts = [];
    var links = entry && (entry.EvidenceLinks || []).filter(Boolean), parts = entry && (entry.Parts || []).filter(Boolean);
    if (links && links.length) counts.push("наблюдения: " + links.length);
    if (parts && parts.length) counts.push("части: " + parts.length);
    var source = sourceDisclosure(parent, "Источники и полнота", ambiguous ? "Неоднозначная привязка" : counts.join(" · ") || note(entry), key, saved);
    if (!entry) {
      source.appendChild(el("p", ambiguous ? "Несколько записей состава ссылаются на это сообщение. Источник не выбран; точное содержимое доступно ниже и в JSON запроса." :
        "Сведения об источниках сообщения не сохранены. Точное содержимое доступно ниже и в JSON запроса.", "model-context-note"));
      return;
    }
    if (entry.Reason) source.appendChild(el("p", entry.Reason, "model-context-note"));
    sourceLinks(c, source, entry, key, saved, content, wireCallId);
    (entry.Parts || []).filter(Boolean).forEach(function (part, index) { partSource(source, part, key + "/part/" + index, saved); });
    if (!(entry.EvidenceLinks || []).length && !(entry.Parts || []).length && !entry.Reason)
      source.appendChild(el("p", "Сохранена только отметка представления; подробности источников отсутствуют.", "model-context-note"));
  }
  function modelDocument(c, text, entries) {
    if (!c.document || c.document.text !== text) c.document = RNAssistantRequestContents.model(text, entries, members);
    return c.document;
  }
  function selectedContent(c) {
    var item = RNAssistantRequestContents.message(c.document, c.contents.index), raw = item.content;
    var path = "messages[" + c.contents.index + "].content", parts = [];
    if (raw && raw[0] === "[") {
      parts = members(raw);
      var part = parts[c.contents.part || 0];
      if (part) {
        path += "[" + c.contents.part + "]"; raw = part.raw;
        if (raw[0] === "{") { var text = field(members(raw), "text"); if (text !== undefined) { raw = text; path += ".text"; } }
      }
    }
    var value = decoded(raw), isString = typeof value === "string";
    return { item: item, raw: raw, value: value, text: isString ? value : raw, isString: isString, path: path, parts: parts };
  }
  function messageHeader(c) {
    var content = selectedContent(c), mode = c.contents.source, format = c.contents.format;
    $("modelContextReaderTitle").textContent = (c.contents.index + 1) + " · " + (RNAssistantRequestContents.roles[content.item.role] || content.item.role);
    $("modelContextMessageActions").hidden = false; $("modelContextFormats").hidden = false;
    $("modelContextContentBack").hidden = mode === "content";
    var type = content.raw === undefined ? "отсутствует" : content.raw === "null" ? "null" : content.isString ? "string" : content.raw[0] === "[" ? "array" : typeof content.value;
    $("modelContextMessagePath").textContent = mode === "whole" ? "messages[" + c.contents.index + "] · точный JSON сообщения" :
      mode === "sources" ? "Записанные источники и полнота" : content.path + " · " + type +
        (content.isString && format === "json" ? " · разбор строки для просмотра; в запросе это строка" : content.isString ? " · декодированное содержимое" : "");
    document.querySelectorAll("[data-request-format]").forEach(function (control) {
      control.classList.toggle("active", mode === "content" && control.dataset.requestFormat === format);
      control.setAttribute("aria-pressed", String(mode === "content" && control.dataset.requestFormat === format));
      control.disabled = content.raw === undefined || !content.isString && control.dataset.requestFormat === "markdown";
    });
    var picker = $("modelContextPart"); picker.replaceChildren();
    content.parts.forEach(function (part, index) {
      var fields = members(part.raw), kind = decoded(field(fields, "type"));
      var option = el("option", "Часть " + (index + 1) + (typeof kind === "string" ? " · " + kind : "")); option.value = index; picker.appendChild(option);
    });
    picker.value = c.contents.part || 0; picker.hidden = !content.parts.length || mode !== "content";
    $("modelContextJsonMode").hidden = !(mode === "whole" || mode === "content" && format === "json");
    $("modelContextJsonMode").value = c.contents.jsonMode || "tree";
    return content;
  }
  function messageView(c, root) {
    var content = messageHeader(c), mode = c.contents.source;
    if (mode === "sources") {
      messageSources(c, root, content.item.entry, "message/" + c.contents.index, null, content.item.ambiguous,
        decoded(content.item.content), decoded(field(content.item.fields, "tool_call_id")));
      var source = root.querySelector("details"); if (source) source.open = true;
      var entry = content.item.entry, version = c.version, entries = requestRecord(c) && requestRecord(c).trace.ContextMessages || [];
      if (entry && entry.OriginalPayload && c.mode === "actual") lazy(c, root, "Локальный оригинал · не обязательно включён целиком", function () {
        return payload(c, c.selected, entries.indexOf(entry), version);
      }, false);
      return;
    }
    if (mode !== "whole" && content.raw === undefined) { root.textContent = "Поле content отсутствует. Откройте точное сообщение: оно может содержать вызовы инструментов в других полях."; return; }
    if (mode !== "whole" && content.item.role === "assistant") root.appendChild(el("p", "Историческая проекция · это сообщение внутри запроса, не новый ответ модели.", "diagnostic-body-note"));
    if (mode !== "whole" && /^(tool|function)$/.test(content.item.role)) root.appendChild(el("p", "Результат инструмента в истории запроса. Факт выполнения и подтверждение изменений — в доказательствах.", "diagnostic-body-note"));
    var host = el("div"); root.appendChild(host);
    var kind = mode === "whole" ? "json" : c.contents.format === "markdown" && !content.isString ? "text" : c.contents.format;
    c.viewer = mount(c, host, mode === "whole" ? content.item.raw : content.text, kind === "json", null, kind,
      { toolbar: kind === "text", showCopy: false, showDownload: false, mode: c.contents.jsonMode || "tree",
        sourceLabel: mode === "whole" ? "Точный JSON сообщения" : content.path + (content.isString ? " · полная декодированная строка" : " · точное значение") });
    if (c.viewer && c.viewer.getMode) $("modelContextJsonMode").value = c.viewer.getMode();
  }
  function omissions(c, root) {
    var entries = c.mode === "preview" ? c.previewEntries : requestRecord(c).trace.ContextMessages, items = [];
    (entries || []).filter(Boolean).forEach(function (entry, index) {
      if (entry.Presentation === "Excluded") items.push({ entry: entry, index: index });
      else (entry.Parts || []).filter(Boolean).forEach(function (part, partIndex) { if (part.Presentation === "Excluded") items.push({ entry: entry, index: index, part: part, partIndex: partIndex }); });
    });
    if (!items.length) { root.textContent = entries ? "Пропуски не записаны. Это не доказывает, что весь доступный контекст был отправлен." : "Сведения о пропусках не сохранены."; return; }
    var page = c.omissionPage || 0;
    root.appendChild(el("p", "Не отправлено · записей: " + items.length));
    items.slice(page * 50, (page + 1) * 50).forEach(function (item) {
      var key = "omission/" + item.index, entry = item.entry;
      if (item.part) {
        var partRow = partSource(root, item.part, key + "/" + item.partIndex);
        var count = c.document ? c.document.list.length : requestRecord(c) && requestRecord(c).trace.MessageCount;
        var linked = Number.isInteger(entry.MessageIndex) && entry.MessageIndex >= 0 && entry.MessageIndex < count &&
          entries.filter(function (candidate) { return candidate && candidate.MessageIndex === entry.MessageIndex; }).length === 1;
        partRow.appendChild(el("p", linked ? "В составе сообщения " + (entry.MessageIndex + 1) : "Привязка к сообщению не установлена.", "model-context-note")); return;
      }
      var row = sourceDisclosure(root, RNAssistantRequestContents.roles[entry.Role] || entry.Role || "Источник", "Не включено", key);
      row.appendChild(el("p", entry.Reason || "Причина исключения не сохранена.", "model-context-note"));
      sourceLinks(c, row, entry, key);
      if (entry.OriginalPayload && c.mode === "actual") { var version = c.version;
        lazy(c, row, "Локальный оригинал источника", function () { return payload(c, c.selected, item.index, version); }, false); }
    });
    var pages = el("div", undefined, "model-context-toolbar"); root.appendChild(pages);
    if (page) pages.appendChild(button("Предыдущие пропуски", function () { c.omissionPage--; render(c); }));
    if ((page + 1) * 50 < items.length) pages.appendChild(button("Следующие пропуски", function () { c.omissionPage = page + 1; render(c); }));
  }
  function requestFields(c, root) {
    root.appendChild(el("p", "Точные поля отправленного запроса. Формат нового ответа и способ записи истории независимы.", "diagnostic-body-note"));
    c.document.fields.filter(function (part) { return part.key !== "messages"; }).forEach(function (part) {
      lazy(c, root, part.key, function () { return part.raw; }, true);
    });
  }
  function modelMessage(c, body, text) {
    body.appendChild(el("p", "Исходный content · строка. Решение parser и runtime показано в итоге; здесь сохранённый текст модели.", "diagnostic-body-note"));
    var view = el("div"); body.appendChild(view); mount(c, view, text, false, undefined, /^[\s]*[\[{]/.test(text) ? "text" : "markdown");
  }
  function responseBody(c, body, value) {
    var text = value.text, fields = members(text), content = field(fields, "Content") || field(fields, "content");
    var choices = field(fields, "choices");
    if (!content && choices) {
      var first = members(choices)[0], message = first && field(members(first.raw), "message");
      if (message) content = field(members(message), "content");
    }
    var messageText = decoded(content);
    if (typeof messageText !== "string") { mount(c, body, text, true); return; }
    modelMessage(c, body, messageText);
    lazy(c, body, "Полный ответ · исходный JSON", function () { return text; }, true);
  }

  function referenceText(ref) { return (ref.uri || "?") + (ref.revision ? " @ " + ref.revision : ""); }

  function executionEvidence(c, root) {
    var report = c.detail && c.detail.evidenceReport;
    if (!report) { notice(c, "Нет сведений о доказательствах", "Для этого запроса связь вызовов и итогов недоступна."); return; }
    var rows = report.tools || [], rendered = new Map(), claimRows = new Map(), count = 0, source = c.selected;
    var saved = c.evidenceView && c.evidenceView.source === source ? c.evidenceView : null;
    var outcomes = { Ok: "Инструмент вернул ok", Error: "Ошибка инструмента", Unknown: "Эффект неизвестен",
      NotDispatched: "Не исполнялся", AwaitingConfirmation: "Ожидает подтверждения" };
    var assessments = { satisfied: "выполнено", partially_satisfied: "выполнено частично", unverified: "не проверено", not_satisfied: "не выполнено" };
    var finishes = report.finishes || [];
    root.appendChild(el("h3", "Вызовы и доказательства"));
    root.appendChild(el("p", "T — принятый вызов; E — допустимая ссылка на завершённый результат. Наличие E и статус ok сами по себе не доказывают выполнение задачи.", "model-context-note"));
    if (!report.hasContextTrace) root.appendChild(el("p", "Состав этого запроса не сохранён: доставка E не установлена.", "model-context-note"));
    c.captureEvidence = function () {
      c.evidenceView = { source: source, count: count, scroll: root.scrollTop,
        expanded: Array.from(rendered).filter(function (pair) { return pair[1].open; }).map(function (pair) { return pair[0]; }),
        claims: Array.from(claimRows).filter(function (pair) { return pair[1].open; }).map(function (pair) { return pair[0]; }) };
    };
    function reveal(reference) {
      var index = rows.findIndex(function (row) { return row.evidenceAlias === reference || row.operationAlias === reference; });
      if (index < 0) return;
      while (count <= index && count < rows.length) page();
      var detail = rendered.get(rows[index].callId);
      detail.open = true; detail.children[0].focus(); detail.scrollIntoView({ block: "nearest" });
    }
    if (!finishes.length) root.appendChild(el("p", "Принятый runtime итог finish для этого запроса не записан. Текст ответа доступен на вкладке «Ответ».", "model-context-note"));
    finishes.forEach(function (finish, finishIndex) {
      var section = el("section");
      section.appendChild(el("h3", "Принятый finish"));
      section.appendChild(el("p", "Оценка модели: " + ({ completed: "завершено", partial: "частично", unable_to_complete: "завершить не удалось" }[finish.outcome] || finish.outcome) +
        ". Это принятие решения, а не отдельная проверка результата.", "model-context-note"));
      (finish.claims || []).forEach(function (claim, claimIndex) {
        var detail = el("details", undefined, "model-context-message");
        detail.appendChild(el("summary", claim.criterion));
        var claimKey = (finish.eventId || finish.messageId || finishIndex) + "/" + claimIndex;
        claimRows.set(claimKey, detail);
        if (saved && (saved.claims || []).indexOf(claimKey) >= 0) detail.open = true;
        var body = el("div", undefined, "model-context-message-body"); detail.appendChild(body);
        body.appendChild(el("p", "Оценка модели: " + (assessments[claim.assessment] || claim.assessment)));
        var links = el("div", undefined, "model-context-toolbar"); body.appendChild(links);
        (claim.evidence || []).forEach(function (reference) {
          if (rows.some(function (row) { return row.evidenceAlias === reference; }))
            links.appendChild(button(reference, function () { reveal(reference); }));
          else links.appendChild(el("span", reference + " · связь не установлена", "model-context-note"));
        });
        if (!(claim.evidence || []).length) body.appendChild(el("p", "Без ссылок на инструменты.", "model-context-note"));
        section.appendChild(detail);
      });
      if (!(finish.claims || []).length) section.appendChild(el("p", "Критерии не указаны.", "model-context-note"));
      root.appendChild(section);
    });
    if (!rows.length) { root.appendChild(el("p", "Вызовы из этого запуска и сохранённого состава запроса отсутствуют.", "model-context-note")); return; }
    var list = el("section"); root.appendChild(list);
    var more = button("Показать ещё вызовы", page);
    function page() {
      more.remove();
      rows.slice(count, count + 50).forEach(function (row) {
        var detail = el("details", undefined, "model-context-message");
        detail.dataset.evidenceCall = row.callId;
        var summary = el("summary"), heading = el("span", (row.operationAlias || "Вызов без T") + " → " +
          (row.evidenceAlias || (row.eligibility === "None" ? "без E" : "E не установлен")) +
          (row.toolName ? " · " + row.toolName : ""));
        heading.appendChild(el("span", (outcomes[row.outcome] || "Завершение не установлено") +
          (row.evidenceAlias ? " · " + (row.delivery === "Delivered" ? "E доставлен" : row.delivery === "Excluded" ? "E исключён" : "доставка E неизвестна") : ""),
          "model-context-message-preview"));
        summary.appendChild(heading); detail.appendChild(summary);
        var body = el("div", undefined, "model-context-message-body"); detail.appendChild(body);
        rendered.set(row.callId, detail); list.appendChild(detail);
        var loaded = false;
        detail.addEventListener("toggle", function () {
          if (!detail.open || loaded || !live(c) || !detail.isConnected) return; loaded = true;
          body.appendChild(el("p", outcomes[row.outcome] || "Завершение инструмента не установлено."));
          body.appendChild(el("p", row.eligibility === "None" ? "Служебный результат: E не выдаётся." :
            row.evidenceAlias ? "Ссылка результата: " + row.evidenceAlias + ". Для цитирования нужна доставка в запрос." : "Допуск к цитированию не установлен.", "model-context-note"));
          if (row.evidenceAlias) body.appendChild(el("p", row.delivery === "Delivered" ? "E доставлен в этом запросе." :
            row.delivery === "Excluded" ? "E исключён из этого запроса." : "Доставка E в этом запросе не установлена.", "model-context-note"));
          var reasons = (c.detail.events.find(function (event) { return event.eventId === source; }).trace.ContextMessages || [])
            .filter(function (entry) { return entry && entry.ToolCallId === row.callId && entry.Reason; }).map(function (entry) { return entry.Reason; });
          Array.from(new Set(reasons)).forEach(function (reason) { body.appendChild(el("p", reason, "model-context-note")); });
          var links = el("div", undefined, "model-context-toolbar"); body.appendChild(links);
          (row.messageIndices || []).forEach(function (index) {
            links.appendChild(button("Сообщение " + (index + 1), function () {
              c.messageLayout = "wire"; chooseTab("messages", index);
            }));
          });
          (c.detail.resources || []).filter(function (resource) {
            return resource.alias && row.operationAlias && (resource.observations || []).some(function (item) { return item.operationAlias === row.operationAlias; });
          }).forEach(function (resource) {
            links.appendChild(button(resource.alias, function () { c.focusResource = resource.alias; discardViews(c); chooseTab("resources"); }));
          });
          if (row.runId && typeof window.openRunJournal === "function") links.appendChild(button("Ход выполнения", function () {
            if (live(c)) window.openRunJournal({ owner: c.owner, workspaceId: c.workspaceId, sessionId: c.chatId,
              chatId: c.chatId, runId: row.runId, stepId: row.stepId, toolCallId: row.callId });
          }));
          lazy(c, body, "Сохранённые связи · JSON", function () { return JSON.stringify(row, null, 2); }, true);
        });
        if (saved && saved.expanded.indexOf(row.callId) >= 0) detail.open = true;
      });
      count += 50; if (count < rows.length) list.appendChild(more);
    }
    page(); while (saved && count < saved.count && count < rows.length) page();
    if (c.focusEvidence) { reveal(c.focusEvidence); c.focusEvidence = null; }
    else if (saved) { var viewVersion = c.viewVersion; setTimeout(function () { if (live(c) && c.viewVersion === viewVersion) root.scrollTop = saved.scroll; }, 0); }
  }
  async function verifyResource(c, row, control, note, retainedView) {
    var version = c.version, eventId = c.selected;
    c.follow = false; control.disabled = true; note.textContent = retainedView ?
      "Проверка сохранённого view…" : "Проверка полного сохранённого тела…";
    try {
      var query = { requestEventId: eventId, verifyResourceAlias: row.alias };
      if (retainedView) query.verifyRetainedView = retainedView;
      var response = await call(c, "getModelContext", query);
      if (!valid(c, version) || c.selected !== eventId) return;
      var updated = (response.resources || []).find(function (item) { return item.alias === row.alias; });
      if (!matches(c, response) || !updated || !(response.events || []).some(function (item) { return item.eventId === eventId; }))
        throw new Error("Ответ проверки не соответствует выбранному ресурсу.");
      c.detail.resources = c.detail.resources.map(function (item) { return item.alias === row.alias ? updated : item; });
      if (c.mode === "actual" && c.tab === "resources") { c.focusResource = row.alias; await render(c); }
    } catch (error) { if (valid(c, version) && note.isConnected) note.textContent = error.message || String(error); }
    finally { control.disabled = false; }
  }
  async function loadViewInventory(c, row, control, note, previous) {
    var version = c.version, eventId = c.selected, prior = c.inventory[row.alias];
    if (c.inventoryBusy[row.alias]) return;
    var offset = prior ? previous ? Math.max(0, prior.offset - prior.pageSize) : prior.nextOffset : 0;
    if (offset == null) return;
    var operation = {}; c.inventoryBusy[row.alias] = operation;
    c.follow = false; control.disabled = true; note.textContent = "Чтение списка сохранённых views…";
    try {
      var query = { requestEventId: eventId, viewInventoryAlias: row.alias, viewInventoryOffset: offset };
      if (prior) query.viewInventorySnapshotCount = prior.snapshotCount;
      var response = await call(c, "getModelContext", query), page = response.viewInventory;
      if (!valid(c, version) || c.selected !== eventId) return;
      if (!matches(c, response) || !(response.events || []).some(function (item) { return item.eventId === eventId; }) ||
          !page || page.resourceAlias !== row.alias || !Array.isArray(page.items) ||
          !Number.isSafeInteger(page.snapshotCount) || page.snapshotCount < 0 ||
          (prior && page.snapshotCount !== prior.snapshotCount) ||
          page.items.length > page.snapshotCount - offset ||
          page.items.some(function (item, index) { return item.ordinal !== offset + index; }) ||
          (page.nextOffset == null && offset + page.items.length !== page.snapshotCount) ||
          (page.nextOffset != null && (page.nextOffset !== offset + page.items.length ||
            page.nextOffset >= page.snapshotCount || !page.items.length)))
        throw new Error("Ответ списка views не соответствует выбранной ревизии.");
      c.inventory[row.alias] = { snapshotCount: page.snapshotCount, offset: offset,
        pageSize: prior ? prior.pageSize : page.items.length, nextOffset: page.nextOffset, items: page.items };
      if (c.mode === "actual" && c.tab === "resources") { c.focusResource = row.alias; await render(c); }
    } catch (error) { if (valid(c, version) && note.isConnected) note.textContent = error.message || String(error); }
    finally {
      if (c.inventoryBusy[row.alias] === operation) delete c.inventoryBusy[row.alias];
      control.disabled = false;
    }
  }
  async function verifyInventoryVariant(c, row, inventory, item, control, note) {
    var version = c.version, eventId = c.selected;
    c.follow = false; control.disabled = true; note.textContent = "Проверка варианта view…";
    try {
      var response = await call(c, "getModelContext", { requestEventId: eventId,
        verifyResourceAlias: row.alias, verifyViewVariantOrdinal: item.ordinal,
        verifyViewVariantSnapshotCount: inventory.snapshotCount });
      if (!valid(c, version) || c.selected !== eventId || c.inventory[row.alias] !== inventory) return;
      var checked = response.viewVariantVerification, variant = checked && checked.variant;
      if (!matches(c, response) || !(response.events || []).some(function (event) { return event.eventId === eventId; }) ||
          !checked || checked.resourceAlias !== row.alias || checked.ordinal !== item.ordinal ||
          checked.snapshotCount !== inventory.snapshotCount || !variant || variant.view !== item.view ||
          JSON.stringify(variant.coverage) !== JSON.stringify(item.coverage) || variant.partsCount !== item.partsCount ||
          variant.estimatedTextBodyTokens !== item.estimatedTextBodyTokens ||
          (variant.payloadAvailability !== "NotRetained") !== item.payloadRegistered)
        throw new Error("Ответ проверки не соответствует выбранному варианту view.");
      item.payloadAvailability = variant.payloadAvailability;
      item.partsAvailability = variant.partsAvailability;
      item.checkedUtc = variant.checkedUtc;
      if (c.mode === "actual" && c.tab === "resources") { c.focusResource = row.alias; await render(c); }
    } catch (error) { if (valid(c, version) && note.isConnected) note.textContent = error.message || String(error); }
    finally { control.disabled = false; }
  }
  function retainedTextCost(view) {
    return Number.isSafeInteger(view.estimatedTextBodyTokens) && view.estimatedTextBodyTokens >= 0 ?
      " · оценка текста ≈ " + view.estimatedTextBodyTokens + " токенов (по метаданным, UTF-8 байты/4; без обвязки запроса)" :
      " · оценка текста неизвестна";
  }
  function resourceMetadata(c, row, body) {
    var metadata = row.revisionMetadata, payload = metadata && metadata.payload;
    if (metadata) {
      if (metadata.createdUtc) body.appendChild(el("p", "Время создания ревизии: " + new Date(metadata.createdUtc).toLocaleString()));
      if (metadata.contentSha256) body.appendChild(el("p", "Хеш содержимого ревизии: " + metadata.contentSha256));
      if (metadata.parent) body.appendChild(el("p", "Предыдущая ревизия: " + referenceText(metadata.parent)));
      if (metadata.restoredFrom) body.appendChild(el("p", "Восстановлена из: " + referenceText(metadata.restoredFrom)));
      (metadata.dependencies || []).forEach(function (dependency) {
        body.appendChild(el("p", "Зависимость: " + referenceText(dependency.resource || {}) +
          (dependency.kind ? " · " + dependency.kind : "") + (dependency.view ? " · " + dependency.view : "") +
          (dependency.coverage ? " · " + JSON.stringify(dependency.coverage) : "")));
      });
    }
    if (payload) body.appendChild(el("p", "Сохранённое тело: " + payload.byteLength + " байт · " +
      (payload.contentType || "тип не указан") + " · SHA-256 " + payload.sha256));
    var availability = { Unknown: "нет сведений о сохранённом теле", NotRetained: "у ревизии не зарегистрирован payload",
      Unchecked: "сохранённое тело не проверено", Verified: "хеш и длина сохранённого тела проверены",
      Unavailable: "сохранённое тело отсутствует, повреждено или недоступно" };
    var note = el("p", "CAS: " + (availability[row.payloadAvailability] || availability.Unknown) +
      (row.payloadCheckedUtc ? " · " + new Date(row.payloadCheckedUtc).toLocaleString() : ""));
    body.appendChild(note);
    (row.retainedViews || []).forEach(function (view) {
      var viewAvailability = { Unknown: "тело не проверено", NotRetained: "основной payload не зарегистрирован",
        Unchecked: "тело не проверено", Verified: "хеш и длина тела проверены",
        Unavailable: "тело отсутствует, повреждено или недоступно" };
      var partsAvailability = { Unchecked: "части не проверены", Verified: "хеши и длины всех частей проверены",
        Unavailable: "одна или более частей отсутствует, повреждена или недоступна" };
      var viewNote = el("p", "Сохранённое view " + view.view +
        (view.coverage ? " · покрытие " + JSON.stringify(view.coverage) : " · покрытие неизвестно") +
        retainedTextCost(view) +
        " · " + (viewAvailability[view.payloadAvailability] || viewAvailability.Unknown) +
        (view.partsCount ? " · частей: " + view.partsCount + " · " +
          (partsAvailability[view.partsAvailability] || "состояние частей неизвестно") : "") +
        (view.checkedUtc ? " · " + new Date(view.checkedUtc).toLocaleString() : ""));
      body.appendChild(viewNote);
      if (row.alias && (view.payloadAvailability !== "NotRetained" || view.partsCount > 0)) {
        var checkView = button("Проверить view " + view.view, function () {
          return verifyResource(c, row, checkView, viewNote, view.view);
        });
        body.appendChild(checkView);
      }
    });
    if (payload && row.alias) {
      var check = button("Проверить сохранённое тело", function () { return verifyResource(c, row, check, note); });
      body.appendChild(check);
    }
  }
  function resources(c, root) {
    var rows = c.detail && c.detail.resources || [];
    if (!rows.length) { notice(c, "Нет связей с ресурсами", "У этого запроса нет сохранённых связей с ресурсами."); return; }
    root.appendChild(el("p", "Ревизии из выбранного запроса. Views из наблюдений и descriptor — неполный список; полный список сохранённых вариантов загружается по страницам. CAS проверяется отдельно. Live-источник и зависимости не проверяются; доставка модели определяется наблюдениями ниже.", "model-context-note"));
    var currentness = { Current: "Текущая", Superseded: "Устаревшая", Removed: "Удалена",
      Unavailable: "Недоступна", Unknown: "Неизвестна" };
    rows.forEach(function (row) {
      var detail = el("details", undefined, "model-context-message model-context-resource");
      detail.appendChild(el("summary", (row.alias || "Ресурс без сохранённой ссылки") + " · " + (currentness[row.currentness] || currentness.Unknown)));
      root.appendChild(detail);
      if (row.alias && c.focusResource === row.alias) detail.open = true;
      var body = el("div", undefined, "model-context-message-body"), ref = row.resource || {};
      detail.appendChild(body);
      body.appendChild(el("p", referenceText(ref)));
      var scope = row.scope || {};
      body.appendChild(el("p", "Scope: " + (scope.kind ? scope.kind + ":" + scope.id : "не указан в сохранённой трассе")));
      body.appendChild(el("p", row.revisionRecorded === true ? "Ревизия есть в authority." :
        row.revisionRecorded === false ? "Ревизия не найдена в текущем authority." : "Наличие ревизии не проверено."));
      resourceMetadata(c, row, body);
      var inventory = row.alias && c.inventory[row.alias];
      if (inventory) {
        var range = inventory.items.length ? (inventory.offset + 1) + "–" +
          (inventory.offset + inventory.items.length) : "0";
        body.appendChild(el("p", "Сохранённые views: " + range + " из " +
          inventory.snapshotCount + " в снимке authority. Байты проверяются отдельно для каждого варианта."));
        inventory.items.forEach(function (item) {
          var availability = { NotRetained: "тело не зарегистрировано", Unchecked: "тело не проверено",
            Verified: "хеш и длина тела проверены", Unavailable: "тело отсутствует или повреждено" };
          var parts = { Unchecked: "части не проверены", Verified: "все части проверены",
            Unavailable: "одна или более частей недоступна" };
          var itemNote = el("p", "#" + (item.ordinal + 1) + " · " + item.view +
            (item.coverage ? " · покрытие " + JSON.stringify(item.coverage) : " · покрытие неизвестно") +
            retainedTextCost(item) +
            " · " + (item.payloadRegistered ? "payload зарегистрирован" : "payload не зарегистрирован") +
            " · частей: " + item.partsCount +
            (item.payloadAvailability ? " · " + (availability[item.payloadAvailability] || "состояние тела неизвестно") :
              (item.payloadRegistered || item.partsCount ? " · байты не проверены" : " · байты не сохранены")) +
            (item.partsCount && item.partsAvailability ? " · " + (parts[item.partsAvailability] || "состояние частей неизвестно") : "") +
            (item.checkedUtc ? " · " + new Date(item.checkedUtc).toLocaleString() : ""));
          body.appendChild(itemNote);
          if (item.payloadRegistered || item.partsCount > 0) {
            var checkVariant = button("Проверить вариант #" + (item.ordinal + 1), function () {
              return verifyInventoryVariant(c, row, inventory, item, checkVariant, itemNote);
            });
            body.appendChild(checkVariant);
          }
        });
      }
      if (row.alias && row.revisionRecorded === true && (!inventory || inventory.offset > 0 || inventory.nextOffset != null)) {
        var inventoryNote = el("p", inventory ? "Доступен переход между страницами views." :
          "Список сохранённых views ещё не загружен.");
        body.appendChild(inventoryNote);
        if (inventory && inventory.offset > 0) {
          var previous = button("Предыдущая страница views", function () {
            return loadViewInventory(c, row, previous, inventoryNote, true);
          });
          body.appendChild(previous);
        }
        if (!inventory || inventory.nextOffset != null) {
          var load = button(inventory ? "Следующая страница views" : "Показать сохранённые views", function () {
            return loadViewInventory(c, row, load, inventoryNote, false);
          });
          body.appendChild(load);
        }
      }
      (row.observations || []).forEach(function (item) {
        var details = [item.requestAlias || "Q?", item.operationAlias || "", item.evidenceAlias || "",
          item.view || "view?", item.coverage ? JSON.stringify(item.coverage) : "coverage?",
          item.observationComplete === true ? "наблюдение целиком" :
            item.observationComplete === false ? "частичное наблюдение" : "полнота неизвестна",
          item.bodyIncludedInMessage === true ? "тело включено" :
            item.bodyIncludedInMessage === false ? "тело не включено" : "доставка неизвестна"].filter(Boolean);
        body.appendChild(el("p", details.join(" · ")));
        body.appendChild(el("p", Number.isSafeInteger(item.estimatedWireMessageTokens) && item.estimatedWireMessageTokens >= 0 ?
          "Оценка всего сериализованного read-сообщения для этого view: ≈ " +
            item.estimatedWireMessageTokens + " токенов (не измеренное usage)." :
          "Оценка сериализованного read-сообщения для этого view неизвестна."));
        body.appendChild(el("p", readMetadataText(item.readMetadata)));
      });
    });
  }

  async function render(c) {
    var selected = c.selected, tab = c.tab;
    await renderCurrent(c);
    if (live(c) && c.selected === selected && c.tab === tab) restorePresentation(c);
  }
  async function renderCurrent(c) {
    if (!live(c)) return;
    syncTabs(c); updateOutline(c);
    clearView(c); c.activeKey = viewKey(c);
    var root = $("modelContextBody"), version = c.version;
    var retained = c.viewCache && c.viewCache.get(c.activeKey);
    if (retained) {
      c.viewCache.delete(c.activeKey); root.appendChild(retained.fragment); c.mounts = retained.mounts;
      c.captureEvidence = retained.captureEvidence;
      c.viewer = retained.viewer;
      if (c.tab === "messages") { messageHeader(c); if (c.viewer && c.viewer.setMode && retained.jsonMode !== c.contents.jsonMode) c.viewer.setMode(c.contents.jsonMode); }
      if (c.viewer && c.viewer.getMode) $("modelContextJsonMode").value = c.viewer.getMode();
      root.scrollTop = retained.scroll; return;
    }
    if (c.mode === "preview") {
      if (!c.preview) {
        notice(c, state.chatReadOnly ? "Чат доступен только для чтения" : c.previewError ? "Предпросмотр недоступен" : "Подготовка предпросмотра…",
          state.chatReadOnly ? "Для нового запроса создайте новый чат." : c.previewError || "Запрос ещё не отправляется.", c.previewError ? "error" : "loading");
        return;
      }
      if (c.tab === "response") { root.textContent = "Это предпросмотр — ответа ещё нет."; return; }
      if (c.tab === "resources") { root.textContent = "Сохранённые ревизии появятся после фактического запроса."; return; }
      if (c.tab === "evidence") { root.textContent = "Доказательства доступны в сохранённом запросе."; return; }
      modelDocument(c, c.preview.text, c.previewEntries); updateOutline(c);
      if (c.tab === "json") mount(c, root, c.preview.text, true);
      else if (c.tab === "omissions") omissions(c, root);
      else if (c.tab === "parameters") requestFields(c, root);
      else if (c.tab === "summary" || c.tab === "group") root.textContent = "Предварительный состав. Выберите сообщение; запрос ещё не отправлен.";
      else messageView(c, root);
      return;
    }
    if (!c.detail || !c.selected) {
      notice(c, c.selectionError ? "Не удалось открыть запрос" : c.loading ? "Загрузка запроса…" : c.scope ? "Запрос " + (c.scope.stepId ? "шага" : "запуска") + " не найден" : "Нет сохранённых запросов",
        c.selectionError || c.emptyMessage, c.selectionError ? "error" : "empty");
      return;
    }
    if (c.tab === "omissions") { omissions(c, root); return; }
    if (c.tab === "group") { var groupTrace = requestRecord(c).trace;
      RNAssistantRequestContents.group(root, groupTrace, c.document ? c.document.list.length : groupTrace.MessageCount, c.contents.group); return; }
    if (c.tab === "resources") { resources(c, root); return; }
    if (c.tab === "evidence") { executionEvidence(c, root); return; }
    function navigate(view, target) {
      if (view === "request") { c.follow = false; c.tab = "summary";
        // Previous dispatch may belong to another step of this same run.
        if (c.scope && c.scope.stepId) c.scope = { runId: c.scope.runId };
        select(c, target, true).then(function () {
        if (!live(c) || !c.detail) return;
        var item = c.detail.events.find(function (event) { return event.type === "llm.request"; });
        if (item && !c.attempts.some(function (event) { return event.eventId === item.eventId; })) c.attempts.push(item);
        attemptPicker(c).value = c.selected;
      }); }
      else chooseTab(view, target);
    }
    if (c.tab === "summary") { RNAssistantModelContextDetails.summary(root, c.detail, navigate); return; }
    if (c.tab === "compare") {
      var previous = c.detail.summary && c.detail.summary.previousRequestEventId;
      if (!previous) { notice(c, "Предыдущий запрос не найден", "Сравниваются сохранённые запросы одного запуска. Отсутствие записи не означает совпадение."); return; }
      if (!c.detail.comparison) {
        notice(c, "Сравниваю запросы…", "Чтение двух сохранённых тел, до 8 МБ каждое.", "loading");
        try {
          var compared = await call(c, "getModelContext", { requestEventId: c.selected, compareRequestEventId: previous });
          if (!valid(c, version) || c.tab !== "compare") return;
          if (!matches(c, compared) || !compared.comparison || compared.comparison.afterEventId !== c.selected)
            throw new Error("Сравнение не соответствует выбранному запросу.");
          c.detail.comparison = compared.comparison;
        } catch (error) { if (valid(c, version) && c.tab === "compare") notice(c, "Сравнение недоступно", error.message, "error"); return; }
      }
      root.replaceChildren(); RNAssistantModelContextDetails.compare(root, c.detail.comparison, navigate); return;
    }
    var request = c.detail.events.find(function (e) { return e.eventId === c.selected; });
    if (c.tab === "response") {
      var events = c.detail.events.filter(function (e) { return e.type !== "llm.request"; });
      if (!events.length) { notice(c, "Ответ ещё не сохранён", "Проверьте ход работы или обновите историю запросов."); return; }
      events.forEach(function (event) {
        var streaming = request.trace.Streaming === true || c.detail.events.some(function (e) { return e.type === "assistant.chunk"; });
        var title = event.type === "llm.response" ? (streaming ? "Собранный потоковый ответ" : request.trace.Streaming === false ? "Ответ провайдера" : "Сохранённый ответ · формат записи не указан") :
          event.type === "assistant.chunk" ? "Исходные SSE data-фрагменты" : event.type === "agent.response.rejected" ? "Отклонённый ответ модели" :
          event.type === "model.response.accepted" ? "Parser принял форму ответа" : event.type === "llm.failure" ? "Ошибка обращения к модели" : event.type;
        var body = el("section", undefined, "model-context-response"); body.dataset.responseEvent = event.eventId; root.appendChild(body);
        var head = el("div", undefined, "model-context-response-head"), heading = el("div");
        heading.appendChild(el("h3", title));
        heading.appendChild(el("small", dateText(event.createdUtc)));
        head.appendChild(heading); body.appendChild(head);
        if (event.trace.Error) body.appendChild(el("p", event.trace.Error, "diagnostic-reason"));
        if (event.trace.CaptureTruncated || event.trace.Completed === false) body.appendChild(el("p", "Запись неполная: " +
          (event.trace.CaptureTruncated ? "достигнут лимит захвата." : "полнота ответа не подтверждена."), "diagnostic-body-note"));
        if (event.hasPayload) {
          var content = lazy(c, body, event.type === "agent.response.rejected" ? "Точный отклонённый ответ" : "Сохранённое содержимое", function () { return payload(c, event.eventId, undefined, version); }, true,
            event.type === "llm.response" ? responseBody : event.type === "agent.response.rejected" ? function (context, target, value) {
              mount(context, target, value.text, true, ["$", "$.tool_calls", "$.tool_calls[0]", "$.response", "$.response.tool_calls", "$.response.tool_calls[0]"]);
            } : null);
          head.appendChild(button("Скачать", function () { downloadEvent(c, event.eventId); }));
        } else body.appendChild(el("p", "Тело не сохранено."));
      }); return;
    }
    if (!request.hasPayload) { notice(c, "Содержимое не сохранено", "Точное тело запроса не сохранено."); return; }
    notice(c, "Загрузка запроса…", "Чтение сохранённого содержимого.", "loading");
    var viewVersion = c.viewVersion;
    try {
      var value = await payload(c, request.eventId, undefined, version);
      if (!valid(c, version) || c.viewVersion !== viewVersion) return;
      root.replaceChildren();
      if (c.tab === "json") mount(c, root, value.text, true);
      else {
        modelDocument(c, value.text, request.trace.ContextMessages); updateOutline(c, true);
        if (c.tab === "parameters") requestFields(c, root); else messageView(c, root);
      }
    } catch (error) {
      if (valid(c, version) && c.viewVersion === viewVersion)
        notice(c, "Не удалось прочитать запрос", error.message || String(error), "error");
    }
  }
  async function downloadEvent(c, eventId) {
    var version = c.version;
    // Start the picker while this click still has user activation. Open the
    // lease only after a file is chosen, then close it on every outcome.
    var data;
    try {
      var handle = window.showSaveFilePicker ?
        await window.showSaveFilePicker({ suggestedName: eventId + ".json" }) : null;
      var response = await call(c, "getModelContextPayload", { eventId: eventId });
      data = response && response.data;
      if (!valid(c, version) || !matches(c, response) || response.eventId !== eventId || !data)
        throw new Error("RESOURCE_DOWNLOAD_INVALID");
      if (handle) {
        var writable = await handle.createWritable();
        await window.RNAssistantResourceDownload.save(data, { fetch: window.fetch.bind(window),
          signal: c.abort.signal, isCurrent: function () { return valid(c, version); }, writable: writable });
      } else {
        // Older WebViews lack a file stream; their native Blob save is a fallback.
        var bytes = await window.RNAssistantResourceDownload.read(data, { fetch: window.fetch.bind(window),
          signal: c.abort.signal, isCurrent: function () { return valid(c, version); }, maxBytes: Number.MAX_SAFE_INTEGER });
        if (valid(c, version)) save({ bytes: bytes, contentType: data.payload.contentType }, eventId + ".json");
      }
    }
    catch (error) { if (valid(c, version)) status(c, error.message); }
    finally { if (data && data.leaseId) await send("resourceDataClose", { chatId: c.chatId,
      workspaceId: payloadOwner(c), leaseId: data.leaseId }).catch(function () {}); }
  }
  async function select(c, eventId, forceRender) {
    var same = c.selected === eventId && c.detail;
    if (!same) { cancelReads(c); discardViews(c); c.document = null; c.contents.index = 0; c.contents.page = 0; c.contents.group = null; c.contents.collapsed = false; c.contents.part = 0; c.contents.source = "content"; c.presentation = {}; c.cache = {}; c.pending = {}; c.inventory = {}; c.inventoryBusy = {}; c.selected = eventId; c.detail = null; c.focusResource = null; c.focusEvidence = null; }
    if (!same || forceRender) {
      c.loading = true; c.emptyMessage = ""; c.selectionError = "";
      notice(c, "Загрузка запроса…", "Чтение сохранённого обмена.", "loading");
      previewAvailability(c);
    }
    var version = c.version;
    status(c, "Чтение сохранённого обмена…");
    try {
      var detail = await call(c, "getModelContext", Object.assign({ requestEventId: eventId, includeEvidence: c.tab === "evidence" }, c.scope || {}));
      if (!valid(c, version)) return;
      var request = (detail.events || []).find(function (e) { return e.type === "llm.request" && e.eventId === eventId; });
      if (!matches(c, detail) || !request || !request.trace || c.scope &&
          (request.trace.RunId !== c.scope.runId || c.scope.stepId && request.trace.StepId !== c.scope.stepId))
        throw new Error("Сохранённый запрос не соответствует выбранному чату и шагу.");
      var changed = !c.detail || JSON.stringify(c.detail.events.map(function (e) { return e.eventId; })) !== JSON.stringify(detail.events.map(function (e) { return e.eventId; }));
      var resourcesChanged = !c.detail || JSON.stringify(c.detail.resources) !== JSON.stringify(detail.resources);
      var evidenceChanged = !c.detail || JSON.stringify(c.detail.evidenceReport) !== JSON.stringify(detail.evidenceReport);
      var summaryChanged = !c.detail || JSON.stringify(c.detail.summary) !== JSON.stringify(detail.summary);
      var invalidated = { summary: summaryChanged, response: changed, resources: resourcesChanged, evidence: evidenceChanged };
      Object.keys(invalidated).forEach(function (tab) {
        var cached = c.viewCache.get(tab);
        if (invalidated[tab] && cached) { cached.mounts.forEach(function (host) { RNAssistantViewerRegistry.unmount(host); }); c.viewCache.delete(tab); }
      });
      if (same && c.detail.comparison) detail.comparison = c.detail.comparison;
      c.detail = detail;
      var response = detail.events.find(function (e) { return e.type === "llm.response" || e.type === "llm.failure"; });
      $("modelContextAttempt").title = request.trace.Purpose || "Сохранённый запрос";
      status(c, (detail.summary && detail.summary.parser === "Rejected" ? "Отклонённый ответ сохранён" : response ? response.type === "llm.response" ? "Ответ сохранён" : "Ошибка запроса" : "Ответ пока не сохранён") +
        (c.follow ? " · обновляется автоматически" : c.scope ? c.scope.stepId ? " · выбранный шаг" : " · выбранный запуск" : " · выбранный запрос"));
      c.loading = false; previewAvailability(c);
      if (!same || forceRender || invalidated[c.tab]) await render(c);
    } catch (error) {
      if (valid(c, version)) {
        status(c, error.message);
        if (!same || forceRender) { c.detail = null; c.selectionError = error.message; notice(c, "Не удалось открыть запрос", error.message, "error"); }
      }
    } finally { if (valid(c, version)) { c.loading = false; previewAvailability(c); } }
  }
  function attemptPicker(c) {
    var picker = $("modelContextAttempt"); picker.replaceChildren();
    c.attempts.forEach(function (event) {
      var option = el("option", (event.alias || "#" + event.sequence) + " · " + dateText(event.createdUtc) +
        (event.trace.Purpose ? " · " + event.trace.Purpose : ""));
      option.value = event.eventId; picker.appendChild(option);
    });
    picker.disabled = !c.attempts.length;
    return picker;
  }
  async function list(c, older, forceRender) {
    if (!live(c) || c.mode !== "actual" || c.listRequest && c.listRequest.version === c.version) return;
    var operation = { version: c.version }; c.listRequest = operation;
    try {
      var response = await call(c, "getModelContext", Object.assign({}, c.scope || {}, older ? { beforeSequence: c.before } : { knownRevision: c.revision }));
      if (!valid(c, operation.version) || c.mode !== "actual") return;
      if (!matches(c, response)) throw new Error("История запросов не соответствует выбранному чату.");
      if (response.unchanged) return;
      if (c.scope) response.events = response.events.filter(function (event) {
        return event.trace && event.trace.RunId === c.scope.runId && (!c.scope.stepId || event.trace.StepId === c.scope.stepId);
      });
      if (older) c.attempts = c.attempts.concat(response.events);
      else {
        c.revision = response.revision;
        var selected = c.attempts.find(function (e) { return e.eventId === c.selected; });
        c.attempts = response.events;
        if (!c.follow && selected && !c.attempts.some(function (e) { return e.eventId === selected.eventId; })) c.attempts.push(selected);
      }
      c.before = response.nextBeforeSequence;
      var picker = attemptPicker(c);
      $("modelContextOlder").disabled = !c.before;
      var chosen = c.follow || c.scope && !c.selected ? c.attempts[0] && c.attempts[0].eventId : c.selected;
      if (chosen) {
        picker.value = chosen;
        if (!older || chosen !== c.selected) {
          var selecting = select(c, chosen, forceRender); operation.version = c.version; await selecting;
        }
      }
      else {
        c.version++; operation.version = c.version;
        c.selected = null; c.detail = null; c.loading = false; c.selectionError = ""; c.cache = {}; c.pending = {};
        c.emptyMessage = c.scope ? "Точный запрос этого " + (c.scope.stepId ? "шага" : "запуска") + " не найден в загруженной части истории." + (c.before ? " Нажмите «Ранее» для предыдущей страницы." : "") : c.workspaceId ? "В этой беседе ещё нет сохранённых запросов модели." : state.chatReadOnly ? "Сохранённых запросов нет. Для нового запроса создайте новый чат." :
          "Сохранённых запросов пока нет. Доступен предварительный контекст.";
        status(c, c.emptyMessage); previewAvailability(c);
        if (c.before) $("modelContextHistory").open = true;
        await render(c);
      }
    } catch (error) {
      if (valid(c, operation.version)) {
        status(c, error.message || String(error));
        if (!c.detail) {
          c.selectionError = error.message || String(error);
          notice(c, "Не удалось загрузить историю", c.selectionError, "error");
        } else if (forceRender) await render(c);
      }
    } finally { if (c.listRequest === operation) c.listRequest = null; }
  }
  async function findAlias(c) {
    if (!c || !live(c) || c.mode !== "actual") return;
    var name = $("modelContextAliasSearch").value.trim().toUpperCase().replace(/@V/g, "@v");
    if (!name) return;
    c.version++; var version = c.version;
    c.pending = {}; c.follow = false;
    status(c, "Поиск " + name + "…");
    notice(c, "Поиск ссылки " + name, "В сохранённой истории этого чата.", "loading");
    try {
      var detail = await call(c, "getModelContext", { alias: name, includeEvidence: /^[TE][1-9][0-9]*$/.test(name) });
      if (!valid(c, version)) return;
      var request = (detail.events || []).find(function (event) { return event.type === "llm.request"; });
      if (!matches(c, detail) || !request || detail.selectedAlias !== name) throw new Error("Короткая ссылка не найдена в этом чате.");
      c.scope = null; discardViews(c); c.document = null; c.presentation = {};
      c.cache = {}; c.loading = false; c.selectionError = "";
      c.selected = request.eventId; c.detail = detail; c.inventory = {}; c.inventoryBusy = {};
      c.focusResource = detail.selectedResourceAlias || null;
      c.focusEvidence = detail.evidenceReport && (detail.evidenceReport.tools || []).some(function (row) {
        return row.operationAlias === name || row.evidenceAlias === name;
      }) ? name : null;
      c.tab = c.focusResource ? "resources" : c.focusEvidence ? "evidence" : "summary";
      if (!c.attempts.some(function (event) { return event.eventId === request.eventId; })) c.attempts.push(request);
      attemptPicker(c).value = request.eventId;
      previewAvailability(c);
      status(c, "Найдено " + name + " в " + (request.alias || "сохранённом запросе") + ".");
      await render(c);
    } catch (error) {
      if (valid(c, version)) { await render(c); if (valid(c, version)) status(c, error.message || String(error)); }
    }
  }
  function composerText() { return typeof officeChatDraftText === "function" ? officeChatDraftText() : $("chatInput").value; }
  function draft() { var chat = activeChatSummary(); return JSON.stringify([composerText(), contextDraftResourceIds(), chat && (chat.revision || chat.Revision)]); }
  function previewAvailability(c) {
    $("modelContextActions").disabled = false;
    $("modelContextDialog").classList.toggle("is-workspace", !!c.workspaceId);
    $("modelContextDialog").classList.toggle("is-preview", c.mode === "preview");
    $("modelContextPreviewOption").disabled = !!c.workspaceId || !!state.chatReadOnly;
    $("modelContextPreviewOption").hidden = !!c.workspaceId;
    $("modelContextMode").disabled = !!c.workspaceId;
    $("modelContextMode").hidden = !!c.workspaceId;
    $("modelContextAttempt").hidden = c.mode === "preview";
    $("modelContextPreviewOption").disabled = !!c.workspaceId || !!state.chatReadOnly || !sameSource(c);

  }
  async function preview(c) {
    if (c.workspaceId) return;
    if (!sameSource(c)) { notice(c, "Источник предпросмотра изменён", "Откройте следующий запрос из нужного чата."); return; }
    discardViews(c); c.document = null;
    c.version++; var version = c.version; c.preview = null; c.previewError = ""; clearView(c);
    c.previewVersion = version;
    previewAvailability(c);
    if (state.chatReadOnly) {
      status(c, "Этот чат доступен только для чтения. Для нового запроса создайте новый чат.");
      notice(c, "Чат доступен только для чтения", "Для нового запроса создайте новый чат.");
      return;
    }
    c.previewDraft = draft(); status(c, "Сборка предварительного контекста…");
    notice(c, "Подготовка предпросмотра…", "Запрос ещё не отправляется.", "loading");
    try {
      var response = await call(c, "inspectModelContextPreview", { text: composerText(),
        resourceDraftIds: contextDraftResourceIds(), includeRaw: true });
      var value = await download(c, response, "context-inspector", version);
      if (!valid(c, version)) return;
      c.preview = value; c.previewEntries = response.resourceContextReceipt && response.resourceContextReceipt.Messages;
      c.previewStatus = "Предпросмотр, ещё не отправлено · " + dateText(response.generatedUtc) +
        "\nВложения пока представлены сведениями о файлах.";
      status(c, c.previewStatus); previewAvailability(c); await render(c);
    } catch (error) {
      if (valid(c, version)) {
        c.previewError = error.message || String(error);
        status(c, c.previewError); notice(c, "Предпросмотр недоступен", c.previewError, "error");
      }
    }
  }
  function tick(c) {
    if (!live(c)) return;
    if (c.mode === "actual" && c.follow) list(c, false);
    if (c.mode === "preview" && c.preview && (!sameSource(c) || c.previewDraft !== draft())) status(c, c.previewStatus + " · Снимок устарел — обновите.");
    c.timer = setTimeout(function () { tick(c); }, 2000);
  }
  function open(options) {
    var selected = source();
    // A saved-data link carries its full owner scope; active chat is only the default entry point.
    if (options && options.owner === "office" && options.chatId && !options.workspaceId || options &&
        (options.owner === "project" || options.owner === "plugin") && options.workspaceId && options.sessionId) {
      var explicit = { owner: options.owner, workspaceId: options.workspaceId || null, chatId: options.chatId || options.sessionId };
      if (RNAssistantDiagnostics.key(explicit) !== RNAssistantDiagnostics.key(selected)) selected = Object.assign(explicit,
        { label: options.label || explicit.chatId, document: options.document || explicit.workspaceId || "Сохранённый документ" });
    }
    if (!selected.chatId || state.bridgeUnavailable) return;
    if (selected.owner === "office") {
      if (options && (options.owner && options.owner !== "office" || options.workspaceId ||
          options.chatId && options.chatId !== selected.chatId)) return;
    } else if (!selected.workspaceId || !options || options.owner !== selected.owner ||
        options.workspaceId !== selected.workspaceId || options.sessionId !== selected.chatId) return;
    var scope = options && options.runId ? { runId: options.runId, stepId: options.stepId || "" } : null;
    if (current) dispose();
    current = { owner: selected.owner, chatId: selected.chatId, workspaceId: selected.workspaceId, label: selected.label, documentLabel: selected.document,
      contents: { index: 0, format: "markdown", order: "groups", group: null, page: 0, part: 0, source: "content", jsonMode: "tree" },
      abort: new AbortController(), requests: new Set(), version: 0,
      mounts: [], cache: {}, pending: {}, viewCache: new Map(), presentation: {}, inventory: {}, inventoryBusy: {}, mode: "actual", tab: "summary", follow: !scope && !(options && options.requestEventId), attempts: [],
      scope: scope };
    $("modelContextLayout").dataset.detail = "false";
    window.RNAssistantDiagnostics.showRequest(Object.assign({}, selected, scope || {}));
    $("modelContextMode").value = "actual"; syncTabs(current); attemptPicker(current);
    notice(current, "Загрузка запросов…", "Чтение истории этого чата.", "loading");
    $("modelContextHistory").open = false;
    previewAvailability(current);
    $("modelContextActualControls").classList.remove("hidden"); $("closeModelContextButton").focus();
    $("closeModelContextButton").title = "Вернуться к запуску";
    $("closeModelContextButton").setAttribute("aria-label", $("closeModelContextButton").title);
    if (options && options.mode === "preview" && !current.workspaceId) {
      current.mode = "preview"; current.tab = "messages"; $("modelContextMode").value = "preview";
      $("modelContextActualControls").classList.add("hidden"); preview(current);
    } else if (options && options.requestEventId) {
      var opened = current;
      select(opened, options.requestEventId, true).then(function () {
        if (!live(opened) || !opened.detail) return;
        opened.attempts = opened.detail.events.filter(function (event) { return event.type === "llm.request"; });
        attemptPicker(opened).value = opened.selected;
      });
    } else if (scope) list(current, false);
    tick(current);
  }
  async function refresh() {
    var c = current; if (!c || !live(c)) return;
    if (c.mode === "preview") { await preview(c); return; }
    cancelReads(c); discardViews(c); c.inventory = {}; c.inventoryBusy = {}; c.revision = null;
    if (c.selected && !c.follow) await select(c, c.selected, true); else await list(c, false, true);
  }
  function requestMenu(trigger) {
    var c = current; if (!c) return;
    var record = requestRecord(c);
    RNAssistantActionMenu.open(trigger, [
      { label: "Точный запрос JSON", icon: "code", onSelect: function () { chooseTab("json"); } },
      { label: "Скачать точный запрос", icon: "download", disabled: c.mode === "preview" ? !c.preview : !record || !record.hasPayload,
        onSelect: function () { if (c.mode === "preview") save(c.preview, "context-preview.json"); else downloadEvent(c, c.selected); } },
      { label: "Обновить", icon: "refresh", onSelect: refresh },
      { label: "Экспорт диагностики чата", icon: "download", disabled: c.mode === "preview", onSelect: function () {
        var target = c.owner === "office" ? { owner: "office", chatId: c.chatId } : { owner: c.owner, workspaceId: c.workspaceId, sessionId: c.chatId };
        if (window.openTrajectoryExport && window.openTrajectoryExport(target)) {
          var summary = document.querySelector("#trajectoryExportOptions > summary"); if (summary) summary.focus();
        } else status(c, "Экспорт диагностики недоступен для этого чата.");
      } }
    ]);
  }
  function messageChange(change) {
    var c = current; if (!c || c.tab !== "messages") return;
    retainView(c); Object.assign(c.contents, change); render(c);
  }
  function bind() {
    $("closeModelContextButton").addEventListener("click", function () { backToRun(current); });
    $("modelContextActions").addEventListener("click", function () { requestMenu(this); });
    $("modelContextMessageActions").addEventListener("click", function () {
      var c = current; if (!c || !c.document) return;
      RNAssistantActionMenu.open(this, [
        { label: "Источники и полнота", icon: "info", onSelect: function () { messageChange({ source: "sources" }); } },
        { label: "Точное сообщение JSON", icon: "code", onSelect: function () { messageChange({ source: "whole" }); } },
        { label: "Копировать содержимое", icon: "copy", disabled: selectedContent(c).text == null, onSelect: function () { copyTextResult(selectedContent(c).text); } },
        { label: "Копировать точное сообщение", icon: "copy", onSelect: function () { copyTextResult(selectedContent(c).item.raw); } },
        { label: "Свернуть JSON", icon: "layers", disabled: !c.viewer || !c.viewer.collapseAll, onSelect: function () { c.viewer.collapseAll(); } }
      ]);
    });
    $("modelContextContentBack").addEventListener("click", function () { messageChange({ source: "content" }); });
    document.querySelectorAll("[data-request-format]").forEach(function (control) { control.addEventListener("click", function () {
      messageChange({ format: control.dataset.requestFormat, source: "content" });
    }); });
    $("modelContextJsonMode").addEventListener("change", function () { if (current && current.viewer && current.viewer.setMode) {
      current.contents.jsonMode = this.value; current.viewer.setMode(this.value);
      if (current.viewer.getMode) this.value = current.viewer.getMode();
    } });
    $("modelContextPart").addEventListener("change", function () { messageChange({ part: Number(this.value) }); });
    $("modelContextOrder").addEventListener("change", function () { if (!current) return; current.contents.order = this.value; updateOutline(current, true); });
    $("modelContextPageBefore").addEventListener("click", function () { if (current) { current.contents.page--; updateOutline(current); } });
    $("modelContextPageAfter").addEventListener("click", function () { if (current) { current.contents.page++; updateOutline(current); } });
    $("modelContextBackToList").addEventListener("click", function () {
      $("modelContextLayout").dataset.detail = "false";
      var selected = $("modelContextTree").querySelector('[aria-selected="true"]'); if (selected) selected.focus({ preventScroll: true });
    });
    $("modelContextMode").addEventListener("change", function () {
      if (!current || current.workspaceId || this.value === "preview" && !sameSource(current)) { this.value = "actual"; return; }
      var c = current; cancelReads(c); discardViews(c); c.document = null;
      c.mode = this.value; c.revision = null; c.detail = null; c.presentation = {}; c.cache = {}; c.inventory = {}; c.inventoryBusy = {};
      c.loading = false; c.emptyMessage = ""; c.selectionError = ""; clearView(c); c.tab = "summary"; c.structureTab = "summary";
      c.contents.source = "content"; c.contents.index = 0; c.contents.page = 0;
      syncTabs(c);
      $("modelContextActualControls").classList.toggle("hidden", c.mode !== "actual");
      previewAvailability(c);
      if (c.mode === "preview") preview(c); else { c.selected = null; c.follow = true; list(c, false); }
    });
    Object.keys(tasks).forEach(function (key) {
      var control = $(tasks[key]);
      control.addEventListener("click", function () { chooseTab(key === "structure" ? current && current.structureTab || "summary" : key); });
      control.addEventListener("keydown", function (event) {
        if (["ArrowLeft", "ArrowRight", "Home", "End"].indexOf(event.key) < 0) return;
        event.preventDefault();
        var available = Object.keys(tasks).filter(function (tab) { return !$(tasks[tab]).disabled; });
        var index = available.indexOf(key), next = event.key === "Home" ? 0 : event.key === "End" ? available.length - 1 :
          (index + (event.key === "ArrowRight" ? 1 : -1) + available.length) % available.length;
        $(tasks[available[next]]).click(); $(tasks[available[next]]).focus();
      });
    });
    $("modelContextAttempt").addEventListener("change", function () { if (current) { current.follow = false; select(current, this.value); } });
    $("modelContextAliasFind").addEventListener("click", function () { findAlias(current); });
    $("modelContextAliasSearch").addEventListener("keydown", function (event) { if (event.key === "Enter") { event.preventDefault(); findAlias(current); } });
    $("modelContextLatest").addEventListener("click", function () { if (current) { current.follow = true; current.revision = null; list(current, false); } });
    $("modelContextOlder").addEventListener("click", function () { if (current) list(current, true); });
  }
  window.bindModelContextActions = bind;
  window.RNAssistantModelContext = { members: members, open: open, close: close, dispose: dispose,
    pause: pause, resume: resume,
    onDockState: function (opened, visible) {
      if (!opened && current && current.mounted) dispose();
      else if (current && opened) { current.mounted = true; if (visible) resume(); else pause(); }
    },
    sync: function () {
      if (!current) return;
      if (!sameSource(current)) current.follow = false;
      if (state.bridgeUnavailable) { cancelReads(current); status(current, "Нет связи с хостом. Сохранённый просмотр остаётся доступен; обновите после подключения."); }
      previewAvailability(current);
      if (current.mode === "preview" && (!sameSource(current) || state.chatReadOnly)) {
        cancelReads(current); current.preview = null; current.previewError = state.chatReadOnly ? "Для нового запроса создайте новый чат." : "Источник предпросмотра недоступен. Откройте его из нужного чата."; status(current, current.previewError); render(current);
      }
    } };
}());
