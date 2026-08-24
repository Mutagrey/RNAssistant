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

    async function refreshPlan(planId) {
      var response = await options.send("selectChat", { chatId: state.activeChatId });
      options.applyPlanRefresh(planId, response);
    }

    async function savePlan(selection) {
      var plan = options.validatePlanDraft(selection.item);
      var result = await options.send("runTool", {
        toolId: "common.plan_update",
        arguments: { id: plan.id, goal: plan.goal, steps: plan.steps },
        dryRun: false
      });
      if (!toolSucceeded(result)) throw new Error(toolMessage(result, "План не сохранён."));
      await refreshPlan(plan.id);
      options.log("План сохранён как новая версия.");
    }

    async function saveSelection() {
      var selected = options.getSelection();
      if (!selected || selected.type === "artifact" || state.bridgeUnavailable) return;
      options.syncEditor();
      selected = options.getSelection();
      if (!selected || selected.type === "artifact") return;
      try {
        if (selected.type === "plan") {
          await savePlan(selected);
          return;
        }
        if (selected.type === "data") {
          options.applyWorkspaceResponse(await options.send("saveHtmlWorkspaceData", {
            chatId: state.activeChatId,
            name: selected.name,
            json: selected.json
          }));
        } else {
          options.applyWorkspaceResponse(await options.send("saveHtmlWorkspaceFile", {
            chatId: state.activeChatId,
            path: selected.path,
            kind: selected.kind,
            content: selected.content,
            setActive: selected.kind === "html"
          }));
        }
        options.log("Артефакт сохранён.");
      } catch (error) {
        options.log(error.detail || error.message, "error");
        window.alert(error.message || "Артефакт не сохранён.");
      }
    }

    async function deleteSelection() {
      var selected = options.getSelection();
      if (!selected || selected.type === "artifact" || state.bridgeUnavailable) return;
      var warning = selected.type === "plan"
        ? "Удалить план «" + selected.label + "» и все его версии?"
        : "Удалить «" + selected.label + "» из HTML? Удаление можно отменить через Undo.";
      if (state.htmlWorkspaceDirty) warning = "Есть несохраненные изменения. " + warning;
      if (!window.confirm(warning)) return;

      try {
        if (selected.type === "plan") {
          var result = await options.send("runTool", {
            toolId: "common.plan_delete",
            arguments: { id: selected.planId },
            dryRun: false
          });
          if (!toolSucceeded(result)) throw new Error(toolMessage(result, "План не удалён."));
          await refreshPlan(selected.planId);
          options.log("План удалён: " + selected.label);
          return;
        }
        var response = selected.type === "data"
          ? await options.send("deleteHtmlWorkspaceData", { chatId: state.activeChatId, name: selected.name })
          : await options.send("deleteHtmlWorkspaceFile", { chatId: state.activeChatId, path: selected.path });
        state.htmlWorkspaceSelection = { type: "file", id: "" };
        options.applyWorkspaceResponse(response);
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
        options.applyWorkspaceResponse(await options.send(method, {
          chatId: actionState.chatId,
          snapshotId: snapshotId
        }));
        options.log(direction === "redo" ? "HTML workspace redo выполнен." : "HTML workspace восстановлен.");
      } catch (error) {
        options.log(error.detail || error.message, "error");
        window.alert(error.message || (direction === "redo" ? "HTML workspace redo не выполнен." : "HTML workspace не восстановлен."));
      }
    }

    async function createPlan() {
      if (state.bridgeUnavailable) return;
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
        await refreshPlan(plan.id || plan.Id || "");
        state.htmlWorkspaceMode = "preview";
        options.render();
        options.log("План создан.");
      } catch (error) {
        options.log(error.detail || error.message, "error");
        window.alert(error.message || "План не создан.");
      }
    }

    async function createFile(kind, path) {
      try {
        options.applyWorkspaceResponse(await options.send("saveHtmlWorkspaceFile", {
          chatId: state.activeChatId,
          path: path,
          kind: kind,
          content: defaultFileContent(kind),
          setActive: kind === "html"
        }));
        state.htmlWorkspaceSelection = { type: "file", id: path.toLowerCase() };
        options.hideCreate();
        options.render();
      } catch (error) {
        options.log(error.detail || error.message, "error");
        window.alert(error.message || "Файл не создан.");
      }
    }

    async function createData(name) {
      try {
        options.applyWorkspaceResponse(await options.send("saveHtmlWorkspaceData", {
          chatId: state.activeChatId,
          name: name,
          json: "{\n  \"items\": []\n}\n"
        }));
        state.htmlWorkspaceSelection = { type: "data", id: name.toLowerCase() };
        options.hideCreate();
        options.render();
      } catch (error) {
        options.log(error.detail || error.message, "error");
        window.alert(error.message || "Data source не создан.");
      }
    }

    return {
      createData: createData,
      createFile: createFile,
      createPlan: createPlan,
      deleteSelection: deleteSelection,
      redo: function () { return restore("redo"); },
      saveSelection: saveSelection,
      undo: function () { return restore("undo"); }
    };
  }

  window.RNAssistantHtmlWorkspaceActions = { create: create };
}());
