var logFilterCounts = { all: 0, error: 0, warning: 0, success: 0, info: 0 };

function updateLogFilterCounts() {
  var box = $("logBox");
  Array.prototype.forEach.call(document.querySelectorAll("[data-log-filter]"), function (button) {
    var count = button.querySelector(".log-filter-count");
    if (count) count.textContent = String(logFilterCounts[button.dataset.logFilter] || 0);
  });
  if (box) box.classList.toggle("is-filter-empty",
    logFilterCounts.all > 0 && (logFilterCounts[state.logFilter] || 0) === 0);
}

function recordLogEntry(entry, removed) {
  logFilterCounts.all += 1;
  if (Object.prototype.hasOwnProperty.call(logFilterCounts, entry.dataset.logType))
    logFilterCounts[entry.dataset.logType] += 1;
  if (removed) {
    logFilterCounts.all -= 1;
    if (Object.prototype.hasOwnProperty.call(logFilterCounts, removed.dataset.logType))
      logFilterCounts[removed.dataset.logType] -= 1;
  }
  updateLogFilterCounts();
}

function isLogNearBottom(box) {
  return box.scrollHeight - box.clientHeight - box.scrollTop <= 32;
}

function updateLogScrollButton() {
  var box = $("logBox");
  var button = $("logScrollBottomButton");
  if (!box || !button) return;
  var visible = !isLogNearBottom(box);
  button.classList.toggle("is-visible", visible);
  button.setAttribute("aria-hidden", visible ? "false" : "true");
  button.tabIndex = visible ? 0 : -1;
}

function setLogFilter(filter) {
  var allowed = { all: true, error: true, warning: true, success: true, info: true };
  state.logFilter = allowed[filter] ? filter : "all";
  Array.prototype.forEach.call(document.querySelectorAll("[data-log-filter]"), function (button) {
    var active = button.dataset.logFilter === state.logFilter;
    button.classList.toggle("active", active);
    button.setAttribute("aria-pressed", active ? "true" : "false");
  });
  Array.prototype.forEach.call(document.querySelectorAll("#logBox .log-entry"), function (entry) {
    entry.hidden = state.logFilter !== "all" && entry.dataset.logType !== state.logFilter;
  });
  updateLogFilterCounts();
  updateLogScrollButton();
}

function bindLogActions() {
  var clear = $("clearLogButton");
  if (clear) clear.addEventListener("click", function () {
    var box = $("logBox");
    if (box) box.textContent = "";
    logFilterCounts = { all: 0, error: 0, warning: 0, success: 0, info: 0 };
    updateLogFilterCounts();
    updateLogScrollButton();
  });
  var box = $("logBox");
  if (box) box.addEventListener("scroll", updateLogScrollButton, { passive: true });
  var bottom = $("logScrollBottomButton");
  if (bottom) bottom.addEventListener("click", function () {
    if (box) box.scrollTop = box.scrollHeight;
    updateLogScrollButton();
  });
  Array.prototype.forEach.call(document.querySelectorAll("[data-log-filter]"), function (button) {
    button.addEventListener("click", function () {
      setLogFilter(button.dataset.logFilter);
    });
  });
  setLogFilter(state.logFilter);
  window.RNAssistantFileLogs.bind();
}

