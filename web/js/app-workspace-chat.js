(function () {
  "use strict";
  var selectedSessions = new Map();
  var viewNodes = { Messages: "messages", Form: "chatForm", Input: "chatInput", Send: "sendButton", Stop: "stopButton",
    Notice: "inboxStatus", Control: "workspaceChatControl", Pending: "workspaceChatPending",
    Approve: "workspaceChatApprove", Reject: "workspaceChatReject", Questions: "workspaceChatQuestions" };
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
      return window.RNAssistantConversationState.messageWindow(null, incoming, false);
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
    return window.RNAssistantConversationState.messageWindow(current, incoming,
      Number.isSafeInteger(previousRevision) && previousRevision === nextRevision, !!(options && options.latestMessages));
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
  function visibleMessage(message) {
    return !message.ProtocolMessage && (!!(message.Content || "").trim() || !!message.Activity ||
      !!message.ReflectionDecision || !!message.ReasoningContent ||
      !!(message.Results && message.Results.length) || !!(message.Attachments && message.Attachments.length));
  }
  function conversationTarget(chat) {
    return { source: "workspace", workspaceId: field(field(chat, "project"), "workspaceId") || "",
      sessionId: field(chat, "sessionId") || "" };
  }
  function snapshotSource(target) {
    return window.RNAssistantConversationState.createSnapshotSource(workspaceComposerTarget(target), conversationTarget);
  }
  function renderMessages(list, chat, emptyOptions, options, commands) {
    var view = messageView(list);
    if (!view.canRender()) return;
    chat = chat || { messages: [], state: {} };
    var target = conversationTarget(chat), frame = view.begin(target, options);
    var presentation = createMessagePresentation(view, chat, commands || chatComposer().capture());
    var changed = frame.changed, following = frame.follow;
    var snapshot = field(chat, "state"), status = runStatus(snapshot), live = field(snapshot, "modelProgress");
    if (status !== "running" || field(live, "runId") !== field(snapshot, "runId")) live = null;
    var start = field(chat, "messageStartIndex") || 0;
    var messages = chat.messages || [];
    var actionMessage = messages.find(visibleMessage);
    var historyActions = !!actionMessage && presentation.invoke("canMessageAction", { name: "fork", message: actionMessage, index: 0 });
    var signature = JSON.stringify([presentation.actionKey, chat.messages || [], live, field(snapshot, "pendingInputs"), status, start, field(chat, "messageTotalCount"), historyActions, emptyOptions,
      RNAssistantConversationPanel.find(conversationTarget(chat))?.inboxState?.inboxEditing]);
    if (!changed && view.cache.signature === signature) return;
    var viewport = !following ? frame.viewport : null;
    list.dataset.session = JSON.stringify([target.source, target.workspaceId, target.sessionId]);
    list.dataset.startIndex = String(start); view.cache.signature = signature;
    var units = [];
    if (start > 0) {
      appendMessageUnit(units, "history:older", start + ":" + presentation.actionKey, function () {
        return createChatHistoryButton("previous", function () { return presentation.invoke("loadPrevious"); });
      });
    }
    var visible = messages.filter(visibleMessage);
    if (!visible.length && !start && !live) {
      appendMessageUnit(units, "empty", JSON.stringify(emptyOptions || {}), function () { return renderChatEmptyState(emptyOptions || (!field(chat, "sessionId")
        ? { title: "Чатов пока нет", text: "Нажмите «+», чтобы создать чат." }
        : { title: "С чего начнём?", text: "Опишите задачу или задайте вопрос." })); });
    }
    var liveAttached = false;
    view.cache.liveUnitKey = null;
    var liveRunId = field(live, "runId");
    var liveProgress = live ? { content: field(live, "message"), reasoning: field(live, "reasoning"),
      reasoningTruncated: field(live, "reasoningTruncated"), retrying: field(live, "retrying"), showStatus: true } : null;
    forEachChatDisplayEntry(messages, visibleMessage, function (run) {
        run.workspace = true;
        if (live && !run.finalMessage && !run.continued && run.nextIndex >= messages.length &&
            agentRunId(run.items, run.finalMessage) === liveRunId) {
          var savedView = agentRunViewState(run.items, null);
          if (!savedView || ["completed", "failed", "cancelled"].indexOf(savedView.lifecycle) < 0) {
            run.live = true;
            run.workspaceProgress = liveProgress;
            liveAttached = true;
          }
        }
        appendSavedRunUnit(units, run, presentation, presentation.actionKey + ":" + historyActions,
          run.live ? (function (progress) {
            return function (node) { paintLiveModelOutput(node.querySelector(".agent-stream-step"), progress); };
          }(liveProgress)) : null);
        if (run.live) view.cache.liveUnitKey = agentRunUnitKey(run);
      }, function (message, index) {
        appendSavedMessageUnit(units, message, index, presentation, presentation.actionKey + ":" + historyActions);
      });
    if (live && !liveAttached) {
      var liveRun = { items: [], runId: liveRunId, workspace: true, live: true, workspaceProgress: liveProgress };
      view.cache.liveUnitKey = agentRunUnitKey(liveRun);
      appendMessageUnit(units, view.cache.liveUnitKey, String(liveRunId), function () {
        return renderAgentRunArticle(liveRun, presentation);
      }, "", null, function (node) {
        paintLiveModelOutput(node.querySelector(".agent-stream-step"), liveProgress);
      });
      units[units.length - 1].transient = true;
    }
    if (start + messages.length < field(chat, "messageTotalCount")) {
      appendMessageUnit(units, "history:latest", field(chat, "messageTotalCount") + ":" + presentation.actionKey, function () {
        return createChatHistoryButton("latest", function () { return presentation.invoke("loadLatest"); });
      });
    }
    var component = RNAssistantConversationPanel.find(conversationTarget(chat));
    if (component) {
      var queue = (field(snapshot, "pendingInputs") || []).map(function (item) {
        return Object.assign({}, item, { AcceptedRunId: item.RunId,
          Delivery: item.Delivery || "Steer", Status: item.Status || "Pending" });
      });
      var inboxSource = { box: { items: queue, running: status === "running", runId: field(snapshot, "runId"), activeRun: status === "running" || !!chat.pendingId,
        paused: status !== "running" && !chat.pendingId },
        change: function (action, item, text) { return presentation.invoke("updateInbox", { action: action, item: item, text: text }); },
        appendDraft: function (text) { return presentation.invoke("appendDraft", text); },
        resume: function () { return presentation.invoke("updateInbox", { action: "resumeChatInbox" }); } };
      buildPendingInputUnits(component, inboxSource).forEach(function (unit) { units.push(unit); });
    }
    reconcileMessageUnits(list, units, false);
    view.cache.liveRunId = liveRunId || null;
    if (viewport) restoreMessageViewport(list, viewport);
    syncChatScroll(following, false, list);
  }
  function paintProgress(list, chat) {
    var snapshot = field(chat, "state"), preview = field(snapshot, "modelProgress");
    var runId = field(snapshot, "runId");
    if (!list) return false;
    var view = messageView(list);
    if (!view.canRender()) return false;
    var entry = view.cache.units[view.cache.liveUnitKey];
    if (!entry || !entry.node || entry.node.parentNode !== list || !view.matches(conversationTarget(chat)) ||
        view.cache.liveRunId !== runId || runStatus(snapshot) !== "running" ||
        field(preview, "runId") !== runId) return false;
    var section = entry.node.querySelector(".agent-stream-step");
    if (!section) return false;
    var following = shouldFollowChatScroll(list);
    var viewport = following ? null : captureMessageViewport(list);
    paintLiveModelOutput(section, { content: field(preview, "message"), reasoning: field(preview, "reasoning"),
      reasoningTruncated: field(preview, "reasoningTruncated"), retrying: field(preview, "retrying"), showStatus: true });
    if (viewport) restoreMessageViewport(list, viewport);
    syncChatScroll(following, false, list);
    return true;
  }
  function createPanelAdapter(component) {
    var target = component.target, node = component.node, composer = component.composer;
    var owner = component.view.bindProjection(snapshotSource(target), latestSnapshot), uploading = false, cancelling = false;
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
      var title = field(state(), "title"); if (title) RNAssistantDockWorkspace.setTitle(RNAssistantConversationPanel.key(target), title);
      component.contextOwner = snapshot().project && snapshot().project.pluginId ? "plugin" : "project";
      observer.select(snapshot(), pending()); component.render();
      RNAssistantProjectChats.refreshTree();
      if (selected() === component) { if (window.RNAssistantPlugins) RNAssistantPlugins.onChatSnapshot(snapshot()); RNAssistantChatInspector.render(); }
      return true;
    }
    function read() { return RNAssistantProjectChats.read(target, component); }
    var observer = createRunObserver(component.view, read, accept, component.notice,
      function () { component.stream(false); });
    var history = component.view.observe({ current: function () { return target; }, read: read, changed: accept,
      failed: function (error) { component.loading = false; component.loadError = error.detail || error.message; component.render(); } });
    function refresh(options) {
      if (!owner.snapshot()) component.loading = true;
      if (options && options.latestMessages) return read().then(function (value) { accept(value, options); }).catch(component.notice);
      return history.refresh();
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
      await run(function () {
        return gate.submit(function () { return answers ? Promise.resolve() : RNAssistantChatDrafts.flushWorkspace(target); },
          function (acceptReceipt) { return send("submitProjectChat", { workspaceId: target.workspaceId, sessionId: target.sessionId, input: input },
            function (progress) {
              if (progress.workspaceId === target.workspaceId && progress.sessionId === target.sessionId) {
                acceptReceipt(progress.inputReceipt); observer.acceptProgress(progress);
              }
            }); }, function (value) { return value.inputReceipt; });
      }, !!answers);
      if (editing) editing.busy = false; component.render();
    }
    var commands = {
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
      resolve: function (approve) { var pendingId = snapshot().pendingId;
        if (pendingId) return run(function () { return send("resolveProjectChat", { workspaceId: target.workspaceId,
          sessionId: target.sessionId, pendingId: pendingId, approve: approve }); }); },
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
      loadPrevious: async function () {
        var before = snapshot().messageStartIndex, revision = field(state(), "revision"); if (!before) return;
        try {
          var page = await send("getPreviousProjectChatMessages", { workspaceId: target.workspaceId, sessionId: target.sessionId, beforeIndex: before });
          if (component.isCurrent() && snapshot().messageStartIndex === before && field(state(), "revision") === revision) {
            prependMessages(snapshot(), page); component.render({ preserveTop: true });
          }
        } catch (error) { component.notice(error); }
      }, loadLatest: function () { return refresh({ latestMessages: true }); }, progress: observer.acceptProgress
    };
    function renderUtilities() {
      node("activeChatTitle").textContent = field(state(), "title") || "Чат проекта";
      node("activeChatSubtitle").textContent = [snapshot().project && snapshot().project.name,
        component.loading ? "Загрузка чата…" : component.loadError || formatChatMessageCount((snapshot().messages || []).length)].filter(Boolean).join(" · ");
      node("documentNotice").classList.add("hidden"); node("agentApprovalDock").classList.add("hidden");
      renderConversationAttachments(component, draft(), async function (item) {
        try {
          if (!item.unavailable) await send("discardChatResourceDraft", { workspaceId: target.workspaceId, chatId: target.sessionId, id: attachmentId(item) });
          var saved = draft();
          if (saved) updateWorkspaceDraft(target, { attachments: saved.attachments.filter(function (value) { return attachmentId(value) !== attachmentId(item); }) });
          if (item.previewUrl) URL.revokeObjectURL(item.previewUrl); component.controls();
        } catch (error) { component.notice(error); }
      });
      node("workspaceChatControl").hidden = !snapshot().pendingId;
      node("workspaceChatPending").textContent = snapshot().pendingId ? (snapshot().pendingTool || "Действие") + "\n" + (snapshot().pendingArguments || "") : "";
      if (snapshot().pendingId && snapshot().patchPreview && window.RNAssistantRunChanges)
        RNAssistantRunChanges.renderPrepared(node("workspaceChatPending"), snapshot().patchPreview);
      var input = field(state(), "userInput"), questions = field(input, "questionSet");
      if (questions) RNAssistantQuestions.render(node("workspaceChatQuestions"), questions, {
        scope: "workspace:" + target.workspaceId + ":" + target.sessionId, runId: field(input, "runId"), submit: submit });
      else { node("workspaceChatQuestions").hidden = true; node("workspaceChatQuestions").replaceChildren(); }
      renderChatRunNotice(component); renderContextMeter(component);
      node("compactContextButton").hidden = true;
      node("addSelectionContextButton").disabled = true;
    }
    return { commands: commands, projection: function () { return owner; }, snapshot: snapshot, accept: accept, refresh: refresh,
      draft: draft, writeDraft: writeDraft, captureDraft: captureDraft, pending: pending, running: running,
      hasAttachments: function () { var saved = draft(); return !!(saved && saved.attachments.length); },
      renderUtilities: renderUtilities,
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
  window.RNAssistantWorkspaceChat = { createPanelAdapter: createPanelAdapter, renderMessages: renderMessages,
    mount: mount, owner: owner, node: node,
    snapshot: function () { var component = selected(); return component && component.target.source === "workspace" ? component.snapshot() : null; },
    runStatus: runStatus, readOnly: readOnly, runNotice: runNotice,
    rememberSession: rememberSession, selectedSession: function (workspaceId) { return selectedSessions.get(workspaceId); },
    latestSnapshot: latestSnapshot, prependMessages: prependMessages, snapshotSource: snapshotSource,
    createRunObserver: createRunObserver, paintProgress: paintProgress,
    acceptProgress: function (payload) {
      var component = window.RNAssistantConversationPanel && RNAssistantConversationPanel.find({ source: "workspace", workspaceId: field(payload, "workspaceId"), sessionId: field(payload, "sessionId") });
      if (component) component.progress(payload);
    }
  };
}());
