(function (root) {
  "use strict";
  const pageSize = 25;
  const node = (tag, className, value) => {
    const element = document.createElement(tag);
    if (className) element.className = className;
    if (value !== undefined) element.textContent = value;
    return element;
  };
  function createList(panel, prefix, kindSelector) {
    panel.innerHTML = `<div class="dt-inspection-heading"><div><p class="dt-eyebrow">${kindSelector ? "СООТВЕТСТВИЯ" : "СПРАВОЧНИК"}</p><h4 id="dt${prefix}Title"></h4></div><span class="dt-inspection-badge">Просмотр</span></div>
      <p class="dt-inspection-note">Содержимое выбранного JSON-шаблона. Изменения и публикация здесь не выполняются.</p>
      <div class="dt-inspection-list"><div class="dt-inspection-controls">${kindSelector ? `<select id="dt${prefix}Kind" aria-label="Тип соответствий"><option value="parameter">Параметры</option><option value="unit">Единицы</option></select>` : ""}<input type="search" id="dt${prefix}Search" placeholder="Найти код или название…" aria-label="Поиск по списку"></div>
      <p class="dt-inspection-count" id="dt${prefix}Count"></p><div class="dt-inspection-table-wrap"><table class="dt-inspection-table"><thead><tr id="dt${prefix}Columns"></tr></thead><tbody id="dt${prefix}Rows"></tbody></table></div>
      <div class="dt-inspection-paging"><button type="button" id="dt${prefix}Previous">← Назад</button><span id="dt${prefix}Page"></span><button type="button" id="dt${prefix}Next">Далее →</button></div></div>`;
    const get = name => panel.querySelector("#dt" + prefix + name), events = new AbortController();
    let current = null, page = 0;
    get("Search").addEventListener("input", () => { page = 0; renderPage(); }, { signal: events.signal });
    if (kindSelector) get("Kind").addEventListener("change", () => { page = 0; renderPage(); }, { signal: events.signal });
    get("Previous").addEventListener("click", () => { page--; renderPage(); }, { signal: events.signal });
    get("Next").addEventListener("click", () => { page++; renderPage(); }, { signal: events.signal });
    function renderPage() {
      if (!current) return;
      const type = kindSelector ? get("Kind").value : "parameter";
      const query = get("Search").value.trim().toLocaleLowerCase();
      const source = kindSelector ? current.mappings.filter(item => item.kind === type) : current.parameters;
      const matching = source.filter(item => [item.id, item.name, item.unitId, item.unitName, item.scope, item.source, item.targetId]
        .some(value => value && value.toLocaleLowerCase().includes(query)));
      page = Math.max(0, Math.min(page, Math.ceil(matching.length / pageSize) - 1));
      const labels = kindSelector ? ["Область", "Источник", "Канонический код"] : ["Код", "Название", "Единица", "Измерения"];
      get("Columns").replaceChildren(...labels.map(label => node("th", "", label)));
      get("Rows").replaceChildren(...matching.slice(page * pageSize, (page + 1) * pageSize).map(item => {
        const row = node("tr"), values = kindSelector
          ? [item.scope, item.source, item.targetId] : [item.id, item.name, item.unitId + " · " + item.unitName, item.dimensions.join(", ") || "—"];
        values.forEach((value, index) => {
          const cell = node("td", "", value); cell.dataset.label = labels[index]; row.append(cell);
        });
        return row;
      }));
      get("Count").textContent = matching.length ? "Показано " + (page * pageSize + 1) + "–" + Math.min((page + 1) * pageSize, matching.length) + " из " + matching.length
        : "Совпадений нет";
      get("Page").textContent = matching.length ? (page + 1) + " / " + Math.ceil(matching.length / pageSize) : "0 / 0";
      get("Previous").disabled = page === 0; get("Next").disabled = (page + 1) * pageSize >= matching.length;
    }
    function render(inspection) {
      if (current === inspection) return;
      current = inspection; panel.hidden = !inspection;
      if (!inspection) return;
      page = 0; get("Search").value = "";
      if (kindSelector) get("Kind").value = "parameter";
      get("Title").textContent = kindSelector ? "Карта соответствий · " + inspection.mappings.length
        : "Канонические параметры · " + inspection.parameters.length;
      renderPage();
    }
    return Object.freeze({ render: render, dispose: () => { events.abort(); panel.remove(); } });
  }
  function create(container) {
    const templates = container.querySelector("#dtViewTemplates"), parameters = container.querySelector("#dtViewParameters"),
      mapping = container.querySelector("#dtViewMapping"), anchor = templates.querySelector("#dtTemplateDrop");
    const parameterEmpty = parameters.querySelector(".dt-empty-state"), mappingGuide = mapping.querySelector(".dt-mapping-guide"),
      mappingExplanation = mapping.querySelector(".dt-explanation");
    const parameterPhase = parameters.querySelector(".dt-phase"), mappingPhase = mapping.querySelector(".dt-phase");
    const mappingSubtitle = mapping.querySelector(".dt-section-heading p"), initialMappingSubtitle = mappingSubtitle.textContent;
    const summary = node("section", "dt-template-inspection dt-card");
    summary.id = "dtTemplateInspection"; summary.hidden = true;
    summary.setAttribute("aria-label", "Проверенный шаблон");
    summary.innerHTML = `<div class="dt-inspection-heading"><div><p class="dt-eyebrow">ПРОВЕРКА ШАБЛОНА</p><h4 id="dtInspectionTitle"></h4></div><span class="dt-inspection-badge">Схема проверена</span></div>
      <p class="dt-inspection-note">Проверены язык, селекторы, роли и справочники. Наличие листа, значения ячеек и соответствия данным проверяются при обработке файла.</p>
      <div class="dt-inspection-metrics" id="dtInspectionMetrics"></div>
      <details class="dt-inspection-detail"><summary>Области и правила</summary><div id="dtInspectionRules"></div></details>
      <div class="dt-inspection-links"><button type="button" id="dtInspectionOpenParameters">Смотреть параметры →</button><button type="button" id="dtInspectionOpenMapping">Смотреть маппинг →</button></div>`;
    anchor.insertAdjacentElement("afterend", summary);
    const parameterPanel = node("section", "dt-template-inspection dt-card"), mappingPanel = node("section", "dt-template-inspection dt-card");
    parameterPanel.id = "dtParameterInspection"; mappingPanel.id = "dtMappingInspection";
    parameterPanel.hidden = true; mappingPanel.hidden = true;
    parameters.querySelector(".dt-section-heading").insertAdjacentElement("afterend", parameterPanel);
    mapping.querySelector(".dt-section-heading").insertAdjacentElement("afterend", mappingPanel);
    const parameterList = createList(parameterPanel, "Parameter", false), mappingList = createList(mappingPanel, "Mapping", true);
    const events = new AbortController();
    summary.querySelector("#dtInspectionOpenParameters").addEventListener("click", () => container.querySelector("#dtTabParameters").click(), { signal: events.signal });
    summary.querySelector("#dtInspectionOpenMapping").addEventListener("click", () => container.querySelector("#dtTabMapping").click(), { signal: events.signal });
    let current = null;
    function render(inspection) {
      if (current === inspection) return;
      current = inspection; summary.hidden = !inspection;
      parameterEmpty.hidden = !!inspection; mappingGuide.hidden = !!inspection; mappingExplanation.hidden = !!inspection;
      parameterPhase.textContent = mappingPhase.textContent = inspection ? "Просмотр" : "Следующий этап";
      mappingSubtitle.textContent = inspection ? "Правила соответствий из выбранного шаблона; значения источника проверяются при обработке." : initialMappingSubtitle;
      parameterList.render(inspection); mappingList.render(inspection);
      if (!inspection) return;
      summary.querySelector("#dtInspectionTitle").textContent = inspection.template.id + " · ревизия " + inspection.template.revision;
      summary.querySelector("#dtInspectionMetrics").replaceChildren(...[
        ["Лист и область", inspection.sheet + " · " + inspection.bounds],
        ["Записей по плану", String(inspection.records)],
        ["Параметров", String(inspection.parameters.length)],
        ["Соответствий", String(inspection.mappings.length)]
      ].map(([label, value]) => {
        const metric = node("div"); metric.append(node("small", "", label), node("strong", "", value)); return metric;
      }));
      const rules = summary.querySelector("#dtInspectionRules"); rules.replaceChildren();
      rules.append(node("p", "", "Набор: " + inspection.dataset));
      function appendRules(container, entries) {
        entries.forEach(entry => {
          const item = node("li"); item.append(node("strong", "", entry.role || entry.name), node("code", "", entry.expression));
          container.append(item);
        });
      }
      if (inspection.contexts.length) {
        rules.append(node("h5", "", "Контекст"));
        const list = node("ul", "dt-inspection-expressions"); appendRules(list, inspection.contexts); rules.append(list);
      }
      rules.append(node("h5", "", "Общие правила"));
      const common = node("ul", "dt-inspection-expressions"); appendRules(common, inspection.commonRules); rules.append(common);
      for (const [label, groups] of [["Строки", inspection.rows], ["Столбцы", inspection.columns]]) {
        rules.append(node("h5", "", label + " · " + groups.length));
        const list = node("ul", "dt-inspection-rules");
        groups.forEach(group => {
          const item = node("li");
          item.append(node("strong", "", group.id), node("span", "", group.select));
          const expressions = node("ul", "dt-inspection-expressions"); appendRules(expressions, group.rules);
          item.append(expressions);
          list.append(item);
        });
        rules.append(list);
      }
    }
    return Object.freeze({ render: render, dispose: () => {
      events.abort(); parameterList.dispose(); mappingList.dispose(); summary.remove();
      parameterEmpty.hidden = false; mappingGuide.hidden = false; mappingExplanation.hidden = false;
      parameterPhase.textContent = mappingPhase.textContent = "Следующий этап";
      mappingSubtitle.textContent = initialMappingSubtitle;
    } });
  }
  root.DataTemplatesTemplateInspectionView = Object.freeze({ create: create });
}(window));
