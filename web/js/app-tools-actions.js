(function () {
  "use strict";

  function findToolIndex(tools, id) {
    return (tools || []).findIndex(function (tool) {
      return tool && String(tool.Id || "").toLowerCase() === String(id || "").toLowerCase();
    });
  }

  function requireToolRunResult(result) {
    if (!result || result.type !== "rnassistant.toolRunResult" ||
        result.contractVersion !== 1 || typeof result.success !== "boolean" ||
        ["ok", "error", "unknown", "awaiting_confirmation", "awaiting_user", "cancelled"].indexOf(result.status) < 0 ||
        typeof result.message !== "string" ||
        result.dataJson !== null && typeof result.dataJson !== "string" ||
        typeof result.toolStepsConsumed !== "number" ||
        result.code !== null && typeof result.code !== "string" ||
        result.retryable !== null && typeof result.retryable !== "boolean" ||
        result.pendingId !== null && typeof result.pendingId !== "string" ||
        result.catalogRevision !== null && typeof result.catalogRevision !== "string") {
      throw new Error("Tool Library получила несовместимый ToolRunResult v1.");
    }
    return result;
  }

  function continuationFrom(tool, args, result) {
    if (!tool || tool.Id !== "common.capabilities_read" ||
        !args || typeof args.id !== "string" || !args.id ||
        typeof args.referencePath !== "string" || !args.referencePath ||
        result.status !== "ok" || !result.dataJson) return null;
    try {
      var data = JSON.parse(result.dataJson);
      return data && data.kind === "reference" && data.id === args.id &&
        data.path === args.referencePath && data.hasMore === true &&
        data.complete !== true
        ? { toolId: tool.Id, id: args.id, referencePath: args.referencePath }
        : null;
    } catch (error) {
      return null;
    }
  }

  function create(options) {
    options = options || {};
    var state = options.state;

    async function changeVbaInstallation(action) {
      options.syncSelected();
      var tool = state.tools[state.selectedToolIndex];
      if (!tool) return;
      var actionButtonId = action === "installVbaTool" ? "installVbaToolButton" : "uninstallVbaToolButton";
      var outputKind = "text";
      var outputValue = "";
      options.setBusy(actionButtonId, true);
      try {
        if (action === "installVbaTool") {
          var selectedId = tool.Id;
          var saved = options.parseMutation(await options.send(
            "saveTools", options.mutationRequest()));
          state.tools = saved.failure && options.reconcile
            ? options.reconcile(saved.tools) : saved.tools;
          if (saved.failure) {
            var saveFailure = new Error(saved.failure.message ||
              "Инструмент не сохранён.");
            saveFailure.code = saved.failure.code ||
              "tool_library_mutation_failed";
            throw saveFailure;
          }
          if (options.acceptSaved) options.acceptSaved();
          state.selectedToolIndex = findToolIndex(state.tools, selectedId);
          tool = state.tools[state.selectedToolIndex];
          if (!tool) throw new Error("VBA package was not found after saving.");
        }
        var response = await options.send(action, { id: tool.Id, dryRun: false });
        var result = response && response.result;
        if (!result || result.contractVersion !== 1 ||
            !["ok", "error", "unknown"].includes(result.status) ||
            !["none", "verified_no_change", "verified_change", "unknown"].includes(result.effect)) {
          throw new Error("VBA package action returned an incompatible result contract.");
        }
        state.tools = options.parseLibrary(response.tools);
        state.selectedToolIndex = findToolIndex(state.tools, tool.Id);
        state.selectedToolComponentIndex = 0;
        options.renderTools();
        outputKind = "json";
        outputValue = result;
        if (result.status !== "ok") {
          throw new Error(result.message || result.code ||
            "VBA package state could not be verified.");
        }
        options.log(result.message || "VBA package state updated.");
      } catch (error) {
        outputValue = error.detail || error.message;
        options.log(error.message, "error");
      } finally {
        options.setBusy(actionButtonId, false);
        options.renderEditor();
        if (outputKind === "json") options.setJsonOutput(outputValue);
        else options.setTextOutput(outputValue);
      }
    }

    async function runSelected(dryRun, semanticNext) {
      if (!options.validateSelected()) {
        options.log("Исправьте JSON инструмента перед запуском.", "warning");
        return;
      }
      options.syncSelected();
      var tool = state.tools[state.selectedToolIndex];
      if (!tool) return;

      var runButtonId = semanticNext ? "nextToolPageButton" : dryRun ? "dryRunToolButton" : "runToolButton";
      options.setBusy(runButtonId, true);
      options.setTextOutput(semanticNext ? "Читаю следующую часть..." : dryRun ? "Проверка..." : "Выполняю...");
      try {
        var args = semanticNext ? options.readNextArguments() : options.readRunArguments();
        if (options.setContinuation) options.setContinuation(null);
        var response = requireToolRunResult(await options.send("runTool", {
          toolId: tool.Id,
          arguments: args,
          dryRun: !!dryRun
        }));
        options.setJsonOutput(response);
        if (options.setContinuation) options.setContinuation(continuationFrom(tool, args, response));
        options.logToolResult(semanticNext ? "Продолжение чтения" : dryRun ? "Проверка инструмента" : "Запуск инструмента", tool.Id, response);
        return response;
      } catch (error) {
        if (options.setContinuation) options.setContinuation(null);
        options.setTextOutput(error.detail || error.message);
        options.log(error.message, "error");
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
        var response = options.parseMutation(await options.send(
          "saveTools", options.mutationRequest()));
        state.tools = response.failure && options.reconcile
          ? options.reconcile(response.tools) : response.tools;
        state.selectedToolIndex = selectedId ? findToolIndex(state.tools, selectedId) : -1;
        if (response.failure) {
          var failure = new Error(response.failure.message ||
            "Инструменты не сохранены.");
          failure.detail = response.failure.message;
          failure.code = response.failure.code ||
            "tool_library_mutation_failed";
          throw failure;
        }
        if (options.acceptSaved) options.acceptSaved();
        options.renderTools();
        options.log("Инструменты сохранены.");
      } catch (error) {
        options.log(error.message, "error");
      } finally {
        options.setBusy("saveToolsButton", false);
      }
    }

    return {
      installVba: function () { return changeVbaInstallation("installVbaTool"); },
      next: function () { return runSelected(false, true); },
      run: function () { return runSelected(false); },
      save: saveTools,
      uninstallVba: function () { return changeVbaInstallation("uninstallVbaTool"); },
      validate: function () { return runSelected(true); }
    };
  }

  window.RNAssistantToolActions = { create: create };
}());
