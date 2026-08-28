(function () {
  "use strict";

  function create(options) {
    options = options || {};
    var state = options.state;

    function editorJson(id) {
      if (typeof syncCodeEditors === "function") syncCodeEditors([id]);
      var text = typeof getCodeEditorValue === "function" ? getCodeEditorValue(id) : $(id).value;
      return JSON.parse(text || "{}");
    }

    function setEditorJson(id, value) {
      var text = JSON.stringify(value, null, 2);
      $(id).value = text;
      if (typeof setCodeEditorValue === "function") setCodeEditorValue(id, text);
      if (id !== "toolRunArgsInput" && options.markDirty && !state.toolLibraryRendering) options.markDirty();
    }

    function showJsonError(id, message) {
      if ($(id)) $(id).textContent = message || "";
    }

    function formatJson(id, errorId) {
      try {
        var value = editorJson(id);
        setEditorJson(id, value);
        showJsonError(errorId, "");
        return value;
      } catch (error) {
        showJsonError(errorId, "Не удалось открыть форму: исправьте JSON — " + error.message);
        return null;
      }
    }

    function schemaDefaultText(value) {
      return value === undefined ? "" : (typeof value === "string" ? value : JSON.stringify(value));
    }

    function parseSchemaDefault(text, type) {
      if (text === "") return undefined;
      if (type === "boolean") return String(text).toLowerCase() === "true";
      if (type === "number" || type === "integer") return Number(text);
      if (type === "array" || type === "object") return JSON.parse(text);
      return text;
    }

    function syncSchemaDraft() {
      try {
        state.toolSchemaVisualDraft = editorJson("toolSchemaInput");
        showJsonError("toolSchemaError", "");
        return true;
      } catch (error) {
        showJsonError("toolSchemaError", "Ошибка JSON: " + error.message);
        return false;
      }
    }

    function renderSchema() {
      var root = $("toolSchemaVisual");
      if (!root) return;
      if (!state.toolSchemaVisualDraft && !syncSchemaDraft()) return;
      var schema = state.toolSchemaVisualDraft || {};
      schema.type = "object";
      schema.properties = schema.properties || {};
      schema.required = Array.isArray(schema.required) ? schema.required : [];
      schema.additionalProperties = false;
      root.innerHTML = "";
      Object.keys(schema.properties).forEach(function (name) {
        var property = schema.properties[name] || {};
        var row = document.createElement("div");
        row.className = "schema-property-row";
        var nameInput = document.createElement("input"); nameInput.value = name; nameInput.placeholder = "name"; nameInput.title = "Имя параметра";
        var type = document.createElement("select");
        ["string", "integer", "number", "boolean", "array", "object"].forEach(function (value) { var option = document.createElement("option"); option.value = value; option.textContent = value; type.appendChild(option); });
        type.value = property.type || "string";
        var required = document.createElement("input"); required.type = "checkbox"; required.checked = schema.required.indexOf(name) >= 0; required.title = "Обязательный";
        var description = document.createElement("input"); description.value = property.description || ""; description.placeholder = "Описание";
        var defaultValue = document.createElement("input"); defaultValue.value = schemaDefaultText(property.default); defaultValue.placeholder = "Default";
        var remove = document.createElement("button"); remove.type = "button"; remove.className = "secondary danger-soft"; remove.textContent = "×"; remove.title = "Удалить параметр";
        nameInput.addEventListener("change", function () {
          var next = nameInput.value.trim(); if (!next || next === name || schema.properties[next]) { nameInput.value = name; return; }
          schema.properties[next] = property; delete schema.properties[name]; schema.required = schema.required.map(function (item) { return item === name ? next : item; }); setEditorJson("toolSchemaInput", schema); renderSchema();
        });
        type.addEventListener("change", function () { property.type = type.value; setEditorJson("toolSchemaInput", schema); });
        required.addEventListener("change", function () { schema.required = schema.required.filter(function (item) { return item !== name; }); if (required.checked) schema.required.push(name); setEditorJson("toolSchemaInput", schema); });
        description.addEventListener("input", function () { property.description = description.value; setEditorJson("toolSchemaInput", schema); });
        defaultValue.addEventListener("change", function () { try { var parsed = parseSchemaDefault(defaultValue.value, property.type); if (parsed === undefined) delete property.default; else property.default = parsed; showJsonError("toolSchemaError", ""); setEditorJson("toolSchemaInput", schema); } catch (error) { showJsonError("toolSchemaError", "Некорректный default: " + error.message); } });
        remove.addEventListener("click", function () { delete schema.properties[name]; schema.required = schema.required.filter(function (item) { return item !== name; }); setEditorJson("toolSchemaInput", schema); renderSchema(); });
        [nameInput, type, required, description, defaultValue, remove].forEach(function (node) { row.appendChild(node); }); root.appendChild(row);
      });
      var add = document.createElement("button"); add.type = "button"; add.className = "secondary"; add.textContent = "+ Параметр";
      add.addEventListener("click", function () { var index = 1; var name = "argument"; while (schema.properties[name]) name = "argument" + (++index); schema.properties[name] = { type: "string", description: "" }; setEditorJson("toolSchemaInput", schema); renderSchema(); });
      root.appendChild(add);
      Array.prototype.slice.call(root.querySelectorAll("input,select,textarea,button")).forEach(function (control) { control.disabled = !!state.toolBuilderReadOnly; });
      renderRunArguments();
    }

    function renderRunArguments() {
      var root = $("toolRunArgsVisual");
      if (!root) return;
      root.innerHTML = "";
      var schema = state.toolSchemaVisualDraft || {};
      var properties = schema.properties || {};
      var args = {};
      try { args = editorJson("toolRunArgsInput"); } catch (error) { args = {}; }
      Object.keys(properties).forEach(function (name) {
        var property = properties[name] || {};
        var label = document.createElement("label"); label.textContent = name;
        var input = document.createElement("input"); input.value = args[name] === undefined ? "" : (typeof args[name] === "string" ? args[name] : JSON.stringify(args[name])); input.placeholder = property.description || property.type || "value";
        input.addEventListener("change", function () { if (!input.value) delete args[name]; else { try { args[name] = parseSchemaDefault(input.value, property.type || "string"); } catch (error) { args[name] = input.value; } } setEditorJson("toolRunArgsInput", args); });
        label.appendChild(input);
        root.appendChild(label);
      });
      if (!Object.keys(properties).length) root.appendChild(createResourceEmptyState("У инструмента нет параметров."));
    }

    function setMode(mode) {
      var valid = syncSchemaDraft();
      if (mode === "json" && valid) {
        var formatted = formatJson("toolSchemaInput", "toolSchemaError");
        if (formatted) state.toolSchemaVisualDraft = formatted;
      }
      if (mode === "form" && !valid) mode = "json";
      state.toolSchemaMode = mode;
      Array.prototype.slice.call(document.querySelectorAll(".tool-schema-mode")).forEach(function (button) { button.classList.toggle("active", button.getAttribute("data-tool-schema-mode") === mode); });
      var visual = $("toolSchemaVisual"); if (visual) visual.classList.toggle("hidden", mode !== "form");
      Array.prototype.slice.call(document.querySelectorAll(".tool-schema-json")).forEach(function (node) { node.classList.toggle("hidden", mode !== "json"); });
      if (mode === "form") renderSchema();
      else if (typeof refreshCodeEditors === "function") refreshCodeEditors(["toolSchemaInput"]);
    }

    function readRunArguments() {
      if (typeof syncCodeEditors === "function") syncCodeEditors(["toolRunArgsInput"]);
      var text = (typeof getCodeEditorValue === "function" ? getCodeEditorValue("toolRunArgsInput") : $("toolRunArgsInput").value).trim();
      return text ? JSON.parse(text) : {};
    }

    function bind() {
      Array.prototype.slice.call(document.querySelectorAll(".tool-schema-mode")).forEach(function (button) { button.addEventListener("click", function () { setMode(button.getAttribute("data-tool-schema-mode")); }); });
      $("formatToolSchemaButton").addEventListener("click", function () {
        var value = formatJson("toolSchemaInput", "toolSchemaError");
        if (value) state.toolSchemaVisualDraft = value;
      });

    }

    return {
      bind: bind,
      readRunArguments: readRunArguments,
      setMode: setMode,
      syncSchemaDraft: syncSchemaDraft
    };
  }

  window.RNAssistantToolStructuredEditor = { create: create };
}());
