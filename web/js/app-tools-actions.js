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
    var write = null;
    var maximumMutationBytes = 16 * 1024 * 1024;

    function closeUpload(operation) {
      if (!operation || operation.closed || !operation.lease || !/^[a-f0-9]{64}$/.test(operation.lease.leaseId)) return Promise.resolve();
      operation.closed = true;
      return options.send("cancelToolMutationUpload", { chatId: operation.chatId, leaseId: operation.lease.leaseId }).catch(function () {});
    }

    function cancelWrite() {
      if (!write) return;
      write.abort.abort();
      if (write.requestId) options.cancelRequest(write.requestId).catch(function () {});
      closeUpload(write);
    }

    function beginWrite() {
      if (write) throw new Error("Дождитесь завершения записи Tool Library.");
      var operation = { chatId: state.activeChatId, library: state.tools, abort: new AbortController(), possibleEffect: false };
      operation.current = function () { return write === operation && !operation.abort.signal.aborted && !state.bridgeUnavailable &&
        !!operation.chatId && state.activeChatId === operation.chatId && state.tools === operation.library; };
      operation.active = function () { if (!operation.current()) throw new Error("Запись остановлена: контекст Tool Library изменился."); };
      write = operation; state.toolLibraryWriting = true; options.updateWriteState();
      return operation;
    }

    async function endWrite(operation) {
      if (!operation) return;
      await closeUpload(operation);
      if (write === operation) { write = null; state.toolLibraryWriting = false; options.updateWriteState(); }
    }

    function writeError(error, operation) {
      var message = error.detail || error.message;
      return operation && operation.possibleEffect
        ? message + " Обновите Library перед повтором: результат записи не подтверждён в редакторе." : message;
    }

    async function saveUploaded(operation) {
      operation.active();
      if (!options.validateSelected()) throw new Error("Исправьте JSON перед сохранением.");
      options.syncSelected(); options.validateAll();
      var body = options.mutationRequest(), submitted = options.captureSave();
      if (!Array.isArray(body.mutations) || body.mutations.length > 256) throw new Error("RESOURCE_BATCH_TOO_LARGE");
      function validateUnicode(value) {
        if (typeof value === "string") {
          if (value.length > maximumMutationBytes) throw new Error("RESOURCE_BATCH_TOO_LARGE");
          if (new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(new TextEncoder().encode(value)) !== value)
            throw new Error("RESOURCE_UPLOAD_INVALID: некорректный Unicode в определении инструмента.");
        } else if (value && typeof value === "object") Object.keys(value).forEach(function (key) { validateUnicode(value[key]); });
      }
      var length = 256;
      body.mutations.forEach(function (mutation) {
        validateUnicode(mutation); length += JSON.stringify(mutation).length + 1;
        if (length > maximumMutationBytes) throw new Error("RESOURCE_BATCH_TOO_LARGE");
      });
      var bytes = new TextEncoder().encode(JSON.stringify(body));
      if (bytes.length > maximumMutationBytes) throw new Error("RESOURCE_BATCH_TOO_LARGE");
      var hash = Array.from(new Uint8Array(await crypto.subtle.digest("SHA-256", bytes)))
        .map(function (part) { return part.toString(16).padStart(2, "0"); }).join("");
      operation.active();
      try {
        var opening = options.send("beginToolMutationUpload", { chatId: operation.chatId, byteLength: bytes.length });
        operation.requestId = opening.requestId;
        operation.lease = await opening; operation.requestId = null;
        operation.active();
        await window.RNAssistantResourceUpload.write(operation.lease, new Blob([bytes]), {
          maxBytes: maximumMutationBytes, signal: operation.abort.signal, isCurrent: operation.current
        });
        operation.active(); operation.possibleEffect = body.mutations.length > 0;
        var saving = options.send("saveTools", { chatId: operation.chatId, uploadLeaseId: operation.lease.leaseId, sha256: hash });
        operation.requestId = saving.requestId;
        var response = await saving; operation.requestId = null;
        operation.active();
        var saved = options.parseMutation(response);
        if (saved.results.length > body.mutations.length || !saved.failure && saved.results.length !== body.mutations.length ||
            saved.results.some(function (result, index) {
              var mutation = body.mutations[index];
              return result.id !== (mutation.kind === "delete" ? mutation.baseId : mutation.id) &&
                  !(result.status !== "ok" && result.id === mutation.baseId) ||
                result.status !== "ok" && index !== saved.results.length - 1;
            })) throw new Error("Результат записи Tool Library не совпадает с отправленным набором.");
        operation.possibleEffect = saved.results.some(function (result) { return result.status === "unknown" || result.effect === "unknown"; });
        var selectedId = (state.tools[state.selectedToolIndex] || {}).Id;
        options.acknowledgeSave(submitted, saved);
        state.tools = options.reconcile(saved.tools); operation.library = state.tools;
        state.selectedToolIndex = findToolIndex(state.tools, selectedId);
        options.renderTools();
        if (saved.failure) {
          var failure = new Error(saved.failure.message || "Инструменты не сохранены.");
          failure.code = saved.failure.code || "tool_library_mutation_failed";
          throw failure;
        }
        return saved;
      } finally { await closeUpload(operation); }
    }

    async function changeVbaInstallation(action) {
      if (write) { options.log("Дождитесь завершения записи Tool Library.", "error"); return; }
      var actionButtonId = action === "installVbaTool" ? "installVbaToolButton" : "uninstallVbaToolButton";
      var outputKind = "text";
      var outputValue = "";
      var operation;
      try {
        operation = beginWrite(); operation.active();
        options.syncSelected();
        var tool = state.tools[state.selectedToolIndex];
        if (!tool) return;
        options.setBusy(actionButtonId, true);
        if (action === "installVbaTool") {
          var targetId = tool.Id;
          var saved = await saveUploaded(operation);
          tool = state.tools[findToolIndex(state.tools, targetId)];
          if (!tool || tool !== saved.tools[findToolIndex(saved.tools, targetId)])
            throw new Error("Определение изменилось во время сохранения. Сохраните черновик перед установкой.");
        }
        operation.active(); operation.possibleEffect = true;
        var installing = options.send(action, { id: tool.Id, dryRun: false });
        operation.requestId = installing.requestId;
        var response = await installing; operation.requestId = null;
        operation.active();
        var result = response && response.result;
        if (!result || result.contractVersion !== 1 ||
            !["ok", "error", "unknown"].includes(result.status) ||
            !["none", "verified_no_change", "verified_change", "unknown"].includes(result.effect)) {
          throw new Error("VBA package action returned an incompatible result contract.");
        }
        operation.possibleEffect = result.status === "unknown" || result.effect === "unknown";
        var selectedId = (state.tools[state.selectedToolIndex] || {}).Id;
        state.tools = options.reconcile(options.parseLibrary(response.tools)); operation.library = state.tools;
        state.selectedToolIndex = findToolIndex(state.tools, selectedId);
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
        outputValue = writeError(error, operation);
        options.log(outputValue, "error");
      } finally {
        await endWrite(operation);
        options.setBusy(actionButtonId, false);
        options.renderEditor();
        options.updateWriteState();
        if (outputKind === "json") options.setJsonOutput(outputValue);
        else options.setTextOutput(outputValue);
      }
    }

    async function runSelected(dryRun, semanticNext) {
      if (write) { options.log("Дождитесь завершения записи Tool Library.", "error"); return; }
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
      if (write) { options.log("Дождитесь завершения записи Tool Library.", "error"); return; }
      var operation;
      try {
        operation = beginWrite();
        options.setBusy("saveToolsButton", true);
        await saveUploaded(operation);
        options.log("Инструменты сохранены.");
      } catch (error) {
        options.log(writeError(error, operation), "error");
      } finally {
        await endWrite(operation);
        options.setBusy("saveToolsButton", false);
        options.updateWriteState();
      }
    }

    return {
      cancelWrite: cancelWrite,
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
