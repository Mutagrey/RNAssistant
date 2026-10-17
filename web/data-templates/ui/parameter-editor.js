(function (root) {
  "use strict";
  const node = (tag, className, value) => {
    const element = document.createElement(tag);
    if (className) element.className = className;
    if (value !== undefined) element.textContent = value;
    return element;
  };
  function create(container, actions) {
    const panel = container.querySelector("#dtParameterInspection"), list = panel.querySelector(".dt-inspection-list");
    const toolbar = node("div", "dt-mapping-toolbar"); toolbar.hidden = true;
    toolbar.innerHTML = `<p>Параметры и единицы здесь принадлежат текущему JSON-шаблону; изменения проверяются до применения.</p><div class="dt-parameter-actions"><button type="button" id="dtParameterAdd">Добавить параметр</button><button type="button" id="dtUnitAdd">Добавить единицу</button></div>`;
    list.insertAdjacentElement("beforebegin", toolbar);
    const form = node("section", "dt-mapping-editor dt-parameter-editor"); form.hidden = true;
    form.setAttribute("aria-label", "Редактирование параметра или единицы");
    form.innerHTML = `<div class="dt-mapping-editor-head"><div><p class="dt-eyebrow">СПРАВОЧНИК</p><h5 id="dtDictionaryTitle">Новый параметр</h5></div><span id="dtDictionaryState">Черновик</span></div>
      <div class="dt-parameter-fields"><label>Устойчивый код<input id="dtDictionaryId" maxlength="128" autocomplete="off" spellcheck="false" placeholder="production.volume"></label><label>Название<input id="dtDictionaryName" maxlength="128" autocomplete="off" placeholder="Объём производства"></label><label class="dt-parameter-only">Единица<select id="dtDictionaryUnit"></select></label><label class="dt-parameter-only">Измерения через запятую<input id="dtDictionaryDimensions" autocomplete="off" spellcheck="false" placeholder="entity, variant"></label><label class="dt-parameter-only">Временной смысл<select id="dtDictionaryTemporal"><option value="flow">Поток за период</option><option value="at_period_end">На конец периода</option><option value="cumulative">Накопительно</option><option value="unspecified">Не указан</option></select></label></div>
      <p class="dt-parameter-guidance">Тип значения: decimal. Код существующего элемента не меняется; для нового смысла создайте новый код.</p><div class="dt-mapping-editor-foot"><p id="dtDictionaryNotice" role="status" aria-live="polite">Проверьте значения перед применением.</p><div><button type="button" id="dtDictionaryDelete" hidden>Удалить</button><button type="button" id="dtDictionaryCancel">Отменить</button><button type="button" class="dt-primary" id="dtDictionaryApply">Применить</button></div></div>`;
    toolbar.insertAdjacentElement("afterend", form);
    const units = node("details", "dt-parameter-units");
    units.innerHTML = `<summary>Единицы измерения <span id="dtUnitCount"></span></summary><div class="dt-parameter-unit-controls"><input type="search" id="dtUnitSearch" placeholder="Найти единицу…" aria-label="Поиск единицы"><span id="dtUnitRange"></span></div><ul id="dtUnitList"></ul><div class="dt-library-paging"><button type="button" id="dtUnitPrevious">← Назад</button><span id="dtUnitPage"></span><button type="button" id="dtUnitNext">Далее →</button></div>`;
    list.insertAdjacentElement("afterend", units);
    const get = id => panel.querySelector("#dt" + id), events = new AbortController();
    const listen = (element, type, handler) => element.addEventListener(type, handler, { signal: events.signal });
    let snapshot = null, generation = null, original = null, kind = "parameter", opened = false,
      dirty = false, applying = false, disposed = false, unitPage = 0, listedInspection = null;
    function values() {
      const dimensionsText = get("DictionaryDimensions").value.trim();
      return kind === "unit" ? { id: get("DictionaryId").value.trim(), name: get("DictionaryName").value.trim(),
        path: original && original.path } : {
        id: get("DictionaryId").value.trim(), name: get("DictionaryName").value.trim(),
        unitId: get("DictionaryUnit").value,
        dimensions: dimensionsText ? dimensionsText.split(",").map(value => value.trim()) : [],
        temporalMeaning: get("DictionaryTemporal").value, path: original && original.path
      };
    }
    function equal(value) {
      return original && (kind === "unit" ? value.id === original.id && value.name === original.name :
        value.id === original.id && value.name === original.name && value.unitId === original.unitId &&
        value.temporalMeaning === original.temporalMeaning &&
        JSON.stringify(value.dimensions) === JSON.stringify(original.dimensions));
    }
    function update() {
      if (!opened) return;
      const value = values(); dirty = !equal(value);
      if (!applying) actions.dictionaryDirty(dirty);
      const blocked = applying || !snapshot || snapshot.running || snapshot.templateLoading ||
        snapshot.rulePending || snapshot.mappingPending;
      get("DictionaryId").disabled = blocked || !!original;
      get("DictionaryName").disabled = blocked;
      for (const id of ["DictionaryUnit", "DictionaryDimensions", "DictionaryTemporal"]) get(id).disabled = blocked;
      get("DictionaryApply").disabled = blocked || !dirty || !/^[A-Za-z][A-Za-z0-9_.-]{0,127}$/.test(value.id) ||
        !value.name || value.name.length > 128 || kind === "parameter" &&
        (!value.unitId || value.dimensions.some(item => !/^[A-Za-z][A-Za-z0-9_.-]{0,127}$/.test(item)));
      get("DictionaryDelete").hidden = !original;
      get("DictionaryDelete").disabled = blocked || dirty;
      get("DictionaryCancel").disabled = applying;
      get("DictionaryState").textContent = applying ? "Проверка…" : dirty ? "Не применено" : "Без изменений";
    }
    function close() {
      opened = false; dirty = false; original = null; applying = false; form.hidden = true;
      actions.dictionaryDirty(false);
    }
    function open(nextKind, item) {
      if (!snapshot || !snapshot.templateInspection || snapshot.running) return;
      if (snapshot.rulePending || snapshot.mappingPending) {
        toolbar.querySelector("p").textContent = "Сначала завершите правку выражения или маппинга.";
        return;
      }
      if (opened && dirty) { get("DictionaryNotice").textContent = "Сначала примените или отмените текущую правку."; return; }
      kind = nextKind; original = item || null; opened = true; form.hidden = false;
      get("DictionaryTitle").textContent = (item ? "Изменить " : "Новый ") + (kind === "unit" ? "единицу" : "параметр");
      get("DictionaryId").value = item ? item.id : "";
      get("DictionaryName").value = item ? item.name : "";
      const available = snapshot.templateInspection.units;
      get("DictionaryUnit").replaceChildren(...available.map(unit => {
        const option = node("option", "", unit.id + " · " + unit.name); option.value = unit.id; return option;
      }));
      get("DictionaryUnit").value = item && item.unitId || (available[0] && available[0].id || "");
      get("DictionaryDimensions").value = item && item.dimensions ? item.dimensions.join(", ") : "";
      get("DictionaryTemporal").value = item && item.temporalMeaning || "unspecified";
      form.querySelectorAll(".dt-parameter-only").forEach(element => { element.hidden = kind === "unit"; });
      get("DictionaryNotice").textContent = item ? "Код остаётся прежним; правка затрагивает текущий JSON-черновик." :
        "Новый элемент будет проверен вместе со всем шаблоном.";
      update(); form.scrollIntoView({ block: "nearest", behavior: "smooth" });
      get("DictionaryName").focus({ preventScroll: true });
    }
    function renderUnits(inspection) {
      const all = inspection.units, query = get("UnitSearch").value.trim().toLocaleLowerCase();
      const matching = all.filter(item => (item.id + " " + item.name).toLocaleLowerCase().includes(query));
      unitPage = Math.max(0, Math.min(unitPage, Math.ceil(matching.length / 25) - 1));
      get("UnitCount").textContent = "· " + all.length;
      get("UnitRange").textContent = matching.length ? (unitPage * 25 + 1) + "–" + Math.min((unitPage + 1) * 25, matching.length) + " из " + matching.length : "Нет совпадений";
      get("UnitList").replaceChildren(...matching.slice(unitPage * 25, (unitPage + 1) * 25).map(item => {
        const row = node("li"), button = node("button", "dt-text-button", "Править");
        button.type = "button"; button.dataset.unitPath = item.path;
        row.append(node("span", "", item.id + " · " + item.name), button); return row;
      }));
      get("UnitPage").textContent = (unitPage + 1) + " / " + Math.max(1, Math.ceil(matching.length / 25));
      get("UnitPrevious").disabled = unitPage === 0;
      get("UnitNext").disabled = (unitPage + 1) * 25 >= matching.length;
    }
    function render(next) {
      if (generation !== next.draftGeneration) {
        generation = next.draftGeneration; opened = false; dirty = false; original = null; form.hidden = true;
      }
      snapshot = next;
      toolbar.hidden = units.hidden = !next.templateInspection;
      toolbar.querySelector("p").textContent = next.rulePending || next.mappingPending ?
        "Сначала завершите правку выражения или маппинга." :
        "Параметры и единицы здесь принадлежат текущему JSON-шаблону; изменения проверяются до применения.";
      toolbar.querySelector("#dtParameterAdd").disabled = toolbar.querySelector("#dtUnitAdd").disabled =
        next.rulePending || next.mappingPending || next.running;
      if (!next.templateInspection && opened) close();
      if (next.templateInspection !== listedInspection) {
        listedInspection = next.templateInspection; unitPage = 0; get("UnitSearch").value = "";
        if (listedInspection) renderUnits(listedInspection);
      }
      if (opened) update();
    }
    listen(toolbar.querySelector("#dtParameterAdd"), "click", () => open("parameter", null));
    listen(toolbar.querySelector("#dtUnitAdd"), "click", () => open("unit", null));
    listen(get("UnitList"), "click", event => {
      const button = event.target.closest("button[data-unit-path]");
      if (!button || !snapshot || !snapshot.templateInspection) return;
      const item = snapshot.templateInspection.units.find(entry => entry.path === button.dataset.unitPath);
      if (item) open("unit", item);
    });
    listen(get("UnitSearch"), "input", () => { unitPage = 0; if (listedInspection) renderUnits(listedInspection); });
    listen(get("UnitPrevious"), "click", () => { unitPage--; renderUnits(listedInspection); });
    listen(get("UnitNext"), "click", () => { unitPage++; renderUnits(listedInspection); });
    for (const id of ["DictionaryId", "DictionaryName", "DictionaryDimensions"])
      listen(get(id), "input", update);
    for (const id of ["DictionaryUnit", "DictionaryTemporal"]) listen(get(id), "change", update);
    listen(get("DictionaryCancel"), "click", close);
    listen(get("DictionaryApply"), "click", async () => {
      if (!opened || applying || get("DictionaryApply").disabled) return;
      applying = true; update();
      const result = await (kind === "unit" ? actions.upsertUnit(values()) : actions.upsertParameter(values()));
      if (disposed) return;
      applying = false;
      if (result.ok) close(); else { get("DictionaryNotice").textContent = result.message; update(); }
    });
    listen(get("DictionaryDelete"), "click", async () => {
      if (!original || applying || dirty || get("DictionaryDelete").disabled ||
          !root.confirm("Удалить «" + original.name + "» из текущего шаблона? Зависимые параметры и соответствия удалите отдельно.")) return;
      applying = true; update();
      const result = await (kind === "unit" ? actions.removeUnit(original.path) : actions.removeParameter(original.path));
      if (disposed) return;
      applying = false;
      if (result.ok) close(); else { get("DictionaryNotice").textContent = result.message; update(); }
    });
    return Object.freeze({ openParameter: item => open("parameter", item), render,
      dispose: () => { disposed = true; events.abort(); toolbar.remove(); form.remove(); units.remove(); } });
  }
  root.DataTemplatesParameterEditor = Object.freeze({ create });
}(window));
