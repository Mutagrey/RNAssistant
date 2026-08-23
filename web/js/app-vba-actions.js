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
        options.log(error.detail || error.message);
        return false;
      }
    }

    async function refreshProject() {
      await runWork(async function () {
        var response = await options.send("getVbaProject", {});
        options.applyProjectResponse(response);
        await options.loadSelectedModule();
      });
    }

    async function saveModule() {
      var moduleName = options.getModuleName();
      if (!moduleName) {
        return;
      }

      options.previewDiff();
      if (await runWork(async function () {
        var response = await options.send("saveVbaModule", {
          moduleName: moduleName,
          code: options.getEditorCode()
        });
        options.setStatus(response.Message || response.message || "VBA-модуль сохранен.");
      })) {
        await refreshProject();
      }
    }

    async function restoreBackup() {
      var backupId = options.getBackupId();
      var moduleName = options.getModuleName();
      if (await runWork(async function () {
        var response = await options.send("restoreVbaBackup", {
          backupId: backupId,
          moduleName: moduleName
        });
        options.setStatus(response.Message || response.message || "Резервная копия VBA восстановлена.");
      })) {
        await refreshProject();
      }
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
        options.log(error.detail || error.message);
      } finally {
        options.updateMacroRunState();
      }
    }

    return {
      refreshProject: refreshProject,
      restoreBackup: restoreBackup,
      runMacro: runMacro,
      saveModule: saveModule
    };
  }

  window.RNAssistantVbaActions = { create: create };
}());
