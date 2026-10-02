(function () {
  "use strict";
  var current = null, limit = 50 * 1024 * 1024;
  var labels = { Full: "Полностью", Fragment: "Фрагмент", Summary: "Сводка / описание ресурса",
    Excluded: "Не включено", Unknown: "Нет сведений" };
  function el(tag, text, cls) {
    var node = document.createElement(tag);
    if (text !== undefined) node.textContent = text;
    if (cls) node.className = cls;
    return node;
  }
  function button(text, action) {
    var node = el("button", text, "secondary compact"); node.type = "button";
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
  function live(c) { return current === c && state.activeChatId === c.chatId && !c.abort.signal.aborted; }
  function valid(c, version) { return live(c) && c.version === version; }
  function status(c, text) { if (live(c)) $("modelContextStatus").textContent = text; }
  function clearView(c) {
    c.mounts.forEach(function (node) { window.RNAssistantViewerRegistry.unmount(node); }); c.mounts = [];
    $("modelContextBody").replaceChildren();
  }
  function close() {
    var c = current; current = null;
    if (c) {
      clearTimeout(c.timer); c.abort.abort();
      c.requests.forEach(function (id) { cancelBridgeRequest(id).catch(function () {}); });
      clearView(c); c.cache = {};
    }
    $("modelContextDialog").classList.add("hidden");
    $("openModelContextButton").setAttribute("aria-expanded", "false");
    if (c && c.returnFocus && c.returnFocus.isConnected) c.returnFocus.focus();
  }
  async function call(c, type, payload) {
    var pending = send(type, Object.assign({ chatId: c.chatId }, payload || {}));
    if (pending.requestId) c.requests.add(pending.requestId);
    try { return await pending; } finally { c.requests.delete(pending.requestId); }
  }
  async function download(c, response, owner, version) {
    var data = response && (response.data || response.rawData);
    try {
      if (!valid(c, version)) throw new Error("Просмотр закрыт или изменён.");
      if (!data || !data.payload || response.chatId !== c.chatId) throw new Error("RESOURCE_DOWNLOAD_INVALID");
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
      var value = await download(c, response, "model-context", version);
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
  function messages(c, text, entries) {
    var root = $("modelContextBody"), fields;
    try { fields = members(text); } catch (error) { root.appendChild(el("p", error.message)); return mount(c, root, text, true); }
    var raw = field(fields, "messages");
    if (!raw) { root.appendChild(el("p", "В payload нет массива messages. Откройте JSON запроса.")); return; }
    var listRoot = el("section"); root.appendChild(listRoot);
    var list = members(raw), count = 0, previousGroup = null;
    var more = button("Показать ещё сообщения", page);
    function page() {
      more.remove();
      list.slice(count, count + 50).forEach(function (message, offset) {
        var index = count + offset, parts = members(message.raw), role = decoded(field(parts, "role")) || "message";
        var group = role === "system" || role === "developer" ? "Инструкции" : role === "user" ? "Пользователь / контекст" : "Шаг модели / инструменты";
        if (group !== previousGroup) { listRoot.appendChild(el("h3", group)); previousGroup = group; }
        var entry = (entries || []).find(function (item) { return item.MessageIndex === index; });
        var detail = el("details", undefined, "model-context-message"), summary = el("summary");
        summary.appendChild(el("span", (index + 1) + " · " + role + " · " + message.raw.length + " симв."));
        var badge = el("span", note(entry), "model-context-badge"); badge.dataset.kind = entry && entry.Presentation || "Unknown";
        summary.appendChild(badge); detail.appendChild(summary); listRoot.appendChild(detail);
        var body = el("div", undefined, "model-context-message-body"); detail.appendChild(body);
        var loaded = false;
        detail.addEventListener("toggle", function () {
          if (!detail.open || loaded) return; loaded = true;
          if (entry && entry.Reason) body.appendChild(el("p", entry.Reason, "model-context-note"));
          (entry && entry.Parts || []).forEach(function (part) { body.appendChild(el("p", (part.Kind || "Часть") + ": " + note(part) + " · " + (part.Reason || ""))); });
          body.appendChild(button("Копировать сообщение JSON", function () { copyTextResult(message.raw); }));
          var content = field(parts, "content"), contentValue = decoded(content);
          if (typeof contentValue === "string") { var contentNode = el("div"); body.appendChild(contentNode); mount(c, contentNode, contentValue, false); }
          else if (content) lazy(c, body, "Части содержимого · JSON", function () { return content; }, true);
          parts.filter(function (part) { return part.key !== "role" && part.key !== "content"; }).forEach(function (part) {
            lazy(c, body, part.key, function () { return part.raw; }, true);
          });
          lazy(c, body, "Исходное сообщение JSON", function () { return message.raw; }, true);
          if (entry && entry.OriginalPayload && c.selected && c.mode === "actual") {
            var originalIndex = entries.indexOf(entry), version = c.version;
            lazy(c, body, "Локальный оригинал · не обязательно включён целиком", function () {
              return payload(c, c.selected, originalIndex, version);
            }, false);
          }
        });
      });
      count += 50; if (count < list.length) listRoot.appendChild(more);
    }
    listRoot.appendChild(el("p", list.length + " сообщений · исходный порядок", "model-context-note")); page();
    fields.filter(function (part) { return part.key !== "messages"; }).forEach(function (part) {
      lazy(c, root, "Параметр запроса: " + part.key, function () { return part.raw; }, true);
    });
    var excluded = (entries || []).filter(function (entry) { return entry.Presentation === "Excluded"; });
    if (excluded.length) {
      var local = el("details"); local.appendChild(el("summary", "Не включено отдельными сообщениями · " + excluded.length)); root.appendChild(local);
      excluded.forEach(function (entry) {
        var row = el("div", undefined, "model-context-message-body"); local.appendChild(row);
        row.appendChild(el("p", (entry.Role || "Источник") + " · " + (entry.Reason || "Не включено")));
        if (entry.OriginalPayload && c.mode === "actual") {
          var version = c.version;
          lazy(c, row, "Локальный оригинал", function () { return payload(c, c.selected, entries.indexOf(entry), version); }, false);
        }
      });
    }
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
    body.appendChild(el("h3", "Сообщение модели"));
    var view = el("div"); body.appendChild(view); mount(c, view, messageText, false);
    lazy(c, body, "Полный ответ · исходный JSON", function () { return text; }, true);
  }

  async function render(c) {
    if (!live(c)) return;
    clearView(c);
    var root = $("modelContextBody"), version = c.version;
    if (c.mode === "preview") {
      if (!c.preview) return;
      if (c.tab === "response") { root.textContent = "Это предпросмотр — ответа ещё нет."; return; }
      if (c.tab === "json") mount(c, root, c.preview.text, true);
      else messages(c, c.preview.text, c.previewEntries);
      return;
    }
    if (!c.detail || !c.selected) { root.textContent = "Сохранённых запросов пока нет."; return; }
    var request = c.detail.events.find(function (e) { return e.eventId === c.selected; });
    if (c.tab === "response") {
      var events = c.detail.events.filter(function (e) { return e.type !== "llm.request"; });
      if (!events.length) root.textContent = "Ответ ещё не сохранён. Подготовка запроса не доказывает доставку модели.";
      events.forEach(function (event) {
        var streaming = request.trace.Streaming === true || c.detail.events.some(function (e) { return e.type === "assistant.chunk"; });
        var title = event.type === "llm.response" ? (streaming ? "Собранный потоковый ответ" : request.trace.Streaming === false ? "Ответ провайдера" : "Сохранённый ответ · формат записи не указан") :
          event.type === "assistant.chunk" ? "Исходные SSE data-фрагменты" : event.type;
        var body = el("section"); root.appendChild(body);
        body.appendChild(el("h3", title + " · " + new Date(event.createdUtc).toLocaleTimeString()));
        if (event.trace.Error) body.appendChild(el("p", event.trace.Error));
        if (event.hasPayload) {
          lazy(c, body, "Содержимое", function () { return payload(c, event.eventId, undefined, version); }, true, event.type === "llm.response" ? responseBody : null);
          body.appendChild(button("Скачать полностью", function () { downloadEvent(c, event.eventId); }));
        } else body.appendChild(el("p", "Тело не сохранено."));
      }); return;
    }
    if (!request.hasPayload) { root.textContent = "Точное тело запроса не сохранено."; return; }
    root.textContent = "Загрузка запроса…";
    try {
      var value = await payload(c, request.eventId, undefined, version);
      if (!valid(c, version)) return;
      root.replaceChildren();
      if (c.tab === "json") mount(c, root, value.text, true);
      else messages(c, value.text, request.trace.ContextMessages);
    } catch (error) { if (valid(c, version)) root.textContent = error.message || String(error); }
  }
  async function downloadEvent(c, eventId) {
    var version = c.version;
    try { var value = await payload(c, eventId, undefined, version); if (valid(c, version)) save(value, eventId + ".json"); }
    catch (error) { status(c, error.message); }
  }
  async function select(c, eventId) {
    var same = c.selected === eventId && c.detail;
    if (!same) { c.version++; c.cache = {}; c.pending = {}; c.selected = eventId; c.detail = null; }
    var version = c.version;
    status(c, "Чтение сохранённого обмена…");
    try {
      var detail = await call(c, "getModelContext", { requestEventId: eventId });
      if (!valid(c, version)) return;
      var changed = !c.detail || JSON.stringify(c.detail.events.map(function (e) { return e.eventId; })) !== JSON.stringify(detail.events.map(function (e) { return e.eventId; }));
      c.detail = detail;
      var request = detail.events.find(function (e) { return e.eventId === eventId; });
      var response = detail.events.find(function (e) { return e.type === "llm.response" || e.type === "llm.failure"; });
      status(c, "Подготовлен " + new Date(request.createdUtc).toLocaleString() + " · " + (request.trace.Model || "") +
        " · " + (response ? response.type === "llm.response" ? "ответ сохранён" : "ошибка" : "ответ пока отсутствует") +
        (request.trace.Streaming ? " · streaming" : "") + (c.follow ? " · слежение" : " · просмотр выбранного шага"));
      if (!same || changed && c.tab === "response") await render(c);
    } catch (error) { if (valid(c, version)) status(c, error.message); }
  }
  async function list(c, older) {
    if (!live(c) || c.busy) return;
    c.busy = true;
    var listVersion = c.version;
    try {
      var response = await call(c, "getModelContext", older ? { beforeSequence: c.before } : { knownRevision: c.revision });
      if (!valid(c, listVersion) || c.mode !== "actual" || response.unchanged) return;
      if (older) c.attempts = c.attempts.concat(response.events);
      else {
        c.revision = response.revision;
        var selected = c.attempts.find(function (e) { return e.eventId === c.selected; });
        c.attempts = response.events;
        if (!c.follow && selected && !c.attempts.some(function (e) { return e.eventId === selected.eventId; })) c.attempts.push(selected);
      }
      c.before = response.nextBeforeSequence;
      var picker = $("modelContextAttempt"); picker.replaceChildren();
      c.attempts.forEach(function (event) {
        var option = el("option", "#" + event.sequence + " · " + new Date(event.createdUtc).toLocaleTimeString() + " · " + (event.trace.Purpose || "model"));
        option.value = event.eventId; picker.appendChild(option);
      });
      $("modelContextOlder").disabled = !c.before;
      var chosen = c.follow ? c.attempts[0] && c.attempts[0].eventId : c.selected;
      if (chosen) { picker.value = chosen; if (!older || chosen !== c.selected) await select(c, chosen); }
      else { status(c, "Сохранённых запросов пока нет. Доступен предварительный контекст."); await render(c); }
    } catch (error) { status(c, error.message || String(error)); }
    finally { c.busy = false; }
  }
  function draft() { var chat = activeChatSummary(); return JSON.stringify([$("chatInput").value, promptContextResourceDraftIds(), chat && (chat.revision || chat.Revision)]); }
  async function preview(c) {
    c.version++; var version = c.version; c.preview = null; clearView(c);
    c.previewDraft = draft(); status(c, "Сборка предварительного контекста…");
    try {
      var response = await call(c, "inspectModelContextPreview", { text: $("chatInput").value,
        resourceDraftIds: promptContextResourceDraftIds(), includeRaw: true });
      var value = await download(c, response, "context-inspector", version);
      if (!valid(c, version)) return;
      c.preview = value; c.previewEntries = response.resourceContextReceipt && response.resourceContextReceipt.Messages;
      c.previewStatus = "Предпросмотр, ещё не отправлено · " + new Date(response.generatedUtc).toLocaleString() +
        " · вложения представлены метаданными; transport материализует их при отправке";
      status(c, c.previewStatus); await render(c);
    } catch (error) { if (valid(c, version)) status(c, error.message || String(error)); }
  }
  function tick(c) {
    if (!live(c)) { if (current === c) close(); return; }
    if (c.mode === "actual" && c.follow) list(c, false);
    if (c.mode === "preview" && c.preview && c.previewDraft !== draft()) status(c, c.previewStatus + " · Снимок устарел — обновите.");
    c.timer = setTimeout(function () { tick(c); }, 2000);
  }
  function open() {
    if (!state.activeChatId || state.bridgeUnavailable) return;
    if (current) close();
    if (typeof closePromptContextInspector === "function") closePromptContextInspector();
    current = { chatId: state.activeChatId, abort: new AbortController(), requests: new Set(), version: 0,
      mounts: [], cache: {}, pending: {}, mode: "actual", tab: "messages", follow: true, attempts: [], returnFocus: document.activeElement };
    $("modelContextDialog").classList.remove("hidden");
    $("openModelContextButton").setAttribute("aria-expanded", "true");
    $("modelContextMode").value = "actual"; $("modelContextTab").value = "messages";
    $("modelContextActualControls").classList.remove("hidden"); $("closeModelContextButton").focus();
    tick(current);
  }
  function bind() {
    $("openModelContextButton").addEventListener("click", open);
    $("openModelContextFromTokens").addEventListener("click", open);
    $("closeModelContextButton").addEventListener("click", close);
    $("modelContextMode").addEventListener("change", function () {
      var c = current; c.mode = this.value; c.version++; c.revision = null; c.detail = null; c.cache = {}; c.pending = {};
      $("modelContextActualControls").classList.toggle("hidden", c.mode !== "actual");
      if (c.mode === "preview") preview(c); else list(c, false);
    });
    $("modelContextTab").addEventListener("change", function () { var c = current; c.tab = this.value; render(c); });
    $("modelContextAttempt").addEventListener("change", function () { current.follow = false; select(current, this.value); });
    $("modelContextLatest").addEventListener("click", function () { current.follow = true; current.revision = null; list(current, false); });
    $("modelContextOlder").addEventListener("click", function () { list(current, true); });
    $("modelContextRefresh").addEventListener("click", function () {
      if (current.mode === "preview") preview(current); else { current.revision = null; list(current, false); }
    });
    $("modelContextDownload").addEventListener("click", function () {
      var c = current;
      if (c.mode === "preview" && c.preview) save(c.preview, "context-preview.json");
      else if (c.selected) downloadEvent(c, c.selected);
    });
    $("modelContextDialog").addEventListener("keydown", function (event) {
      if (event.key === "Escape") { event.preventDefault(); event.stopPropagation(); close(); }
      if (event.key === "Tab") {
        var focusable = Array.from(this.querySelectorAll("button, select, summary, input, a[href]")).filter(function (node) { return !node.disabled && node.getClientRects().length; });
        var first = focusable[0], last = focusable[focusable.length - 1];
        if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
        else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
      }
    });
  }
  window.bindModelContextActions = bind;
  window.RNAssistantModelContext = { members: members, open: open, close: close,
    sync: function () { if (current && (!live(current) || state.bridgeUnavailable)) close(); } };
}());
