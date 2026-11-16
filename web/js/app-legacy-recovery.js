(function () {
  "use strict";
  // Explicit archive/import presentation. Native owners validate sources and retain import receipts.
  var bound = false, active = false, generation = 0, pending = Object.create(null);
  var chats = null, plugins = null, transcript = null, chatOffset = 0, pluginOffset = 0;
  var connecting = new Set(), connected = new Map();
  var imports = { chat: new Map(), plugin: new Map() }, batch = null, lastBatch = null;
  var statusLabels = { readable: "Доступен", unreadable: "Ошибка чтения", tooLarge: "Превышен предел чтения",
    incomplete: "Неполный журнал", missingSource: "Папка исходников недоступна", sourceChanged: "Источник изменился", linkedPath: "Ссылка вместо обычного пути" };

  function node(id) { return $("legacyRecovery" + id); }
  function text(id, value) { var element = node(id); if (element) element.textContent = value || ""; }
  function visible() {
    return !document.hidden && window.RNAssistantDiagnostics && RNAssistantDiagnostics.isVisible() &&
      state.diagnosticsTab === "application" && $("diagnosticsAppView").value === "health";
  }
  function notice(value) { text("Notice", value); }
  function selected() {
    var id = node("ChatSelect").value;
    return chats && chats.items.find(function (item) { return item.sourceId === id; });
  }
  function capture(kind, item) {
    return kind === "chat" ? { sourceId: item.sourceId, expectedRevision: item.revision, chatId: item.chatId, title: item.title }
      : { pluginId: item.pluginId, expectedRegistrationRevision: item.registrationRevision };
  }
  function importKey(kind, value) {
    return JSON.stringify(kind === "chat" ? [value.sourceId, value.expectedRevision] : [value.pluginId, value.expectedRegistrationRevision]);
  }
  function importState(kind, item) { return item && imports[kind].get(importKey(kind, capture(kind, item))); }
  function canImport(kind, item) {
    if (!item || (kind === "chat" ? item.status !== "readable" || !item.chatId : !item.canImport || !item.registrationRevision)) return false;
    var entry = importState(kind, item);
    return !(entry && ["pending", "unknown", "imported"].includes(entry.status)) &&
      !pending[kind + "Import:" + importKey(kind, capture(kind, item))] &&
      !(kind === "plugin" && (connecting.has(item.pluginId) || connected.has(item.pluginId)));
  }
  function importLabel(entry) {
    if (!entry) return "";
    if (entry.status === "pending") return "Перенос…";
    if (entry.status === "imported") return "Перенос подтверждён";
    if (entry.status === "unknown") return "Результат неизвестен";
    if (entry.status === "failed") return "Перенос отклонён";
    return "Перенос не найден";
  }
  function importMessage(entry) {
    if (!entry) return "";
    return entry.message + (entry.refreshError ? " Перенос подтверждён; список не обновлён: " + entry.refreshError : "");
  }
  function controls() {
    if (!bound) return;
    var item = selected(), reading = !!pending.transcript;
    node("Refresh").disabled = !active || !!pending.chats || !!pending.plugins;
    node("Read").disabled = !active || !!pending.chats || reading || !item || item.status !== "readable";
    node("ChatSelect").disabled = !!pending.chats || reading || !chats || !chats.items.length;
    node("ChatFirst").disabled = !active || !!pending.chats || chatOffset === 0;
    node("ChatNext").disabled = !active || !!pending.chats || !chats || chats.nextOffset == null;
    node("TextFirst").disabled = !active || reading || !transcript;
    node("TextNext").disabled = !active || reading || !transcript || transcript.nextMessageIndex == null;
    node("Copy").disabled = !transcript || !transcript.text;
    node("PluginFirst").disabled = !active || !!pending.plugins || pluginOffset === 0;
    node("PluginNext").disabled = !active || !!pending.plugins || !plugins || plugins.nextOffset == null;
    var entry = importState("chat", item), key = item && importKey("chat", capture("chat", item));
    node("ImportChat").disabled = !active || !!batch || !!pending.chats || !canImport("chat", item);
    node("CheckChat").disabled = !active || !item || !item.chatId || !!pending["chatImport:" + key] ||
      !!(entry && (entry.status === "pending" || entry.status === "imported"));
    node("ImportPage").disabled = !active || !!batch || !!pending.chats || !chats || !chats.items.some(function (value) { return canImport("chat", value); });
    node("StopImport").disabled = !batch || batch.stop;
    text("ImportStatus", importMessage(entry));
  }
  function current(slot, request) { return active && visible() && pending[slot] === request && request.generation === generation; }
  async function read(slot, type, payload, accept) {
    if (!active || !visible() || pending[slot]) return;
    var request = { generation: generation, promise: send(type, payload) };
    pending[slot] = request; controls();
    try {
      var value = await request.promise;
      if (current(slot, request)) { await accept(value); return true; }
      return false;
    } catch (error) {
      if (current(slot, request)) notice("Не удалось прочитать прежние данные: " + (error.detail || error.message));
      return false;
    } finally {
      if (pending[slot] === request) delete pending[slot];
      controls();
    }
  }
  function clearTranscript() { transcript = null; text("Text", ""); text("TextStatus", ""); }
  function showChatInfo() {
    var item = selected();
    text("ChatInfo", item ? [item.host, item.documentTitle, item.documentPath,
      "Сообщений: " + item.messageCount, statusLabels[item.status] || item.status, item.error].filter(Boolean).join(" · ") : "");
    controls();
  }
  function renderChatOptions() {
    var select = node("ChatSelect"), old = select.value;
    select.replaceChildren();
    if (!chats) return;
    chats.items.forEach(function (item) {
      var option = document.createElement("option"), imported = importLabel(importState("chat", item)); option.value = item.sourceId;
      option.textContent = (item.title || item.chatId || "Не удалось прочитать чат") + " · " +
        (item.documentTitle || item.host || "") + (item.status === "readable" ? "" : " · " + (statusLabels[item.status] || item.status)) +
        (imported ? " · " + imported : "");
      select.appendChild(option);
    });
    select.value = chats.items.some(function (item) { return item.sourceId === old; }) ? old : chats.items.length ? chats.items[0].sourceId : "";
  }
  function loadChats(offset) {
    return read("chats", "listLegacyChats", { offset: offset, limit: 25 }, function (value) {
      chats = value; chatOffset = offset; clearTranscript();
      renderChatOptions();
      text("ChatStatus", "Найдено архивных чатов: " + value.total + ". Показано: " + value.items.length + ". " + value.rootPath);
      node("Chats").open = true; showChatInfo();
    });
  }
  function readTranscript(messageIndex, characterOffset) {
    var item = selected();
    if (!item || item.status !== "readable") return Promise.resolve();
    return read("transcript", "readLegacyTranscript", { sourceId: item.sourceId, expectedRevision: item.revision,
      messageIndex: messageIndex || 0, characterOffset: characterOffset || 0, maxCharacters: 32000 }, function (value) {
      if (!selected() || selected().sourceId !== value.sourceId || selected().revision !== value.revision) return;
      transcript = value; text("Text", value.text);
      node("Text").scrollTop = 0;
      text("TextStatus", "Архив · сообщений: " + value.totalMessages +
        (value.totalMessages ? ", с № " + (value.messageStartIndex + 1) : "") +
        (characterOffset ? " (продолжение)" : "") + (value.nextMessageIndex == null ? " · конец текста" : " · есть продолжение"));
      notice("Архив открыт для чтения. Его текст можно скопировать; старые запуски не возобновляются.");
    });
  }
  function renderPlugins() {
    var root = node("PluginList"); root.replaceChildren();
    if (!plugins) return;
    plugins.items.forEach(function (item) {
      var row = document.createElement("div"); row.className = "legacy-recovery-plugin";
      var title = document.createElement("strong"); title.textContent = item.name || item.pluginId || "Недоступная регистрация";
      var path = document.createElement("p"); path.textContent = item.sourceRoot || "Путь не прочитан";
      var detail = document.createElement("p"); detail.className = "muted";
      var entry = importState("plugin", item), key = importKey("plugin", capture("plugin", item));
      detail.textContent = [statusLabels[item.status] || item.status, item.error, connected.get(item.pluginId), importMessage(entry)].filter(Boolean).join(" · ");
      var actions = document.createElement("div"); actions.className = "legacy-recovery-controls";
      var restore = document.createElement("button"); restore.type = "button"; restore.className = "button compact";
      restore.textContent = entry && entry.status === "pending" ? "Перенос…" : entry && entry.status === "imported" ? "Регистрация восстановлена" : "Восстановить регистрацию";
      restore.disabled = !active || !canImport("plugin", item);
      restore.addEventListener("click", function () { importItem("plugin", item); });
      var check = document.createElement("button"); check.type = "button"; check.className = "button compact"; check.textContent = "Проверить перенос";
      check.disabled = !active || !item.registrationRevision || !!pending["pluginImport:" + key] ||
        !!(entry && (entry.status === "pending" || entry.status === "imported"));
      check.addEventListener("click", function () { checkImport("plugin", capture("plugin", item)); });
      var button = document.createElement("button"); button.type = "button"; button.className = "button compact";
      button.textContent = connecting.has(item.pluginId) ? "Подключение…" : connected.has(item.pluginId) ? "Подключено" : "Подключить исходники";
      button.disabled = !active || !item.canConnect || connecting.has(item.pluginId) || connected.has(item.pluginId) ||
        !!(entry && ["pending", "unknown", "imported"].includes(entry.status));
      button.addEventListener("click", function () { reconnect(item.pluginId); });
      actions.appendChild(restore); actions.appendChild(check); actions.appendChild(button);
      row.appendChild(title); row.appendChild(path); row.appendChild(detail); row.appendChild(actions); root.appendChild(row);
    });
  }
  function loadPlugins(offset) {
    return read("plugins", "listLegacyPlugins", { offset: offset, limit: 25 }, function (value) {
      plugins = value; pluginOffset = offset;
      text("PluginStatus", "Найдено прежних регистраций: " + value.total + ". Показано: " + value.items.length + ". " + value.rootPath);
      node("Plugins").open = true; renderPlugins();
    });
  }
  function renderImports() {
    if (!active || !visible()) return;
    renderChatOptions(); controls(); renderPlugins();
    if (lastBatch) text("BatchStatus", (batch ? batch.stop ? "Остановка после текущего переноса. " : "Перенос показанных чатов. " : "Перенос страницы завершён. ") +
      "Обработано: " + lastBatch.completed + "/" + lastBatch.total + "; подтверждено: " + lastBatch.confirmed +
      "; отказов: " + lastBatch.failed + "; неизвестно: " + lastBatch.unknown +
      (lastBatch.completed < lastBatch.total ? "; не начато: " + (lastBatch.total - lastBatch.completed) : "") + ".");
  }
  function exactReceipt(kind, value, expected) {
    if (kind === "plugin") return value && value.item && value.item.pluginId === expected.pluginId &&
      value.registrationRevision === expected.expectedRegistrationRevision && typeof value.alreadyImported === "boolean" &&
      Number.isSafeInteger(value.savedVersions) && value.savedVersions >= 0 && Number.isSafeInteger(value.releases) && value.releases >= 0;
    return value && ["imported", "alreadyImported"].includes(value.status) && value.sourceId === expected.sourceId &&
      value.sourceRevision === expected.expectedRevision && value.chatId === expected.chatId && typeof value.host === "string" && value.host &&
      (value.host !== "Workspace" || typeof value.workspaceId === "string" && value.workspaceId) &&
      typeof value.targetDeleted === "boolean" && Number.isSafeInteger(value.messageCount) && value.messageCount >= 0 &&
      Number.isSafeInteger(value.revision) && value.revision > 0;
  }
  async function refreshImported(kind, value) {
    if (kind === "plugin") {
      if (window.RNAssistantBrowser) RNAssistantBrowser.pluginImported(value.item.pluginId);
      await Promise.all([
        window.RNAssistantPlugins && RNAssistantPlugins.refresh({ afterPending: true }),
        window.RNAssistantProjectChats && RNAssistantProjectChats.refreshCatalog()
      ]);
      return;
    }
    var workspace = value.host === "Workspace";
    var target = workspace ? { source: "workspace", workspaceId: value.workspaceId, sessionId: value.chatId }
      : { source: "office", sessionId: value.chatId };
    var view = !value.targetDeleted && window.RNAssistantConversationPanel && RNAssistantConversationPanel.find(target);
    // The existing observer defers hidden reads and rejects disposed/stale bindings.
    var viewRefresh = view ? view.refresh() : Promise.resolve();
    var catalogRefresh = (async function () {
      if (workspace) {
        if (window.RNAssistantProjectChats) await RNAssistantProjectChats.refreshCatalog();
      } else {
        if (state.chatNavigationPromise) { try { await state.chatNavigationPromise; } catch (_) {} }
        if (typeof synchronizeChatState === "function") await synchronizeChatState(true);
      }
    }());
    await Promise.all([viewRefresh, catalogRefresh]);
  }
  async function acceptImport(kind, expected, value) {
    if (!exactReceipt(kind, value, expected)) throw new Error("Ответ переноса не соответствует выбранному источнику. Проверьте перенос по квитанции.");
    var entry = { status: "imported", receipt: value, message: kind === "chat"
      ? value.targetDeleted ? "Чат перенесён ранее, затем удалён. Повторная запись не выполняется."
        : "Чат перенесён в историю. Сообщений: " + value.messageCount + ". Старые запуски не возобновляются."
      : "Регистрация и сохранённые версии восстановлены. Версий: " + value.savedVersions + "; публикаций: " + value.releases + "." };
    imports[kind].set(importKey(kind, expected), entry); renderImports();
    try { await refreshImported(kind, value); }
    catch (error) { entry.refreshError = error.detail || error.message; }
    renderImports(); return entry;
  }
  function knownRefusal(kind, error) {
    var codes = kind === "chat" ? ["source_changed", "target_exists", "unavailable", "identity_invalid"]
      : ["invalid", "target_invalid", "target_exists", "source_changed", "unavailable", "target_changed", "dependency_unsupported", "payload_unavailable", "too_large"];
    return codes.some(function (code) { return error.code === "legacy_" + kind + "_import_" + code; });
  }
  async function importItem(kind, item) {
    if (!active || !visible() || !canImport(kind, item)) return;
    var expected = capture(kind, item), key = importKey(kind, expected);
    var entry = { status: "pending", message: "Перенос выполняется…" };
    imports[kind].set(key, entry); renderImports();
    try {
      var payload = kind === "chat" ? { sourceId: expected.sourceId, expectedRevision: expected.expectedRevision } : expected;
      var value = await send(kind === "chat" ? "importLegacyChat" : "importLegacyPlugin", payload);
      return await acceptImport(kind, expected, value);
    } catch (error) {
      entry.status = knownRefusal(kind, error) ? "failed" : "unknown";
      entry.message = (entry.status === "failed" ? "Перенос отклонён: " : "Результат переноса неизвестен: ") + (error.detail || error.message) +
        (entry.status === "unknown" ? " Используйте «Проверить перенос»; повторная запись не отправляется." : "");
      renderImports(); return entry;
    }
  }
  async function checkImport(kind, expected) {
    var key = importKey(kind, expected), previous = imports[kind].get(key);
    if (previous && ["pending", "imported"].includes(previous.status)) return;
    var payload = kind === "chat" ? { sourceId: expected.sourceId, expectedRevision: expected.expectedRevision, chatId: expected.chatId } : expected;
    var checking = read(kind + "Import:" + key, kind === "chat" ? "getLegacyChatImport" : "getLegacyPluginImport", payload, async function (value) {
      if (value) { await acceptImport(kind, expected, value); return; }
      imports[kind].set(key, { status: previous && previous.status === "unknown" ? "unknown" : "absent",
        message: "Подтверждение переноса не найдено. " + (previous && previous.status === "unknown"
          ? "Результат остаётся неизвестным; запись не повторялась. Можно проверить позже." : "Перенос не запускался этой проверкой.") });
      renderImports();
    });
    renderImports(); await checking; renderImports();
  }
  async function importPage() {
    if (!active || !visible() || batch || !chats) return;
    var items = chats.items.slice(0, 25).filter(function (item) { return canImport("chat", item); });
    if (!items.length) return;
    var work = { total: items.length, completed: 0, confirmed: 0, failed: 0, unknown: 0, stop: false };
    batch = lastBatch = work; renderImports();
    try {
      for (var index = 0; index < items.length; index++) {
        if (work.stop || !active || !visible()) break;
        var entry = await importItem("chat", items[index]);
        if (!entry) continue;
        work.completed++;
        if (entry.status === "imported") work.confirmed++;
        else if (entry.status === "failed") work.failed++;
        else { work.unknown++; work.stop = true; }
        renderImports();
      }
    } finally { if (batch === work) batch = null; renderImports(); }
  }
  async function reconnect(pluginId) {
    var item = plugins && plugins.items.find(function (value) { return value.pluginId === pluginId; }), entry = importState("plugin", item);
    if (!active || !visible() || !item || !item.canConnect || connecting.has(pluginId) || connected.has(pluginId) ||
      entry && ["pending", "unknown", "imported"].includes(entry.status)) return;
    connecting.add(pluginId); renderPlugins();
    try {
      var value = await send("reconnectLegacyPlugin", { pluginId: pluginId });
      connected.set(pluginId, value.enabled ? "Плагин уже подключён и активен." : "Исходники подключены. Проверьте, сохраните и активируйте нужную версию.");
      if (active && visible()) notice(connected.get(pluginId));
      if (window.RNAssistantPlugins) {
        try { await RNAssistantPlugins.refresh({ afterPending: true }); }
        catch (error) { if (active && visible()) notice("Исходники подключены; не удалось обновить список плагинов: " + (error.detail || error.message)); }
      }
    } catch (error) {
      if (active && visible()) notice("Подключение не завершено: " + (error.detail || error.message) + ". Обновите список плагинов перед повтором.");
    } finally {
      connecting.delete(pluginId);
      if (active && visible()) renderPlugins();
    }
  }
  function pause() {
    active = false; generation++;
    if (batch) batch.stop = true;
    Object.keys(pending).forEach(function (slot) {
      var request = pending[slot].promise;
      if (request.requestId) cancelBridgeRequest(request.requestId).catch(function () {});
    });
    pending = Object.create(null); controls();
  }
  function sync() {
    if (!bound) return;
    if (!visible()) { if (active) pause(); return; }
    active = true; renderImports();
  }
  function bind() {
    if (bound) return;
    bound = true;
    node("Refresh").addEventListener("click", function () {
      notice("Читаю прежние данные…");
      Promise.all([loadChats(0), loadPlugins(0)]).then(function (results) {
        if (active && visible() && results.every(Boolean)) notice("Проверка завершена. Выберите архивный чат или исходники плагина.");
        controls();
      });
    });
    node("ChatSelect").addEventListener("change", function () { clearTranscript(); showChatInfo(); });
    node("Read").addEventListener("click", function () { readTranscript(0, 0); });
    node("ImportChat").addEventListener("click", function () { if (!batch && selected()) importItem("chat", selected()); });
    node("CheckChat").addEventListener("click", function () { if (selected()) checkImport("chat", capture("chat", selected())); });
    node("ImportPage").addEventListener("click", importPage);
    node("StopImport").addEventListener("click", function () { if (batch) { batch.stop = true; renderImports(); } });
    node("ChatFirst").addEventListener("click", function () { loadChats(0); });
    node("ChatNext").addEventListener("click", function () { if (chats && chats.nextOffset != null) loadChats(chats.nextOffset); });
    node("TextFirst").addEventListener("click", function () { readTranscript(0, 0); });
    node("TextNext").addEventListener("click", function () {
      if (transcript && transcript.nextMessageIndex != null) readTranscript(transcript.nextMessageIndex, transcript.nextCharacterOffset);
    });
    node("Copy").addEventListener("click", function () {
      if (transcript) copyTextResult(transcript.text).then(function () { if (active) notice("Показанный фрагмент скопирован."); },
        function (error) { if (active) notice("Копирование не выполнено: " + error.message); });
    });
    node("PluginFirst").addEventListener("click", function () { loadPlugins(0); });
    node("PluginNext").addEventListener("click", function () { if (plugins && plugins.nextOffset != null) loadPlugins(plugins.nextOffset); });
    document.addEventListener("visibilitychange", sync);
    window.addEventListener("pagehide", pause);
    sync();
  }
  window.RNAssistantLegacyRecovery = { bind: bind, sync: sync, pause: pause };
}());
