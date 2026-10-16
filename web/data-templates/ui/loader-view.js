(function (root) {
  "use strict";
  const number = new Intl.NumberFormat("ru", { maximumFractionDigits: 1 });
  const collator = new Intl.Collator("ru", { numeric: true, sensitivity: "base" });
  const pageSize = 50;
  function size(bytes) {
    if (bytes < 1024) return bytes + " Б";
    const divisor = bytes < 1048576 ? 1024 : bytes < 1073741824 ? 1048576 : 1073741824;
    return number.format(bytes / divisor) + (divisor === 1024 ? " КБ" : divisor === 1048576 ? " МБ" : " ГБ");
  }
  function node(tag, className, text) {
    const element = document.createElement(tag);
    if (className) element.className = className;
    if (text !== undefined) element.textContent = text;
    return element;
  }
  function sourceState(source) {
    if (!source.format) return "Формат не поддерживается";
    if (!source.size) return "Пустой файл";
    if (source.duplicate) return "Возможный повтор";
    return source.included ? "В подборке" : "Не выбран";
  }

  // DOM, navigation and display choices only. No bridge, readers, File ownership or job lifecycle.
  function create(container, formats, actions) {
    const el = suffix => container.querySelector("#dt" + suffix);
    const tabs = Array.from(container.querySelectorAll("[data-dt-view]"));
    const events = new AbortController();
    const listen = (element, event, handler) => element.addEventListener(event, handler, { signal: events.signal });
    let state = { sources: [], busy: false }, selectedId = null, dragDepth = 0, pageIndex = 0;
    const operationView = root.DataTemplatesOperationView.create(el("Operation"), { cancel: actions.cancel, dismiss: actions.dismiss });
    function show(name, focus) {
      const active = tabs.find(tab => tab.dataset.dtView === name);
      if (!active) return;
      tabs.forEach(tab => {
        const selected = tab === active;
        tab.setAttribute("aria-selected", String(selected)); tab.tabIndex = selected ? 0 : -1;
        container.querySelector("#" + tab.getAttribute("aria-controls")).hidden = !selected;
      });
      el("ViewSelect").value = name;
      if (focus) container.querySelector("#" + active.getAttribute("aria-controls")).focus({ preventScroll: true });
    }
    function matchingSources() {
      const query = el("SourceSearch").value.trim().toLocaleLowerCase(), filter = el("SourceFilter").value;
      const status = el("SourceState").value, order = el("SourceSort").value;
      const matches = state.sources.filter(source => (!query || source.path.toLocaleLowerCase().includes(query)) &&
        (!filter || (source.format ? source.format.id : "unsupported") === filter) &&
        (!status || (status === "included" ? source.included : status === "excluded" ? !source.included : !source.available || source.duplicate)));
      if (order !== "added") matches.sort((a, b) => order === "size" ? b.size - a.size
        : collator.compare(order === "path" ? a.path : a.name, order === "path" ? b.path : b.name));
      return matches;
    }
    function visibleSources() { return matchingSources().slice(pageIndex * pageSize, (pageIndex + 1) * pageSize); }
    function renderPagination(total) {
      const pages = Math.ceil(total / pageSize);
      el("Pagination").hidden = pages <= 1;
      el("PageRange").textContent = total ? (pageIndex * pageSize + 1) + "–" + Math.min((pageIndex + 1) * pageSize, total) + " из " + total : "";
      el("SourcePage").replaceChildren(...Array.from({ length: pages }, (_, index) => {
        const option = node("option", "", (index + 1) + " / " + pages); option.value = String(index); return option;
      }));
      el("SourcePage").value = String(pageIndex);
      el("PreviousPage").disabled = pageIndex === 0; el("NextPage").disabled = pageIndex >= pages - 1;
      el("ListOptionsHint").textContent = el("SourceState").value || el("SourceSort").value !== "added" ? " · изменены" : "";
    }
    function renderDetail() {
      const source = state.sources.find(item => item.id === selectedId);
      el("SourceDetail").hidden = !source;
      if (!source) return;
      el("DetailTitle").textContent = source.name;
      el("DetailMetadata").replaceChildren();
      [["Формат", source.format ? source.format.label + " · по расширению" : "Не поддерживается"],
        ["Размер", size(source.size)], ["Путь в подборке", source.path], ["Состояние", sourceState(source)]].forEach(pair => {
        el("DetailMetadata").append(node("dt", "", pair[0]), node("dd", "", pair[1]));
      });
      el("DetailNote").textContent = source.duplicate
        ? "Совпали путь, размер и дата изменения. Содержимое не сравнивалось — решите, нужны ли оба включения."
        : source.available ? container.dataset.dtProcessing === "true" && source.format.id === "xlsx"
          ? "Содержимое ещё не прочитано. Выберите JSON-шаблон на вкладке «Шаблоны» и запустите обработку."
          : "Содержимое ещё не прочитано. Предпросмотр для этого формата пока недоступен."
          : source.format ? "В файле нет данных. Выберите непустой файл."
            : "Для этого источника пока не запланирован импорт. Подготовьте файл XLSX, CSV или JSON.";
    }
    function sourceRow(source) {
      const row = node("li", "dt-source-row");
      row.dataset.selected = String(source.id === selectedId); row.dataset.unavailable = String(!source.available);
      const check = node("input");
      check.type = "checkbox"; check.checked = source.included; check.disabled = state.busy || !source.available;
      check.dataset.sourceToggle = source.id; check.setAttribute("aria-label", "Включить " + source.path);
      const icon = node("span", "dt-file-icon", source.format ? source.format.id.toUpperCase() : "ФАЙЛ");
      icon.dataset.format = source.format ? source.format.id : "unsupported"; icon.setAttribute("aria-hidden", "true");
      const copy = node("div", "dt-file-copy"), name = node("button", "dt-file-name", source.name);
      name.type = "button"; name.dataset.sourceDetail = source.id; name.title = source.path;
      name.setAttribute("aria-expanded", String(source.id === selectedId)); name.setAttribute("aria-controls", "dtSourceDetail");
      copy.append(name, node("div", "dt-file-meta", size(source.size) + (source.path !== source.name ? " · " + source.path : "")));
      const remove = node("button", "dt-remove-source", "×");
      remove.type = "button"; remove.disabled = state.busy; remove.dataset.sourceRemove = source.id;
      remove.setAttribute("aria-label", "Убрать " + source.path);
      row.append(check, icon, copy, node("span", "dt-file-state", sourceState(source)), remove);
      return row;
    }
    function renderSources() {
      const sources = state.sources, matches = matchingSources();
      pageIndex = Math.min(pageIndex, Math.max(0, Math.ceil(matches.length / pageSize) - 1));
      const visible = matches.slice(pageIndex * pageSize, (pageIndex + 1) * pageSize), available = visible.filter(source => source.available);
      if (!visible.some(source => source.id === selectedId)) selectedId = null;
      el("SourceQueue").hidden = sources.length === 0;
      const populated = sources.length > 0 || state.hasExternalSources;
      el("SourceHeading").textContent = populated ? "Источники" : "Начните с источников";
      el("SourceIntro").textContent = state.hasExternalSources ? "Выбранные файлы и состояние подготовки."
        : sources.length ? "Отметьте файлы для дальнейшей обработки." : "Добавьте файлы, которые хотите привести к общей структуре.";
      el("IntakeGuide").hidden = populated; el("Dropzone").parentElement.dataset.populated = String(populated);
      el("FileCount").textContent = String(sources.length);
      const included = sources.filter(source => source.included), largest = included.reduce((max, source) => Math.max(max, source.size), 0);
      el("SelectionCount").textContent = "Выбрано: " + included.length + " из " + sources.length + " · " + size(included.reduce((sum, source) => sum + source.size, 0));
      el("SourceLoadNote").hidden = largest < 64 * 1024 * 1024;
      el("SourceLoadNote").textContent = "Крупный источник: " + size(largest) + ". Расход памяти при разборе может быть выше размера файла. Обработка крупных книг ещё не квалифицирована.";
      el("NoMatches").hidden = visible.length > 0;
      el("SelectVisible").checked = available.length > 0 && available.every(source => source.included);
      el("SelectVisible").indeterminate = available.some(source => source.included) && !el("SelectVisible").checked;
      el("SourceList").replaceChildren(...visible.map(sourceRow));
      renderPagination(matches.length);
      renderDetail();
      ["ChooseFiles", "ChooseFolder", "Files", "Folder", "ClearSources"].forEach(id => { el(id).disabled = state.busy; });
      el("SelectVisible").disabled = state.busy || !available.length;
      el("ChooseFolder").hidden = !("webkitdirectory" in el("Folder"));
    }
    function renderNotice(notice) {
      el("IntakeStatus").textContent = notice.text;
      el("IntakeStatus").dataset.error = String(notice.error);
      el("IntakeStatus").setAttribute("role", notice.error ? "alert" : "status");
    }
    function renderActivity(activity) {
      if (!activity) { operationView.render(null); el("Dropzone").setAttribute("aria-busy", "false"); return; }
      const progress = activity.progress;
      const labels = { running: "Получаем список файлов", completed: "Подбор завершён", cancelled: "Добавление отменено", failed: "Добавление не завершено" };
      el("Dropzone").setAttribute("aria-busy", String(activity.state === "running"));
      operationView.render({ state: activity.state, stage: "Подбор источников", title: labels[activity.state],
        currentItem: progress.currentPath, elapsedMs: activity.elapsedMs,
        measure: { completed: progress.completedFiles, total: progress.totalFiles, label: "Найдено файлов" },
        detail: "Размер файлов: " + size(progress.sourceBytes), note: "Содержимое не читается. Это объём источников, а не расход памяти." });
    }
    function resetSelection() {
      selectedId = null; pageIndex = 0;
      ["SourceSearch", "SourceFilter", "SourceState"].forEach(id => { el(id).value = ""; });
      el("SourceSort").value = "added";
    }
    function changePage(index) {
      pageIndex = index; selectedId = null; renderSources(); el("SourceList").scrollTop = 0;
    }

    el("FormatLabels").replaceChildren(); el("FormatGuide").replaceChildren();
    const allFormats = node("option", "", "Все форматы"); allFormats.value = ""; el("SourceFilter").replaceChildren(allFormats);
    formats.forEach(format => {
      el("FormatLabels").append(node("span", "", format.id.toUpperCase()));
      const option = node("option", "", format.label); option.value = format.id; el("SourceFilter").append(option);
      const card = node("article", "dt-format-card dt-card");
      card.append(node("small", "", format.extension.toUpperCase()), node("h4", "", format.shape), node("p", "", format.description));
      el("FormatGuide").append(card);
    });
    const unsupported = node("option", "", "Другие форматы"); unsupported.value = "unsupported"; el("SourceFilter").append(unsupported);
    el("Files").accept = formats.map(format => format.extension).join(",");
    tabs.forEach(tab => {
      listen(tab, "click", () => show(tab.dataset.dtView, false));
      listen(tab, "keydown", event => {
        const index = tabs.indexOf(tab);
        const next = event.key === "Home" ? 0 : event.key === "End" ? tabs.length - 1
          : event.key === "ArrowRight" ? (index + 1) % tabs.length : event.key === "ArrowLeft" ? (index + tabs.length - 1) % tabs.length : -1;
        if (next < 0) return;
        event.preventDefault(); tabs[next].focus(); show(tabs[next].dataset.dtView, false);
      });
    });
    listen(el("ViewSelect"), "change", () => show(el("ViewSelect").value, false));
    container.querySelectorAll("[data-dt-go]").forEach(button => listen(button, "click", () => show(button.dataset.dtGo, true)));
    ["Files", "Folder"].forEach(kind => {
      listen(el("Choose" + kind), "click", () => el(kind).click());
      listen(el(kind), "change", () => { if (el(kind).files.length) actions.files(el(kind).files); el(kind).value = ""; });
    });
    const fileDrag = event => event.dataTransfer && Array.from(event.dataTransfer.types).includes("Files");
    const surface = container.closest("#tab-workspace") || container;
    listen(surface, "dragenter", event => { if (fileDrag(event)) { event.preventDefault(); dragDepth++; el("Dropzone").dataset.dragging = "true"; } });
    listen(surface, "dragleave", () => { if (--dragDepth <= 0) { dragDepth = 0; el("Dropzone").dataset.dragging = "false"; } });
    listen(surface, "dragover", event => { if (fileDrag(event)) { event.preventDefault(); event.dataTransfer.dropEffect = state.busy ? "none" : "copy"; } });
    listen(surface, "drop", event => {
      if (!fileDrag(event)) return;
      event.preventDefault(); dragDepth = 0; el("Dropzone").dataset.dragging = "false"; actions.drop(event.dataTransfer);
    });
    listen(el("ClearSources"), "click", () => { actions.clear(); el("ChooseFiles").focus(); });
    const filterChanged = () => { pageIndex = 0; renderSources(); el("SourceList").scrollTop = 0; };
    listen(el("SourceSearch"), "input", filterChanged);
    ["SourceFilter", "SourceState", "SourceSort"].forEach(id => listen(el(id), "change", filterChanged));
    listen(el("PreviousPage"), "click", () => changePage(pageIndex - 1));
    listen(el("NextPage"), "click", () => changePage(pageIndex + 1));
    listen(el("SourcePage"), "change", () => changePage(Number(el("SourcePage").value)));
    listen(el("SelectVisible"), "change", () => actions.include(visibleSources().filter(source => source.available).map(source => source.id), el("SelectVisible").checked));
    listen(el("SourceList"), "change", event => {
      const id = event.target.dataset.sourceToggle;
      if (!id) return;
      const index = visibleSources().findIndex(source => source.id === id);
      actions.include([id], event.target.checked);
      const checks = el("SourceList").querySelectorAll("[data-source-toggle]:not(:disabled)");
      (el("SourceList").querySelector('[data-source-toggle="' + id + '"]') || checks[Math.min(index, checks.length - 1)] || el("SourceState")).focus({ preventScroll: true });
    });
    listen(el("SourceList"), "click", event => {
      const target = event.target.closest("button");
      if (!target) return;
      if (target.dataset.sourceDetail) {
        selectedId = selectedId === target.dataset.sourceDetail ? null : target.dataset.sourceDetail; renderSources();
        el("SourceList").querySelector('[data-source-detail="' + target.dataset.sourceDetail + '"]').focus({ preventScroll: true });
        if (selectedId) el("SourceDetail").scrollIntoView({ block: "nearest" });
      } else if (target.dataset.sourceRemove && !state.busy) {
        const index = visibleSources().findIndex(item => item.id === target.dataset.sourceRemove);
        actions.remove(target.dataset.sourceRemove);
        const buttons = el("SourceList").querySelectorAll("[data-source-remove]");
        (buttons[Math.min(index, buttons.length - 1)] || el("ChooseFiles")).focus({ preventScroll: true });
      }
    });
    listen(el("CloseDetail"), "click", () => {
      const previous = selectedId; selectedId = null; renderSources();
      (el("SourceList").querySelector('[data-source-detail="' + previous + '"]') || el("ChooseFiles")).focus({ preventScroll: true });
    });
    show(el("ViewSelect").value || "sources", false);
    return Object.freeze({
      render: snapshot => { state = snapshot; renderSources(); renderNotice(snapshot.notice); renderActivity(snapshot.activity); },
      renderNotice: renderNotice, renderActivity: renderActivity, resetSelection: resetSelection,
      showSources: () => show("sources", false),
      dispose: () => { events.abort(); operationView.dispose(); el("Dropzone").dataset.dragging = "false"; }
    });
  }
  root.DataTemplatesLoaderView = Object.freeze({ create: create });
}(window));
