(function (root) {
  "use strict";
  const node = (tag, className, value) => {
    const element = document.createElement(tag);
    if (className) element.className = className;
    if (value !== undefined) element.textContent = value;
    return element;
  };
  function create(container, actions) {
    const panel = container.querySelector("#dtMappingInspection"), list = panel.querySelector(".dt-inspection-list");
    const toolbar = node("div", "dt-mapping-toolbar"); toolbar.hidden = true;
    toolbar.innerHTML = `<p>Связь задаёт точное совпадение области и исходного значения с каноническим кодом.</p><button type="button" id="dtMappingAdd">Добавить соответствие</button>`;
    list.insertAdjacentElement("beforebegin", toolbar);
    const form = node("section", "dt-mapping-editor"); form.id = "dtMappingEditor"; form.hidden = true;
    form.setAttribute("aria-label", "Редактирование соответствия");
    form.innerHTML = `<div class="dt-mapping-editor-head"><div><p class="dt-eyebrow">ПРАВИЛО МАППИНГА</p><h5 id="dtMappingEditorTitle">Соответствие</h5></div><span id="dtMappingEditorState">Черновик</span></div>
      <div class="dt-mapping-fields"><label>Тип<select id="dtMappingEditKind"><option value="parameter">Параметр</option><option value="unit">Единица</option></select></label><label>Область<input id="dtMappingEditScope" type="text" maxlength="128" autocomplete="off" spellcheck="false" placeholder="production-form"></label><label>Исходное значение<input id="dtMappingEditSource" type="text" maxlength="256" autocomplete="off" spellcheck="false" placeholder="Как в файле"></label><label>Канонический код<select id="dtMappingEditTarget" aria-label="Канонический код"></select></label></div>
      <div class="dt-mapping-editor-foot"><p id="dtMappingEditNotice" role="status" aria-live="polite">Проверьте область и исходное значение перед применением.</p><div><button type="button" id="dtMappingEditDelete" hidden>Удалить</button><button type="button" id="dtMappingEditCancel">Отменить</button><button type="button" class="dt-primary" id="dtMappingEditApply">Применить</button></div></div>`;
    toolbar.insertAdjacentElement("afterend", form);
    const get = id => form.querySelector("#dt" + id), events = new AbortController();
    const listen = (element, type, handler) => element.addEventListener(type, handler, { signal: events.signal });
    let snapshot = null, generation = null, original = null, opened = false, dirty = false,
      applying = false, disposed = false;
    function values() { return { kind: get("MappingEditKind").value, scope: get("MappingEditScope").value,
      source: get("MappingEditSource").value, targetId: get("MappingEditTarget").value,
      path: original && original.path }; }
    function targets() {
      const inspection = snapshot && snapshot.templateInspection;
      const entries = !inspection ? [] : get("MappingEditKind").value === "parameter" ? inspection.parameters : inspection.units;
      const selected = original && original.kind === get("MappingEditKind").value ? original.targetId : "";
      get("MappingEditTarget").replaceChildren(node("option", "", "Выберите код…"), ...entries.map(entry => {
        const option = node("option", "", entry.id + " · " + entry.name); option.value = entry.id; return option;
      }));
      get("MappingEditTarget").firstChild.value = "";
      get("MappingEditTarget").value = selected;
    }
    function update() {
      if (!opened) return;
      dirty = !original || ["kind", "scope", "source", "targetId"].some(key => values()[key] !== original[key]);
      if (!applying) actions.mappingDirty(dirty);
      const blocked = applying || !snapshot || snapshot.running || snapshot.templateLoading || snapshot.rulePending || snapshot.dictionaryPending;
      for (const id of ["MappingEditKind", "MappingEditScope", "MappingEditSource", "MappingEditTarget"])
        get(id).disabled = blocked || id === "MappingEditKind" && !!original;
      get("MappingEditApply").disabled = blocked || !dirty || !values().scope.trim() || !values().source.trim() || !values().targetId;
      get("MappingEditDelete").hidden = !original;
      get("MappingEditDelete").disabled = blocked || dirty;
      get("MappingEditCancel").disabled = applying;
      get("MappingEditorState").textContent = applying ? "Проверка…" : dirty ? "Не применено" : "Без изменений";
    }
    function close() {
      opened = false; dirty = false; original = null; applying = false; form.hidden = true;
      actions.mappingDirty(false);
    }
    function open(item) {
      if (!snapshot || !snapshot.templateInspection || snapshot.running) return;
      if (snapshot.rulePending || snapshot.dictionaryPending) {
        toolbar.querySelector("p").textContent = "Сначала завершите правку выражения или параметра.";
        toolbar.scrollIntoView({ block: "nearest", behavior: "smooth" }); return;
      }
      if (opened && dirty) {
        get("MappingEditNotice").textContent = "Сначала примените или отмените текущую правку.";
        form.scrollIntoView({ block: "nearest", behavior: "smooth" }); return;
      }
      original = item || null; opened = true; form.hidden = false;
      get("MappingEditorTitle").textContent = item ? "Изменить соответствие" : "Новое соответствие";
      get("MappingEditKind").value = item ? item.kind : "parameter";
      get("MappingEditScope").value = item ? item.scope : "";
      get("MappingEditSource").value = item ? item.source : "";
      targets();
      get("MappingEditNotice").textContent = item ? "Изменение затронет только выбранную точную связь." :
        "Добавление будет проверено с полным шаблоном до применения.";
      update(); form.scrollIntoView({ block: "nearest", behavior: "smooth" });
      get("MappingEditScope").focus({ preventScroll: true });
    }
    function render(next) {
      if (generation !== next.draftGeneration) {
        generation = next.draftGeneration; opened = false; dirty = false; original = null;
        form.hidden = true;
      }
      snapshot = next;
      toolbar.hidden = !next.templateInspection;
      toolbar.querySelector("#dtMappingAdd").disabled = next.rulePending || next.dictionaryPending || next.running;
      toolbar.querySelector("p").textContent = next.rulePending || next.dictionaryPending
        ? "Сначала завершите правку выражения или параметра."
        : "Связь задаёт точное совпадение области и исходного значения с каноническим кодом.";
      if (!next.templateInspection && opened) close();
      if (opened) update();
    }
    listen(toolbar.querySelector("#dtMappingAdd"), "click", () => open(null));
    listen(get("MappingEditKind"), "change", () => { targets(); update(); });
    for (const id of ["MappingEditScope", "MappingEditSource"])
      listen(get(id), "input", update);
    listen(get("MappingEditTarget"), "change", update);
    listen(get("MappingEditCancel"), "click", close);
    listen(get("MappingEditApply"), "click", async () => {
      if (!opened || applying || get("MappingEditApply").disabled) return;
      applying = true; update();
      const result = await actions.applyMapping(values());
      if (disposed) return;
      applying = false;
      if (result.ok) close();
      else { get("MappingEditNotice").textContent = result.message; update(); }
    });
    listen(get("MappingEditDelete"), "click", async () => {
      if (!original || applying || dirty || get("MappingEditDelete").disabled ||
          !root.confirm("Удалить соответствие «" + original.source + "» в области «" + original.scope + "»?")) return;
      applying = true; update();
      const result = await actions.removeMapping(original.path);
      if (disposed) return;
      applying = false;
      if (result.ok) close();
      else { get("MappingEditNotice").textContent = result.message; update(); }
    });
    return Object.freeze({ open, render, dispose: () => {
      disposed = true; events.abort(); toolbar.remove(); form.remove();
    } });
  }
  root.DataTemplatesMappingEditor = Object.freeze({ create });
}(window));
