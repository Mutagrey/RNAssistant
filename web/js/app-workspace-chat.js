(function () {
  "use strict";
  var selectedSessions = new Map();
  var viewNodes = { Messages: "messages", Form: "chatForm", Input: "chatInput", Send: "sendButton", Stop: "stopButton",
    Notice: "inboxStatus" };
  function selected() { return window.RNAssistantConversationPanel && RNAssistantConversationPanel.selected(); }
  function node(name) { var component = selected(); return viewNodes[name] ? component ? component.node(viewNodes[name]) : document.getElementById(viewNodes[name]) : null; }
  function owner() { var component = selected(); return !component || component.target.source === "office" ? "office" : component.contextOwner || "project"; }
  function mount(nextOwner) {
    var component = selected();
    if (component && component.target.source === "workspace" && nextOwner !== "office") component.contextOwner = nextOwner;
    if (window.RNAssistantDockWorkspace) RNAssistantDockWorkspace.mount(nextOwner);
  }
  function rememberSession(workspaceId, sessionId) {
    if (!workspaceId) return;
    if (sessionId) selectedSessions.set(workspaceId, sessionId); else selectedSessions.delete(workspaceId);
  }
  function field(value, key) { return value && (value[key] !== undefined ? value[key] : value[key[0].toUpperCase() + key.slice(1)]); }
  function runStatus(snapshot) {
    return window.RNAssistantConversationState.lifecycle(field(snapshot, "status"), field(snapshot, "runId"),
      window.RNAssistantRunViewState.normalize(field(snapshot, "runViewState")));
  }
  function readOnly(snapshot) { return !!field(field(snapshot, "state"), "readOnly"); }
  function runNotice(snapshot) {
    if (readOnly(snapshot)) return { title: "Чат доступен только для чтения", text: "Старый протокол. Создайте новый чат для продолжения.", warning: false };
    var state = field(snapshot, "state");
    return window.RNAssistantRunViewState.runNotice(
      window.RNAssistantRunViewState.normalize(field(state, "runViewState")));
  }
  function latestSnapshot(current, incoming, options) {
    if (!current || field(current, "sessionId") !== field(incoming, "sessionId") ||
        field(field(current, "project"), "workspaceId") !== field(field(incoming, "project"), "workspaceId"))
    {
      var initial = window.RNAssistantConversationState.messageWindow(null, incoming, false);
      var initialPreview = field(field(incoming, "state"), "modelProgress");
      RNAssistantConversationState.retainUnsavedAttempt(initial, field(initialPreview, "runId"),
        field(initialPreview, "unsavedAttempt"), field(initialPreview, "stepId"));
      return initial;
    }
    var previousRevision = field(field(current, "state"), "revision");
    var nextRevision = field(field(incoming, "state"), "revision");
    if (Number.isSafeInteger(previousRevision) && (!Number.isSafeInteger(nextRevision) || nextRevision < previousRevision))
      return current;
    var currentState = field(current, "state"), incomingState = field(incoming, "state");
    var currentStatus = runStatus(currentState), incomingStatus = runStatus(incomingState);
    if (Number.isSafeInteger(previousRevision) && previousRevision === nextRevision &&
        field(currentState, "runId") && field(currentState, "runId") === field(incomingState, "runId") &&
        ["completed", "failed", "cancelled"].indexOf(currentStatus) >= 0 &&
        incomingStatus === "running") return current;
    var currentPreview = field(currentState, "modelProgress"), incomingPreview = field(incomingState, "modelProgress");
    if (previousRevision === nextRevision && currentStatus === "running" &&
        incomingStatus === "running" && field(currentState, "runId") === field(incomingState, "runId") &&
        currentPreview && !field(incomingState, "modelProgressUnavailable") &&
        (!incomingPreview || field(currentPreview, "sequence") > field(incomingPreview, "sequence")))
      incomingState.modelProgress = currentPreview;
    var merged = window.RNAssistantConversationState.messageWindow(current, incoming,
      Number.isSafeInteger(previousRevision) && previousRevision === nextRevision, !!(options && options.latestMessages));
    RNAssistantConversationState.retainUnsavedAttempt(merged, field(incomingPreview, "runId"),
      field(incomingPreview, "unsavedAttempt"), field(incomingPreview, "stepId"));
    return RNAssistantConversationState.preserveUnsavedAttempts(current, merged);
  }
  function prependMessages(current, page) {
    var start = field(current, "messageStartIndex"), total = field(current, "messageTotalCount");
    var revision = field(field(current, "state"), "revision");
    if (!current || !page || field(page, "chatId") !== field(current, "sessionId") ||
        field(page, "sessionRevision") !== revision || field(page, "totalCount") !== total ||
        field(page, "startIndex") + (page.messages || []).length !== start)
      throw new Error("История изменилась. Обновите чат и повторите загрузку.");
    var windowed = window.RNAssistantConversationState.prependMessageWindow(current, page);
    current.messages = windowed.messages;
    current.messageStartIndex = windowed.messageStartIndex;
    current.messageTotalCount = windowed.messageTotalCount;
    return current;
  }
  function createRunObserver(view, read, changed, failed, progressChanged) {
    var target = null, generation = 0, lastPushAt = 0, polling = false;
    var observer = view.observe({
      current: function () { return target ? { source: "workspace", workspaceId: target.workspaceId,
        sessionId: target.sessionId, generation: generation } : null; },
      read: read, changed: changed, failed: failed, interval: 10000,
      shouldPoll: function () { return !lastPushAt || Date.now() - lastPushAt >= 30000; }
    });
    return { select: function (snapshot, pending) {
      var workspaceId = field(field(snapshot, "project"), "workspaceId"), sessionId = field(snapshot, "sessionId");
      var observe = !!(workspaceId && sessionId &&
        (pending || runStatus(field(snapshot, "state")) === "running"));
      if (!target || target.workspaceId !== workspaceId || target.sessionId !== sessionId) {
        if (target || workspaceId && sessionId) ++generation;
        target = workspaceId && sessionId ? { workspaceId: workspaceId, sessionId: sessionId } : null;
        lastPushAt = 0;
      }
      if (polling !== observe) {
        polling = observe;
        if (polling) observer.start(); else observer.stop();
      }
    }, acceptProgress: function (payload) {
      var owner = view.projection(), selected = owner && owner.snapshot();
      if (!view.matches(conversationTarget(selected)) || !target || target.workspaceId !== field(payload, "workspaceId") ||
          target.sessionId !== field(payload, "sessionId")) return;
      var preview = field(payload, "modelProgress"), state = field(selected, "state");
      if (field(state, "runId") && field(payload, "runId") &&
          field(state, "runId") !== field(payload, "runId")) return;
      lastPushAt = Date.now();
      if (field(payload, "phase") === "session_changed") {
        if (field(payload, "cursor") !== field(state, "cursor")) observer.refresh();
        return;
      }
      if (field(preview, "unsavedAttempt")) {
        RNAssistantConversationState.retainUnsavedAttempt(selected, field(preview, "runId"),
          field(preview, "unsavedAttempt"), field(preview, "stepId"));
        changed(selected); return;
      }
      if (!preview || !state || runStatus(state) !== "running" ||
          field(state, "runId") !== field(preview, "runId")) { observer.refresh(); return; }
      var existing = field(state, "modelProgress");
      if (existing && field(existing, "sequence") >= field(preview, "sequence")) return;
      state.modelProgress = preview;
      if (progressChanged) progressChanged(selected);
      else changed(selected);
    } };
  }
  function button(label, className, icon, action) {
    var node = document.createElement("button"); node.type = "button"; node.className = className;
    node.title = label; node.setAttribute("aria-label", label);
    node.innerHTML = window.RNAssistantIcons.render(icon); node.onclick = action; return node;
  }
  function visibleMessage(message) { return conversationMessageVisible(message); }
  function conversationTarget(chat) {
    return { source: "workspace", workspaceId: field(field(chat, "project"), "workspaceId") || "",
      sessionId: field(chat, "sessionId") || "" };
  }
  function snapshotSource(target) {
    return window.RNAssistantConversationState.createSnapshotSource(workspaceComposerTarget(target), conversationTarget);
  }
  function transcriptData(chat, emptyOptions) {
    chat = chat || { messages: [], state: {} };
    var snapshot = field(chat, "state"), status = runStatus(snapshot), preview = field(snapshot, "modelProgress");
    var queued = status === "running" && field(snapshot, "executionQueued") === true;
    if (queued || status !== "running" || field(preview, "runId") !== field(snapshot, "runId")) preview = null;
    return { messages: chat.messages || [], start: field(chat, "messageStartIndex") || 0,
      total: field(chat, "messageTotalCount") || (chat.messages || []).length, emptyOptions: emptyOptions,
      live: { activities: [], runId: status === "running" ? field(snapshot, "runId") || "" : "",
        content: field(preview, "message") || "", reasoning: field(preview, "reasoning") || "",
        reasoningTruncated: !!field(preview, "reasoningTruncated"), retrying: !!field(preview, "retrying"),
        showStatus: !!preview, executionQueued: queued, statusText: queued ? "Ожидаю свободный слот выполнения…" : "Получаю ответ модели…" } };
  }
  function pendingUnits(component, presentation) {
    if (!component) return [];
    var chat = component.snapshot(), snapshot = field(chat, "state"), status = runStatus(snapshot);
    var queue = (field(snapshot, "pendingInputs") || []).map(function (item) {
      return Object.assign({}, item, { AcceptedRunId: item.RunId, Delivery: item.Delivery || "Steer", Status: item.Status || "Pending" });
    });
    var pendingWait = !!field(snapshot, "hasPendingWait"), waitActive = !!field(snapshot, "canSteer"), runPaused = !!field(snapshot, "runPaused");
    var inboxSource = { box: { items: queue, hasPendingWait: pendingWait, running: pendingWait ? waitActive : status === "running",
      runId: field(snapshot, "runId"), activeRun: status === "running" || !!chat.pendingId,
      paused: pendingWait ? !waitActive : status !== "running" && !chat.pendingId, runPaused: runPaused, waitTarget: field(snapshot, "waitTarget") },
      change: function (action, item, text) { return presentation.invoke("updateInbox", { action: action, item: item, text: text }); },
      appendDraft: function (text) { return presentation.invoke("appendDraft", text); },
      resume: function () { return presentation.invoke("updateInbox", { action: "resumeChatInbox" }); } };
    return buildPendingInputUnits(component, inboxSource);
  }
  function createPanelAdapter(component) {
    var target = component.target, node = component.node, composer = component.composer;
    var owner = component.view.bindProjection(snapshotSource(target), latestSnapshot), uploading = false, cancelling = false, pageRead = null, historyGeneration = 0;
    function snapshot() { return owner.snapshot() || { sessionId: target.sessionId, project: { workspaceId: target.workspaceId }, messages: [], state: {} }; }
    function state() { return field(snapshot(), "state") || {}; }
    function draft() { return readWorkspaceDraft(target); }
    function writeDraft(text) { return updateWorkspaceDraft(target, { text: text }); }
    function captureDraft() { return composer.captureDraft(target, writeDraft); }
    function edit() { return composer.edit(target); }
    function running() { return runStatus(state()) === "running"; }
    function pluginBusy() { return window.RNAssistantPlugins && RNAssistantPlugins.isChatBusy(target); }
    function pending() { return uploading || RNAssistantConversationInput.isPending("workspace", target); }
    function sendBlocked() { return pluginBusy() || snapshot().project && snapshot().project.available === false ||
      !!snapshot().pendingId || running() && !field(state(), "canSteer"); }
    function accept(value, options) {
      if (!component.isCurrent()) return false;
      owner.accept(value, options); component.loading = false; component.loadError = "";
      component.hasSnapshot = true;
      var title = field(state(), "title"); if (title) RNAssistantDockWorkspace.setTitle(RNAssistantConversationPanel.key(target), title);
      component.contextOwner = snapshot().project && snapshot().project.pluginId ? "plugin" : "project";
      observer.select(snapshot(), pending()); component.render();
      RNAssistantProjectChats.refreshTree();
      if (selected() === component) { if (window.RNAssistantPlugins) RNAssistantPlugins.onChatSnapshot(snapshot()); RNAssistantChatInspector.render(); }
      return true;
    }
    function read() { return RNAssistantProjectChats.read(target, component); }
    var observer = createRunObserver(component.view, read, accept, component.loadFailed,
      function () { component.stream(false); });
    var history = component.view.observe({ current: function () { return target; }, read: read, changed: accept,
      failed: component.loadFailed });
    function refresh(options) {
      if (!component.isCurrent()) return Promise.resolve();
      component.loading = !component.hasSnapshot; component.loadError = ""; component.render();
      if (options && options.latestMessages) {
        var generation = ++historyGeneration;
        return read().then(function (value) {
          if (component.isCurrent() && generation === historyGeneration) accept(value, options);
        }).catch(function (error) { if (generation === historyGeneration) component.loadFailed(error); });
      }
      return history.refresh();
    }
    function loadPrevious() {
      var before = snapshot().messageStartIndex, revision = field(state(), "revision");
      if (!before) return Promise.resolve();
      if (pageRead && pageRead.generation === historyGeneration && pageRead.before === before && pageRead.revision === revision) return pageRead.promise;
      var request = { generation: ++historyGeneration, before: before, revision: revision }; pageRead = request;
      request.promise = (async function () {
        try {
          var page = await send("getPreviousProjectChatMessages", { workspaceId: target.workspaceId, sessionId: target.sessionId, beforeIndex: before });
          if (component.isCurrent() && request.generation === historyGeneration && snapshot().messageStartIndex === before && field(state(), "revision") === revision) {
            prependMessages(snapshot(), page); component.render({ preserveTop: true });
          }
        } catch (error) {
          if (component.isCurrent() && request.generation === historyGeneration && snapshot().messageStartIndex === before && field(state(), "revision") === revision)
            component.notice(error);
        }
        finally { if (pageRead === request) pageRead = null; }
      })();
      return request.promise;
    }
    async function run(action, reportFailure) {
      var release = RNAssistantConversationInput.begin("workspace", target);
      if (!release) return false;
      component.controls(); observer.select(snapshot(), true);
      try { var response = await snapshotSource(target).read(action); if (component.isCurrent()) { accept(response); component.notice(""); } return true; }
      catch (error) { component.notice(error); if (reportFailure) throw error; return false; }
      finally { release(); observer.select(snapshot(), pending()); component.render(); }
    }
    async function submit(answers, requestId) {
      if (pending() || readOnly(snapshot()) || component.loading || component.loadError || !answers && draft() && draft().pendingVisualAnnotation) return;
      var editing = answers ? null : edit(), saved = answers ? null : captureDraft(), text = answers ? "" : node("chatInput").value.trim();
      if (!answers && (sendBlocked() || !text && !(saved && saved.attachments.length))) return;
      var input = answers ? { sessionId: target.sessionId, requestId: requestId, answers: answers } : prepareWorkspaceInput(target, text, {
        Mode: running() ? field(state(), "mode") : saved && saved.mode || "agent",
        ReasoningEnabled: running() ? !!field(field(state(), "modelConfiguration"), "reasoningEnabled") :
          !!(saved && saved.reasoning) && effectiveModelSupportsReasoning(field(state(), "model") || settingsModel()) !== false,
        SteerRunId: running() && runningMessageDelivery() !== "Queue" ? field(state(), "runId") : null,
        Enqueue: running() && runningMessageDelivery() === "Queue",
        EditMessageId: editing && editing.messageId, ExpectedRevision: editing && editing.revision });
      var gate = RNAssistantConversationInput.create("workspace", target, input.requestId, input.SteerRunId, function () {
        if (answers) return;
        if (editing) {
          acknowledgeWorkspaceInput(target, input);
          if (component.isCurrent() && edit() === editing) composer.cancelEdit(target);
        } else {
          var before = draft(); acceptWorkspaceInput(target, input, text, saved && saved.version); component.acceptDraft(before, draft());
        }
      }, input.Enqueue);
      if (editing) editing.busy = true;
      var accepted = await run(function () {
        return gate.submit(function () { return answers ? Promise.resolve() : RNAssistantChatDrafts.flushWorkspace(target); },
          function (acceptReceipt) { return send("submitProjectChat", { workspaceId: target.workspaceId, sessionId: target.sessionId, input: input },
            function (progress) {
              if (progress.workspaceId === target.workspaceId && progress.sessionId === target.sessionId) {
                acceptReceipt(progress.inputReceipt); observer.acceptProgress(progress);
              }
            }); }, function (value) { return value.inputReceipt; });
      }, !!answers);
      if (editing) editing.busy = false; component.render();
      return accepted;
    }
    var commands = {
      capabilities: function () { return { artifactCards: false, inlineCharts: false, runChanges: false }; },
      updateInbox: function (change) {
        var actions = { editChatInput: "Edit", removeChatInput: "Remove", steerChatInput: "Steer", resumeChatInbox: "Resume" };
        var action = actions[change.action]; if (!action) return;
        return run(async function () {
          var value = await send("updateProjectChatInput", { workspaceId: target.workspaceId, sessionId: target.sessionId,
            inbox: { SessionId: target.sessionId, Action: action, InputId: change.item && change.item.Id,
              ExpectedRevision: change.item && change.item.Revision || 0, Text: change.text } });
          component.inboxState.inboxEditing = null; component.inboxState.inboxEditDraft = null;
          return value;
        });
      },
      snapshot: snapshot,
      preferences: function () { var saved = draft(); return { selected: field(state(), "model") || "", mode: saved && saved.mode || "agent",
        reasoning: !!(saved && saved.reasoning), runningModel: running() ? field(field(state(), "modelConfiguration"), "model") : null,
        disabled: component.loading || !!component.loadError || readOnly(snapshot()) ||
          running() || pending() || pluginBusy() || !!edit() || !!snapshot().pendingId || window.state.bridgeUnavailable }; },
      preferenceService: function () { return {
        write: function (change) {
          if (change.name === "model") return send("setProjectChatModel", { workspaceId: target.workspaceId, sessionId: target.sessionId, model: change.value });
          var changes = {}; changes[change.name] = change.value; updateWorkspaceDraft(target, changes);
          return RNAssistantChatDrafts.flushWorkspace(target);
        }, read: read, apply: function (response, change) {
          if (!component.isCurrent()) return false;
          if (!change) return accept(response);
          if (change.name !== "model") return true;
          if (!response || response.workspaceId !== target.workspaceId || response.sessionId !== target.sessionId ||
              !Number.isSafeInteger(response.revision) || (response.model || "") !== change.value) throw new Error("Выбор модели не подтверждён.");
          if (response.revision < field(state(), "revision")) return false;
          state().Model = state().model = response.model; state().Revision = state().revision = response.revision; return true;
        }
      }; },
      submit: function () { return submit(); },
      stop: async function () {
        if (!running() || cancelling) return;
        var runId = field(state(), "runId"); cancelling = true; component.controls();
        function cancel() { return send("cancelProjectChat", { workspaceId: target.workspaceId, sessionId: target.sessionId,
          runId: runId, cursor: field(state(), "cursor") }); }
        try {
          var result;
          try { result = await cancel(); }
          catch (error) {
            if (error.code !== "project_chat_stale" || !component.isCurrent()) throw error;
            accept(await read());
            if (!component.isCurrent() || field(state(), "runId") !== runId || !running()) return;
            result = await cancel();
          }
          accept(result);
        } catch (error) { component.notice(error); }
        finally { cancelling = false; component.controls(); }
      },
      resolve: function (value) {
        var current = RNAssistantRunViewState.normalize(field(state(), "runViewState"));
        if (!value || !current || value.runId !== current.runId || !current.pendingConfirmation ||
            current.pendingConfirmation.pendingId !== value.pendingId || pending() || component.loading || component.loadError) return false;
        return run(function () { return send("resolveProjectChat", { workspaceId: target.workspaceId,
          sessionId: target.sessionId, pendingId: value.pendingId, approve: value.approve }); });
      },
      answerQuestions: function (value) {
        var input = field(state(), "userInput");
        if (!value || !input || value.answers.runId !== field(input, "runId")) return false;
        return submit(value.answers, value.requestId);
      },
      canMessageAction: function (action) { return !!action.message && !!action.message.Id && !running() && !pending() && !edit() &&
        !readOnly(snapshot()) && !snapshot().pendingId && (action.name !== "edit" || messageRole(action.message) === "user" && !RNAssistantAgentCommunication.sender(action.message)); },
      messageAction: async function (action) {
        if (!commands.canMessageAction(action)) return;
        if (action.name === "edit") {
          captureDraft(); composer.startEdit(target, { messageId: action.message.Id, revision: field(state(), "revision"), text: messageContent(action.message) });
          node("chatInput").value = messageContent(action.message); component.render(); node("chatInput").focus(); return;
        }
        try {
          var result = await send(action.name === "fork" ? "forkProjectChat" : "deleteProjectChatMessage", {
            workspaceId: target.workspaceId, sessionId: target.sessionId, messageId: action.message.Id });
          if (!component.isCurrent()) return;
          if (action.name === "fork") RNAssistantConversationPanel.open({ source: "workspace", workspaceId: target.workspaceId, sessionId: result.sessionId });
          else accept(result);
        } catch (error) { component.notice(error); }
      },
      loadPrevious: loadPrevious, loadLatest: function () { return refresh({ latestMessages: true }); }, progress: observer.acceptProgress
    };
    function renderUtilities() {
      node("documentNotice").classList.add("hidden");
      renderConversationAttachments(component, draft(), async function (item) {
        try {
          if (!item.unavailable) await send("discardChatResourceDraft", { workspaceId: target.workspaceId, chatId: target.sessionId, id: attachmentId(item) });
          var saved = draft();
          if (saved) updateWorkspaceDraft(target, { attachments: saved.attachments.filter(function (value) { return attachmentId(value) !== attachmentId(item); }) });
          if (item.previewUrl) URL.revokeObjectURL(item.previewUrl); component.controls();
        } catch (error) { component.notice(error); }
      }, window.RNAssistantBrowser && RNAssistantBrowser.annotations(target));
      renderChatRunNotice(component); renderContextMeter(component);
      node("compactContextButton").hidden = true;
      node("addSelectionContextButton").disabled = true;
    }
    return { commands: commands, projection: function () { return owner; }, snapshot: snapshot, accept: accept, refresh: refresh,
      draft: draft, writeDraft: writeDraft, captureDraft: captureDraft, pending: pending, running: running,
      hasAttachments: function () { var saved = draft(); return !!(saved && saved.attachments.length); },
      renderUtilities: renderUtilities,
      transcript: function () { return transcriptData(snapshot()); },
      pendingUnits: function () { return pendingUnits(component, createMessagePresentation(component.view, transcriptData(snapshot()), composer.capture())); },
      runView: function () { return RNAssistantRunViewState.normalize(field(state(), "runViewState")); },
      header: function () { var project = snapshot().project || {}; return { title: field(state(), "title") || "Чат проекта",
        label: project.name || target.workspaceId, detail: project.rootPath, unavailable: project.available === false }; },
      questions: function () {
        var input = field(state(), "userInput");
        return input && { runId: field(input, "runId"), data: field(input, "questionSet") };
      },
      presentation: function () { return { running: running(), canceling: cancelling, readOnly: readOnly(snapshot()), sendBlocked: sendBlocked(),
        awaitingReply: runStatus(state()) === "awaiting_user", awaitingApproval: !!snapshot().pendingId, queue: runningMessageDelivery() === "Queue" }; },
      attachFiles: async function (files) {
        if (!files.length || pending() || edit() || readOnly(snapshot()) || component.loading || window.state.bridgeUnavailable) return;
        uploading = true; component.controls();
        try { await stageChatResourceFiles(target.sessionId, files, target); }
        finally { uploading = false; component.controls(); }
      },
      activate: function () { observer.select(snapshot(), pending()); }, progress: observer.acceptProgress,
      focus: function () {
        rememberSession(target.workspaceId, target.sessionId); RNAssistantProjectChats.focus(target);
        if (window.RNAssistantPlugins) RNAssistantPlugins.onChatSnapshot(snapshot());
      }
    };
  }
  window.RNAssistantWorkspaceChat = { createPanelAdapter: createPanelAdapter, transcriptData: transcriptData,
    mount: mount, owner: owner, node: node,
    snapshot: function () { var component = selected(); return component && component.target.source === "workspace" ? component.snapshot() : null; },
    runStatus: runStatus, readOnly: readOnly, runNotice: runNotice,
    rememberSession: rememberSession, selectedSession: function (workspaceId) { return selectedSessions.get(workspaceId); },
    latestSnapshot: latestSnapshot, prependMessages: prependMessages, snapshotSource: snapshotSource,
    createRunObserver: createRunObserver,
    acceptProgress: function (payload) {
      var component = window.RNAssistantConversationPanel && RNAssistantConversationPanel.find({ source: "workspace", workspaceId: field(payload, "workspaceId"), sessionId: field(payload, "sessionId") });
      if (component) component.progress(payload);
    }
  };
}());
