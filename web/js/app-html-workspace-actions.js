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
      return "(function () {\n  console.log(\"Resource bindings\", RN.resources.names());\n}());\n";
    }
    return "<!doctype html>\n<html>\n<head>\n  <meta charset=\"utf-8\">\n  <title>HTML Workspace</title>\n</head>\n<body>\n  <h1>HTML Workspace</h1>\n</body>\n</html>\n";
  }

  function create(options) {
    options = options || {};
    var state = options.state;
    var artifactViewers = window.RNAssistantArtifactViewerActions.create(options);
    var refreshPending = false;
    var planMutationPending = false;
    var planHandoffPending = false;
    var htmlImportPending = false;
    var workspaceWrite = null;
    state.htmlWorkspaceExportPending = false;

    async function closeWorkspaceUpload(operation) {
      if (!operation.closed && operation.lease && /^[a-f0-9]{64}$/.test(operation.lease.leaseId)) {
        operation.closed = true;
        await options.send("cancelHtmlWorkspaceMutationUpload", {
          chatId: operation.chatId, leaseId: operation.lease.leaseId
        }).catch(function () {});
      }
    }

    function cancelWrite() {
      var operation = workspaceWrite;
      if (!operation) return;
      operation.abort.abort();
      if (operation.requestId && options.cancelRequest) options.cancelRequest(operation.requestId).catch(function () {});
      closeWorkspaceUpload(operation);
    }

    function draftState() {
      options.syncEditor();
      var selected = options.getSelection() || {};
      return { type: selected.type, item: selected.item, content: selected.content, json: selected.json,
        planText: selected.type === "plan" ? value(selected.item, "InlineText", "inlineText", "") : null,
        dirty: !!state.htmlWorkspaceDirty };
    }

    async function writeWorkspace(action, controls, content, creating) {
      if (workspaceWrite || !state.activeChatId || state.bridgeUnavailable)
        throw new Error("Сохранение уже выполняется или чат недоступен.");
      if (creating && state.htmlWorkspaceDirty) throw new Error("Сначала сохраните изменения текущего артефакта.");
      if (typeof state.activeHtmlArtifactId !== "string") throw new Error("Сначала загрузите HTML workspace.");
      if (!state.htmlWorkspace || state.htmlWorkspace.revisionArtifactId !== state.activeHtmlArtifactId)
        throw new Error("Черновик workspace устарел. Скопируйте правки и перезагрузите исходники.");
      if (typeof content !== "string" || content.length > 300000) throw new Error("Лимит исходного текста — 300000 символов.");
      var bytes = new TextEncoder().encode(content);
      if (new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(bytes) !== content)
        throw new Error("Некорректный Unicode в исходном тексте.");
      var operation = { chatId: state.activeChatId, revision: state.activeHtmlArtifactId,
        workspace: state.htmlWorkspace, draft: draftState(), abort: new AbortController(), dispatched: false };
      function current() { return workspaceWrite === operation && !operation.abort.signal.aborted && !state.bridgeUnavailable &&
        state.activeChatId === operation.chatId && state.activeHtmlArtifactId === operation.revision && state.htmlWorkspace === operation.workspace; }
      function unchanged() {
        var draft = draftState();
        return Object.keys(operation.draft).every(function (key) { return draft[key] === operation.draft[key]; });
      }
      function active() { if (!current()) throw new Error("Контекст сохранения изменился."); }
      workspaceWrite = operation;
      try {
        var hash = Array.from(new Uint8Array(await crypto.subtle.digest("SHA-256", bytes)))
          .map(function (part) { return part.toString(16).padStart(2, "0"); }).join("");
        active();
        var opening = options.send("beginHtmlWorkspaceMutationUpload", { chatId: operation.chatId, byteLength: bytes.length });
        operation.requestId = opening.requestId;
        operation.lease = await opening; operation.requestId = null; active();
        await window.RNAssistantResourceUpload.write(operation.lease, new Blob([bytes]),
          { maxBytes: 1200000, signal: operation.abort.signal, isCurrent: current });
        active();
        if (!unchanged()) throw new Error("Черновик изменён во время загрузки. Запись не начата; сохраните актуальные правки.");
        var saving = options.send(action, Object.assign({}, controls, { chatId: operation.chatId,
          expectedActiveHtmlArtifactId: operation.revision, uploadLeaseId: operation.lease.leaseId, sha256: hash }));
        operation.dispatched = true; operation.requestId = saving.requestId;
        var response = await saving; operation.requestId = null; active();
        await closeWorkspaceUpload(operation); active();
        if (!response || value(response, "ActiveChatId", "activeChatId", null) !== operation.chatId ||
            typeof value(response, "ActiveHtmlArtifactId", "activeHtmlArtifactId", null) !== "string" ||
            !value(response, "ActiveHtmlArtifactId", "activeHtmlArtifactId", ""))
          throw new Error("Не получено подтверждение точного workspace.");
        if (!unchanged()) {
          var notice = "Отправленная версия сохранена. Новые правки оставлены в редакторе; скопируйте их и обновите workspace перед следующим сохранением.";
          options.log(notice); window.alert(notice); return false;
        }
        if (!options.applyWorkspaceResponse(response, operation.chatId))
          throw new Error("Ответ сохранения устарел; workspace не заменён.");
        return true;
      } catch (error) {
        if (operation.dispatched) throw new Error((error.message || "Сохранение прервано.") +
          " Запись могла завершиться. Скопируйте правки и обновите workspace перед повтором.");
        throw error;
      } finally {
        await closeWorkspaceUpload(operation);
        if (workspaceWrite === operation) workspaceWrite = null;
      }
    }

    async function refreshPlan(planId, chatId) {
      if (state.activeChatId !== chatId) return false;
      var response = await options.send("selectChat", { chatId: chatId });
      return options.applyPlanRefresh(planId, response, chatId);
    }

    async function savePlan(selection, chatId) {
      var plan = options.validatePlanDraft(selection.item);
      var result = await options.send("runTool", {
        toolId: "common.plan_doc_save",
        arguments: { title: plan.title, markdown: plan.markdown, status: "draft" },
        dryRun: false
      });
      if (!toolSucceeded(result)) throw new Error(toolMessage(result, "План не сохранён."));
      if (!await refreshPlan(plan.id, chatId)) return false;
      options.log("План сохранён как новая версия.");
      return true;
    }

    async function saveSelection() {
      var selected = options.getSelection();
      if (!selected || selected.type === "artifact" || selected.type === "collection" || state.bridgeUnavailable) return;
      var chatId = state.activeChatId;
      options.syncEditor();
      selected = options.getSelection();
      if (!selected || selected.type === "artifact" || selected.type === "collection") return;
      try {
        if (selected.type === "plan") {
          await savePlan(selected, chatId);
          return;
        }
        if (selected.type === "data") {
          if (selected.binding && !window.confirm("Сохранение JSON отключит привязку к Office и автообновление. Продолжить?")) return;
          if (!await writeWorkspace("saveHtmlWorkspaceData", { name: selected.name }, selected.json, false)) return;
        } else {
          if (!await writeWorkspace("saveHtmlWorkspaceFile", {
            path: selected.path,
            kind: selected.kind,
            setActive: selected.kind === "html"
          }, selected.content, false)) return;
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
        var args = {};
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
      if (!selected || selected.type === "artifact" || selected.type === "collection" || state.bridgeUnavailable) return;
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
      if (state.bridgeUnavailable || !request.planId || Number(request.revision || 0) < 1 ||
          planMutationPending) return false;
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
            version: Number(request.revision)
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

    async function exportWorkspace() {
      if (state.bridgeUnavailable || state.htmlWorkspaceDirty || state.htmlWorkspaceExportPending ||
          !state.activeChatId || !state.activeHtmlArtifactId) return false;
      var chatId = state.activeChatId;
      var expectedArtifactId = state.activeHtmlArtifactId;
      state.htmlWorkspaceExportPending = true;
      var resourceExport = null, exportArtifactId = "";
      if (options.render) options.render();
      try {
        var response = await options.send("prepareHtmlWorkspaceExport", {
          chatId: chatId,
          expectedActiveHtmlArtifactId: expectedArtifactId
        });
        exportArtifactId = value(response, "ExportRevisionArtifactId", "exportRevisionArtifactId", "") || "";
        resourceExport = value(response, "ResourceExport", "resourceExport", null);
        if (state.activeChatId !== chatId || state.htmlWorkspaceDirty || state.activeHtmlArtifactId !== expectedArtifactId) return false;
        var responseArtifactId = value(response, "ActiveHtmlArtifactId", "activeHtmlArtifactId", "") || "";
        var resourceUri = value(response, "ExportResourceUri", "exportResourceUri", "") || "";
        var contentSha256 = value(response, "ExportContentSha256", "exportContentSha256", "") || "";
        if (!exportArtifactId || exportArtifactId !== responseArtifactId ||
            !/^rna:\/\/chat\//.test(resourceUri) || !/^[a-f0-9]{64}$/i.test(contentSha256)) {
          throw new Error("HTML export returned incomplete revision evidence.");
        }
        if (!options.applyWorkspaceResponse(response, chatId) || state.activeHtmlArtifactId !== exportArtifactId) {
          throw new Error("HTML workspace changed before export.");
        }
        if (typeof options.downloadHtmlExport !== "function") {
          throw new Error("HTML export download is unavailable.");
        }
        await options.downloadHtmlExport({
          chatId: chatId,
          workspace: state.htmlWorkspace,
          resourceExport: resourceExport,
          revisionArtifactId: exportArtifactId,
          resourceUri: resourceUri,
          contentSha256: contentSha256
        });
        options.log("HTML экспортирован из exact revision " + resourceUri + ".");
        return true;
      } catch (error) {
        options.log(error.detail || error.message, "error");
        window.alert(error.message || "HTML не экспортирован.");
        return false;
      } finally {
        await Promise.all((resourceExport && resourceExport.bindings || []).map(function (binding) {
          return options.send("resourceDataClose", { chatId: chatId, workspaceId: exportArtifactId,
            leaseId: binding.lease.leaseId }).catch(function () {});
        }));
        state.htmlWorkspaceExportPending = false;
        if (options.render) options.render();
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
          toolId: "common.plan_doc_save",
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
        if (!await writeWorkspace("saveHtmlWorkspaceFile", {
          path: path,
          kind: kind,
          setActive: kind === "html"
        }, defaultFileContent(kind), true)) return;
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
        if (!await writeWorkspace("saveHtmlWorkspaceData", { name: name }, "{\n  \"items\": []\n}\n", true)) return;
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
        var targets = name ? [name] : [""];
        if (!name && policy === "on_preview" &&
            typeof options.refreshableDataNames === "function") {
          targets = options.refreshableDataNames("on_preview") || [];
        }
        var failure = null;
        for (var index = 0; index < targets.length; index += 1) {
          var args = {};
          if (targets[index]) args.name = targets[index];
          var result = await options.send("runTool", {
            toolId: "common.html_data_refresh",
            arguments: args,
            dryRun: false
          });
          if (!toolSucceeded(result) && !failure) failure = result;
        }
        if (state.activeChatId !== chatId) return;
        if (!options.applyWorkspaceResponse(await options.send("getHtmlWorkspace", { chatId: chatId }), chatId)) return;
        if (failure) {
          throw new Error(toolMessage(failure, "Данные обновлены частично."));
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
      cancelWrite: cancelWrite,
      createData: createData,
      createFile: createFile,
      createPlan: createPlan,
      deleteSelection: deleteSelection,
      exportWorkspace: exportWorkspace,
      handoffPlan: handoffPlan,
      importUploadedHtml: importUploadedHtml,
      artifactImageThumbnailState: artifactViewers.artifactImageThumbnailState,
      artifactViewerState: artifactViewers.artifactViewerState,
      closeArtifactViewers: artifactViewers.closeAll,
      changeArtifactPdfPage: artifactViewers.changeArtifactPdfPage,
      changeArtifactViewerPage: artifactViewers.changeArtifactViewerPage,
      downloadArtifactViewer: artifactViewers.downloadArtifactViewer,
      loadArtifactImage: artifactViewers.loadArtifactImage,
      loadArtifactImageThumbnail: artifactViewers.loadArtifactImageThumbnail,
      loadArtifactPdf: artifactViewers.loadArtifactPdf,
      loadArtifactPdfThumbnail: artifactViewers.loadArtifactPdfThumbnail,
      loadArtifactViewer: artifactViewers.loadArtifactViewer,
      loadArtifactViewerFull: artifactViewers.loadArtifactViewerFull,
      selectArtifactPdfPage: artifactViewers.selectArtifactPdfPage,
      recoverRevision: recoverRevision,
      refreshAll: function () { return refreshData("", "all", true); },
      refreshAuto: function () { return refreshData("", "on_preview", false); },
      redo: function () { return restore("redo"); },
      restorePlanRevision: restorePlanRevision,
      saveSelection: saveSelection,
      undo: function () { return restore("undo"); }
    };
  }

  window.RNAssistantHtmlWorkspaceActions = { create: create };
}());
