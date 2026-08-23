(function () {
  "use strict";

  function findToolIndex(tools, id) {
    return (tools || []).findIndex(function (tool) {
      return tool && String(tool.Id || "").toLowerCase() === String(id || "").toLowerCase();
    });
  }

  function create(options) {
    options = options || {};
    var state = options.state;

    async function changeVbaInstallation(action) {
      options.syncSelected();
      var tool = state.tools[state.selectedToolIndex];
      if (!tool) return;
      var actionButtonId = action === "installVbaTool" ? "installVbaToolButton" : "uninstallVbaToolButton";
      options.setBusy(actionButtonId, true);
      try {
        if (action === "installVbaTool") {
          var selectedId = tool.Id;
          state.tools = await options.send("saveTools", { tools: options.readTools() }) || state.tools;
          state.selectedToolIndex = findToolIndex(state.tools, selectedId);
          tool = state.tools[state.selectedToolIndex];
          if (!tool) throw new Error("VBA package was not found after saving.");
        }
        var response = await options.send(action, { id: tool.Id, dryRun: false });
        var result = response.result || response.Result || {};
        state.tools = response.tools || response.Tools || state.tools;
        state.selectedToolIndex = findToolIndex(state.tools, tool.Id);
        state.selectedToolComponentIndex = 0;
        options.renderTools();
        options.setOutput(JSON.stringify(result, null, 2));
        options.log(result.Message || result.message || "VBA package state updated.");
      } catch (error) {
        options.setOutput(error.detail || error.message);
        options.log(error.message);
      } finally {
        options.setBusy(actionButtonId, false);
        options.renderEditor();
      }
    }

    async function runSelected(dryRun) {
      if (!options.validateSelected()) {
        options.log("Исправьте JSON инструмента перед запуском.");
        return;
      }
      options.syncSelected();
      var tool = state.tools[state.selectedToolIndex];
      if (!tool) return;

      var runButtonId = dryRun ? "dryRunToolButton" : "runToolButton";
      options.setBusy(runButtonId, true);
      options.setOutput(dryRun ? "Проверка..." : "Выполняю...");
      try {
        var response = await options.send("runTool", {
          toolId: tool.Id,
          arguments: options.readRunArguments(),
          dryRun: !!dryRun
        });
        options.setOutput(JSON.stringify(response, null, 2));
        options.logToolResult(dryRun ? "Проверка инструмента" : "Запуск инструмента", tool.Id, response);
      } catch (error) {
        options.setOutput(error.detail || error.message);
        options.log(error.message);
      } finally {
        options.setBusy(runButtonId, false);
      }
    }

    async function saveTools() {
      options.setBusy("saveToolsButton", true);
      try {
        if (!options.validateSelected()) throw new Error("Исправьте JSON перед сохранением.");
        options.syncSelected();
        options.validateAll();
        var selected = state.tools[state.selectedToolIndex];
        var selectedId = selected ? selected.Id : "";
        var response = await options.send("saveTools", { tools: options.readTools() });
        state.tools = response || [];
        state.selectedToolIndex = selectedId ? findToolIndex(state.tools, selectedId) : -1;
        options.renderTools();
        options.log("Инструменты сохранены.");
      } catch (error) {
        options.log(error.message);
      } finally {
        options.setBusy("saveToolsButton", false);
      }
    }

    return {
      installVba: function () { return changeVbaInstallation("installVbaTool"); },
      run: function () { return runSelected(false); },
      save: saveTools,
      uninstallVba: function () { return changeVbaInstallation("uninstallVbaTool"); },
      validate: function () { return runSelected(true); }
    };
  }

  window.RNAssistantToolActions = { create: create };
}());