(function () {
  "use strict";
  // Presentation of one bounded file page. Files, cursors and retention belong to the host.
  var mode = "files", active = false, bound = false, live = true, busy = false;
  var generation = 0, pending = null, timer = null, selectedId = "", inventory = null, page = null;
  var optionsDirty = false, acceptedStamp = "", searchOffset = -1, renderScroll = false;
  var pageBytes = 65536;

  function visible() {
    return mode === "files" && !document.hidden && window.RNAssistantDiagnostics &&
      RNAssistantDiagnostics.isVisible() && state.diagnosticsTab === "application" &&
      $("diagnosticsAppView").value === "log";
  }
  function text(id, value) { var node = $(id); if (node) node.textContent = value; }
  function bytes(value) { return formatStorageBytes(value); }
  function issueText(issues) {
    return (issues || []).slice(0, 12).map(function (issue) {
      return [issue.fileId, issue.operation, issue.message].filter(Boolean).join(" · ");
    }).join("\n") + ((issues || []).length > 12 ? "\n…остальные ошибки не показаны." : "");
  }
  function notice(message, error) {
    text("fileLogNotice", message || "");
    $("fileLogNotice").classList.toggle("hidden", !message);
    $("fileLogNotice").classList.toggle("is-error", !!error);
    $("fileLogNotice").setAttribute("role", error ? "alert" : "status");
  }
  function cancelRead() {
    generation++;
    if (timer !== null) { clearTimeout(timer); timer = null; }
    if (pending && pending.requestId && typeof cancelBridgeRequest === "function")
      Promise.resolve(cancelBridgeRequest(pending.requestId)).catch(function () {});
    pending = null;
  }
  function selectedFile() {
    return inventory && inventory.files.find(function (file) { return file.id === selectedId; });
  }
  function stamp(file) { return file ? file.id + "|" + file.byteLength + "|" + file.lastWriteUtc : ""; }
  function controls() {
    var hasFile = !!selectedId, reading = !!pending;
    $("fileLogSelect").disabled = busy;
    $("fileLogRefresh").disabled = busy;
    $("fileLogLive").disabled = busy;
    $("fileLogLive").setAttribute("aria-pressed", live ? "true" : "false");
    $("fileLogLive").textContent = live ? "Автообновление" : "На паузе";
    $("fileLogEarlier").disabled = busy || reading || !page || !page.hasEarlier;
    $("fileLogLatest").disabled = busy || reading || !hasFile;
    $("fileLogActions").disabled = busy;
    $("fileLogSaveOptions").disabled = busy || !inventory;
    $("fileLogPrune").disabled = busy || !inventory;
    ["fileLogMaxFile", "fileLogMaxTotal", "fileLogDays", "fileLogMaxFiles"].forEach(function (id) { $(id).disabled = busy || !inventory; });
    $("fileLogPane").setAttribute("aria-busy", busy || reading ? "true" : "false");
  }
  function schedule() {
    if (timer !== null) clearTimeout(timer);
    timer = null;
    if (active && visible() && live && !busy && !pending)
      timer = setTimeout(function () { timer = null; refresh(false); }, 2000);
  }
  function applyInventory(result) {
    inventory = result;
    var select = $("fileLogSelect"), previous = selectedId;
    var files = result.files || []; inventory.files = files;
    if (!files.some(function (file) { return file.id === selectedId; })) {
      var preferred = files.find(function (file) { return file.name === "rnassistant.log"; });
      selectedId = preferred ? preferred.id : files.length ? files[0].id : "";
    }
    select.replaceChildren();
    files.forEach(function (file) {
      var option = document.createElement("option"); option.value = file.id;
      var date = new Date(file.lastWriteUtc);
      option.textContent = file.name + " · " + bytes(file.byteLength) +
        (Number.isNaN(date.getTime()) ? "" : " · " + date.toLocaleString("ru")) + (file.isActive ? " · текущий" : "");
      select.appendChild(option);
    });
    if (!files.length) {
      var empty = document.createElement("option"); empty.value = ""; empty.textContent = "Диагностических файлов пока нет";
      select.appendChild(empty);
    }
    select.value = selectedId;
    if (previous !== selectedId) { page = null; acceptedStamp = ""; text("fileLogText", ""); }
    var options = result.options;
    if (options && !optionsDirty) {
      $("fileLogMaxFile").value = options.maxFileMiB;
      $("fileLogMaxTotal").value = options.maxTotalMiB;
      $("fileLogDays").value = options.retentionDays;
      $("fileLogMaxFiles").value = options.maxFiles;
    }
    text("fileLogUsage", "· " + bytes(result.totalBytes) + (options ? " / " + options.maxTotalMiB + " МиБ" : "") +
      " · файлов: " + files.length + (result.overLimit ? " · лимит превышен" : ""));
    var issues = issueText(result.issues);
    if (result.overLimit) issues = "Лимит хранения пока не соблюдён. Проверьте ошибки очистки.\n" + issues;
    text("fileLogIssues", issues); $("fileLogIssues").classList.toggle("hidden", !issues);
    controls();
  }
  function renderSearch(next) {
    var host = $("fileLogText"), raw = page ? page.text : "", query = $("fileLogSearch").value;
    if (!query || !raw) {
      host.textContent = raw || (selectedId ? "Файл пуст." : "Диагностических файлов пока нет.");
      text("fileLogMatches", ""); searchOffset = -1; return;
    }
    var folded = raw.toLowerCase(), search = query.toLowerCase(), count = 0, index = 0;
    while ((index = folded.indexOf(search, index)) >= 0) { count++; index += search.length; }
    var found = folded.indexOf(search, next ? searchOffset + Math.max(1, search.length) : 0);
    if (found < 0 && next) found = folded.indexOf(search);
    searchOffset = found; host.replaceChildren();
    if (found < 0) host.textContent = raw;
    else {
      host.appendChild(document.createTextNode(raw.slice(0, found)));
      var match = document.createElement("mark"); match.textContent = raw.slice(found, found + query.length);
      host.appendChild(match); host.appendChild(document.createTextNode(raw.slice(found + query.length)));
      var region = $("fileLogReadRegion");
      region.scrollTop += match.getBoundingClientRect().top - region.getBoundingClientRect().top - region.clientHeight / 3;
    }
    text("fileLogMatches", count ? "Совпадений: " + count : "Не найдено в этом фрагменте");
  }
  function applyPage(result, follow) {
    // The host bounds the response. The defensive bound also protects malformed preview transports.
    if (typeof result.text !== "string" || result.text.length > 131072)
      throw new Error("Ответ журнала превышает допустимый размер страницы.");
    page = result; searchOffset = -1;
    var region = $("fileLogReadRegion");
    renderScroll = true;
    renderSearch(false);
    if (!$("fileLogSearch").value) region.scrollTop = follow ? region.scrollHeight : 0;
    requestAnimationFrame(function () { renderScroll = false; });
    text("fileLogStatus", result.snapshotLength ? "Байты " + (result.startOffset + 1) + "–" + result.endOffset +
      " из " + result.snapshotLength + (result.partialFirstLine ? " · начало строки на предыдущей странице" : "") : "Файл пуст.");
    controls();
  }
  async function request(type, payload, token) {
    var current = send(type, payload); pending = current; controls();
    try {
      var result = await current;
      return token === generation && active && visible() ? result : null;
    } finally { if (pending === current) { pending = null; controls(); } }
  }
  async function readLatest(token) {
    var id = selectedId, fileStamp = stamp(selectedFile());
    if (!id) { page = null; renderSearch(false); text("fileLogStatus", "Диагностических файлов пока нет."); return; }
    var result = await request("readDiagnosticLog", { fileId: id, maxBytes: pageBytes }, token);
    if (!result || selectedId !== id) return;
    if (result.fileId !== id || result.resetRequired) throw new Error("Файл изменился во время чтения. Обновите журнал.");
    applyPage(result, true); acceptedStamp = fileStamp;
  }
  async function refresh(force) {
    if (!active || !visible() || busy || pending) return;
    var token = generation;
    try {
      var result = await request("listDiagnosticLogs", {}, token);
      if (!result) return;
      applyInventory(result);
      if (force || !page || live && acceptedStamp !== stamp(selectedFile())) await readLatest(token);
    } catch (error) {
      if (token === generation && active) { notice(error.detail || error.message || "Не удалось прочитать журнал.", true); setLive(false); }
    } finally { if (token === generation) { controls(); schedule(); } }
  }
  function setLive(value) {
    var next = !!value;
    if (live && !next) cancelRead();
    live = next; controls(); schedule();
  }
  async function earlier() {
    if (busy || pending || !page || !page.hasEarlier) return;
    setLive(false); var token = generation, id = selectedId;
    try {
      var result = await request("readDiagnosticLog", { fileId: id, beforeOffset: page.startOffset,
        snapshotToken: page.snapshotToken, maxBytes: pageBytes }, token);
      if (!result || selectedId !== id) return;
      if (result.fileId !== id) throw new Error("Получен ответ для другого файла журнала.");
      if (result.resetRequired) {
        notice("Файл был очищен или сменился при ротации. Показан новый последний фрагмент; автообновление на паузе.", false);
        await readLatest(token);
      } else applyPage(result, false);
    } catch (error) { if (token === generation && active) notice(error.detail || error.message, true); }
    finally { if (token === generation) controls(); }
  }
  async function mutate(type, payload, message) {
    if (busy) throw new Error("Дождитесь завершения текущей операции.");
    busy = true; cancelRead(); controls(); notice("Операция выполняется…", false);
    try {
      var result = await send(type, payload);
      optionsDirty = type === "saveDiagnosticLogOptions" ? false : optionsDirty;
      page = null; acceptedStamp = "";
      if (active && visible()) applyInventory(result.inventory);
      // Closing a view does not cancel an accepted mutation; keep its terminal receipt for return.
      var problems = issueText(result.issues);
      notice((problems ? "Операция завершена частично." : message) + " Обработано файлов: " + result.affectedFiles + "; освобождено " + bytes(result.reclaimedBytes) +
        (problems ? ".\nНе все действия завершены:\n" + problems : "."), !!problems);
      return result;
    } catch (error) {
      notice(error.detail || error.message || "Не удалось изменить логи.", true);
      throw error;
    } finally {
      busy = false; controls();
      if (active && visible()) refresh(true);
    }
  }
  function confirmMutation(type, all, remove) {
    var id = selectedId, file = selectedFile();
    if (!all && !file) return;
    RNAssistantDialog.open({ title: all ? "Очистить все диагностические логи?" : remove ? "Удалить файл журнала?" : "Очистить файл журнала?",
      message: (all ? "Все диагностические файлы будут удалены. Новые записи создадут файлы снова." : "Файл: " + file.name + ".") +
        " Действие необратимо. Новые диагностические записи продолжат поступать. История чатов и документы сохранятся.",
      submitLabel: all || !remove ? "Очистить" : "Удалить", danger: true,
      onSubmit: function () { return mutate(type, all ? {} : { fileId: id }, all ? "Очистка логов выполнена." : remove ? "Удаление выполнено." : "Очистка выполнена."); }
    });
  }
  function actions() {
    var file = selectedFile();
    RNAssistantActionMenu.open($("fileLogActions"), [
      { label: "Копировать показанный фрагмент", icon: "copy", disabled: !page || !page.text,
        onSelect: function () { return copyTextResult(page.text).then(function () { notice("Показанный фрагмент скопирован.", false); },
          function () { notice("Не удалось скопировать фрагмент.", true); }); } },
      { label: "Очистить выбранный файл", icon: "clear", danger: true, disabled: !file,
        onSelect: function () { confirmMutation("clearDiagnosticLog", false, false); } },
      { label: "Удалить выбранный файл", icon: "trash", danger: true, disabled: !file,
        onSelect: function () { confirmMutation("deleteDiagnosticLog", false, true); } },
      { label: "Очистить все диагностические логи", icon: "trash", danger: true, disabled: !inventory || !inventory.files.length,
        onSelect: function () { confirmMutation("clearDiagnosticLogs", true, false); } }
    ]);
  }
  function sync() {
    var next = !!visible();
    if (next === active) return;
    active = next;
    if (active) refresh(!page || live); else { cancelRead(); controls(); }
  }
  function pause() { active = false; cancelRead(); controls(); }
  function source(next) {
    mode = next === "panel" ? "panel" : "files";
    $("fileLogPane").classList.toggle("hidden", mode !== "files");
    $("panelLogPane").classList.toggle("hidden", mode !== "panel");
    document.querySelectorAll("[data-log-source]").forEach(function (button) {
      var current = button.dataset.logSource === mode;
      button.classList.toggle("active", current); button.setAttribute("aria-pressed", current ? "true" : "false");
    });
    sync();
    if (mode === "panel") requestAnimationFrame(updateLogScrollButton);
  }
  function bind() {
    if (bound || !$("fileLogPane")) return; bound = true;
    document.querySelectorAll("[data-log-source]").forEach(function (button) {
      button.addEventListener("click", function () { source(button.dataset.logSource); });
    });
    $("fileLogSelect").addEventListener("change", function () {
      cancelRead(); selectedId = this.value; page = null; acceptedStamp = "";
      text("fileLogText", "Загрузка…"); notice("", false); refresh(true);
    });
    $("fileLogRefresh").addEventListener("click", function () { cancelRead(); notice("", false); refresh(true); });
    $("fileLogLive").addEventListener("click", function () { setLive(!live); if (live) { cancelRead(); refresh(true); } });
    $("fileLogEarlier").addEventListener("click", earlier);
    $("fileLogLatest").addEventListener("click", function () { setLive(true); cancelRead(); refresh(true); });
    $("fileLogActions").addEventListener("click", actions);
    $("fileLogReadRegion").addEventListener("scroll", function () {
      if (active && live && !renderScroll && !isLogNearBottom(this)) setLive(false);
    }, { passive: true });
    $("fileLogSearch").addEventListener("input", function () { setLive(false); renderSearch(false); });
    $("fileLogSearch").addEventListener("keydown", function (event) { if (event.key === "Enter") { event.preventDefault(); renderSearch(true); } });
    $("fileLogFindNext").addEventListener("click", function () { setLive(false); renderSearch(true); });
    $("fileLogOptionsForm").addEventListener("input", function () { optionsDirty = true; });
    $("fileLogOptionsForm").addEventListener("submit", function (event) {
      event.preventDefault(); if (busy) return;
      var options = { maxFileMiB: Number($("fileLogMaxFile").value), maxTotalMiB: Number($("fileLogMaxTotal").value),
        retentionDays: Number($("fileLogDays").value), maxFiles: Number($("fileLogMaxFiles").value) };
      if (!Number.isInteger(options.maxFileMiB) || options.maxFileMiB < 1 || options.maxFileMiB > 100 ||
          !Number.isInteger(options.maxTotalMiB) || options.maxTotalMiB < 2 * options.maxFileMiB || options.maxTotalMiB > 1000 ||
          !Number.isInteger(options.retentionDays) || options.retentionDays < 1 || options.retentionDays > 365 ||
          !Number.isInteger(options.maxFiles) || options.maxFiles < 2 || options.maxFiles > 200) {
        notice("Укажите целые значения: файл 1–100 МиБ; всего не меньше двух файлов и не больше 1000 МиБ; срок 1–365 дней; число файлов 2–200.", true); return;
      }
      RNAssistantDialog.open({ title: "Применить лимиты хранения?",
        message: "Старые диагностические файлы сверх новых лимитов будут удалены сразу. История чатов и документы сохранятся.",
        submitLabel: "Применить", danger: true,
        onSubmit: function () { return mutate("saveDiagnosticLogOptions", options, "Лимиты хранения применены."); }
      });
    });
    $("fileLogPrune").addEventListener("click", function () {
      RNAssistantDialog.open({ title: "Удалить старые логи?", message: "Будут удалены диагностические файлы сверх действующих лимитов хранения.",
        submitLabel: "Удалить старые", danger: true,
        onSubmit: function () { return mutate("pruneDiagnosticLogs", {}, "Очистка по лимитам выполнена."); } });
    });
    document.addEventListener("visibilitychange", sync);
    window.addEventListener("pagehide", pause);
    controls();
  }
  window.RNAssistantFileLogs = Object.freeze({ bind: bind, sync: sync, pause: pause,
    dispose: function () { pause(); page = null; inventory = null; acceptedStamp = ""; text("fileLogText", ""); }
  });
}());
