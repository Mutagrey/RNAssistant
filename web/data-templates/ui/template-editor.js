(function (root) {
  "use strict";
  const node = (tag, className, value) => {
    const element = document.createElement(tag);
    if (className) element.className = className;
    if (value !== undefined) element.textContent = value;
    return element;
  };
  const hints = Object.freeze(["cell(", "text(", "raw(", "field(", "path(", "lookup(", "decimal(", "scale(",
    "round(", "parse.year(", "parse.monthRu(", "period.month(", "period.year(", "row", "col", "ctx."]);
  function installExpressionMode(CodeMirror) {
    CodeMirror.defineMode("data-templates-expression", () => ({
      token: stream => {
        if (stream.eatSpace()) return null;
        if (stream.match(/^"(?:[^"\\]|\\.)*"/)) return "string";
        if (stream.match(/^-?(?:0|[1-9]\d*)/)) return "number";
        const name = stream.match(/^[A-Za-z_][A-Za-z0-9_.]*/);
        if (name) return name[0] === "row" || name[0] === "col" || name[0].startsWith("ctx.") ? "variable-2" : "builtin";
        stream.next(); return null;
      }
    }));
    CodeMirror.registerHelper("hint", "data-templates-expression", editor => {
      const cursor = editor.getCursor(), line = editor.getLine(cursor.line);
      const before = line.slice(0, cursor.ch), match = /[A-Za-z_][A-Za-z0-9_.]*$/.exec(before);
      const prefix = match ? match[0] : "", start = cursor.ch - prefix.length;
      return { list: hints.filter(item => item.startsWith(prefix)).map(text => ({ text, displayText: text })),
        from: CodeMirror.Pos(cursor.line, start), to: cursor };
    });
  }
  function entries(inspection) {
    if (!inspection) return [];
    return [
      ...inspection.contexts.map(item => ({ ...item, label: "Контекст · " + item.name })),
      ...inspection.commonRules.map(item => ({ ...item, label: "Общие · " + item.role })),
      ...inspection.rows.flatMap(group => group.rules.map(item => ({ ...item, label: "Строки / " + group.id + " · " + item.role }))),
      ...inspection.columns.flatMap(group => group.rules.map(item => ({ ...item, label: "Столбцы / " + group.id + " · " + item.role })))
    ];
  }
  function create(container, actions) {
    const anchor = container.querySelector("#dtTemplateDrop"), picker = anchor.querySelector(".dt-template-picker");
    const openButton = node("button", "dt-text-button", "Редактировать правила");
    openButton.type = "button"; openButton.id = "dtOpenTemplateEditor"; openButton.hidden = true; picker.append(openButton);
    const panel = node("section", "dt-template-editor dt-card"); panel.id = "dtTemplateEditor"; panel.hidden = true;
    panel.setAttribute("aria-label", "Редактор шаблона");
    panel.innerHTML = `<div class="dt-editor-heading"><div><p class="dt-eyebrow">ЧЕРНОВИК ШАБЛОНА</p><h4 id="dtEditorTitle">Редактор</h4><p id="dtEditorSummary">Правила в JSON и выражения используют одну проверку.</p></div><span class="dt-editor-state" id="dtEditorState"></span></div>
      <div class="dt-editor-toolbar"><div class="dt-editor-tabs" aria-label="Режим редактора"><button type="button" id="dtEditorRuleTab" aria-pressed="true">Выражения</button><button type="button" id="dtEditorJsonTab" aria-pressed="false">Исходный JSON</button></div><button type="button" class="dt-text-button" id="dtCloseTemplateEditor">Свернуть</button></div>
      <div id="dtEditorRulePane" class="dt-editor-body"><div class="dt-editor-rule-grid"><label for="dtEditorRuleSelect">Правило <select id="dtEditorRuleSelect" aria-label="Выберите выражение"></select></label><p id="dtEditorRulePath"></p></div><div id="dtEditorExpressionCode" class="dt-template-code dt-template-code-expression"></div><div class="dt-editor-rule-footer"><p id="dtEditorRuleNotice" role="status" aria-live="polite">Выберите правило и измените выражение.</p><div><button type="button" id="dtEditorDiscardRule" hidden>Отменить правку</button><button type="button" class="dt-primary" id="dtEditorApplyRule" disabled>Применить правило</button></div></div><details class="dt-editor-reference"><summary>Конструкции языка</summary><p><code>cell("A", row)</code> читает ячейку; <code>text(...)</code> и <code>raw(...)</code> задают тип; <code>lookup(...)</code> использует справочник; <code>decimal(...)</code>, <code>period.month(...)</code> и <code>period.year(...)</code> формируют канонические значения. Подсказки: Ctrl+Пробел.</p></details></div>
      <div id="dtEditorJsonPane" class="dt-editor-body" hidden><p class="dt-editor-guidance" id="dtEditorJsonGuidance">Полный документ шаблона. Каждая правка проверяется в отдельном Worker; ошибочный JSON можно исправить здесь.</p><div id="dtEditorJsonCode" class="dt-template-code dt-template-code-json"></div></div>
      <div class="dt-editor-footer"><p>Черновик остаётся в памяти до явного скачивания или сохранения в проекте. Эти действия не публикуют данные.</p><button type="button" id="dtDownloadTemplateDraft">Скачать JSON-черновик</button></div>`;
    anchor.insertAdjacentElement("afterend", panel);
    const get = id => panel.querySelector("#dt" + id), events = new AbortController();
    const listen = (element, type, handler) => element.addEventListener(type, handler, { signal: events.signal });
    let snapshot = null, generation = null, CodeMirror = null, jsonEditor = null, expressionEditor = null, opened = false,
      disposed = false, mode = "rule", available = [], selectedPath = "", ruleOriginal = "", ruleDirty = false,
      syncing = false, applying = false, loadError = "";
    function findRule(path) { return available.find(item => item.path === path); }
    function setExpression(rule) {
      selectedPath = rule ? rule.path : ""; ruleOriginal = rule ? rule.expression : ""; ruleDirty = false;
      get("EditorRuleSelect").value = selectedPath;
      get("EditorRulePath").textContent = selectedPath || "Сначала загрузите и проверьте JSON-шаблон.";
      if (expressionEditor) { syncing = true; expressionEditor.setValue(ruleOriginal); syncing = false; }
      get("EditorRuleNotice").textContent = rule ? "Правило проверено; после правки нажмите «Применить»." : "Нет доступных правил.";
      actions.ruleDirty(false); updateControls();
    }
    function updateControls() {
      const valid = !!(snapshot && snapshot.templateInspection), hasDraft = !!(snapshot && snapshot.draftName);
      get("EditorRuleTab").disabled = !valid && mode !== "rule";
      get("EditorRuleSelect").disabled = !valid || applying;
      get("EditorApplyRule").disabled = !ruleDirty || !valid || applying ||
        !!(snapshot && (snapshot.templateLoading || snapshot.running || snapshot.mappingPending || snapshot.dictionaryPending));
      get("EditorDiscardRule").hidden = !ruleDirty;
      get("DownloadTemplateDraft").disabled = !hasDraft || !snapshot.draftWithinLimit || ruleDirty || applying ||
        !!(snapshot && (snapshot.mappingPending || snapshot.dictionaryPending));
      get("EditorRuleTab").setAttribute("aria-pressed", String(mode === "rule"));
      get("EditorJsonTab").setAttribute("aria-pressed", String(mode === "json"));
      get("EditorRulePane").hidden = mode !== "rule";
      get("EditorJsonPane").hidden = mode !== "json";
      if (CodeMirror) requestAnimationFrame(() => { if (!disposed) (mode === "rule" ? expressionEditor : jsonEditor).refresh(); });
    }
    function switchMode(next) {
      if (ruleDirty && next !== mode) {
        get("EditorRuleNotice").textContent = "Сначала примените или отмените текущую правку."; return;
      }
      if (next === "rule" && (!snapshot || !snapshot.templateInspection)) return;
      mode = next; updateControls();
      if (CodeMirror) (mode === "rule" ? expressionEditor : jsonEditor).focus();
    }
    async function open(path) {
      if (!snapshot || !snapshot.draftName || disposed) return;
      if (snapshot.mappingPending) { container.querySelector("#dtTabMapping").click(); return; }
      if (snapshot.dictionaryPending) { container.querySelector("#dtTabParameters").click(); return; }
      if (ruleDirty && path && path !== selectedPath) {
        get("EditorRuleNotice").textContent = "Сначала примените или отмените текущую правку.";
        panel.scrollIntoView({ block: "nearest", behavior: "smooth" }); return;
      }
      opened = true; panel.hidden = false;
      if (path && findRule(path)) selectedPath = path;
      if (!CodeMirror) {
        loadError = "";
        get("EditorSummary").textContent = "Загружаем локальный редактор…";
        try {
          const loaded = await (actions.loadCodeMirror ? actions.loadCodeMirror() : root.DataTemplatesVendors.load("codemirror"));
          if (disposed || !opened) return;
          CodeMirror = loaded;
          installExpressionMode(CodeMirror);
          expressionEditor = CodeMirror(get("EditorExpressionCode"), { value: "", mode: "data-templates-expression",
            lineNumbers: false, lineWrapping: true, matchBrackets: true, autoCloseBrackets: true,
            extraKeys: { "Ctrl-Space": editor => editor.showHint({ completeSingle: false }) } });
          jsonEditor = CodeMirror(get("EditorJsonCode"), { value: snapshot.draftText, mode: { name: "javascript", json: true },
            lineNumbers: true, lineWrapping: true, matchBrackets: true, autoCloseBrackets: true });
          expressionEditor.on("change", () => {
            if (syncing || disposed) return;
            ruleDirty = expressionEditor.getValue() !== ruleOriginal;
            get("EditorRuleNotice").textContent = ruleDirty ? "Правка не применена. Запуск приостановлен до проверки или отмены." : "Правило не изменено.";
            actions.ruleDirty(ruleDirty); updateControls();
          });
          jsonEditor.on("change", () => { if (!syncing && !disposed) actions.changeDraft(jsonEditor.getValue()); });
        } catch (error) {
          loadError = "Редактор недоступен: " + error.message;
          get("EditorSummary").textContent = loadError;
          return;
        }
      }
      render(snapshot);
      switchMode(path || snapshot.templateInspection ? "rule" : "json");
      if (path && findRule(path) && !ruleDirty) setExpression(findRule(path));
      panel.scrollIntoView({ block: "nearest", behavior: "smooth" });
    }
    function render(next) {
      if (generation !== next.draftGeneration) {
        generation = next.draftGeneration; opened = false; panel.hidden = true;
        available = []; selectedPath = ""; ruleOriginal = ""; ruleDirty = false; mode = "rule";
      }
      snapshot = next;
      openButton.hidden = !next.draftName;
      openButton.textContent = next.templateUnverified ? "Редактировать JSON" : "Редактировать правила";
      openButton.disabled = next.running || next.mappingPending || next.dictionaryPending;
      if (!opened) return;
      get("EditorTitle").textContent = next.draftName || "Редактор";
      get("EditorState").textContent = next.templateLoading ? "Проверка…" : next.templateUnverified ? "Без проверки" : next.templateName
        ? next.draftDirty ? "Проверен · изменён" : "Проверен" : "Требует исправления";
      get("EditorSummary").textContent = loadError || (next.templateName ? "Правила и справочники проверены. Проверка значений файла выполняется при запуске."
        : next.templateUnverified ? "Сохраняется исходный JSON. Проверка правил и запуск доступны в автономном приложении."
          : next.templateLoading ? "Worker проверяет текущий JSON." : "Исправьте ошибку в исходном JSON, затем повторите запуск.");
      get("EditorJsonGuidance").textContent = next.sharedDictionaryMode
        ? "Это рабочий JSON с подставленным общим справочником проекта. Параметры и маппинг меняйте в своих вкладках; правки этого блока в JSON не применяются к проекту."
        : next.templateUnverified
        ? "Исходный JSON можно править и сохранять даже до проверки. Этот интерфейс не выполняет шаблон."
        : "Полный документ шаблона. Каждая правка проверяется в отдельном Worker; ошибочный JSON можно исправить здесь.";
      if (jsonEditor && jsonEditor.getValue() !== (next.draftText || "")) {
        syncing = true; jsonEditor.setValue(next.draftText || ""); syncing = false;
      }
      const readOnly = !!(next.running || next.mappingPending || next.dictionaryPending);
      if (jsonEditor && jsonEditor.getOption("readOnly") !== readOnly) jsonEditor.setOption("readOnly", readOnly);
      if (expressionEditor && expressionEditor.getOption("readOnly") !== readOnly)
        expressionEditor.setOption("readOnly", readOnly);
      const revised = entries(next.templateInspection);
      if (revised.length !== available.length || revised.some((item, index) =>
        item.path !== available[index].path || item.expression !== available[index].expression)) {
        available = revised;
        get("EditorRuleSelect").replaceChildren(...available.map(item => {
          const option = node("option", "", item.label); option.value = item.path; return option;
        }));
        if (!ruleDirty) setExpression(findRule(selectedPath) || available[0]);
      }
      if (!next.templateInspection && mode === "rule" && !ruleDirty) mode = "json";
      updateControls();
    }
    listen(openButton, "click", () => open());
    listen(get("CloseTemplateEditor"), "click", () => {
      if (ruleDirty) { get("EditorRuleNotice").textContent = "Сначала примените или отмените текущую правку."; return; }
      opened = false; panel.hidden = true;
    });
    listen(get("EditorRuleTab"), "click", () => switchMode("rule"));
    listen(get("EditorJsonTab"), "click", () => switchMode("json"));
    listen(get("EditorRuleSelect"), "change", () => {
      if (ruleDirty) {
        get("EditorRuleSelect").value = selectedPath;
        get("EditorRuleNotice").textContent = "Сначала примените или отмените текущую правку.";
        return;
      }
      setExpression(findRule(get("EditorRuleSelect").value));
    });
    listen(get("EditorDiscardRule"), "click", () => setExpression(findRule(selectedPath)));
    listen(get("EditorApplyRule"), "click", async () => {
      if (!ruleDirty || applying || !expressionEditor) return;
      applying = true; updateControls();
      const result = await actions.applyRule(selectedPath, expressionEditor.getValue());
      applying = false;
      if (disposed) return;
      if (result.ok) setExpression(findRule(selectedPath));
      else get("EditorRuleNotice").textContent = result.message;
      updateControls();
    });
    listen(get("DownloadTemplateDraft"), "click", actions.downloadDraft);
    return Object.freeze({ render, open, dispose: () => {
      disposed = true; events.abort();
      if (jsonEditor) jsonEditor.toTextArea ? jsonEditor.toTextArea() : jsonEditor.getWrapperElement().remove();
      if (expressionEditor) expressionEditor.toTextArea ? expressionEditor.toTextArea() : expressionEditor.getWrapperElement().remove();
      panel.remove(); openButton.remove();
    } });
  }
  root.DataTemplatesTemplateEditor = Object.freeze({ create });
}(window));
