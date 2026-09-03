(function () {
  "use strict";

  var MAX_COMPLEX_ARGUMENT_CHARS = 1000000;
  var MAX_VALIDATION_DEPTH = 32;

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
        var schema = editorJson("toolSchemaInput");
        if (!schema || Array.isArray(schema) || typeof schema !== "object") throw new Error("schema должна быть объектом");
        state.toolSchemaVisualDraft = schema;
        showJsonError("toolSchemaError", "");
        return true;
      } catch (error) {
        showJsonError("toolSchemaError", "Ошибка JSON: " + error.message);
        return false;
      }
    }

    function typeList(schema) {
      schema = schema || {};
      var types = Array.isArray(schema.type) ? schema.type.slice() : (typeof schema.type === "string" ? [schema.type] : []);
      (Array.isArray(schema.anyOf) ? schema.anyOf : []).forEach(function (candidate) {
        typeList(candidate).forEach(function (type) {
          if (types.indexOf(type) < 0) types.push(type);
        });
      });
      return types;
    }

    function primaryType(schema) {
      return typeList(schema).filter(function (type) { return type !== "null"; })[0] || "null";
    }

    function isNullable(schema) {
      return typeList(schema).indexOf("null") >= 0;
    }

    function isLongString(name, schema) {
      var maximum = Number(schema && schema.maxLength);
      return maximum > 240 || !Number.isFinite(maximum) && /(?:content|markdown|source|code|html|text|body|prompt|note)/i.test(name || "");
    }

    function requirement(schema, name) {
      if ((schema.required || []).indexOf(name) >= 0) return "required";
      var conditional = (schema.anyOf || []).some(function (candidate) {
        return (candidate.required || []).indexOf(name) >= 0;
      });
      return conditional ? "conditional" : "optional";
    }

    function constraintText(schema) {
      var parts = [];
      if (Object.prototype.hasOwnProperty.call(schema, "const")) parts.push("const: " + JSON.stringify(schema.const));
      if (Array.isArray(schema.enum)) parts.push("варианты: " + schema.enum.map(function (value) { return JSON.stringify(value); }).join(", "));
      if (Object.prototype.hasOwnProperty.call(schema, "default")) parts.push("по умолчанию: " + JSON.stringify(schema.default));
      [["minimum", "min"], ["maximum", "max"], ["minLength", "minLength"], ["maxLength", "maxLength"], ["minItems", "minItems"], ["maxItems", "maxItems"]].forEach(function (entry) {
        if (schema[entry[0]] !== undefined) parts.push(entry[1] + ": " + schema[entry[0]]);
      });
      return parts.join(" · ");
    }

    function sameJson(left, right) {
      return JSON.stringify(left) === JSON.stringify(right);
    }

    function valueMatchesType(value, type) {
      if (type === "null") return value === null;
      if (type === "string") return typeof value === "string";
      if (type === "boolean") return typeof value === "boolean";
      if (type === "integer") return typeof value === "number" && Number.isFinite(value) && Number.isInteger(value);
      if (type === "number") return typeof value === "number" && Number.isFinite(value);
      if (type === "array") return Array.isArray(value);
      if (type === "object") return value !== null && !Array.isArray(value) && typeof value === "object";
      return false;
    }

    function validateValue(value, schema, path, depth) {
      schema = schema || {};
      if (depth > MAX_VALIDATION_DEPTH) return path + ": превышена допустимая вложенность JSON.";
      var types = Array.isArray(schema.type) ? schema.type : (typeof schema.type === "string" ? [schema.type] : []);
      if (types.length && !types.some(function (type) { return valueMatchesType(value, type); })) {
        return path + ": ожидается тип " + types.join("/") + ".";
      }
      if (Array.isArray(schema.enum) && !schema.enum.some(function (candidate) { return sameJson(candidate, value); })) {
        return path + ": выберите одно из разрешённых значений.";
      }
      if (Object.prototype.hasOwnProperty.call(schema, "const") && !sameJson(schema.const, value)) {
        return path + ": требуется значение " + JSON.stringify(schema.const) + ".";
      }
      if (typeof value === "number") {
        if (!Number.isFinite(value)) return path + ": число должно быть конечным.";
        if (schema.minimum !== undefined && value < Number(schema.minimum)) return path + ": значение меньше minimum.";
        if (schema.maximum !== undefined && value > Number(schema.maximum)) return path + ": значение больше maximum.";
      }
      if (typeof value === "string") {
        if (schema.minLength !== undefined && value.length < Number(schema.minLength)) return path + ": строка короче minLength.";
        if (schema.maxLength !== undefined && value.length > Number(schema.maxLength)) return path + ": строка длиннее maxLength.";
      }
      if (Array.isArray(value)) {
        if (schema.minItems !== undefined && value.length < Number(schema.minItems)) return path + ": элементов меньше minItems.";
        if (schema.maxItems !== undefined && value.length > Number(schema.maxItems)) return path + ": элементов больше maxItems.";
        if (schema.items) {
          for (var index = 0; index < value.length; index += 1) {
            var itemError = validateValue(value[index], schema.items, path + "[" + index + "]", depth + 1);
            if (itemError) return itemError;
          }
        }
      }
      if (value !== null && !Array.isArray(value) && typeof value === "object") {
        var properties = schema.properties || {};
        var required = schema.required || [];
        for (var requiredIndex = 0; requiredIndex < required.length; requiredIndex += 1) {
          var requiredName = required[requiredIndex];
          if (!Object.prototype.hasOwnProperty.call(value, requiredName)) {
            return path + "." + requiredName + ": обязательное поле не передано.";
          }
        }
        if (schema.additionalProperties === false) {
          var unknown = Object.keys(value).filter(function (name) { return !Object.prototype.hasOwnProperty.call(properties, name); })[0];
          if (unknown) return path + ": неподдерживаемое поле " + unknown + ".";
        }
        var names = Object.keys(value);
        for (var nameIndex = 0; nameIndex < names.length; nameIndex += 1) {
          var name = names[nameIndex];
          if (!properties[name]) continue;
          var propertyError = validateValue(value[name], properties[name], path + "." + name, depth + 1);
          if (propertyError) return propertyError;
        }
      }
      if (Array.isArray(schema.anyOf) && schema.anyOf.length) {
        var errors = schema.anyOf.map(function (candidate) {
          return validateValue(value, candidate, path, depth + 1);
        });
        if (!errors.some(function (error) { return !error; })) {
          return errors[0] || path + ": значение не соответствует ни одному варианту schema.";
        }
      }
      return "";
    }

    function validateRunArguments(args, showError) {
      var schema = state.toolSchemaVisualDraft || {};
      var error = validateValue(args, schema, "$", 0);
      if (showError !== false) showJsonError("toolRunArgsError", error);
      return error;
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
        type.value = primaryType(property) === "null" ? "string" : primaryType(property);
        var required = document.createElement("input"); required.type = "checkbox"; required.checked = schema.required.indexOf(name) >= 0; required.title = "Обязательный";
        var description = document.createElement("input"); description.value = property.description || ""; description.placeholder = "Описание";
        var defaultValue = document.createElement("input"); defaultValue.value = schemaDefaultText(property.default); defaultValue.placeholder = "Default";
        var remove = document.createElement("button"); remove.type = "button"; remove.className = "secondary danger-soft"; remove.textContent = "×"; remove.title = "Удалить параметр";
        nameInput.addEventListener("change", function () {
          var next = nameInput.value.trim(); if (!next || next === name || schema.properties[next]) { nameInput.value = name; return; }
          schema.properties[next] = property; delete schema.properties[name]; schema.required = schema.required.map(function (item) { return item === name ? next : item; }); setEditorJson("toolSchemaInput", schema); renderSchema();
        });
        type.addEventListener("change", function () { property.type = type.value; delete property.anyOf; setEditorJson("toolSchemaInput", schema); renderRunArguments(); });
        required.addEventListener("change", function () { schema.required = schema.required.filter(function (item) { return item !== name; }); if (required.checked) schema.required.push(name); setEditorJson("toolSchemaInput", schema); renderRunArguments(); });
        description.addEventListener("input", function () { property.description = description.value; setEditorJson("toolSchemaInput", schema); });
        defaultValue.addEventListener("change", function () { try { var parsed = parseSchemaDefault(defaultValue.value, primaryType(property)); if (parsed === undefined) delete property.default; else property.default = parsed; showJsonError("toolSchemaError", ""); setEditorJson("toolSchemaInput", schema); renderRunArguments(); } catch (error) { showJsonError("toolSchemaError", "Некорректный default: " + error.message); } });
        remove.addEventListener("click", function () { delete schema.properties[name]; schema.required = schema.required.filter(function (item) { return item !== name; }); setEditorJson("toolSchemaInput", schema); renderSchema(); });
        [nameInput, type, required, description, defaultValue, remove].forEach(function (node) { row.appendChild(node); }); root.appendChild(row);
      });
      var add = document.createElement("button"); add.type = "button"; add.className = "secondary"; add.textContent = "+ Параметр";
      add.addEventListener("click", function () { var index = 1; var name = "argument"; while (schema.properties[name]) name = "argument" + (++index); schema.properties[name] = { type: "string", description: "" }; setEditorJson("toolSchemaInput", schema); renderSchema(); });
      root.appendChild(add);
      Array.prototype.slice.call(root.querySelectorAll("input,select,textarea,button")).forEach(function (control) { control.disabled = !!state.toolBuilderReadOnly; });
      renderRunArguments();
    }

    function createValueControl(name, schema, value) {
      var type = primaryType(schema);
      var control;
      if (Array.isArray(schema.enum)) {
        control = document.createElement("select");
        control._rnValues = {};
        schema.enum.forEach(function (entry, index) {
          if (entry === null) return;
          var option = document.createElement("option");
          option.value = String(index);
          option.textContent = typeof entry === "string" ? entry : JSON.stringify(entry);
          option._rnValue = entry;
          control._rnValues[option.value] = entry;
          control.appendChild(option);
          if (sameJson(entry, value)) control.value = option.value;
        });
        control._rnRead = function () {
          return Object.prototype.hasOwnProperty.call(control._rnValues, control.value)
            ? control._rnValues[control.value]
            : schema.enum.filter(function (entry) { return entry !== null; })[0];
        };
      } else if (type === "boolean") {
        control = document.createElement("input");
        control.type = "checkbox";
        control.checked = value === undefined ? !!schema.default : !!value;
        control._rnRead = function () { return !!control.checked; };
      } else if (type === "number" || type === "integer") {
        control = document.createElement("input");
        control.type = "number";
        control.step = type === "integer" ? "1" : "any";
        if (schema.minimum !== undefined) control.min = String(schema.minimum);
        if (schema.maximum !== undefined) control.max = String(schema.maximum);
        var numberValue = value === undefined ? schema.default : value;
        control.value = numberValue === undefined ? "" : String(numberValue);
        control._rnRead = function () {
          if (control.value === "") throw new Error(name + ": введите число.");
          var parsed = Number(control.value);
          if (!Number.isFinite(parsed) || type === "integer" && !Number.isInteger(parsed)) throw new Error(name + ": неверный числовой тип.");
          return parsed;
        };
      } else if (type === "array" || type === "object") {
        control = document.createElement("textarea");
        control.rows = 5;
        control.maxLength = MAX_COMPLEX_ARGUMENT_CHARS;
        control.className = "tool-argument-json";
        var complexValue = value === undefined ? schema.default : value;
        control.value = complexValue === undefined ? (type === "array" ? "[]" : "{}") : JSON.stringify(complexValue, null, 2);
        control._rnRead = function () {
          if (control.value.length > MAX_COMPLEX_ARGUMENT_CHARS) throw new Error(name + ": JSON слишком большой.");
          return JSON.parse(control.value || (type === "array" ? "[]" : "{}"));
        };
      } else if (isLongString(name, schema)) {
        control = document.createElement("textarea");
        control.rows = 5;
        control.value = value === undefined ? (schema.default === undefined ? "" : String(schema.default)) : String(value);
        if (schema.maxLength !== undefined) control.maxLength = Number(schema.maxLength);
        control._rnRead = function () { return control.value; };
      } else {
        control = document.createElement("input");
        control.type = "text";
        control.value = value === undefined ? (schema.default === undefined ? "" : String(schema.default)) : String(value);
        if (schema.maxLength !== undefined) control.maxLength = Number(schema.maxLength);
        if (schema.minLength !== undefined) control.minLength = Number(schema.minLength);
        control._rnRead = function () { return control.value; };
      }
      if (Object.prototype.hasOwnProperty.call(schema, "const")) {
        control.disabled = true;
        control._rnRead = function () { return schema.const; };
      }
      return control;
    }

    function renderRunArguments() {
      var root = $("toolRunArgsVisual");
      if (!root) return;
      root.innerHTML = "";
      var schema = state.toolSchemaVisualDraft || {};
      var properties = schema.properties || {};
      var args;
      try {
        args = editorJson("toolRunArgsInput");
        if (!args || Array.isArray(args) || typeof args !== "object") throw new Error("аргументы должны быть JSON object");
      } catch (error) {
        showJsonError("toolRunArgsError", "Ошибка JSON: " + error.message);
        return;
      }
      showJsonError("toolRunArgsError", "");
      var initializedRequiredValues = false;
      Object.keys(properties).forEach(function (name) {
        var property = properties[name] || {};
        var required = requirement(schema, name);
        var hasValue = Object.prototype.hasOwnProperty.call(args, name);
        if (!hasValue && required === "required" &&
            (Object.prototype.hasOwnProperty.call(property, "const") ||
             Object.prototype.hasOwnProperty.call(property, "default"))) {
          args[name] = Object.prototype.hasOwnProperty.call(property, "const")
            ? property.const : property.default;
          hasValue = true;
          initializedRequiredValues = true;
        }
        var currentValue = hasValue ? args[name] : undefined;
        var row = document.createElement("div");
        row.className = "tool-argument-row";
        var head = document.createElement("div");
        head.className = "tool-argument-head";
        var label = document.createElement("label");
        label.textContent = name;
        var badge = document.createElement("span");
        badge.className = "tool-argument-requirement is-" + required;
        badge.textContent = required === "required" ? "обязательный" : required === "conditional" ? "условный" : "необязательный";
        head.appendChild(label);
        head.appendChild(badge);

        var body = document.createElement("div");
        body.className = "tool-argument-control-row";
        var mode = null;
        if (required !== "required" || isNullable(property)) {
          mode = document.createElement("select");
          mode.className = "tool-argument-mode";
          if (required !== "required") {
            var omit = document.createElement("option"); omit.value = "omit"; omit.textContent = "Не передавать"; mode.appendChild(omit);
          }
          var use = document.createElement("option"); use.value = "value"; use.textContent = "Значение"; mode.appendChild(use);
          if (isNullable(property)) {
            var nullOption = document.createElement("option"); nullOption.value = "null"; nullOption.textContent = "null"; mode.appendChild(nullOption);
          }
          mode.value = !hasValue && required !== "required" ? "omit" : currentValue === null ? "null" : "value";
          body.appendChild(mode);
        }
        var control = createValueControl(name, property, currentValue);
        body.appendChild(control);
        var fieldError = document.createElement("div");
        fieldError.className = "tool-argument-error";
        fieldError.setAttribute("role", "alert");

        function setEnabled() {
          var active = !mode || mode.value === "value";
          control.disabled = !active || Object.prototype.hasOwnProperty.call(property, "const");
        }
        function update() {
          fieldError.textContent = "";
          try {
            var selectedMode = mode ? mode.value : "value";
            setEnabled();
            if (selectedMode === "omit") delete args[name];
            else if (selectedMode === "null") args[name] = null;
            else args[name] = control._rnRead();
            setEditorJson("toolRunArgsInput", args);
            validateRunArguments(args, true);
          } catch (error) {
            fieldError.textContent = error.message;
          }
        }
        if (mode) mode.addEventListener("change", update);
        control.addEventListener(control.type === "checkbox" || control.tagName === "SELECT" ? "change" : "input", update);
        setEnabled();

        var description = document.createElement("div");
        description.className = "tool-argument-description";
        description.textContent = property.description || "Описание параметра отсутствует.";
        var constraints = document.createElement("div");
        constraints.className = "tool-argument-constraints";
        constraints.textContent = ["тип: " + typeList(property).join(" / "), constraintText(property)].filter(Boolean).join(" · ");
        row.appendChild(head);
        row.appendChild(body);
        row.appendChild(description);
        row.appendChild(constraints);
        row.appendChild(fieldError);
        root.appendChild(row);
      });
      if (initializedRequiredValues) setEditorJson("toolRunArgsInput", args);
      if (!Object.keys(properties).length) root.appendChild(createResourceEmptyState("У инструмента нет параметров."));
      validateRunArguments(args, true);
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
      if (!state.toolSchemaVisualDraft && !syncSchemaDraft()) throw new Error("Исправьте JSON Schema.");
      var args;
      try {
        args = editorJson("toolRunArgsInput");
      } catch (error) {
        showJsonError("toolRunArgsError", "Ошибка JSON: " + error.message);
        throw error;
      }
      if (!args || Array.isArray(args) || typeof args !== "object") {
        var shapeError = new Error("Аргументы должны быть одним JSON object.");
        showJsonError("toolRunArgsError", shapeError.message);
        throw shapeError;
      }
      var validation = validateRunArguments(args, true);
      if (validation) throw new Error(validation);
      return args;
    }

    function setRunArguments(args) {
      setEditorJson("toolRunArgsInput", args || {});
      renderRunArguments();
    }

    function readNextArguments() {
      var args = readRunArguments();
      var schema = state.toolSchemaVisualDraft || {};
      var action = (schema.properties || {}).action || {};
      if (!Array.isArray(action.enum) || action.enum.indexOf("next") < 0 ||
          typeof args.id !== "string" || !args.id ||
          typeof args.referencePath !== "string" || !args.referencePath) {
        throw new Error("Для продолжения нужны exact id/referencePath и schema action=next.");
      }
      args.action = "next";
      setRunArguments(args);
      return args;
    }

    function applyRunJson() {
      try {
        var args = editorJson("toolRunArgsInput");
        if (!args || Array.isArray(args) || typeof args !== "object") throw new Error("аргументы должны быть JSON object");
        var validation = validateRunArguments(args, true);
        if (validation) throw new Error(validation);
        setRunArguments(args);
      } catch (error) {
        showJsonError("toolRunArgsError", "Ошибка аргументов: " + error.message);
      }
    }

    function bind() {
      Array.prototype.slice.call(document.querySelectorAll(".tool-schema-mode")).forEach(function (button) { button.addEventListener("click", function () { setMode(button.getAttribute("data-tool-schema-mode")); }); });
      $("formatToolSchemaButton").addEventListener("click", function () {
        var value = formatJson("toolSchemaInput", "toolSchemaError");
        if (value) state.toolSchemaVisualDraft = value;
      });
      $("applyToolRunJsonButton").addEventListener("click", applyRunJson);
      var advanced = $("toolRunAdvancedJson");
      if (advanced) advanced.addEventListener("toggle", function () {
        if (advanced.open && typeof refreshCodeEditors === "function") {
          refreshCodeEditors(["toolRunArgsInput"]);
        }
      });
    }

    return {
      bind: bind,
      readNextArguments: readNextArguments,
      readRunArguments: readRunArguments,
      renderRunArguments: renderRunArguments,
      setMode: setMode,
      setRunArguments: setRunArguments,
      syncSchemaDraft: syncSchemaDraft,
      validateRunArguments: validateRunArguments
    };
  }

  window.RNAssistantToolStructuredEditor = { create: create };
}());
