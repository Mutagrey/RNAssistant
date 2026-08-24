(function () {
  "use strict";

  function toolSucceeded(result) {
    return !!(result && (result.Success === true || result.success === true));
  }

  function toolMessage(result, fallback) {
    return result && (result.Message || result.message) || fallback;
  }

  function defaultFileContent(kind) {
    if (kind === "css") return "body {\n  font-family: Segoe UI, Arial, sans-serif;\n}\n";
    if (kind === "script") {
      return "(function () {\n  var data = window.RNAssistantData || {};\n  console.log(\"HTML workspace data\", data);\n}());\n";
    }
    return "<!doctype html>\n<html>\n<head>\n  <meta charset=\"utf-8\">\n  <title>HTML Workspace</title>\n</head>\n<body>\n  <h1>HTML Workspace</h1>\n</body>\n</html>\n";
  }

  function create(options) {
    options = options || {};
    var state = options.state;
    var refreshPending = false;

    async function refreshPlan(planId, chatId) {
      if (state.activeChatId !== chatId) return false;
      var response = await options.send("selectChat", { chatId: chatId });
      return options.applyPlanRefresh(planId, response, chatId);
    }

    async function savePlan(selection, chatId) {
      var plan = options.validatePlanDraft(selection.item);
      var result = await options.send("runTool", {
        toolId: "common.plan_update",
        arguments: { id: plan.id, goal: plan.goal, steps: plan.steps },
        dryRun: false
      });
      if (!toolSucceeded(result)) throw new Error(toolMessage(result, "План не сохранён."));
      if (!await refreshPlan(plan.id, chatId)) return false;
      options.log("План сохранён как новая версия.");
      return true;
    }

    async function saveSelection() {
      var selected = options.getSelection();
      if (!selected || selected.type === "artifact" || state.bridgeUnavailable) return;
      var chatId = state.activeChatId;
      options.syncEditor();
      selected = options.getSelection();
      if (!selected || selected.type === "artifact") return;
      try {
        if (selected.type === "plan") {
          await savePlan(selected, chatId);
          return;
        }
        if (selected.type === "data") {
          if (selected.binding && !window.confirm("Сохранение JSON отключит привязку к Office и автообновление. Продолжить?")) return;
          if (!options.applyWorkspaceResponse(await options.send("saveHtmlWorkspaceData", {
            chatId: chatId,
            name: selected.name,
            json: selected.json
          }), chatId)) return;
        } else {
          if (!options.applyWorkspaceResponse(await options.send("saveHtmlWorkspaceFile", {
            chatId: chatId,
            path: selected.path,
            kind: selected.kind,
            content: selected.content,
            setActive: selected.kind === "html"
          }), chatId)) return;
        }
        options.log("Артефакт сохранён.");
      } catch (error) {
        options.log(error.detail || error.message, "error");
        window.alert(error.message || "Артефакт не сохранён.");
      }
    }

    async function deleteSelection(target) {
      var selected = target && typeof target.type === "string" ? target : options.getSelection();
      if (!selected || selected.type === "artifact" || state.bridgeUnavailable) return;
      var warning = selected.type === "plan"
        ? "Удалить план «" + selected.label + "» и все его версии?"
        : "Удалить «" + selected.label + "» из HTML? Удаление можно отменить через Undo.";
      if (state.htmlWorkspaceDirty) warning = "Есть несохраненные изменения. " + warning;
      if (!window.confirm(warning)) return;
      var chatId = state.activeChatId;

      try {
        if (selected.type === "plan") {
          var result = await options.send("runTool", {
            toolId: "common.plan_delete",
            arguments: { id: selected.planId },
            dryRun: false
          });
          if (!toolSucceeded(result)) throw new Error(toolMessage(result, "План не удалён."));
          if (!await refreshPlan(selected.planId, chatId)) return;
          options.log("План удалён: " + selected.label);
          return;
        }
        var response = selected.type === "data"
          ? await options.send("deleteHtmlWorkspaceData", { chatId: chatId, name: selected.name })
          : await options.send("deleteHtmlWorkspaceFile", { chatId: chatId, path: selected.path });
        if (state.activeChatId !== chatId) return;
        state.htmlWorkspaceSelection = { type: "file", id: "" };
        options.applyWorkspaceResponse(response, chatId);
        options.log("Удалено из HTML: " + selected.label);
      } catch (error) {
        options.log(error.detail || error.message, "error");
        window.alert(error.message || "Элемент HTML workspace не удален.");
      }
    }

    async function restore(direction) {
      var actionState = options.getActionState();
      var snapshotId = direction === "redo" ? actionState.redoSnapshotId : actionState.undoSnapshotId;
      if (actionState.bridgeUnavailable || !snapshotId) return;
      var confirmation = direction === "redo"
        ? "Есть несохраненные изменения. Повторить отмененную версию?"
        : "Есть несохраненные изменения. Вернуть предыдущую версию?";
      if (actionState.dirty && !window.confirm(confirmation)) return;

      var method = direction === "redo" ? "redoHtmlWorkspaceSnapshot" : "restoreHtmlWorkspaceSnapshot";
      try {
        if (!options.applyWorkspaceResponse(await options.send(method, {
          chatId: actionState.chatId,
          snapshotId: snapshotId
        }), actionState.chatId)) return;
        options.log(direction === "redo" ? "HTML workspace redo выполнен." : "HTML workspace восстановлен.");
      } catch (error) {
        options.log(error.detail || error.message, "error");
        window.alert(error.message || (direction === "redo" ? "HTML workspace redo не выполнен." : "HTML workspace не восстановлен."));
      }
    }

    async function createPlan() {
      if (state.bridgeUnavailable) return;
      var chatId = state.activeChatId;
      try {
        var result = await options.send("runTool", {
          toolId: "common.plan_create",
          arguments: {
            goal: "Новый план",
            steps: [{ id: "step_1", text: "Опишите первый шаг", status: "pending" }]
          },
          dryRun: false
        });
        if (!toolSucceeded(result)) throw new Error(toolMessage(result, "План не создан."));
        var payload = {};
        try { payload = JSON.parse(result.DataJson || result.dataJson || "{}"); } catch (ignore) {}
        var plan = payload.plan || payload.Plan || {};
        if (!await refreshPlan(plan.id || plan.Id || "", chatId)) return;
        if (state.activeChatId !== chatId) return;
        state.htmlWorkspaceMode = "preview";
        options.render();
        options.log("План создан.");
      } catch (error) {
        options.log(error.detail || error.message, "error");
        window.alert(error.message || "План не создан.");
      }
    }

    async function createFile(kind, path) {
      var chatId = state.activeChatId;
      try {
        options.applyWorkspaceResponse(await options.send("saveHtmlWorkspaceFile", {
          chatId: chatId,
          path: path,
          kind: kind,
          content: defaultFileContent(kind),
          setActive: kind === "html"
        }), chatId);
        if (state.activeChatId !== chatId) return;
        state.htmlWorkspaceSelection = { type: "file", id: path.toLowerCase() };
        options.hideCreate();
        options.render();
      } catch (error) {
        options.log(error.detail || error.message, "error");
        window.alert(error.message || "Файл не создан.");
      }
    }

    async function createData(name) {
      var chatId = state.activeChatId;
      try {
        options.applyWorkspaceResponse(await options.send("saveHtmlWorkspaceData", {
          chatId: chatId,
          name: name,
          json: "{\n  \"items\": []\n}\n"
        }), chatId);
        if (state.activeChatId !== chatId) return;
        state.htmlWorkspaceSelection = { type: "data", id: name.toLowerCase() };
        options.hideCreate();
        options.render();
      } catch (error) {
        options.log(error.detail || error.message, "error");
        window.alert(error.message || "Data source не создан.");
      }
    }

    async function refreshData(name, policy, interactive) {
      if (state.bridgeUnavailable || refreshPending) return;
      if (typeof options.hasRefreshableData === "function" && !options.hasRefreshableData(policy)) return;
      if (state.htmlWorkspaceDirty) {
        if (!interactive || !window.confirm("Обновление данных отменит несохранённые изменения. Продолжить?")) return;
      }
      refreshPending = true;
      var chatId = state.activeChatId;
      state.htmlWorkspaceRefreshPending = true;
      options.render();
      try {
        var args = { policy: policy || "all" };
        if (name) args.name = name;
        var result = await options.send("runTool", {
          toolId: "common.html_data_refresh",
          arguments: args,
          dryRun: false
        });
        if (state.activeChatId !== chatId) return;
        if (!options.applyWorkspaceResponse(await options.send("getHtmlWorkspace", { chatId: chatId }), chatId)) return;
        if (!toolSucceeded(result)) {
          throw new Error(toolMessage(result, "Данные обновлены частично."));
        }
        options.log(toolMessage(result, "Данные HTML обновлены."));
      } catch (error) {
        options.log(error.detail || error.message, "error");
        if (interactive) window.alert(error.message || "Данные HTML не обновлены.");
      } finally {
        refreshPending = false;
        state.htmlWorkspaceRefreshPending = false;
        options.render();
      }
    }

    return {
      createData: createData,
      createFile: createFile,
      createPlan: createPlan,
      deleteSelection: deleteSelection,
      refreshAll: function () { return refreshData("", "all", true); },
      refreshAuto: function () { return refreshData("", "on_preview", false); },
      redo: function () { return restore("redo"); },
      saveSelection: saveSelection,
      undo: function () { return restore("undo"); }
    };
  }

  window.RNAssistantHtmlWorkspaceActions = { create: create };
}());
