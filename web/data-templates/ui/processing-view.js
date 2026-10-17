(function (root) {
  "use strict";
  const pageSize = 50;
  const node = (tag, className, value) => {
    const element = document.createElement(tag);
    if (className) element.className = className;
    if (value !== undefined) element.textContent = value;
    return element;
  };
  const stateLabel = { queued: "В очереди", running: "Выполняется", completed: "Готово", failed: "Ошибка", cancelled: "Отменено" };
  const displayValue = record => record.value.kind === "decimal" ? record.value.value
    : record.value.kind === "missing" ? "— " + record.value.reason : JSON.stringify(record.value);
  const displayPeriod = record => record.period ? Object.values(record.period).join(" · ") : "—";
  const displayCell = record => {
    const evidence = record.provenance && record.provenance.value && record.provenance.value.evidence;
    return evidence && evidence[0] && evidence[0].location ? evidence[0].location.address : "—";
  };
  function create(container, actions) {
    const templates = container.querySelector("#dtViewTemplates"), results = container.querySelector("#dtViewResults");
    templates.querySelector(".dt-empty-state").remove();
    results.querySelector(".dt-empty-state").remove();
    templates.querySelector(".dt-phase").textContent = "Рабочий поток";
    results.querySelector(".dt-phase").textContent = "Результаты запуска";
    templates.insertAdjacentHTML("beforeend", `<div class="dt-process-intro dt-card" id="dtTemplateDrop" role="region" aria-label="Выбор или перетаскивание JSON-шаблона"><div><p class="dt-eyebrow">01 / ШАБЛОН</p><h4>Выберите правила извлечения</h4><p>JSON-шаблон описывает точные листы, области и правила. Для начала доступно чтение XLSX; выбранные CSV и JSON будут показаны в очереди с явной ошибкой формата.</p></div><div class="dt-template-picker"><input type="file" id="dtTemplateFile" accept=".json,application/json" hidden><button type="button" id="dtChooseTemplate">Выбрать JSON-шаблон</button><span id="dtTemplateName">Шаблон не выбран</span><small>Можно перетащить сюда .json</small></div></div>
      <div class="dt-process-start dt-card"><div><p class="dt-eyebrow">02 / ИСТОЧНИКИ</p><h4 id="dtProcessSelection">Источники не выбраны</h4><p>Очередь запускается по выбранным файлам на вкладке «Источники». По одному файлу в Worker, без чтения книг в интерфейсе.</p></div><div class="dt-process-actions"><button type="button" id="dtProcessSources">К источникам</button><button type="button" class="dt-primary" id="dtStartProcessing">Запустить обработку</button></div></div>
      <p class="dt-process-note" id="dtProcessNotice" role="status" aria-live="polite"></p>
      <p class="dt-footnote">Пример: <a href="core/examples/matrix-xlsx-extraction.json" download>скачать JSON-шаблон</a> и добавить <a href="core/examples/matrix-source.xlsx" download>пример XLSX</a>.</p><p class="dt-footnote" id="dtStorageNote"></p>`);
    results.insertAdjacentHTML("beforeend", `<div class="dt-process-summary"><div class="dt-card"><small>ФАЙЛОВ</small><strong id="dtResultTotal">0</strong></div><div class="dt-card"><small>ГОТОВО</small><strong id="dtResultCompleted">0</strong></div><div class="dt-card"><small>ОШИБКИ</small><strong id="dtResultFailed">0</strong></div><div class="dt-card"><small>ЗАПИСЕЙ</small><strong id="dtResultRecords">0</strong></div></div>
      <div class="dt-operation dt-card" id="dtProcessOperation" role="region" aria-label="Обработка файлов" hidden><div class="dt-operation-head"><div><span class="dt-operation-stage" data-operation-part="stage"></span><h4 data-operation-part="title"></h4></div><div class="dt-operation-actions"><span data-operation-part="elapsed"></span><button type="button" data-operation-part="cancel">Отменить</button><button type="button" class="dt-text-button" data-operation-part="dismiss" hidden>Скрыть</button></div></div><div class="dt-operation-meter" data-operation-part="meter"><progress data-operation-part="progress" aria-label="Файлов завершено"></progress><span data-operation-part="percent"></span></div><p class="dt-operation-current" data-operation-part="current"></p><div class="dt-operation-metrics"><span data-operation-part="count"></span><span data-operation-part="detail"></span></div><p class="dt-operation-note" data-operation-part="note"></p></div>
      <p class="dt-process-note" id="dtResultNotice" role="status" aria-live="polite"></p>
      <div class="dt-results-layout"><section class="dt-card dt-run-queue" aria-label="Очередь обработки"><div class="dt-run-heading"><div><p class="dt-eyebrow">ФАЙЛЫ</p><h4>Очередь обработки</h4></div><span id="dtRunRange"></span></div><div class="dt-run-filters"><input type="search" id="dtRunSearch" placeholder="Найти файл…" aria-label="Поиск в очереди"><select id="dtRunFilter" aria-label="Состояние файла"><option value="">Все</option><option value="queued">В очереди</option><option value="running">Выполняется</option><option value="completed">Готово</option><option value="failed">Ошибка</option><option value="cancelled">Отменено</option></select></div><ul id="dtRunList" class="dt-run-list"></ul><p id="dtRunEmpty" class="dt-process-empty">Очередь пуста. Выберите шаблон и запустите обработку.</p><div class="dt-run-paging" id="dtRunPaging" hidden><button type="button" id="dtRunPrevious">← Назад</button><span id="dtRunPage"></span><button type="button" id="dtRunNext">Далее →</button></div></section>
      <section class="dt-card dt-result-detail" aria-label="Просмотр результата"><div class="dt-run-heading"><div><p class="dt-eyebrow">ПРОСМОТР</p><h4 id="dtResultTitle">Выберите файл</h4></div><button type="button" class="dt-primary" id="dtDownloadResult" hidden>Скачать полный JSON</button></div><p id="dtResultDescription" class="dt-result-description">После обработки выберите файл из очереди. Здесь появятся записи и диагностика.</p><div id="dtResultPreview" hidden><div class="dt-result-meta" id="dtResultMeta"></div><p class="dt-footnote">Показаны первые 20 записей. Экспорт успешного файла содержит все его записи и происхождение.</p><div class="dt-preview-scroll"><table class="dt-preview-table"><thead><tr><th>Параметр</th><th>Период</th><th>Значение</th><th>Ячейка</th></tr></thead><tbody id="dtPreviewRows"></tbody></table></div><details class="dt-result-raw"><summary>Диагностика и полный пример записи</summary><pre id="dtResultRaw"></pre></details></div></section></div>`);
    const get = id => container.querySelector("#dt" + id), events = new AbortController();
    const listen = (element, type, handler) => element.addEventListener(type, handler, { signal: events.signal });
    const operation = root.DataTemplatesOperationView.create(get("ProcessOperation"), { cancel: actions.cancel, dismiss: actions.dismiss });
    const templateView = root.DataTemplatesTemplateInspectionView.create(container);
    let current = null, query = "", filter = "", page = 0;
    listen(get("ChooseTemplate"), "click", () => get("TemplateFile").click());
    listen(get("TemplateFile"), "change", () => { actions.chooseTemplate(get("TemplateFile").files[0] || null); get("TemplateFile").value = ""; });
    ["dragenter", "dragover"].forEach(type => listen(get("TemplateDrop"), type, event => {
      if (!event.dataTransfer || !Array.from(event.dataTransfer.types).includes("Files")) return;
      event.preventDefault(); event.stopPropagation();
      event.dataTransfer.dropEffect = current && current.running ? "none" : "copy";
      get("TemplateDrop").dataset.dragging = "true";
    }));
    listen(get("TemplateDrop"), "dragleave", event => { event.stopPropagation(); get("TemplateDrop").dataset.dragging = "false"; });
    listen(get("TemplateDrop"), "drop", event => {
      event.preventDefault(); event.stopPropagation(); get("TemplateDrop").dataset.dragging = "false";
      if (current && !current.running) actions.chooseTemplate(event.dataTransfer.files[0] || null);
    });
    listen(get("ProcessSources"), "click", () => container.querySelector("#dtTabSources").click());
    listen(get("StartProcessing"), "click", actions.start);
    listen(get("RunSearch"), "input", () => { query = get("RunSearch").value.trim().toLocaleLowerCase(); page = 0; if (current) renderQueue(current); });
    listen(get("RunFilter"), "change", () => { filter = get("RunFilter").value; page = 0; if (current) renderQueue(current); });
    listen(get("RunPrevious"), "click", () => { page--; renderQueue(current); });
    listen(get("RunNext"), "click", () => { page++; renderQueue(current); });
    listen(get("RunList"), "click", event => {
      const button = event.target.closest("button[data-run-id]");
      if (button) actions.select(button.dataset.runId);
    });
    listen(get("DownloadResult"), "click", async () => {
      if (!current) return;
      const button = get("DownloadResult"); button.disabled = true;
      try { await actions.download(current.selectedId); } finally { button.disabled = false; }
    });
    function renderQueue(snapshot) {
      const list = get("RunList"), focused = list.contains(document.activeElement) ? document.activeElement.dataset.runId : null;
      const matching = snapshot.queue.filter(item => (!filter || item.status === filter) &&
        (!query || item.path.toLocaleLowerCase().includes(query)));
      page = Math.min(page, Math.max(0, Math.ceil(matching.length / pageSize) - 1));
      const visible = matching.slice(page * pageSize, (page + 1) * pageSize);
      list.replaceChildren(...visible.map(item => {
        const row = node("li", "dt-run-row"), button = node("button", "", "");
        button.type = "button"; button.dataset.runId = item.id;
        button.setAttribute("aria-current", String(item.id === snapshot.selectedId));
        button.append(node("span", "dt-run-path", item.path), node("span", "dt-run-status", stateLabel[item.status]));
        const detail = node("small", "", item.status === "completed" ? item.recordCount + " записей"
          : item.status === "failed" ? item.error : item.progress);
        row.dataset.state = item.status; row.append(button, detail); return row;
      }));
      if (focused) {
        const replacement = Array.from(list.querySelectorAll("button[data-run-id]")).find(button => button.dataset.runId === focused);
        if (replacement) replacement.focus({ preventScroll: true });
      }
      get("RunEmpty").hidden = matching.length > 0;
      get("RunEmpty").textContent = snapshot.queue.length ? "Совпадений нет. Измените фильтр." : "Очередь пуста. Выберите шаблон и запустите обработку.";
      get("RunRange").textContent = matching.length ? page * pageSize + 1 + "–" + Math.min((page + 1) * pageSize, matching.length) + " из " + matching.length : "";
      get("RunPaging").hidden = matching.length <= pageSize;
      get("RunPage").textContent = page + 1 + " / " + Math.ceil(matching.length / pageSize);
      get("RunPrevious").disabled = page === 0; get("RunNext").disabled = (page + 1) * pageSize >= matching.length;
    }
    function renderDetail(snapshot) {
      const selected = snapshot.selected;
      get("ResultTitle").textContent = selected ? selected.path : "Выберите файл";
      get("DownloadResult").hidden = !selected || selected.status !== "completed";
      get("ResultPreview").hidden = !selected || selected.status !== "completed";
      get("ResultDescription").textContent = !selected ? "После обработки выберите файл из очереди. Здесь появятся записи и диагностика."
        : selected.status === "completed" ? "Извлечение завершено. Результат временный, публикация проекта не выполнялась."
          : selected.error || (selected.status === "running" ? selected.progress : "Файл ожидает обработки.");
      if (!selected || selected.status !== "completed") return;
      get("ResultMeta").replaceChildren(
        node("span", "", "Записей: " + selected.summary.recordCount),
        node("span", "", "Пропусков: " + selected.summary.missingValueCount),
        node("span", "", "Объём записей: до " + Math.ceil(selected.bytes / 1024) + " КиБ"),
        node("span", "", selected.mode === "disk" ? "Временное хранилище браузера" : "В памяти вкладки"));
      get("PreviewRows").replaceChildren(...selected.preview.map(record => {
        const row = node("tr");
        [record.parameterId, displayPeriod(record), displayValue(record), displayCell(record)].forEach(value => row.append(node("td", "", value)));
        return row;
      }));
      get("ResultRaw").textContent = JSON.stringify({ summary: selected.summary, example: selected.preview[0] || null }, null, 2);
    }
    function render(snapshot) {
      current = snapshot;
      get("TemplateName").textContent = snapshot.templateLoading ? "Проверяем шаблон…" : snapshot.templateName || "Шаблон не выбран";
      templateView.render(snapshot.templateInspection);
      get("ProcessSelection").textContent = snapshot.selectedCount ? "Выбрано " + snapshot.selectedCount + " файлов · XLSX: " + snapshot.selectedXlsx
        : "Источники не выбраны";
      get("ChooseTemplate").disabled = snapshot.running;
      get("StartProcessing").disabled = snapshot.running || snapshot.sourceBusy || snapshot.templateLoading || !snapshot.templateName || !snapshot.selectedCount;
      get("ProcessNotice").textContent = snapshot.templateNotice;
      get("StorageNote").textContent = snapshot.storageMode === "disk"
        ? "Результаты записываются порциями во временное хранилище браузера: до 256 МиБ на файл и 1 ГиБ за запуск. Квота браузера может быть меньше. Скачайте нужное до закрытия вкладки."
        : "Файловое хранилище браузера недоступно. Результат хранится в памяти вкладки, до 8 МиБ на файл.";
      get("ResultNotice").textContent = snapshot.runNotice;
      ["Total", "Completed", "Failed", "Records"].forEach((part, index) => {
        get("Result" + part).textContent = String([snapshot.totals.total, snapshot.totals.completed, snapshot.totals.failed, snapshot.totals.records][index]);
      });
      operation.render(snapshot.activity);
      renderQueue(snapshot); renderDetail(snapshot);
    }
    return Object.freeze({ render: render, showResults: () => container.querySelector("#dtTabResults").click(),
      dispose: () => { events.abort(); operation.dispose(); templateView.dispose(); } });
  }
  root.DataTemplatesProcessingView = Object.freeze({ create: create });
}(window));
