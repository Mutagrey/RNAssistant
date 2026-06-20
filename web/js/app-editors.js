(function () {
  var editors = {};
  var configs = {
    toolSchemaInput: { mode: { name: "javascript", json: true }, minHeight: 120 },
    toolRunArgsInput: { mode: { name: "javascript", json: true }, minHeight: 120 },
    toolPipelineInput: { mode: { name: "javascript", json: true }, minHeight: 210 },
    toolCodeInput: { mode: "vb", minHeight: 210 },
    toolReadmeInput: { mode: "markdown", minHeight: 150, lineWrapping: true },
    skillBodyInput: { mode: "markdown", minHeight: 360, lineWrapping: true },
    vbaCodeInput: { mode: "vb", minHeight: 440 }
  };

  function textarea(id) {
    return document.getElementById(id);
  }

  function addEditorClass(cm, id, config) {
    var wrapper = cm.getWrapperElement();
    wrapper.className += " rn-code-editor rn-code-editor-" + id;
    wrapper.style.minHeight = (config.minHeight || 160) + "px";
  }

  function createEditor(id, config) {
    var node = textarea(id);
    if (!node || !window.CodeMirror || editors[id]) {
      return editors[id] || null;
    }

    var cm = window.CodeMirror.fromTextArea(node, {
      mode: config.mode || null,
      lineNumbers: true,
      lineWrapping: !!config.lineWrapping,
      indentUnit: 2,
      tabSize: 2,
      styleActiveLine: true,
      matchBrackets: true,
      autoCloseBrackets: true,
      viewportMargin: 80,
      extraKeys: {
        "Ctrl-S": function (editor) {
          editor.save();
          if (id === "vbaCodeInput" && typeof saveVbaModule === "function") {
            saveVbaModule();
          }
        },
        "Cmd-S": function (editor) {
          editor.save();
          if (id === "vbaCodeInput" && typeof saveVbaModule === "function") {
            saveVbaModule();
          }
        }
      }
    });

    addEditorClass(cm, id, config);
    cm.on("change", function () {
      cm.save();
      if (cm._rnSettingValue) {
        return;
      }
      if (id === "vbaCodeInput" && typeof markVbaEditorDirty === "function") {
        markVbaEditorDirty();
      }
    });

    editors[id] = cm;
    return cm;
  }

  window.initializeCodeEditors = function () {
    Object.keys(configs).forEach(function (id) {
      createEditor(id, configs[id]);
    });
  };

  window.syncCodeEditors = function (ids) {
    var keys = ids || Object.keys(editors);
    keys.forEach(function (id) {
      if (editors[id]) {
        editors[id].save();
      }
    });
  };

  window.refreshCodeEditors = function (ids) {
    var keys = ids || Object.keys(editors);
    window.setTimeout(function () {
      keys.forEach(function (id) {
        if (editors[id]) {
          editors[id].refresh();
        }
      });
    }, 0);
  };

  window.getCodeEditorValue = function (id) {
    if (editors[id]) {
      editors[id].save();
      return editors[id].getValue();
    }
    var node = textarea(id);
    return node ? node.value : "";
  };

  window.setCodeEditorValue = function (id, value) {
    value = value || "";
    if (editors[id]) {
      if (editors[id].getValue() !== value) {
        editors[id]._rnSettingValue = true;
        try {
          editors[id].setValue(value);
        } finally {
          editors[id]._rnSettingValue = false;
        }
      }
      editors[id].save();
      editors[id].refresh();
      return;
    }
    var node = textarea(id);
    if (node) {
      node.value = value;
    }
  };

  window.setCodeEditorReadOnly = function (id, readOnly) {
    if (editors[id]) {
      editors[id].setOption("readOnly", readOnly ? "nocursor" : false);
      editors[id].getWrapperElement().classList.toggle("cm-readonly", !!readOnly);
    }
  };
}());
