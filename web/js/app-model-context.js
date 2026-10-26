(function () {
  "use strict";
  var current = null, limit = 50 * 1024 * 1024;
  var labels = { Full: "Полностью", Fragment: "Фрагмент", Summary: "Сводка / описание ресурса",
    Excluded: "Не включено", Unknown: "Нет сведений" };
  var tabs = { messages: "modelContextMessagesTab", resources: "modelContextResourcesTab",
    response: "modelContextResponseTab", json: "modelContextJsonTab" };
  function el(tag, text, cls) {
    var node = document.createElement(tag);
    if (text !== undefined) node.textContent = text;
    if (cls) node.className = cls;
    return node;
  }
  function button(text, action) {
    var node = el("button", text, "button secondary compact"); node.type = "button";
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
  function source() {
    var workspace = window.RNAssistantWorkspaceChat, owner = workspace ? workspace.owner() : "office";
    if (owner === "office") return { owner: owner, chatId: state.activeChatId };
    var chat = workspace.snapshot();
    return { owner: owner, workspaceId: snapshotField(snapshotField(chat, "project"), "workspaceId"), chatId: snapshotField(chat, "sessionId") };
  }
  function matches(c, response) {
    return response && response.chatId === c.chatId && (response.workspaceId || null) === (c.workspaceId || null);
  }
  function payloadOwner(c) { return "model-context" + (c.workspaceId ? ":" + c.workspaceId : ""); }
  function live(c) {
    var selected = source();
    return current === c && c.owner === selected.owner && matches(c, selected) && !c.abort.signal.aborted &&
      $("tab-chat").classList.contains("active") && (c.owner !== "office" || typeof officeTranscriptActive !== "function" || officeTranscriptActive());
  }
  function valid(c, version) { return live(c) && c.version === version; }
  function status(c, text) { if (live(c)) $("modelContextStatus").textContent = text; }
  function syncTabs(c) {
    Object.keys(tabs).forEach(function (key) {
      var control = $(tabs[key]), selected = c.tab === key;
      control.classList.toggle("active", selected);
      control.setAttribute("aria-selected", String(selected)); control.tabIndex = selected ? 0 : -1;
      control.disabled = c.mode === "preview" && (key === "response" || key === "resources");
    });
    $("modelContextBody").setAttribute("aria-labelledby", tabs[c.tab]);
  }
  function dateText(value) {
    return new Date(value).toLocaleString("ru-RU", { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" });
  }
  function clearView(c) {
    if (c.captureMessages) { c.captureMessages(); c.captureMessages = null; }
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
  function close(restore) {
    var c = current, returnToSource = c && live(c) && restore !== false; current = null;
    if (c) {
      clearTimeout(c.timer); c.abort.abort();
      c.requests.forEach(function (id) { cancelBridgeRequest(id).catch(function () {}); });
      clearView(c); c.cache = {};
    }
    $("modelContextDialog").classList.add("hidden");
    $("openModelContextFromTokens").setAttribute("aria-expanded", "false");
    window.RNAssistantChatInspector.hideRequest();
    if (returnToSource && typeof c.onClose === "function") c.onClose();
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
  function mount(c, node, text, json) {
    // Viewer bounds limit rendering only; copy/download keep the complete source.
    if (text.length > 2000000) {
      node.appendChild(el("p", "Большой текст: показаны первые 200 000 символов. Копирование и скачивание сохраняют полный исходник."));
      node.appendChild(button("Копировать полностью", function () { copyTextResult(text); }));
      node.appendChild(el("pre", text.slice(0, 200000))); return;
    }
    if (!json) {
      var newline = text.indexOf("\n"), suffix = newline >= 0 ? text.slice(newline + 1).trim() : "";
      if (newline >= 0 && /^[A-Z_]+/.test(text) && /^[\[{]/.test(suffix) && decoded(suffix) !== null) {
        node.appendChild(el("p", text.slice(0, newline)));
        node.appendChild(button("Копировать исходный текст", function () { copyTextResult(text); }));
        var structured = el("div"); node.appendChild(structured); mount(c, structured, suffix, true); return;
      }
    }
    var kind = json || /^[\s]*[\[{]/.test(text) && decoded(text) !== null ? "json" : "markdown";
    node.classList.add("product-viewer");
    window.RNAssistantViewerRegistry.mount(kind, node, { text: text, fullText: text, complete: true,
      completeness: "full", mode: "tree", limits: { maxChars: 2000000 }, onCopy: window.copyTextResult });
    c.mounts.push(node);
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
        loaded = true;
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
  function deliveredSource(c, entry, link, selected, content) {
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
        if (result.name !== "common.resources_read" || result.status !== "ok" ||
            result.tool_call_id !== alias(c, "operations", entry.ToolCallId)) return null;
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
      return { reference: selected.alias, target: data.target, view: link.View,
        coverage: data.sourceCoverage || data.coverage, excerpt: excerpt, excerptTruncated: text != null && excerpt.length < text.length };
    } catch (_) { return null; } // Exact raw message remains available without an inferred label.
  }
  function sourceLinks(c, parent, entry, key, saved, content) {
    (entry.EvidenceLinks || []).filter(Boolean).forEach(function (link, index) {
      var selected = resourceFor(c, link), resource = link.Resource || {}, metadata = link.ReadMetadata;
      var preview = deliveredSource(c, entry, link, selected, content);
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
        c.focusResource = selected.alias; c.tab = "resources"; render(c);
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
  function messageSources(c, parent, entry, key, saved, ambiguous, content) {
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
    sourceLinks(c, source, entry, key, saved, content);
    (entry.Parts || []).filter(Boolean).forEach(function (part, index) { partSource(source, part, key + "/part/" + index, saved); });
    if (!(entry.EvidenceLinks || []).length && !(entry.Parts || []).length && !entry.Reason)
      source.appendChild(el("p", "Сохранена только отметка представления; подробности источников отсутствуют.", "model-context-note"));
  }
  function requestComposition(c, parent, composition, messageCount, reveal, saved, renderMessage, grouped) {
    if (c.mode !== "actual") return;
    var groups = [
      { id: "InstructionsAndCatalog", title: "Инструкции и каталог", color: "color-0" },
      { id: "Conversation", title: "История сообщений", color: "color-1" },
      { id: "ToolActivity", title: "Вызовы и результаты", color: "color-2" },
      { id: "ResourceReferences", title: "Ссылки и состояние ресурсов", color: "is-reference" },
      { id: "Resources", title: "Контекст ресурсов", color: "color-3" },
      { id: "MessagesWithAttachments", title: "Сообщения с вложениями", color: "is-attachment" },
      { id: "ResponseFormat", title: "Формат ответа", color: "color-4" },
      { id: "Unattributed", title: "Не распределено", color: "is-unattributed" }
    ];
    var indices = new Set(), optionsCount = 0, sum = 0;
    var validComposition = composition && composition.Version === 1 && composition.Method === "base_text_media_v1" &&
      typeof composition.HasMedia === "boolean" && Number.isSafeInteger(composition.TotalEstimatedTokens) && composition.TotalEstimatedTokens >= 0 &&
      Array.isArray(composition.Items) && composition.Items.length <= messageCount + 1 && composition.Items.every(function (item) {
        if (!item || !groups.some(function (group) { return group.id === item.Group; }) || !Number.isSafeInteger(item.EstimatedTokens) || item.EstimatedTokens < 0) return false;
        sum += item.EstimatedTokens;
        if (item.Group === "ResponseFormat") return item.MessageIndex == null && ++optionsCount === 1;
        if (!Number.isInteger(item.MessageIndex) || item.MessageIndex < 0 || item.MessageIndex >= messageCount || indices.has(item.MessageIndex)) return false;
        indices.add(item.MessageIndex); return true;
      }) && indices.size === messageCount && sum === composition.TotalEstimatedTokens;
    var section = el("section", undefined, "model-context-composition"); parent.appendChild(section);
    if (!validComposition) {
      var missing = sourceDisclosure(section, "Состав по разделам", composition ? "Недоступен" : "Нет оценки", "composition", saved);
      missing.appendChild(el("p", composition ? "Сводка не соответствует этому запросу или её формат не поддерживается. Сообщения доступны ниже." :
        "Для этого запроса оценки разделов не сохранены. Точные сообщения и сведения об источниках доступны ниже.", "model-context-note"));
      return;
    }
    var chart = el("div", undefined, "chat-inspector-composition-chart model-context-composition-chart");
    chart.setAttribute("role", "group"); chart.setAttribute("aria-label", "Доли базовой оценки сохранённого запроса"); section.appendChild(chart);
    var outer = sourceDisclosure(section, "Состав по разделам", "≈ " + sum.toLocaleString("ru-RU") + " ток. · базовая оценка", "composition", saved);
    outer.classList.add("model-context-composition-total");
    if (!saved || grouped && saved.grouped !== grouped && saved.expanded.length) outer.open = true;
    var explanation = sourceDisclosure(outer, "Как посчитано", "", "composition/method", saved);
    explanation.appendChild(el("p", "Базовая оценка токенов, без калибровки и резервов. Расход API показывается отдельно. " +
      "Каждое сообщение учтено один раз и целиком, включая служебные поля. Тела результатов могут входить в раздел вызовов. " +
      "Ссылки сами по себе не означают, что содержимое ресурса отправлено." +
      (composition.HasMedia ? " Медиа оценено приближённо." : ""), "model-context-note"));
    var version = c.viewVersion;
    groups.forEach(function (group) {
      var items = composition.Items.filter(function (item) { return item.Group === group.id; });
      if (!items.length) return;
      var tokens = items.reduce(function (total, item) { return total + item.EstimatedTokens; }, 0);
      var caption = "≈ " + tokens.toLocaleString("ru-RU") + " ток. · " + (sum ? tokens * 100 / sum : 0).toLocaleString("ru-RU", { maximumFractionDigits: 1 }) + "%";
      var key = "composition/" + group.id, detail = sourceDisclosure(outer, group.title, caption, key, saved), count = 0;
      var restoreCount = saved && saved.compositionPages && saved.compositionPages[key] || 0;
      if (grouped && saved && saved.grouped !== grouped) items.forEach(function (item, index) {
        if (saved.expanded.indexOf(item.MessageIndex) >= 0) { detail.open = true; restoreCount = Math.max(restoreCount, index + 1); }
      });
      detail.classList.add("model-context-composition-group");
      detail.dataset.compositionGroup = group.id;
      var dot = el("i", undefined, "chat-inspector-composition-dot chat-inspector-composition-color " + group.color);
      dot.setAttribute("aria-hidden", "true"); detail.children[0].insertBefore(dot, detail.children[0].children[0]);
      var size = el("span", group.id === "ResponseFormat" ? "Параметры запроса" : "Сообщений: " + items.length, "model-context-composition-count");
      detail.children[0].children[1].appendChild(size);
      detail.dataset.compositionCount = "0";
      if (group.id === "InstructionsAndCatalog") detail.appendChild(el("p", "Инструкции, навыки и каталог в общем сообщении пока не разделены на доли.", "model-context-note"));
      if (group.id === "Unattributed") detail.appendChild(el("p", "Размер учтён, но сохранённых данных недостаточно для однозначного раздела.", "model-context-note"));
      var more = button("Показать ещё", page);
      function page() {
        if (!live(c) || c.viewVersion !== version || !detail.isConnected) return;
        more.remove();
        items.slice(count, count + 50).forEach(function (item) {
          if (grouped && item.MessageIndex != null) {
            renderMessage(item.MessageIndex, detail, item.EstimatedTokens);
            return;
          }
          var control = button((item.MessageIndex == null ? "Параметры запроса" : "Сообщение " + (item.MessageIndex + 1)) +
            " · ≈ " + item.EstimatedTokens.toLocaleString("ru-RU") + " ток.", function () {
            if (live(c) && c.viewVersion === version && control.isConnected) reveal(item.MessageIndex);
          });
          control.className = "button compact ghost model-context-composition-entry"; detail.appendChild(control);
        });
        count += 50; detail.dataset.compositionCount = String(count);
        if (count < items.length) detail.appendChild(more);
      }
      detail.addEventListener("toggle", function () {
        if (!detail.open || count) return;
        page();
        while (count && count < restoreCount && count < items.length) page();
      });
      if (tokens > 0) {
        var segment = button("", function () {
          if (!live(c) || c.viewVersion !== version || !detail.isConnected) return;
          outer.open = true; detail.open = true; detail.children[0].focus();
        });
        segment.className = "chat-inspector-composition-color " + group.color; segment.style.flexGrow = String(tokens);
        segment.title = group.title + " · " + caption; segment.setAttribute("aria-label", segment.title); chart.appendChild(segment);
      }
    });
    if (!sum) chart.remove();
    return composition;
  }
  function messages(c, text, entries, composition) {
    var root = $("modelContextBody"), fields;
    try { fields = members(text); } catch (error) { root.appendChild(el("p", error.message)); return mount(c, root, text, true); }
    var raw = field(fields, "messages");
    if (!raw) { root.appendChild(el("p", "В payload нет массива messages. Откройте JSON запроса.")); return; }
    var listRoot = el("section", undefined, "model-context-messages"); root.appendChild(listRoot);
    var list = members(raw), count = 0, byIndex = new Map();
    (entries || []).filter(Boolean).forEach(function (entry) {
      if (!Number.isInteger(entry.MessageIndex) || entry.MessageIndex < 0 || entry.MessageIndex >= list.length) return;
      var at = byIndex.get(entry.MessageIndex) || []; at.push(entry); byIndex.set(entry.MessageIndex, at);
    });
    var mode = c.mode, source = mode === "preview" ? c.previewVersion : c.selected, rows = [];
    var grouped = mode === "actual" && c.messageLayout !== "wire", costs = new Map();
    var saved = c.messageViews && c.messageViews[mode];
    if (saved && saved.source !== source) saved = null;
    c.captureMessages = function () {
      if (!c.messageViews) c.messageViews = {};
      var compositionPages = {};
      root.querySelectorAll("details[data-composition-count]").forEach(function (row) {
        compositionPages[row.dataset.contextSource] = Number(row.dataset.compositionCount);
      });
      c.messageViews[mode] = { source: source, grouped: grouped, count: count, scroll: root.scrollTop,
        expanded: rows.filter(function (row) { return row.open; }).map(function (row) { return Number(row.dataset.messageIndex); }),
        sources: Array.from(root.querySelectorAll("details[data-context-source][open]")).map(function (row) { return row.dataset.contextSource; }),
        omissionCount: omissionCount, compositionPages: compositionPages };
    };
    var roles = { system: "Инструкции", developer: "Приложение",
      user: "Пользователь", assistant: "Модель", tool: "Инструмент", function: "Функция" };
    var more = button("Показать ещё сообщения", page);
    function page() {
      more.remove();
      list.slice(count, count + 50).forEach(function (message, offset) {
        var index = count + offset;
        renderMessage(index, listRoot, costs.get(index));
      });
      count += 50; if (count < list.length) listRoot.appendChild(more);
    }
    function renderMessage(index, parent, tokens) {
        var message = list[index], parts = members(message.raw), role = decoded(field(parts, "role")) || "message";
        var candidates = byIndex.get(index) || [], entry = candidates.length === 1 && candidates[0].Presentation !== "Excluded" ? candidates[0] : null;
        var content = field(parts, "content"), contentValue = decoded(content);
        var detail = el("details", undefined, "model-context-message is-message"), summary = el("summary");
        detail.dataset.messageIndex = String(index); detail.dataset.role = role;
        rows.push(detail);
        summary.appendChild(el("span", String(index + 1), "model-context-message-number"));
        var heading = el("span"), label = el("span", undefined, "model-context-message-label");
        label.appendChild(el("span", roles[role] || role, "model-context-message-title"));
        label.appendChild(el("span", Number.isSafeInteger(tokens) ? "≈ " + tokens.toLocaleString("ru-RU") + " ток." :
          message.raw.length.toLocaleString("ru-RU") + " симв. JSON", "model-context-message-size"));
        summary.title = message.raw.length.toLocaleString("ru-RU") + " символов в JSON сообщения";
        heading.appendChild(label);
        var excerptValue = contentValue;
        if (role === "assistant" && typeof contentValue === "string") {
          var envelope = modelDecision(contentValue);
          if (envelope) excerptValue = envelope.message || envelope.reason || envelope.decision || contentValue;
        }
        var excerpt = typeof excerptValue === "string" ? excerptValue.slice(0, 300).replace(/\s+/g, " ").trim() :
          Array.isArray(contentValue) ? "Составное содержимое: текст и вложения" : "Служебные поля сообщения";
        if (excerpt) heading.appendChild(el("span", excerpt, "model-context-message-preview"));
        if (entry && entry.Presentation && entry.Presentation !== "Unknown") {
          var meta = el("span", undefined, "model-context-message-meta");
          var badge = el("span", note(entry), "model-context-badge"); badge.dataset.kind = entry.Presentation;
          meta.appendChild(badge); heading.appendChild(meta);
        }
        summary.appendChild(heading); detail.appendChild(summary); parent.appendChild(detail);
        var body = el("div", undefined, "model-context-message-body"); detail.appendChild(body);
        var loaded = false;
        detail.addEventListener("toggle", function () {
          if (!detail.open || loaded || !live(c) || !detail.isConnected) return; loaded = true;
          messageSources(c, body, entry, "message/" + index, saved, candidates.length > 1, contentValue);
          if (role === "assistant" && typeof contentValue === "string" && modelDecision(contentValue)) {
            modelMessage(c, body, contentValue);
          } else if (typeof contentValue === "string") {
            var contentNode = el("div"); body.appendChild(contentNode); mount(c, contentNode, contentValue, false);
          } else if (content) lazy(c, body, "Части содержимого · JSON", function () { return content; }, true);
          var metadata = el("details", undefined, "model-context-meta");
          metadata.appendChild(el("summary", "Данные сообщения")); body.appendChild(metadata);
          metadata.appendChild(el("p", "Роль: " + role + " · " +
            (entry && entry.Presentation && entry.Presentation !== "Unknown" ? note(entry) : "полнота исходных источников не сохранена")));
          metadata.appendChild(el("p", "Размер сообщения в JSON: " + message.raw.length.toLocaleString("ru-RU") + " символов."));
          if (Number.isSafeInteger(tokens)) metadata.appendChild(el("p", "Базовая оценка сообщения: ≈ " +
            tokens.toLocaleString("ru-RU") + " токенов. Учтено один раз в составе запроса."));
          if (entry && entry.ToolCallId) metadata.appendChild(el("p", "Операция: " +
            (alias(c, "operations", entry.ToolCallId) ? alias(c, "operations", entry.ToolCallId) + " · " : "") + entry.ToolCallId));
          if (Number.isSafeInteger(entry && entry.EstimatedWireMessageTokens) && entry.EstimatedWireMessageTokens >= 0)
            metadata.appendChild(el("p", "Оценка всего сериализованного read-сообщения: ≈ " +
              entry.EstimatedWireMessageTokens + " токенов (не измеренное usage)."));
          metadata.appendChild(button("Копировать сообщение JSON", function () { copyTextResult(message.raw); }));
          parts.filter(function (part) { return part.key !== "role" && part.key !== "content"; }).forEach(function (part) {
            lazy(c, metadata, part.key, function () { return part.raw; }, true);
          });
          lazy(c, metadata, "Исходное сообщение JSON", function () { return message.raw; }, true);
          if (entry && entry.OriginalPayload && c.selected && c.mode === "actual") {
            var originalIndex = entries.indexOf(entry), version = c.version;
            lazy(c, metadata, "Локальный оригинал · не обязательно включён целиком", function () {
              return payload(c, c.selected, originalIndex, version);
            }, false);
          }
        });
        if (saved && saved.expanded.indexOf(index) >= 0) detail.open = true;
    }
    var overview = el("div", undefined, "model-context-overview"), heading = el("div");
    heading.appendChild(el("h3", "Состав запроса"));
    var orderNote = el("p", list.length + " сообщений · исходный порядок", "model-context-note");
    heading.appendChild(orderNote);
    overview.appendChild(heading);
    var collapse = button("Свернуть", function () {
      listRoot.querySelectorAll("details[open]").forEach(function (detail) { detail.open = false; });
    });
    collapse.title = "Свернуть все сообщения"; collapse.classList.add("model-context-collapse");
    overview.appendChild(collapse); listRoot.appendChild(overview);
    var checkedComposition = requestComposition(c, listRoot, composition, list.length, function (index) {
      var selected;
      if (index == null) selected = settings;
      else {
        while (count <= index && count < list.length) page();
        selected = rows.find(function (row) { return Number(row.dataset.messageIndex) === index; });
      }
      if (!selected) return;
      selected.open = true; selected.children[0].focus();
      selected.scrollIntoView({ block: "start" });
    }, saved, renderMessage, grouped);
    grouped = grouped && !!checkedComposition;
    if (checkedComposition) {
      checkedComposition.Items.forEach(function (item) { if (item.MessageIndex != null) costs.set(item.MessageIndex, item.EstimatedTokens); });
      orderNote.textContent = list.length + " сообщений · " + (grouped ? "раскройте раздел и сообщение" : "исходный порядок");
      var layout = el("select", undefined, "model-context-message-layout");
      layout.setAttribute("aria-label", "Группировка контекста");
      [["groups", "По разделам"], ["wire", "Порядок отправки"]].forEach(function (item) {
        var option = el("option", item[1]); option.value = item[0]; layout.appendChild(option);
      });
      layout.value = grouped ? "groups" : "wire";
      layout.addEventListener("change", function () { c.messageLayout = layout.value; render(c); });
      overview.insertBefore(layout, collapse);
    }
    var omitted = [];
    (entries || []).filter(Boolean).forEach(function (entry, entryIndex) {
      if (entry.Presentation === "Excluded") omitted.push({ entry: entry, index: entryIndex });
      else (entry.Parts || []).filter(Boolean).forEach(function (part, partIndex) {
        if (part.Presentation === "Excluded") omitted.push({ entry: entry, index: entryIndex, part: part, partIndex: partIndex });
      });
    });
    if (omitted.length) {
      var omissions = sourceDisclosure(listRoot, "Сохранённые пропуски", "записей: " + omitted.length, "omissions", saved), omissionCount = 0;
      omissions.classList.add("model-context-omissions");
      var moreOmissions = button("Показать ещё пропуски", addOmissions);
      function addOmissions() {
        moreOmissions.remove();
        omitted.slice(omissionCount, omissionCount + 50).forEach(function (item) {
          var entry = item.entry, key = "omission/" + item.index + (item.part ? "/" + item.partIndex : "");
          if (item.part) {
            var partRow = partSource(omissions, item.part, key, saved);
            var linked = byIndex.get(entry.MessageIndex);
            partRow.appendChild(el("p", linked && linked.length === 1 && linked[0] === entry ?
              "В составе сообщения " + (entry.MessageIndex + 1) : "Привязка к сообщению не установлена.", "model-context-note"));
            return;
          }
          var reasons = { Explicit: "Исключено при сборке", SupersededEvidence: "Есть новая версия", UnknownEvidence: "Источник не определён",
            UnavailableEvidence: "Источник недоступен", DuplicateBody: "Повтор тела", Budget: "Бюджет запроса", SelectionLimit: "Лимит выбора",
            OperationBudget: "Бюджет операций", OperationSelectionLimit: "Лимит операций", OperationReceipt: "Сводка операции" };
          var row = sourceDisclosure(omissions, roles[entry.Role] || entry.Role || "Источник", reasons[entry.ExclusionKind] || "Не включено", key, saved);
          row.appendChild(el("p", entry.Reason || "Причина исключения не сохранена.", "model-context-note"));
          sourceLinks(c, row, entry, key, saved);
          if (entry.OriginalPayload && c.mode === "actual") {
            var version = c.version;
            lazy(c, row, "Локальный оригинал источника", function () { return payload(c, c.selected, item.index, version); }, false);
          }
        });
        omissionCount += 50; if (omissionCount < omitted.length) omissions.appendChild(moreOmissions);
      }
      omissions.addEventListener("toggle", function () {
        if (!omissions.open || omissionCount !== 0 || !live(c) || !omissions.isConnected) return;
        addOmissions();
        while (saved && omissionCount < saved.omissionCount && omissionCount < omitted.length) addOmissions();
      });
    }
    var unattributed = list.filter(function (_, index) {
      var at = byIndex.get(index); return !at || at.length !== 1 || at[0].Presentation === "Excluded";
    }).length;
    if (unattributed) listRoot.appendChild(el("p", "Нет однозначных сведений об источниках: " + unattributed + " из " + list.length + " сообщений.", "model-context-note"));
    if (!grouped) {
      var restoreCount = saved ? saved.expanded.reduce(function (end, index) { return Math.max(end, index + 1); }, saved.count) : 0;
      page(); while (count < restoreCount && count < list.length) page();
    }
    var parameters = fields.filter(function (part) { return part.key !== "messages"; });
    if (parameters.length) {
      var settings = el("details", undefined, "model-context-meta");
      settings.appendChild(el("summary", "Параметры запроса · " + parameters.length)); root.appendChild(settings);
      parameters.forEach(function (part) { lazy(c, settings, "Параметр запроса: " + part.key, function () { return part.raw; }, true); });
    }
    if (saved && saved.grouped === grouped) {
      var viewVersion = c.viewVersion;
      setTimeout(function () { if (live(c) && c.viewVersion === viewVersion) root.scrollTop = saved.scroll; }, 0);
    }
  }
  function modelDecision(text) {
    var value = decoded(text);
    if (value && Object.keys(value).length === 1 && value.response && typeof value.response === "object") value = value.response;
    return value && typeof value.message === "string" && typeof value.action === "string" ? value : null;
  }
  function modelMessage(c, body, text) {
    var decision = modelDecision(text), narration = decision ? decision.message : text;
    if (narration.trim()) {
      var view = el("div"); body.appendChild(view); mount(c, view, narration, false);
    }
    if (decision) {
      var labels = { execute: "Запрос вызова инструментов", reflection: "Промежуточное решение", finish: "Завершение по ответу модели" };
      var info = el("details", undefined, "model-context-decision");
      info.appendChild(el("summary", labels[decision.action] || "Действие модели: " + decision.action));
      if (typeof decision.reason === "string" && decision.reason.trim()) info.appendChild(el("p", decision.reason));
      if (typeof decision.decision === "string" && decision.decision.trim()) info.appendChild(el("p", decision.decision));
      if (Array.isArray(decision.tool_calls) && decision.tool_calls.length) {
        var calls = el("ul");
        decision.tool_calls.forEach(function (call) {
          if (call && typeof call.name === "string") calls.appendChild(el("li", call.name));
        });
        info.appendChild(calls);
      }
      if (Array.isArray(decision.results) && decision.results.length) {
        var outcomes = el("ul"), assessments = { satisfied: "выполнено", partially_satisfied: "выполнено частично",
          unverified: "не проверено", not_satisfied: "не выполнено" };
        decision.results.forEach(function (result) {
          if (!result || typeof result.summary !== "string") return;
          var row = el("li", result.summary);
          if (typeof result.assessment === "string") row.appendChild(el("small",
            "Оценка модели: " + (assessments[result.assessment] || result.assessment), "model-context-result-assessment"));
          outcomes.appendChild(row);
        });
        info.appendChild(outcomes);
      }
      if (!narration.trim()) info.open = true;
      body.appendChild(info);
    } else if (!narration.trim()) body.appendChild(el("p", "Текстовое сообщение отсутствует.", "model-context-note"));
    return !!decision;
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
    if (modelMessage(c, body, messageText)) lazy(c, body, "Сообщение модели · исходный JSON", function () { return messageText; }, true);
    lazy(c, body, "Полный ответ · исходный JSON", function () { return text; }, true);
  }

  function referenceText(ref) { return (ref.uri || "?") + (ref.revision ? " @ " + ref.revision : ""); }
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
    if (!live(c)) return;
    syncTabs(c);
    clearView(c);
    var root = $("modelContextBody"), version = c.version;
    if (c.mode === "preview") {
      if (!c.preview) {
        notice(c, state.chatReadOnly ? "Чат доступен только для чтения" : c.previewError ? "Предпросмотр недоступен" : "Подготовка предпросмотра…",
          state.chatReadOnly ? "Для нового запроса создайте новый чат." : c.previewError || "Запрос ещё не отправляется.", c.previewError ? "error" : "loading");
        return;
      }
      if (c.tab === "response") { root.textContent = "Это предпросмотр — ответа ещё нет."; return; }
      if (c.tab === "resources") { root.textContent = "Сохранённые ревизии появятся после фактического запроса."; return; }
      if (c.tab === "json") mount(c, root, c.preview.text, true);
      else messages(c, c.preview.text, c.previewEntries);
      return;
    }
    if (!c.detail || !c.selected) {
      notice(c, c.selectionError ? "Не удалось открыть запрос" : c.loading ? "Загрузка запроса…" : c.scope ? "Запрос " + (c.scope.stepId ? "шага" : "запуска") + " не найден" : "Нет сохранённых запросов",
        c.selectionError || c.emptyMessage, c.selectionError ? "error" : "empty");
      return;
    }
    if (c.tab === "resources") { resources(c, root); return; }
    var request = c.detail.events.find(function (e) { return e.eventId === c.selected; });
    if (c.tab === "response") {
      var events = c.detail.events.filter(function (e) { return e.type !== "llm.request"; });
      if (!events.length) { notice(c, "Ответ ещё не сохранён", "Проверьте ход работы или обновите историю запросов."); return; }
      events.forEach(function (event) {
        var streaming = request.trace.Streaming === true || c.detail.events.some(function (e) { return e.type === "assistant.chunk"; });
        var title = event.type === "llm.response" ? (streaming ? "Собранный потоковый ответ" : request.trace.Streaming === false ? "Ответ провайдера" : "Сохранённый ответ · формат записи не указан") :
          event.type === "assistant.chunk" ? "Исходные SSE data-фрагменты" : event.type;
        var body = el("section", undefined, "model-context-response"); root.appendChild(body);
        var head = el("div", undefined, "model-context-response-head"), heading = el("div");
        heading.appendChild(el("h3", title));
        heading.appendChild(el("small", dateText(event.createdUtc)));
        head.appendChild(heading); body.appendChild(head);
        if (event.trace.Error) body.appendChild(el("p", event.trace.Error));
        if (event.hasPayload) {
          var content = lazy(c, body, "Содержимое", function () { return payload(c, event.eventId, undefined, version); }, true, event.type === "llm.response" ? responseBody : null);
          if (event.type === "llm.response") { content.parentElement.classList.add("model-context-response-content"); content.parentElement.open = true; }
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
      else messages(c, value.text, request.trace.ContextMessages, request.trace.RequestComposition);
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
    if (!same) { c.version++; c.cache = {}; c.pending = {}; c.inventory = {}; c.inventoryBusy = {}; c.selected = eventId; c.detail = null; c.focusResource = null; }
    if (!same || forceRender) {
      c.loading = true; c.emptyMessage = ""; c.selectionError = "";
      notice(c, "Загрузка запроса…", "Чтение сохранённого обмена.", "loading");
      previewAvailability(c);
    }
    var version = c.version;
    status(c, "Чтение сохранённого обмена…");
    try {
      var detail = await call(c, "getModelContext", { requestEventId: eventId });
      if (!valid(c, version)) return;
      var request = (detail.events || []).find(function (e) { return e.type === "llm.request" && e.eventId === eventId; });
      if (!matches(c, detail) || !request || !request.trace || c.scope &&
          (request.trace.RunId !== c.scope.runId || c.scope.stepId && request.trace.StepId !== c.scope.stepId))
        throw new Error("Сохранённый запрос не соответствует выбранному чату и шагу.");
      var changed = !c.detail || JSON.stringify(c.detail.events.map(function (e) { return e.eventId; })) !== JSON.stringify(detail.events.map(function (e) { return e.eventId; }));
      var resourcesChanged = !c.detail || JSON.stringify(c.detail.resources) !== JSON.stringify(detail.resources);
      c.detail = detail;
      var response = detail.events.find(function (e) { return e.type === "llm.response" || e.type === "llm.failure"; });
      $("modelContextAttempt").title = request.trace.Purpose || "Сохранённый запрос";
      status(c, (request.trace.Model || "Модель не указана") + " · " + dateText(request.createdUtc) + "\n" +
        (response ? response.type === "llm.response" ? "Ответ сохранён" : "Ошибка запроса" : "Ответ пока не сохранён") +
        (c.follow ? " · обновляется автоматически" : c.scope ? c.scope.stepId ? " · выбранный шаг" : " · выбранный запуск" : " · выбранный запрос"));
      c.loading = false; previewAvailability(c);
      if (!same || forceRender || changed && c.tab === "response" || resourcesChanged && c.tab === "resources") await render(c);
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
      var response = await call(c, "getModelContext", older ? { beforeSequence: c.before } : { knownRevision: c.revision });
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
    var name = $("modelContextAliasSearch").value.trim();
    if (!name) return;
    c.version++; var version = c.version;
    c.pending = {}; c.follow = false;
    status(c, "Поиск " + name + "…");
    notice(c, "Поиск ссылки " + name, "В сохранённой истории этого чата.", "loading");
    try {
      var detail = await call(c, "getModelContext", { alias: name });
      if (!valid(c, version)) return;
      var request = (detail.events || []).find(function (event) { return event.type === "llm.request"; });
      if (!matches(c, detail) || !request || detail.selectedAlias !== name) throw new Error("Короткая ссылка не найдена в этом чате.");
      c.scope = null;
      c.cache = {}; c.loading = false; c.selectionError = "";
      c.selected = request.eventId; c.detail = detail; c.inventory = {}; c.inventoryBusy = {};
      c.focusResource = detail.selectedResourceAlias || null;
      c.tab = c.focusResource ? "resources" : "messages";
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
  function draft() { var chat = activeChatSummary(); return JSON.stringify([composerText(), promptContextResourceDraftIds(), chat && (chat.revision || chat.Revision)]); }
  function previewAvailability(c) {
    $("modelContextDialog").classList.toggle("is-workspace", !!c.workspaceId);
    $("modelContextDialog").classList.toggle("is-preview", c.mode === "preview");
    $("modelContextPreviewOption").disabled = !!c.workspaceId || !!state.chatReadOnly;
    $("modelContextPreviewOption").hidden = !!c.workspaceId;
    $("modelContextMode").disabled = !!c.workspaceId;
    $("modelContextMode").hidden = !!c.workspaceId;
    $("modelContextAttempt").hidden = c.mode === "preview";
    var unavailable = !!state.chatReadOnly && c.mode === "preview";
    $("modelContextRefresh").disabled = unavailable;
    var request = c.detail && (c.detail.events || []).find(function (event) { return event.eventId === c.selected; });
    $("modelContextDownload").disabled = unavailable || (c.mode === "preview" ? !c.preview : c.loading || !request || !request.hasPayload);
  }
  async function preview(c) {
    if (c.workspaceId) return;
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
        resourceDraftIds: promptContextResourceDraftIds(), includeRaw: true });
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
    if (!live(c)) { if (current === c) close(); return; }
    if (c.mode === "actual" && c.follow) list(c, false);
    if (c.mode === "preview" && c.preview && c.previewDraft !== draft()) status(c, c.previewStatus + " · Снимок устарел — обновите.");
    c.timer = setTimeout(function () { tick(c); }, 2000);
  }
  function open(options) {
    var selected = source();
    if (!selected.chatId || state.bridgeUnavailable) return;
    if (selected.owner === "office") {
      if (options && (options.owner && options.owner !== "office" || options.workspaceId ||
          options.chatId && options.chatId !== selected.chatId)) return;
    } else if (!selected.workspaceId || !options || options.owner !== selected.owner ||
        options.workspaceId !== selected.workspaceId || options.sessionId !== selected.chatId) return;
    var scope = options && options.runId ? { runId: options.runId, stepId: options.stepId || "" } : null;
    var trigger = document.activeElement;
    if (current) close(false);
    if (typeof closePromptContextInspector === "function") closePromptContextInspector();
    current = { owner: selected.owner, chatId: selected.chatId, workspaceId: selected.workspaceId,
      abort: new AbortController(), requests: new Set(), version: 0,
      mounts: [], cache: {}, pending: {}, inventory: {}, inventoryBusy: {}, mode: "actual", tab: "messages", follow: !scope && !(options && options.requestEventId), attempts: [],
      scope: scope, onClose: options && options.onClose };
    $("modelContextDialog").classList.remove("hidden");
    window.RNAssistantChatInspector.showRequest(trigger);
    $("openModelContextFromTokens").setAttribute("aria-expanded", "true");
    $("modelContextMode").value = "actual"; syncTabs(current); attemptPicker(current);
    notice(current, "Загрузка запросов…", "Чтение истории этого чата.", "loading");
    $("modelContextHistory").open = false;
    previewAvailability(current);
    $("modelContextActualControls").classList.remove("hidden"); $("closeModelContextButton").focus();
    $("closeModelContextButton").title = current.onClose ? "Вернуться к истории" : "Вернуться к боковой панели";
    $("closeModelContextButton").setAttribute("aria-label", $("closeModelContextButton").title);
    if (options && options.requestEventId) {
      var opened = current;
      select(opened, options.requestEventId, true).then(function () {
        if (!live(opened) || !opened.detail) return;
        opened.attempts = opened.detail.events.filter(function (event) { return event.type === "llm.request"; });
        attemptPicker(opened).value = opened.selected;
      });
    } else if (scope) list(current, false);
    tick(current);
  }
  function bind() {
    $("openModelContextFromTokens").addEventListener("click", open);
    $("closeModelContextButton").addEventListener("click", close);
    $("modelContextLayout").addEventListener("change", function () {
      window.RNAssistantChatInspector.setRequestLayout(this.value);
    });
    $("modelContextMode").addEventListener("change", function () {
      if (!current || current.workspaceId) { this.value = "actual"; return; }
      var c = current; c.mode = this.value; c.version++; c.revision = null; c.detail = null; c.cache = {}; c.pending = {}; c.inventory = {}; c.inventoryBusy = {};
      c.loading = false; c.emptyMessage = ""; c.selectionError = ""; clearView(c);
      if (c.mode === "preview" && (c.tab === "response" || c.tab === "resources")) c.tab = "messages";
      syncTabs(c);
      $("modelContextActualControls").classList.toggle("hidden", c.mode !== "actual");
      previewAvailability(c);
      if (c.mode === "preview") preview(c); else {
        notice(c, "Загрузка запросов…", "Чтение сохранённой истории.", "loading"); list(c, false);
      }
    });
    Object.keys(tabs).forEach(function (key) {
      var control = $(tabs[key]);
      control.addEventListener("click", function () { current.tab = key; render(current); });
      control.addEventListener("keydown", function (event) {
        if (["ArrowLeft", "ArrowRight", "Home", "End"].indexOf(event.key) < 0) return;
        event.preventDefault();
        var available = Object.keys(tabs).filter(function (tab) { return !$(tabs[tab]).disabled; });
        var index = available.indexOf(key), next = event.key === "Home" ? 0 : event.key === "End" ? available.length - 1 :
          (index + (event.key === "ArrowRight" ? 1 : -1) + available.length) % available.length;
        current.tab = available[next]; $(tabs[current.tab]).focus(); render(current);
      });
    });
    $("modelContextAttempt").addEventListener("change", function () { current.follow = false; select(current, this.value); });
    $("modelContextAliasFind").addEventListener("click", function () { findAlias(current); });
    $("modelContextAliasSearch").addEventListener("keydown", function (event) {
      if (event.key === "Enter") { event.preventDefault(); findAlias(current); }
    });
    $("modelContextLatest").addEventListener("click", function () { current.follow = true; current.revision = null; list(current, false); });
    $("modelContextOlder").addEventListener("click", function () { list(current, true); });
    $("modelContextRefresh").addEventListener("click", async function () {
      if (current.mode === "preview") { await preview(current); return; }
      var c = current; c.version++; c.pending = {}; c.inventory = {}; c.inventoryBusy = {}; c.revision = null;
      await list(c, false, true);
    });
    $("modelContextDownload").addEventListener("click", function () {
      var c = current;
      if (c.mode === "preview") {
        if (c.preview && !state.chatReadOnly) save(c.preview, "context-preview.json");
      }
      else if (c.selected) downloadEvent(c, c.selected);
    });
    $("modelContextExportChat").addEventListener("click", function () {
      var c = current;
      if (!c || !live(c) || typeof window.openTrajectoryExport !== "function") return;
      var target = c.owner === "office" ? { owner: "office", chatId: c.chatId } :
        { owner: c.owner, workspaceId: c.workspaceId, sessionId: c.chatId };
      if (window.openTrajectoryExport(target)) {
        close(false);
        var exportSummary = document.querySelector && document.querySelector("#trajectoryExportOptions > summary");
        if (exportSummary) exportSummary.focus();
      } else status(c, "Экспорт диагностики недоступен для этого чата.");
    });
    $("modelContextDialog").addEventListener("keydown", function (event) {
      if (event.key === "Escape") {
        event.preventDefault(); event.stopPropagation();
        if ($("modelContextLayout").value === "focus") window.RNAssistantChatInspector.restoreRequestLayout(); else close();
      }
      if (event.key === "Tab" && $("modelContextLayout").value === "focus") {
        var focusable = Array.from(this.querySelectorAll("button, select, summary, input, textarea, a[href], [tabindex]")).filter(function (node) {
          if (node.disabled || node.tabIndex < 0 || !node.getClientRects().length) return false;
          // Descendants of closed details can still have layout boxes, but cannot receive focus.
          for (var parent = node.parentElement; parent; parent = parent.parentElement) {
            if (parent.tagName === "DETAILS" && !parent.open && !parent.firstElementChild.contains(node)) return false;
          }
          return true;
        });
        var first = focusable[0], last = focusable[focusable.length - 1];
        if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
        else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
      }
    });
  }
  window.bindModelContextActions = bind;
  window.RNAssistantModelContext = { members: members, open: open, close: close,
    sync: function () {
      if (!current) return;
      if (!live(current) || state.bridgeUnavailable) { close(); return; }
      previewAvailability(current);
      if (state.chatReadOnly && current.mode === "preview") preview(current);
    } };
}());
