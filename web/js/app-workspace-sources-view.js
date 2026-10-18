(function (root) {
  "use strict";
  const labels = { Pending: "Ожидает", Running: "Обрабатывается", Prepared: "Подготовлен", Failed: "Ошибка", Cancelled: "Отменён" };
  const stageLabels = { Hashing: "Читаем файл", Compressing: "Подготавливаем снимок", Retaining: "Сохраняем снимок", Verifying: "Проверяем снимок" };
  const size = bytes => bytes < 1048576 ? (bytes / 1024).toLocaleString("ru", { maximumFractionDigits: 1 }) + " КБ"
    : (bytes / 1048576).toLocaleString("ru", { maximumFractionDigits: 1 }) + " МБ";
  function node(tag, name, text) {
    const result = document.createElement(tag); result.className = name || "";
    if (text !== undefined) result.textContent = text;
    return result;
  }
  function create(container, actions) {
    const el = suffix => container.querySelector("#ws" + suffix), events = new AbortController();
    const listen = (element, type, callback) => element.addEventListener(type, callback, { signal: events.signal });
    let state = null, browseKey = "", queueKey = "", retries = new Set(), browseTimer = null, queueTimer = null;
    let selectedKey = "", selectedOffset = 0;
    function pages(prefix, page) {
      const count = page ? page.matched : 0, offset = page ? page.offset : 0;
      el(prefix + "Pages").hidden = count <= 50;
      el(prefix + "Range").textContent = count ? (offset + 1) + "–" + Math.min(offset + 50, count) + " из " + count : "";
      el(prefix + "Previous").disabled = offset === 0 || state.browsing && prefix === "Browse";
      el(prefix + "Next").disabled = offset + 50 >= count || state.browsing && prefix === "Browse";
    }
    function replaceList(list, children, attribute) {
      const focused = list.contains(document.activeElement) ? document.activeElement.dataset[attribute] : null;
      const scroll = list.scrollTop;
      list.replaceChildren(...children); list.scrollTop = scroll;
      if (focused) Array.from(list.querySelectorAll("input,button")).find(item => item.dataset[attribute] === focused)?.focus({ preventScroll: true });
    }
    function browseRow(item, disabled) {
      const row = node("li", "ws-source-row"), copy = node("span", "ws-source-copy");
      if (item.isDirectory) {
        const button = node("button", "ws-source-folder", "▱  " + item.name);
        button.type = "button"; button.dataset.wsDirectory = item.path; button.disabled = state.browsing;
        row.append(button);
      } else {
        const label = node("label", "ws-source-file"), check = node("input"); check.type = "checkbox";
        check.dataset.wsFile = item.path; check.checked = state.chosen.includes(item.path); check.disabled = disabled || item.byteLength === 0;
        copy.append(node("span", "ws-source-name", item.name), node("small", "ws-source-muted", item.byteLength ? size(item.byteLength) : "Пустой файл"));
        label.append(check, copy); row.append(label);
      }
      return row;
    }
    function queueRow(item) {
      const row = node("li", "ws-source-row"), copy = node("div", "ws-source-copy"), check = node("input");
      check.type = "checkbox"; check.dataset.wsRetry = item.id; check.checked = retries.has(item.id);
      check.disabled = state.busy || state.uncertain || !["Failed", "Cancelled"].includes(item.state);
      check.setAttribute("aria-label", "Выбрать для повтора: " + item.path);
      copy.append(node("span", "ws-source-name", item.path));
      if (item.errorMessage) copy.append(node("small", "ws-source-error", item.errorMessage));
      else if (item.byteLength !== null) copy.append(node("small", "ws-source-muted", size(item.byteLength)));
      const badge = node("span", "ws-source-state", labels[item.state]); badge.dataset.state = item.state;
      row.append(check, copy, badge); return row;
    }
    function selectedRows(disabled) {
      const query = el("SelectedSearch").value.toLowerCase(), matches = state.chosen.filter(path => path.toLowerCase().includes(query));
      selectedOffset = matches.length ? Math.min(selectedOffset, Math.floor((matches.length - 1) / 50) * 50) : 0;
      const items = matches.slice(selectedOffset, selectedOffset + 50);
      const known = new Map();
      if (state.files) state.files.items.filter(item => !item.isDirectory).forEach(item => known.set(item.path, item));
      if (state.rows) state.rows.items.forEach(item => known.set(item.path, item));
      if (state.queue && state.queue.currentItem) known.set(state.queue.currentItem.path, state.queue.currentItem);
      const key = JSON.stringify([items.map(path => [path, known.get(path)]), disabled]);
      if (key !== selectedKey) {
        selectedKey = key;
        replaceList(el("SelectedList"), items.map(path => {
          const item = known.get(path), row = node("tr"), selection = node("td"), file = node("td"), name = node("span", "ws-source-name", path.split("/").pop());
          const check = node("input"); check.type = "checkbox"; check.checked = true;
          check.dataset.wsRemove = path; check.disabled = disabled; check.setAttribute("aria-label", "Выбрать " + path);
          selection.append(check); file.append(name); file.title = path;
          const bytes = item && Number.isSafeInteger(item.byteLength) ? size(item.byteLength) : "—";
          const status = item && item.state ? labels[item.state] : "Выбран";
          const badge = node("span", "ws-source-state", status); badge.dataset.state = item && item.state || "Selected";
          const length = node("td", "ws-source-size", bytes), stateCell = node("td"); stateCell.append(badge);
          row.append(selection, file, length, stateCell); return row;
        }), "wsRemove");
      }
      el("SelectedEmpty").hidden = !!matches.length;
      pages("Selected", { matched: matches.length, offset: selectedOffset });
      el("SelectedPages").hidden = state.chosen.length <= 50 && !query;
    }
    function render(snapshot) {
      if (!state) {
        el("QueueSearch").value = snapshot.filters.query;
        el("QueueState").value = snapshot.filters.state || ""; el("QueueSort").value = snapshot.filters.sort;
      }
      state = snapshot;
      const enabled = state.context && state.context.workspace && state.context.project, disabled = state.contextBusy || state.unfinished;
      el("SourceBrowse").disabled = !enabled || state.contextBusy;
      el("SourceContext").textContent = state.unfinished && state.queue ? "Файлов в очереди: " + state.queue.total + ". Список и состояния — ниже."
        : enabled ? "Выберите файлы и шаблон. Обработка начнётся по вашей команде." : "Откройте проект, чтобы выбрать источники.";
      el("SourceSelection").hidden = !enabled;
      el("SourceSelected").textContent = "Выбрано файлов: " + state.chosen.length + " из 500";
      ["SourcePrepare", "SourceClear", "SourceFileBudget", "SourceBatchBudget"].forEach(name => { el(name).disabled = disabled || !enabled; });
      el("SourcePrepare").disabled = disabled || !enabled || !state.chosen.length;
      const readOnly = enabled && state.context.workspace.readOnly;
      el("SourceSave").disabled = disabled || !enabled || readOnly || !state.dirty;
      el("SourcePersistence").textContent = state.dirty ? readOnly ? "Временная подборка · папка открыта только для чтения."
        : "Есть несохранённые изменения в подборке." : "Состав сохранён в проекте. Подготовка запускается отдельно.";
      selectedRows(disabled);
      el("BrowseLoading").textContent = state.browsing ? "Читаем папку…" : "";
      el("BrowseList").setAttribute("aria-busy", String(state.browsing));
      const files = state.files, available = files ? files.items.filter(item => !item.isDirectory && item.byteLength > 0) : [];
      const nextBrowseKey = JSON.stringify([files, state.chosen, disabled, state.browsing]);
      if (nextBrowseKey !== browseKey) {
        browseKey = nextBrowseKey;
        replaceList(el("BrowseList"), files ? files.items.map(item => browseRow(item, disabled || state.browsing)) : [], "wsFile");
      }
      el("BrowseSelectPage").disabled = disabled || state.browsing || !available.length;
      el("BrowseSelectPage").checked = available.length > 0 && available.every(item => state.chosen.includes(item.path));
      el("BrowseSelectPage").indeterminate = available.some(item => state.chosen.includes(item.path)) && !el("BrowseSelectPage").checked;
      el("BrowseEmpty").hidden = !files || !!files.items.length;
      el("BrowseLimit").hidden = !files || !files.scanLimitReached;
      el("BrowseUp").disabled = !files || !files.directory || state.browsing;
      if (files && !state.browsing && document.activeElement !== el("BrowseDirectory")) el("BrowseDirectory").value = files.directory;
      pages("Browse", files);
      const queue = state.queue, current = queue && queue.currentItem;
      el("SourceActivity").hidden = !queue && !state.uncertain;
      el("QueueTitle").textContent = state.uncertain ? "Состояние требует обновления" : !queue ? ""
        : queue.state === "Finished" ? queue.prepared === queue.total ? "Файлы подготовлены" : "Подготовка завершена с замечаниями"
        : queue.state === "Paused" ? "Подготовка приостановлена" : queue.state === "Pausing" ? "Завершаем текущий файл перед паузой" : "Подготавливаем файлы";
      const counts = queue ? "Подготовлено " + queue.prepared + " из " + queue.total + " · Ошибок: " + queue.failed + " · Отменено: " + queue.cancelled : "";
      if (el("QueueCounts").textContent !== counts) el("QueueCounts").textContent = counts;
      el("QueueCurrent").textContent = current ? current.path : queue && queue.pending ? "Ожидают: " + queue.pending : "";
      el("QueueMeter").hidden = !current || state.uncertain;
      const progress = current && current.phase === "Capturing" && current.progress;
      if (progress && progress.totalBytes > 0) {
        el("QueueProgress").max = progress.totalBytes; el("QueueProgress").value = progress.completedBytes;
        el("QueuePercent").textContent = Math.floor(progress.completedBytes / progress.totalBytes * 100) + "%";
        el("QueueProgress").setAttribute("aria-valuetext", size(progress.completedBytes) + " из " + size(progress.totalBytes));
      } else { el("QueueProgress").removeAttribute("value"); el("QueueProgress").removeAttribute("aria-valuetext"); el("QueuePercent").textContent = ""; }
      el("QueueStage").textContent = state.uncertain ? "Действия не повторяются автоматически." : progress
        ? stageLabels[progress.stage] + " · " + size(progress.completedBytes) + " / " + size(progress.totalBytes)
        : current ? current.phase === "WaitingForReader" ? "Ожидаем свободный обработчик" : "Проверяем проект" : queue && queue.errorMessage || "";
      const finished = !queue || queue.state === "Finished", paused = queue && ["Paused", "Pausing"].includes(queue.state);
      el("QueuePause").hidden = finished || paused; el("QueueResume").hidden = finished || !paused;
      el("QueueCancel").hidden = el("QueueMore").hidden = finished;
      container.querySelectorAll("[data-ws-command]").forEach(button => {
        button.disabled = state.busy || state.uncertain || state.contextBusy || button.dataset.wsCommand === "CancelCurrent" && !current;
      });
      el("QueueRefresh").disabled = state.contextBusy || state.busy;
      const rows = state.rows;
      el("QueueDetails").hidden = !rows;
      el("SourceQueueAction").disabled = !rows;
      el("QueueListCard").hidden = !rows;
      if (rows) {
        retries = new Set([...retries].filter(id => rows.items.some(item => item.id === id && ["Failed", "Cancelled"].includes(item.state))));
        const nextQueueKey = JSON.stringify([rows.items.map(item => [item.id, item.path, item.state, item.byteLength, item.errorMessage]), state.busy, state.uncertain, [...retries]]);
        if (queueKey !== nextQueueKey) { queueKey = nextQueueKey; replaceList(el("QueueList"), rows.items.map(queueRow), "wsRetry"); }
        el("QueueEmpty").hidden = !!rows.items.length;
      }
      el("QueueRetry").disabled = state.busy || state.uncertain || !retries.size;
      pages("Queue", rows);
      el("SourceStatus").textContent = state.notice; el("SourceStatus").dataset.error = String(state.error);
      el("SourceStatus").setAttribute("role", state.error ? "alert" : "status");
    }
    const browse = (directory, offset = 0) => actions.browse(directory, el("BrowseSearch").value, offset);
    listen(el("SourceBrowse"), "click", () => { el("SourcePicker").hidden = false; browse(state.files ? state.files.directory : ""); });
    listen(el("SourcePickerClose"), "click", () => { el("SourcePicker").hidden = true; el("SourceBrowse").focus(); });
    listen(el("BrowseForm"), "submit", event => {
      event.preventDefault(); el("BrowseSearch").value = "";
      const directory = el("BrowseDirectory").value.trim().replace(/\\/g, "/");
      browse(directory.startsWith("/") ? directory : directory.replace(/\/+$/, ""));
    });
    listen(el("BrowseUp"), "click", () => { el("BrowseSearch").value = ""; browse(state.files.directory.split("/").slice(0, -1).join("/")); });
    listen(el("BrowseSearch"), "input", () => { clearTimeout(browseTimer); browseTimer = setTimeout(() => browse(state.files ? state.files.directory : ""), 250); });
    listen(el("BrowseList"), "click", event => {
      const button = event.target.closest("[data-ws-directory]");
      if (button) { el("BrowseSearch").value = ""; browse(button.dataset.wsDirectory); }
    });
    listen(el("BrowseList"), "change", event => { if (event.target.dataset.wsFile) actions.choose([event.target.dataset.wsFile], event.target.checked); });
    listen(el("BrowseSelectPage"), "change", event => actions.choose(state.files.items.map(item => item.path), event.target.checked));
    listen(el("BrowsePrevious"), "click", () => browse(state.files.directory, Math.max(0, state.files.offset - 50)));
    listen(el("BrowseNext"), "click", () => browse(state.files.directory, state.files.offset + 50));
    listen(el("SourceClear"), "click", actions.clear);
    listen(el("SourceSave"), "click", actions.save);
    listen(el("SelectedList"), "change", event => { if (event.target.dataset.wsRemove && !event.target.checked) actions.remove(event.target.dataset.wsRemove); });
    listen(el("SelectedSearch"), "input", () => { selectedOffset = 0; render(state); });
    listen(el("SelectedPrevious"), "click", () => { selectedOffset = Math.max(0, selectedOffset - 50); render(state); });
    listen(el("SelectedNext"), "click", () => { selectedOffset += 50; render(state); });
    listen(el("SourcePrepare"), "click", () => actions.prepare(Number(el("SourceFileBudget").value), Number(el("SourceBatchBudget").value)));
    listen(el("SourceQueueAction"), "click", () => {
      el("QueueDetails").open = true;
      container.querySelector(".ws-source-more").open = false;
      el("QueueDetails").scrollIntoView({ block: "nearest" });
      el("QueueDetails").querySelector("summary").focus({ preventScroll: true });
    });
    listen(el("SourceActivity"), "click", event => {
      const button = event.target.closest("[data-ws-command]"); if (button) actions.control(button.dataset.wsCommand);
    });
    listen(el("QueueRefresh"), "click", actions.refresh);
    listen(el("QueueList"), "change", event => {
      const id = event.target.dataset.wsRetry; if (!id) return;
      if (event.target.checked) retries.add(id); else retries.delete(id); render(state);
    });
    listen(el("QueueRetry"), "click", () => actions.control("RetrySelected", [...retries]));
    function filter() {
      retries.clear(); actions.filter({ offset: 0, query: el("QueueSearch").value, state: el("QueueState").value || null, sort: el("QueueSort").value });
    }
    listen(el("QueueSearch"), "input", () => { clearTimeout(queueTimer); queueTimer = setTimeout(filter, 250); });
    listen(el("QueueState"), "change", filter); listen(el("QueueSort"), "change", filter);
    listen(el("QueuePrevious"), "click", () => { retries.clear(); actions.filter({ offset: Math.max(0, state.rows.offset - 50) }); });
    listen(el("QueueNext"), "click", () => { retries.clear(); actions.filter({ offset: state.rows.offset + 50 }); });
    function prepared() {
      el("SourcePicker").hidden = true; retries.clear();
      el("QueueSearch").value = el("QueueState").value = ""; el("QueueSort").value = "Added";
    }
    function reset() { prepared(); el("BrowseDirectory").value = el("BrowseSearch").value = el("SelectedSearch").value = "";
      selectedOffset = 0; el("QueueDetails").open = false; clearTimeout(browseTimer); clearTimeout(queueTimer); }
    return Object.freeze({ render: render, prepared: prepared, reset: reset,
      dispose: () => { events.abort(); clearTimeout(browseTimer); clearTimeout(queueTimer); } });
  }
  root.RNAssistantWorkspaceSourcesView = Object.freeze({ create: create });
}(window));
