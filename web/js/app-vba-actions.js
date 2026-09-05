(function () {
  "use strict";

  function create(options) {
    options = options || {};
    var writing = null;

    function cancelWrite() {
      if (!writing) return;
      writing.abort.abort();
      if (writing.requestId) options.cancelRequest(writing.requestId).catch(function () {});
      writing.close();
    }

    async function writeSource(type, payload, code, onSuccess) {
      if (writing) { options.setStatus("Дождитесь завершения текущей записи VBA."); return false; }
      var chatId = options.getChatId(), project = options.getProject();
      var operation = { abort: new AbortController(), requestId: null, lease: null, closed: false, dispatched: false };
      writing = operation;
      function sameContext() {
        return options.isAvailable() && options.getChatId() === chatId && options.getProject() === project &&
          (type !== "saveVbaModule" || options.getModuleName() === payload.moduleName &&
            options.getEditorCode() === code && options.getModuleHash() === payload.expectedCodeSha256);
      }
      function current() { return writing === operation && !operation.abort.signal.aborted && sameContext(); }
      function active() { if (!current()) throw new Error("RESOURCE_UPLOAD_CANCELLED"); }
      operation.close = function () {
        if (operation.closed || !operation.lease || !/^[a-f0-9]{64}$/.test(operation.lease.leaseId)) return Promise.resolve();
        operation.closed = true;
        return options.send("cancelVbaModuleUpload", { chatId: chatId, leaseId: operation.lease.leaseId }).catch(function () {});
      };
      try {
        active();
        if (!chatId || typeof code !== "string" || code.length > 1000000) throw new Error("RESOURCE_BATCH_TOO_LARGE");
        if (type === "saveVbaModule" && !/^[a-fA-F0-9]{64}$/.test(payload.expectedCodeSha256))
          throw new Error("Перед сохранением загрузите полный исходный код модуля.");
        var bytes = new TextEncoder().encode(code);
        if (new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(bytes) !== code)
          throw new Error("RESOURCE_UPLOAD_INVALID: некорректный Unicode в коде.");
        var hash = Array.from(new Uint8Array(await crypto.subtle.digest("SHA-256", bytes)))
          .map(function (value) { return value.toString(16).padStart(2, "0"); }).join("");
        active();
        var opening = options.send("beginVbaModuleUpload", { chatId: chatId, byteLength: bytes.length });
        operation.requestId = opening.requestId;
        operation.lease = await opening;
        operation.requestId = null;
        active();
        await window.RNAssistantResourceUpload.write(operation.lease, new Blob([bytes]), {
          maxBytes: 4000000, signal: operation.abort.signal, isCurrent: current
        });
        active();
        payload.chatId = chatId;
        payload.uploadLeaseId = operation.lease.leaseId;
        payload.sourceSha256 = hash;
        operation.dispatched = true;
        var saving = options.send(type, payload);
        operation.requestId = saving.requestId;
        var response = await saving;
        operation.requestId = null;
        active();
        if (!response || response.Success !== true && response.success !== true)
          throw new Error(response && (response.Message || response.message) || "Запись VBA не подтверждена.");
        await operation.close();
        active();
        writing = null;
        await onSuccess(response, sameContext);
        return true;
      } catch (error) {
        if (options.getChatId() === chatId && options.isAvailable()) {
          options.setStatus(operation.dispatched && !current()
            ? "Результат записи VBA не подтверждён в редакторе. Обновите модуль перед повторной записью."
            : (error.detail || error.message));
          options.log(error.detail || error.message, "error");
        }
        return false;
      } finally {
        await operation.close();
        if (writing === operation) writing = null;
      }
    }

    async function runWork(work) {
      try {
        await work();
        return true;
      } catch (error) {
        options.setStatus(error.message);
        options.log(error.detail || error.message, "error");
        return false;
      }
    }

    async function refreshProject(isCurrent) {
      var chatId = options.getChatId();
      return runWork(async function () {
        var response = await options.send("getVbaProject", {});
        if (options.getChatId() !== chatId || !options.isAvailable() || isCurrent && !isCurrent()) return;
        var result = response && (response.Result || response.result || response);
        if (!result || result.Success === false || result.success === false) {
          throw new Error((result && (result.Message || result.message)) || "VBA-проект не загружен.");
        }
        options.applyProjectResponse(response);
        await options.loadSelectedModule();
      });
    }

    async function createModule(moduleName, componentType, code) {
      return writeSource("createVbaModule", { moduleName: moduleName, componentType: componentType }, code, async function (response, isCurrent) {
        options.setStatus(response.Message || response.message || "VBA-компонент создан: " + moduleName);
        options.log(response.Message || response.message || "VBA-компонент создан: " + moduleName, "success");
        if (typeof options.selectModule === "function") options.selectModule(moduleName);
        await refreshProject(isCurrent);
      });
    }

    async function deleteModule(moduleName) {
      var deleted = await runWork(async function () {
        var response = await options.send("deleteVbaModule", {
          moduleName: moduleName
        });
        if (response.Success === false || response.success === false) {
          throw new Error(response.Message || response.message || "VBA-модуль не удалён.");
        }
        options.setStatus(response.Message || response.message || "VBA-модуль удалён: " + moduleName);
        options.log(response.Message || response.message || "VBA-модуль удалён: " + moduleName, "success");
      });
      if (deleted) await refreshProject();
      return deleted;
    }

    async function saveModule() {
      var moduleName = options.getModuleName();
      if (!moduleName) {
        return false;
      }

      options.previewDiff();
      return writeSource("saveVbaModule", { moduleName: moduleName, expectedCodeSha256: options.getModuleHash() },
        options.getEditorCode(), async function (response, isCurrent) {
        options.setStatus(response.Message || response.message || "VBA-модуль сохранен.");
        if (typeof options.markSaved === "function") options.markSaved();
        await refreshProject(isCurrent);
      });
    }

    async function restoreBackup() {
      var backupId = options.getBackupId();
      var moduleName = options.getModuleName();
      var restored = await runWork(async function () {
        var response = await options.send("restoreVbaBackup", {
          backupId: backupId,
          moduleName: moduleName
        });
        if (response.Success === false || response.success === false) {
          throw new Error(response.Message || response.message || "Резервная копия VBA не восстановлена.");
        }
        options.setStatus(response.Message || response.message || "Резервная копия VBA восстановлена.");
      });
      if (restored) {
        if (typeof options.markSaved === "function") options.markSaved();
        await refreshProject();
      }
      return restored;
    }

    async function runMacro() {
      var macroName = options.getMacroName();
      if (!macroName) {
        options.setMacroStatus("Введите имя макроса.", "error");
        return;
      }

      options.setMacroBusy(true);
      try {
        var response = await options.send("runVbaMacro", { macroName: macroName });
        if (response.Success === false || response.success === false) {
          throw new Error(response.Message || response.message || "VBA-макрос не выполнен.");
        }
        options.setMacroStatus(response.Message || response.message || "Макрос выполнен: " + macroName, "ok");
        options.logToolResult("Запуск макроса", "VBA", response);
      } catch (error) {
        options.setMacroStatus(error.detail || error.message, "error");
        options.log(error.detail || error.message, "error");
      } finally {
        options.updateMacroRunState();
      }
    }

    return {
      cancelWrite: cancelWrite,
      createModule: createModule,
      deleteModule: deleteModule,
      refreshProject: refreshProject,
      restoreBackup: restoreBackup,
      runMacro: runMacro,
      saveModule: saveModule
    };
  }

  window.RNAssistantVbaActions = { create: create };
}());
