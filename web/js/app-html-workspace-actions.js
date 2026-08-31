(function () {
  "use strict";

  function toolSucceeded(result) {
    return !!(result && (result.Success === true || result.success === true));
  }

  function toolMessage(result, fallback) {
    return result && (result.Message || result.message) || fallback;
  }

  function toolData(result) {
    try { return JSON.parse(result && (result.DataJson || result.dataJson) || "{}"); }
    catch (ignore) { return {}; }
  }

  function value(source, pascal, camel, fallback) {
    source = source || {};
    return source[camel] !== undefined ? source[camel] : (source[pascal] !== undefined ? source[pascal] : fallback);
  }

  function same(left, right) {
    return String(left || "").toLowerCase() === String(right || "").toLowerCase();
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
    var planMutationPending = false;
    var planHandoffPending = false;
    var htmlImportPending = false;

    function uploadedHtmlPreviewCache() {
      state.uploadedHtmlSourcePreviews = state.uploadedHtmlSourcePreviews || {};
      return state.uploadedHtmlSourcePreviews;
    }

    function cacheUploadedHtmlPreview(uri, preview) {
      var cache = uploadedHtmlPreviewCache();
      if (!cache[uri] && Object.keys(cache).length >= 8) delete cache[Object.keys(cache)[0]];
      cache[uri] = preview;
    }

    function uploadedHtmlPreview(uri) {
      return uploadedHtmlPreviewCache()[uri] || null;
    }

    async function refreshPlan(planId, chatId) {
      if (state.activeChatId !== chatId) return false;
      var response = await options.send("selectChat", { chatId: chatId });
      return options.applyPlanRefresh(planId, response, chatId);
    }

    async function savePlan(selection, chatId) {
      var plan = options.validatePlanDraft(selection.item);
      var result = await options.send("runTool", {
        toolId: "common.plan_doc_update",
        arguments: { id: plan.id, expectedRevisionArtifactId: plan.expectedRevisionArtifactId, title: plan.title, markdown: plan.markdown, status: "draft" },
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

    async function deletePlan(selected, chatId) {
      if (planMutationPending) return false;
      planMutationPending = true;
      try {
        var args = {
          id: selected.planId,
          expectedRevisionArtifactId: selected.expectedRevisionArtifactId
        };
        var preview = await options.send("runTool", {
          toolId: "common.plan_doc_delete",
          arguments: args,
          dryRun: true
        });
        if (!toolSucceeded(preview)) throw new Error(toolMessage(preview, "План нельзя удалить."));
        var data = toolData(preview);
        var messageIds = value(data, "ReferencingMessageIds", "referencingMessageIds", []) || [];
        var revisions = Number(value(data, "RemovedRevisions", "removedRevisions", 0) || 0);
        var warning = (state.htmlWorkspaceDirty ? "Есть несохранённые изменения.\n\n" : "") +
          "Удалить план «" + selected.label + "»?\n" +
          "Будет добавлена ревизия удаления; " + revisions + " предыдущих версий останутся в журнале чата.\n" +
          "Точные ссылки останутся в сообщениях как заглушки «Ресурс удалён».\n\n" +
          (messageIds.length
            ? "Ссылки в сообщениях (" + messageIds.length + "):\n" + messageIds.join("\n")
            : "Ссылок в сообщениях нет.");
        if (!window.confirm(warning)) return false;
        var result = await options.send("runTool", {
          toolId: "common.plan_doc_delete",
          arguments: args,
          dryRun: false
        });
        if (!toolSucceeded(result)) throw new Error(toolMessage(result, "План не удалён."));
        if (!await refreshPlan(selected.planId, chatId)) return false;
        options.log("План удалён: " + selected.label);
        return true;
      } finally {
        planMutationPending = false;
      }
    }

    async function deleteSelection(target) {
      var selected = target && typeof target.type === "string" ? target : options.getSelection();
      if (!selected || selected.type === "artifact" || state.bridgeUnavailable) return;
      var chatId = state.activeChatId;

      try {
        if (selected.type === "plan") {
          return await deletePlan(selected, chatId);
        }
        var warning = "Удалить «" + selected.label + "» из HTML? Удаление можно отменить через Undo.";
        if (state.htmlWorkspaceDirty) warning = "Есть несохраненные изменения. " + warning;
        if (!window.confirm(warning)) return;
        var response = selected.type === "data"
          ? await options.send("deleteHtmlWorkspaceData", { chatId: chatId, name: selected.name })
          : await options.send("deleteHtmlWorkspaceFile", { chatId: chatId, path: selected.path });
        if (state.activeChatId !== chatId) return;
        state.htmlWorkspaceSelection = { type: "file", id: "" };
        options.applyWorkspaceResponse(response, chatId);
        options.log("Удалено из HTML: " + selected.label);
      } catch (error) {
        options.log(error.detail || error.message, "error");
        window.alert(error.message || (selected.type === "plan" ? "План не удалён." : "Элемент HTML workspace не удален."));
      }
    }

    async function restorePlanRevision(request) {
      request = request || {};
      if (state.bridgeUnavailable || !request.planId || !request.expectedRevisionArtifactId ||
          !request.sourceRevisionArtifactId || planMutationPending) return false;
      planMutationPending = true;
      try {
        var label = "v" + Number(request.revision || 1);
        var warning = (state.htmlWorkspaceDirty ? "Несохранённые изменения будут отброшены.\n\n" : "") +
          "Восстановить " + label + " как новую версию плана? Историческая ревизия останется неизменной.";
        if (!window.confirm(warning)) return false;
        var chatId = state.activeChatId;
        var result = await options.send("runTool", {
          toolId: "common.plan_doc_restore",
          arguments: {
            id: request.planId,
            expectedRevisionArtifactId: request.expectedRevisionArtifactId,
            sourceRevisionArtifactId: request.sourceRevisionArtifactId
          },
          dryRun: false
        });
        if (!toolSucceeded(result)) throw new Error(toolMessage(result, "Ревизия плана не восстановлена."));
        if (!await refreshPlan(request.planId, chatId)) return false;
        options.log(label + " восстановлена как новая версия плана.");
        return true;
      } catch (error) {
        options.log(error.detail || error.message, "error");
        window.alert(error.message || "Ревизия плана не восстановлена.");
        return false;
      } finally {
        planMutationPending = false;
      }
    }

    async function handoffPlan(request) {
      request = request || {};
      if (state.bridgeUnavailable || state.activeTaskListArtifactId ||
          !request.expectedRevisionArtifactId || !request.revisionUri || planHandoffPending) return false;
      if (!same(state.activePlanDocumentArtifactId, request.expectedRevisionArtifactId)) return false;
      var current = (state.artifacts || []).filter(function (artifact) {
        return same(value(artifact, "Id", "id", ""), request.expectedRevisionArtifactId);
      })[0] || null;
      var exactUri = value(current, "ResourceUri", "resourceUri", "") || "";
      var metadata = {};
      try { metadata = JSON.parse(value(current, "MetadataJson", "metadataJson", "{}") || "{}"); }
      catch (ignore) {}
      if (!current || exactUri !== request.revisionUri || !/^rna:\/\//i.test(exactUri) ||
          String(value(metadata, "Status", "status", "draft")).toLowerCase() !== "ready") return false;
      var chatId = state.activeChatId;
      planHandoffPending = true;
      try {
        if (!options.switchChatMode || !await options.switchChatMode("agent")) return false;
        if (state.activeChatId !== chatId || !options.submitPlanHandoff) return false;
        return options.submitPlanHandoff(exactUri) !== false;
      } finally {
        planHandoffPending = false;
      }
    }

    async function loadUploadedHtmlSource(request) {
      request = request || {};
      var uri = request.sourceResourceUri || "";
      if (state.bridgeUnavailable || !uri) return false;
      var current = uploadedHtmlPreview(uri);
      if (current && (current.status === "loading" || current.status === "ready")) return current.status === "ready";
      var chatId = state.activeChatId;
      cacheUploadedHtmlPreview(uri, { status: "loading" });
      if (options.render) options.render();
      try {
        var response = await options.send("getUploadedHtmlSourcePreview", {
          chatId: chatId,
          sourceResourceUri: uri
        });
        if (state.activeChatId !== chatId) {
          delete uploadedHtmlPreviewCache()[uri];
          return false;
        }
        var returnedUri = value(response, "SourceResourceUri", "sourceResourceUri", "") || "";
        if (returnedUri !== uri) throw new Error("Источник HTML изменился; preview отклонён.");
        cacheUploadedHtmlPreview(uri, {
          status: "ready",
          sourceResourceUri: returnedUri,
          text: value(response, "Text", "text", "") || "",
          returnedCharacters: Number(value(response, "ReturnedCharacters", "returnedCharacters", 0) || 0),
          totalCharacters: Number(value(response, "TotalCharacters", "totalCharacters", 0) || 0),
          complete: value(response, "Complete", "complete", false) === true,
          truncated: value(response, "Truncated", "truncated", false) === true
        });
        if (options.render) options.render();
        return true;
      } catch (error) {
        if (state.activeChatId !== chatId) {
          delete uploadedHtmlPreviewCache()[uri];
          return false;
        }
        cacheUploadedHtmlPreview(uri, {
          status: "error",
          message: error.detail || error.message || "Исходник HTML недоступен."
        });
        options.log(error.detail || error.message, "error");
        if (options.render) options.render();
        return false;
      }
    }

    async function importUploadedHtml(request) {
      request = request || {};
      var uri = request.sourceResourceUri || "";
      if (state.bridgeUnavailable || htmlImportPending || !uri) return false;
      var suggestedPath = request.targetPath || "index.html";
      var targetPath = typeof window.prompt === "function"
        ? window.prompt("Путь нового файла в HTML workspace", suggestedPath)
        : suggestedPath;
      if (targetPath === null || !String(targetPath).trim()) return false;
      targetPath = String(targetPath).trim();
      if (!window.confirm(
        "Импортировать загруженный HTML как «" + targetPath + "»?\n\n" +
        "Оригинал останется неизменным и инертным. Выполнение начнётся только в sandbox preview HTML workspace."
      )) return false;
      var chatId = state.activeChatId;
      htmlImportPending = true;
      try {
        var response = await options.send("importUploadedHtmlToWorkspace", {
          chatId: chatId,
          sourceResourceUri: uri,
          expectedActiveHtmlArtifactId: state.activeHtmlArtifactId || "",
          targetPath: targetPath
        });
        if (state.activeChatId !== chatId) return false;
        var returnedUri = value(response, "ImportedFromResourceUri", "importedFromResourceUri", "") || "";
        var importedPath = value(response, "ImportedPath", "importedPath", "") || "";
        if (returnedUri !== uri || !importedPath) throw new Error("HTML import returned stale provenance.");
        if (!options.applyWorkspaceResponse(response, chatId)) return false;
        state.htmlWorkspaceSelection = { type: "file", id: importedPath.toLowerCase() };
        state.htmlWorkspaceMode = "preview";
        if (options.render) options.render();
        options.log("HTML импортирован: " + importedPath);
        return true;
      } catch (error) {
        options.log(error.detail || error.message, "error");
        window.alert(error.message || "HTML не импортирован.");
        return false;
      } finally {
        htmlImportPending = false;
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
        var response = await options.send(method, {
          chatId: actionState.chatId,
          snapshotId: snapshotId
        });
        if (!options.applyWorkspaceResponse(response, actionState.chatId)) return;
        if (direction === "redo" && (response.redoChoiceRequired || response.RedoChoiceRequired)) {
          options.log("Выберите ветку HTML redo.");
          return;
        }
        options.log(direction === "redo" ? "HTML workspace redo выполнен." : "HTML workspace восстановлен.");
      } catch (error) {
        options.log(error.detail || error.message, "error");
        window.alert(error.message || (direction === "redo" ? "HTML workspace redo не выполнен." : "HTML workspace не восстановлен."));
      }
    }

    async function recoverRevision() {
      var actionState = options.getActionState();
      if (actionState.bridgeUnavailable || !actionState.recoverySnapshotId) return;
      if (actionState.dirty && !window.confirm("Восстановление отменит несохранённые изменения. Продолжить?")) return;
      try {
        var response = await options.send("restoreHtmlWorkspaceSnapshot", {
          chatId: actionState.chatId,
          snapshotId: actionState.recoverySnapshotId
        });
        if (!options.applyWorkspaceResponse(response, actionState.chatId)) return;
        options.log("HTML workspace восстановлен на выбранную ревизию.");
      } catch (error) {
        options.log(error.detail || error.message, "error");
        window.alert(error.message || "Выбранная HTML-ревизия недоступна.");
      }
    }

    async function createPlan() {
      if (state.bridgeUnavailable) return;
      var chatId = state.activeChatId;
      try {
        var result = await options.send("runTool", {
          toolId: "common.plan_doc_create",
          arguments: {
            title: "Новый план",
            markdown: "# Новый план\n\nОпишите цель, решения, этапы и проверку.",
            status: "draft"
          },
          dryRun: false
        });
        if (!toolSucceeded(result)) throw new Error(toolMessage(result, "План не создан."));
        var payload = {};
        try { payload = JSON.parse(result.DataJson || result.dataJson || "{}"); } catch (ignore) {}
        if (!await refreshPlan(payload.planId || payload.PlanId || "", chatId)) return;
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
      handoffPlan: handoffPlan,
      importUploadedHtml: importUploadedHtml,
      loadUploadedHtmlSource: loadUploadedHtmlSource,
      recoverRevision: recoverRevision,
      refreshAll: function () { return refreshData("", "all", true); },
      refreshAuto: function () { return refreshData("", "on_preview", false); },
      redo: function () { return restore("redo"); },
      restorePlanRevision: restorePlanRevision,
      saveSelection: saveSelection,
      uploadedHtmlPreview: uploadedHtmlPreview,
      undo: function () { return restore("undo"); }
    };
  }

  window.RNAssistantHtmlWorkspaceActions = { create: create };
}());
