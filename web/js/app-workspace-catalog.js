(function (root) {
  "use strict";
  // Read-only native projection of the selected project's typed catalog commands.
  const templateHead = /^template-([A-Za-z][A-Za-z0-9_.-]{0,127})-\d{13}-[0-9a-f]{32}\.(json|removed)$/;
  const dictionaryHead = /^dictionary-\d{13}-[0-9a-f]{32}\.json$/;
  const pageSize = 50;
  let container = null, events = null, context = null, key = "", epoch = 0, pending = false,
    needsRefresh = false, refreshScheduled = false, catalog = null, dictionary = null, opened = null,
    listBusy = false, dictionaryBusy = false, openBusy = false,
    listError = "", dictionaryError = "", openError = "", parameterPage = 0, mappingPage = 0;

  const get = id => container && container.querySelector("#dtNative" + id);
  const natural = value => Number.isSafeInteger(value) && value >= 0;
  const string = value => typeof value === "string" ? value : "—";
  const identity = state => state && state.workspace && state.project
    ? [state.workspace.bindingId, state.project.manifestPath, state.revision].join("\n") : "";
  function invalid() { return new Error("Ответ каталога не удалось проверить. Обновите проект."); }
  function request() {
    if (!context || !context.workspace || !context.project || !natural(context.revision)) return null;
    return { bindingId: context.workspace.bindingId, manifestPath: context.project.manifestPath,
      projectRevision: context.revision };
  }
  function validState(state, expected) {
    return state && state.type === "rnassistant.workspaceState" && state.contractVersion === 1 &&
      state.revision === expected.projectRevision && state.workspace &&
      state.workspace.bindingId === expected.bindingId && state.project &&
      state.project.manifestPath === expected.manifestPath;
  }
  function validHead(item) {
    const match = item && typeof item.headFile === "string" && templateHead.exec(item.headFile);
    return !!match && match[1] === item.id && typeof item.archived === "boolean" &&
      natural(item.savedAt) && natural(item.versions) && item.versions <= 1000;
  }
  function validFile(file, kind) {
    if (!file || typeof file.input !== "string") return false;
    const length = new TextEncoder().encode(file.input).length;
    return typeof file.file === "string" &&
      (kind === "dictionary" ? dictionaryHead.test(file.file) : templateHead.test(file.file) && file.file.endsWith(".json")) &&
      file.format === kind && /^[0-9a-f]{64}$/i.test(file.sha256) &&
      length > 0 && length <= 1000000;
  }
  function note(id, value, failed) {
    const element = get(id);
    element.textContent = value || "";
    element.dataset.error = failed ? "true" : "false";
    element.setAttribute("role", failed ? "alert" : "status");
  }
  function row(tag, values) {
    const element = document.createElement(tag);
    values.forEach(value => {
      const cell = document.createElement(tag === "tr" ? "td" : "th");
      cell.textContent = value;
      element.append(cell);
    });
    return element;
  }
  function renderTable(kind) {
    const prefix = kind === "parameters" ? "Parameters" : "Mapping";
    const body = get(prefix + "Rows"), previous = get(prefix + "Previous"), next = get(prefix + "Next");
    const count = get(prefix + "Count"), query = get(prefix + "Search").value.trim().toLocaleLowerCase("ru");
    const value = dictionary && dictionary.value;
    const source = !value ? [] : kind === "parameters"
      ? value.parameters.map(item => ["Параметр", string(item && item.id), string(item && item.name), string(item && item.unitId)])
        .concat(value.units.map(item => ["Единица", string(item && item.id), string(item && item.name), "—"]))
      : value.parameterMappings.map(item => ["Параметр", string(item && item.scope), string(item && item.source), string(item && item.targetId)])
        .concat(value.unitMappings.map(item => ["Единица", string(item && item.scope), string(item && item.source), string(item && item.targetId)]));
    const matches = source.filter(values => !query || values.some(value => value.toLocaleLowerCase("ru").includes(query)));
    let page = kind === "parameters" ? parameterPage : mappingPage;
    page = Math.min(page, Math.max(0, Math.ceil(matches.length / pageSize) - 1));
    if (kind === "parameters") parameterPage = page; else mappingPage = page;
    body.replaceChildren(...matches.slice(page * pageSize, (page + 1) * pageSize).map(values => row("tr", values)));
    count.textContent = value ? matches.length + " из " + source.length : "";
    previous.disabled = page === 0 || !value;
    next.disabled = !value || (page + 1) * pageSize >= matches.length;
    get(prefix + "Page").textContent = matches.length ? (page + 1) + " / " + Math.ceil(matches.length / pageSize) : "0 / 0";
    get(prefix + "Empty").hidden = !!matches.length || !value;
  }
  function render() {
    if (!container) return;
    const project = context && context.project;
    get("CatalogRefresh").disabled = !project || pending || listBusy || openBusy;
    get("DictionaryRefresh").disabled = !project || pending || dictionaryBusy;
    note("CatalogStatus", !project ? "Откройте проект, чтобы посмотреть его версии." :
      pending ? "Ожидаем завершения операции проекта…" :
      listBusy ? "Читаем каталог проекта…" : listError || (catalog ? catalog.total + " шаблонов в каталоге." : ""), !!listError);
    note("DictionaryStatus", !project ? "Откройте проект, чтобы посмотреть общий словарь." :
      pending ? "Ожидаем завершения операции проекта…" :
      dictionaryBusy ? "Читаем общий словарь…" : dictionaryError ||
        (dictionary ? "Версия " + dictionary.value.revision + " · " + dictionary.file.file : "Общий словарь пока не создан."), !!dictionaryError);
    const list = get("CatalogList");
    list.replaceChildren(...(catalog ? catalog.items.map(item => {
      const entry = document.createElement("li"), label = document.createElement("span"), button = document.createElement("button");
      label.textContent = item.id + " · " + (item.archived ? "Архив" : "Активен") + " · версий: " + item.versions;
      label.title = item.headFile;
      button.type = "button"; button.textContent = "Просмотр"; button.dataset.id = item.id;
      button.dataset.headFile = item.headFile; button.disabled = openBusy || !item.versions;
      entry.append(label, button); return entry;
    }) : []));
    if (catalog && !catalog.items.length) {
      const empty = document.createElement("li"); empty.textContent = catalog.total ? "Больше версий нет." : "Версий пока нет.";
      list.append(empty);
    }
    get("CatalogPrevious").disabled = !catalog || listBusy || catalog.offset === 0;
    get("CatalogNext").disabled = !catalog || listBusy || catalog.offset + catalog.items.length >= catalog.total;
    get("CatalogRange").textContent = catalog && catalog.total
      ? (catalog.offset + 1) + "–" + (catalog.offset + catalog.items.length) + " из " + catalog.total : "";
    const preview = get("CatalogPreview"); preview.hidden = !opened && !openError && !openBusy;
    note("CatalogOpenStatus", openBusy ? "Открываем версию…" : openError || "", !!openError);
    get("CatalogPreviewTitle").textContent = opened ? opened.id + " · " + opened.template.file : "";
    get("CatalogPreviewMeta").textContent = opened ?
      (opened.template.format === "legacy" ? "Старая версия со встроенным словарём" : "Версия проекта") +
      " · Просмотр исходного JSON без проверки правил" : "";
    get("CatalogCode").textContent = opened ? opened.template.input : "";
    get("DictionaryRaw").textContent = dictionary ? dictionary.file.input : "";
    get("DictionaryDetails").hidden = !dictionary;
    get("ParametersSearch").disabled = get("MappingSearch").disabled = !dictionary;
    renderTable("parameters"); renderTable("mapping");
  }
  async function refreshTemplates(offset) {
    const selected = request(); if (!selected || pending || listBusy || openBusy) return;
    const current = epoch; listBusy = true; listError = ""; openError = ""; catalog = null; opened = null; render();
    try {
      const response = await root.send("listTabularProjectTemplates", { ...selected, offset });
      if (current !== epoch) return;
      if (!validState(response && response.state, selected) || response.offset !== offset ||
          !natural(response.total) || response.total > 1000 || !Array.isArray(response.items) ||
          response.items.length > pageSize || response.items.length !== Math.min(pageSize, Math.max(0, response.total - offset)) ||
          !response.items.every(validHead) || new Set(response.items.map(item => item.id)).size !== response.items.length) throw invalid();
      catalog = response;
    } catch (failure) {
      if (current === epoch) listError = failure.message || "Не удалось прочитать каталог.";
    } finally { if (current === epoch) { listBusy = false; render(); } }
  }
  async function refreshDictionary() {
    const selected = request(); if (!selected || pending || dictionaryBusy) return;
    const current = epoch; dictionaryBusy = true; dictionaryError = ""; dictionary = null; render();
    try {
      const response = await root.send("readTabularProjectDictionary", selected);
      if (current !== epoch) return;
      if (!validState(response && response.state, selected) ||
          response.dictionary !== null && !validFile(response.dictionary, "dictionary")) throw invalid();
      if (response.dictionary) {
        const value = JSON.parse(response.dictionary.input);
        if (!value || value.version !== 1 || !natural(value.revision) || value.revision < 1 ||
            !["parameters", "units", "parameterMappings", "unitMappings"].every(name => Array.isArray(value[name]))) throw invalid();
        dictionary = { file: response.dictionary, value };
      }
    } catch (failure) {
      if (current === epoch) dictionaryError = failure.message || "Не удалось прочитать общий словарь.";
    } finally { if (current === epoch) { dictionaryBusy = false; render(); } }
  }
  async function openTemplate(id, headFile) {
    const selected = request();
    if (!selected || pending || openBusy || !catalog ||
        !catalog.items.some(item => item.id === id && item.headFile === headFile && item.versions)) return;
    const current = epoch; openBusy = true; opened = null; openError = ""; render();
    try {
      const response = await root.send("openTabularProjectTemplate", { ...selected, id, headFile });
      if (current !== epoch) return;
      if (!validState(response && response.state, selected) || response.id !== id ||
          response.headFile !== headFile || !["project", "legacy"].includes(response.template && response.template.format) ||
          templateHead.exec(response.template.file)?.[1] !== id ||
          !validFile(response.template, response.template.format)) throw invalid();
      opened = response;
    } catch (failure) {
      if (current === epoch) openError = failure.message || "Не удалось открыть версию.";
    } finally { if (current === epoch) { openBusy = false; render(); } }
  }
  function setContext(state, blocked) {
    const next = identity(state);
    if (next !== key) {
      key = next; epoch++; catalog = null; dictionary = null; opened = null;
      listError = dictionaryError = openError = ""; needsRefresh = !!next;
      listBusy = dictionaryBusy = openBusy = false;
      parameterPage = mappingPage = 0;
    }
    context = state; pending = !!blocked; render();
    scheduleRefresh();
  }
  function scheduleRefresh() {
    if (!needsRefresh || pending || !container || refreshScheduled) return;
    refreshScheduled = true;
    const selected = key;
    Promise.resolve().then(async () => {
      refreshScheduled = false;
      if (!needsRefresh || pending || !container || key !== selected) return;
      needsRefresh = false;
      await refreshTemplates(0);
      if (key !== selected) return;
      if (pending) needsRefresh = true;
      else await refreshDictionary();
    });
  }
  function bind(target) {
    if (container) return;
    container = target;
    const templates = target.querySelector("#dtViewTemplates"), parameters = target.querySelector("#dtViewParameters"),
      mapping = target.querySelector("#dtViewMapping");
    if (!target.querySelector("#dtNativeCatalog")) {
      templates.querySelector(".dt-section-heading p").textContent =
        "Версии проекта и отдельные черновики. Правила проверяются в автономном приложении.";
      templates.querySelector(".dt-phase").textContent = "Каталог и черновики";
      templates.querySelector(".dt-section-heading").insertAdjacentHTML("afterend", `<section class="dt-card dt-native-catalog" id="dtNativeCatalog" aria-label="Каталог шаблонов проекта"><div class="dt-native-catalog-head"><div><p class="dt-eyebrow">ШАБЛОНЫ ПРОЕКТА</p><h4>Каталог шаблонов</h4><p>Чтение сохранённых версий без редактирования и проверки правил.</p></div><button type="button" id="dtNativeCatalogRefresh">Обновить</button></div><p id="dtNativeCatalogStatus" role="status"></p><ul id="dtNativeCatalogList"></ul><div class="dt-native-catalog-paging"><button type="button" id="dtNativeCatalogPrevious">← Назад</button><span id="dtNativeCatalogRange"></span><button type="button" id="dtNativeCatalogNext">Далее →</button></div><section id="dtNativeCatalogPreview" hidden><h4 id="dtNativeCatalogPreviewTitle"></h4><p id="dtNativeCatalogPreviewMeta"></p><p id="dtNativeCatalogOpenStatus" role="status"></p><pre id="dtNativeCatalogCode"></pre></section></section>`);
      parameters.querySelector(".dt-empty-state").remove();
      mapping.querySelector(".dt-mapping-guide").remove();
      mapping.querySelector(".dt-explanation").remove();
      parameters.querySelector(".dt-phase").textContent = mapping.querySelector(".dt-phase").textContent = "Только просмотр";
      parameters.querySelector(".dt-section-heading p").textContent =
        "Параметры и единицы выбранного проекта. Семантика правил здесь не проверяется.";
      mapping.querySelector(".dt-section-heading p").textContent =
        "Области и значения общего словаря проекта. Семантика правил здесь не проверяется.";
      parameters.querySelector(".dt-section-heading").insertAdjacentHTML("afterend", `<section class="dt-card dt-native-dictionary" aria-label="Общий словарь проекта"><div class="dt-native-catalog-head"><div><p class="dt-eyebrow">ОБЩИЙ СЛОВАРЬ</p><h4>Параметры и единицы</h4></div><button type="button" id="dtNativeDictionaryRefresh">Обновить</button></div><p id="dtNativeDictionaryStatus" role="status"></p><input type="search" id="dtNativeParametersSearch" placeholder="Найти параметр или единицу…" aria-label="Поиск по параметрам и единицам"><p id="dtNativeParametersCount"></p><div class="dt-native-table-wrap"><table><thead><tr><th>Тип</th><th>Код</th><th>Название</th><th>Единица</th></tr></thead><tbody id="dtNativeParametersRows"></tbody></table></div><p id="dtNativeParametersEmpty" hidden>Записей пока нет.</p><div class="dt-native-catalog-paging"><button type="button" id="dtNativeParametersPrevious">← Назад</button><span id="dtNativeParametersPage"></span><button type="button" id="dtNativeParametersNext">Далее →</button></div><details id="dtNativeDictionaryDetails" hidden><summary>Исходный JSON словаря</summary><pre id="dtNativeDictionaryRaw"></pre></details></section>`);
      mapping.querySelector(".dt-section-heading").insertAdjacentHTML("afterend", `<section class="dt-card dt-native-dictionary" aria-label="Соответствия общего словаря"><p id="dtNativeMappingNotice">Соответствия читаются из того же общего словаря проекта. Изменения выполняются в автономном приложении.</p><input type="search" id="dtNativeMappingSearch" placeholder="Найти соответствие…" aria-label="Поиск по соответствиям"><p id="dtNativeMappingCount"></p><div class="dt-native-table-wrap"><table><thead><tr><th>Тип</th><th>Область</th><th>Источник</th><th>Цель</th></tr></thead><tbody id="dtNativeMappingRows"></tbody></table></div><p id="dtNativeMappingEmpty" hidden>Соответствий пока нет.</p><div class="dt-native-catalog-paging"><button type="button" id="dtNativeMappingPrevious">← Назад</button><span id="dtNativeMappingPage"></span><button type="button" id="dtNativeMappingNext">Далее →</button></div></section>`);
    }
    events = new AbortController();
    const signal = events.signal;
    get("CatalogRefresh").addEventListener("click", () => refreshTemplates(0), { signal });
    get("CatalogPrevious").addEventListener("click", () => refreshTemplates(Math.max(0, catalog.offset - pageSize)), { signal });
    get("CatalogNext").addEventListener("click", () => refreshTemplates(catalog.offset + pageSize), { signal });
    get("CatalogList").addEventListener("click", event => {
      const button = event.target.closest("button[data-head-file]");
      if (button) openTemplate(button.dataset.id, button.dataset.headFile);
    }, { signal });
    get("DictionaryRefresh").addEventListener("click", refreshDictionary, { signal });
    for (const prefix of ["Parameters", "Mapping"]) {
      get(prefix + "Search").addEventListener("input", () => {
        if (prefix === "Parameters") parameterPage = 0; else mappingPage = 0;
        renderTable(prefix === "Parameters" ? "parameters" : "mapping");
      }, { signal });
      get(prefix + "Previous").addEventListener("click", () => {
        if (prefix === "Parameters") parameterPage--; else mappingPage--;
        renderTable(prefix === "Parameters" ? "parameters" : "mapping");
      }, { signal });
      get(prefix + "Next").addEventListener("click", () => {
        if (prefix === "Parameters") parameterPage++; else mappingPage++;
        renderTable(prefix === "Parameters" ? "parameters" : "mapping");
      }, { signal });
    }
    target.querySelector("#dtTabMapping").textContent = "Соответствия";
    target.querySelector('#dtViewSelect option[value="mapping"]').textContent = "Соответствия";
    render();
    scheduleRefresh();
  }
  function unbind() { if (events) events.abort(); events = null; container = null; }
  root.RNAssistantWorkspaceCatalog = Object.freeze({ bind, unbind, setContext });
}(window));
