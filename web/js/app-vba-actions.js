(function () {
  "use strict";

  function create(options) {
    options = options || {};

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

    async function refreshProject() {
      return runWork(async function () {
        var response = await options.send("getVbaProject", {});
        options.applyProjectResponse(response);
        await options.loadSelectedModule();
      });
    }

    async function createModule(moduleName, componentType, code) {
      var created = await runWork(async function () {
        var response = await options.send("createVbaModule", {
          moduleName: moduleName,
          componentType: componentType,
          code: code
        });
        if (response.Success === false || response.success === false) {
          throw new Error(response.Message || response.message || "VBA-компонент не создан.");
        }
        options.setStatus(response.Message || response.message || "VBA-компонент создан: " + moduleName);
        options.log(response.Message || response.message || "VBA-компонент создан: " + moduleName, "success");
      });
      if (created) {
        if (typeof options.selectModule === "function") options.selectModule(moduleName);
        await refreshProject();
      }
      return created;
    }

    async function deleteModule(moduleName, expectedCodeSha256) {
      var deleted = await runWork(async function () {
        var response = await options.send("deleteVbaModule", {
          moduleName: moduleName,
          expectedCodeSha256: expectedCodeSha256
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
      var saved = await runWork(async function () {
        var response = await options.send("saveVbaModule", {
          moduleName: moduleName,
          code: options.getEditorCode()
        });
        options.setStatus(response.Message || response.message || "VBA-модуль сохранен.");
      });
      if (saved) {
        if (typeof options.markSaved === "function") options.markSaved();
        await refreshProject();
      }
      return saved;
    }

    async function restoreBackup() {
      var backupId = options.getBackupId();
      var moduleName = options.getModuleName();
      var restored = await runWork(async function () {
        var response = await options.send("restoreVbaBackup", {
          backupId: backupId,
          moduleName: moduleName
        });
        options.setStatus(response.Message || response.message || "Резервная копия VBA восстановлена.");
      });
      if (restored) {
        if (typeof options.markSaved === "function") options.markSaved();
        await refreshProject();
      }
      return restored;
    }

    async function runMacro() {
      var toolId = options.getMacroToolId();
      var macroName = options.getMacroName();
      if (!toolId) {
        options.setMacroStatus("Текущее приложение не поддерживает запуск макросов.", "error");
        return;
      }
      if (!macroName) {
        options.setMacroStatus("Введите имя макроса.", "error");
        return;
      }

      options.setMacroBusy(true);
      try {
        var response = await options.send("runTool", {
          toolId: toolId,
          arguments: { macroName: macroName },
          dryRun: false
        });
        options.setMacroStatus(response.Message || response.message || "Макрос выполнен: " + macroName, "ok");
        options.logToolResult("Запуск макроса", toolId, response);
      } catch (error) {
        options.setMacroStatus(error.detail || error.message, "error");
        options.log(error.detail || error.message, "error");
      } finally {
        options.updateMacroRunState();
      }
    }

    return {
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
