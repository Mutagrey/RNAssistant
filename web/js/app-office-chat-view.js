(function () {
  "use strict";
  function create(component) {
    var target = component.target, id = target.sessionId, node = component.node, composer = component.composer;
    var owner = id ? component.view.bindProjection(officeChatSource(id), mergeOfficeChatSnapshot) : null;
    if (owner) owner.accept(createOfficeChatSnapshot(id));
    var empty = createOfficeChatSnapshot(""), uploading = false, deleting = false, pageRead = null, historyGeneration = 0;
    function snapshot() { return owner ? owner.snapshot() : empty; }
    function draft() { return readOfficeDraft(id); }
    function writeDraft(text) { var saved = draft(); return saveOfficeDraft(id, text, saved && saved.attachments || [], true); }
    function captureDraft(changed) { return composer.captureDraft(target, function (text) {
      var saved = draft(); return saveOfficeDraft(id, text, saved && saved.attachments || [], changed);
    }); }
    function running() { return !!(state.activeSends[id] || chatInbox(id).running || snapshot().activeRunViewState && snapshot().activeRunViewState.lifecycle === "running"); }
    function pending() { return uploading || RNAssistantConversationInput.isPending("office", { chatId: id }); }
    function edit() { return composer.edit(target); }
    function accept(response, options) {
      if (!owner || !owner.isCurrent()) return false;
      owner.accept(response, options);
      snapshot().liveRun = state.chatRuns[id] || null;
      component.loading = !!response.detailDeferred; component.loadError = "";
      if (response.inbox) applyChatInbox(response.inbox, true);
      reconcileChatInputEchoes(id, snapshot().messages);
      applyChatCatalogState(response);
      if (component.isCurrent()) component.render();
      return true;
    }
    var history = component.view.observe({ current: function () { return id ? target : null; },
      read: function () { return loadChatState(id); }, changed: function (response) { accept(response); },
      failed: function (error) { component.loading = false; component.loadError = error.detail || error.message; component.render(); }
    });
    var inbox = component.view.observe({ current: function () { return id ? target : null; },
      shouldPoll: function () { return !state.bridgeUnavailable && (!state.chatInboxes[id] || running() || chatInputEchoes(id).length || (chatInbox(id).items || []).length); },
      read: function () {
        var queued = new Set((chatInbox(id).items || []).map(function (item) { return item.Id; }));
        var echoes = chatInputEchoes(id).filter(function (item) { return item.OperationId; });
        var ids = echoes.filter(function (item) { return !queued.has(item.Id); }).concat(echoes.filter(function (item) { return queued.has(item.Id); }))
          .map(function (item) { return item.OperationId; }).slice(0, 128);
        return send("getChatInbox", { chatId: id, receiptOperationIds: ids });
      }, changed: function (response) { if (response.chatId === id) applyChatInbox(response); }
    });
    function refresh(options) {
      if (!id || !component.isCurrent()) return Promise.resolve();
      if (!snapshot().sessionRevision) component.loading = true;
      if (options && options.latestMessages) {
        var generation = ++historyGeneration;
        return loadChatState(id).then(function (response) {
          if (component.isCurrent() && generation === historyGeneration) accept(response, options);
        }).catch(function (error) { if (generation === historyGeneration) component.notice(error); });
      }
      return history.refresh();
    }
    function loadPrevious() {
      var data = snapshot(), start = data.messageStartIndex, revision = data.sessionRevision;
      if (!start) return Promise.resolve();
      if (pageRead && pageRead.generation === historyGeneration && pageRead.start === start && pageRead.revision === revision) return pageRead.promise;
      var request = { generation: ++historyGeneration, start: start, revision: revision }; pageRead = request;
      request.promise = (async function () {
        try {
          var page = await send("getPreviousChatMessages", { chatId: id, beforeIndex: start });
          if (!component.isCurrent() || request.generation !== historyGeneration || snapshot().sessionRevision !== revision || snapshot().messageStartIndex !== start) return;
          if (page.chatId !== id || page.sessionRevision !== revision) { await refresh(); return; }
          Object.assign(snapshot(), RNAssistantConversationState.prependMessageWindow(snapshot(), page));
          snapshot().artifacts = snapshot().artifacts.concat((page.artifacts || []).filter(function (entry) {
            return !snapshot().artifacts.some(function (known) { return (known.Id || known.id) === (entry.Id || entry.id); });
          }));
          component.render({ preserveTop: true });
        } catch (error) {
          if (component.isCurrent() && request.generation === historyGeneration && snapshot().sessionRevision === revision && snapshot().messageStartIndex === start)
            component.notice(error);
        }
        finally { if (pageRead === request) pageRead = null; }
      })();
      return request.promise;
    }
    async function action(type, payload) {
      try { var response = await send(type, Object.assign({ chatId: id }, payload));
        if (component.isCurrent()) accept(response); else applyChatCatalogState(response);
        return response;
      } catch (error) { component.notice(error); return null; }
    }
    async function tracked(type, payload) {
      if (running() || pending()) return;
      var request = send(type, Object.assign({ chatId: id }, payload));
      state.activeSends[id] = { requestId: request.requestId, canceling: false };
      beginChatRunTracking(id); component.render();
      try {
        var response = await request;
        if (component.isCurrent()) { accept(response); component.notice(""); return response; }
        applyChatCatalogState(response);
      } catch (error) { component.notice(error); await refresh(); }
      finally { delete state.activeSends[id]; endChatRunTracking(id); component.render(); }
    }
    var approval = RNAssistantAgentApproval.create({ state: Object.defineProperty({}, "activeRunViewState", { get: function () { return snapshot().activeRunViewState; } }),
      node: node, currentActiveSend: running, primaryText: function (value) { return value.toolName || "Действие"; },
      confirm: function (pendingId) { tracked("confirmAgentTool", { pendingId: pendingId }); },
      cancel: function (pendingId) { action("cancelAgentTool", { pendingId: pendingId }); }
    });
    var commands = {
      snapshot: snapshot,
      preferences: function () { var data = snapshot(); return { selected: data.activeChatModel, mode: data.activeChatMode,
        reasoning: data.activeChatReasoning, disabled: !id || running() || !!edit() || pending() || component.loading || !!component.loadError ||
          data.chatReadOnly || state.bridgeUnavailable || !!approval.pendingActivity() }; },
      preferenceService: function () { return officePreferenceService(component); },
      submit: async function () {
        if (pending() || !id || component.loading || component.loadError || snapshot().chatReadOnly || draft() && draft().pendingVisualAnnotation) return;
        var editing = edit(), text = node("chatInput").value.trim();
        if (editing) {
          if (running()) return;
          editing.busy = true;
          var result = await tracked("editMessage", { id: editing.messageId, index: editing.index, text: text });
          editing.busy = false;
          if (result && component.isCurrent() && edit() === editing) composer.cancelEdit(target);
          component.render(); return;
        }
        var saved = captureDraft(false), attachments = saved && saved.attachments || [];
        if (!text && !attachments.length) return;
        var run = snapshot().activeRunViewState, live = state.chatRuns[id];
        var runId = run && (run.lifecycle === "running" || run.lifecycle === "awaiting_confirmation") ? run.runId : live && live.runId;
        var delivery = (running() || approval.pendingActivity()) && runId ? runningMessageDelivery() : "Queue";
        await submitInboxMessage(text, attachments, id, delivery, null, delivery === "Steer" ? runId : null,
          delivery === "Steer" && run && run.pendingConfirmation ? run.pendingConfirmation.pendingId : null, component);
      },
      confirm: function (pendingId) { if (pendingId) return tracked("confirmAgentTool", { pendingId: pendingId }); },
      reject: function (pendingId) { if (pendingId) return action("cancelAgentTool", { pendingId: pendingId }); },
      stop: async function () {
        if (!running()) return;
        var active = state.activeSends[id], live = state.chatRuns[id];
        setChatRunCancelling(id, true); chatInbox(id).canceling = true; component.controls();
        try {
          if (chatInbox(id).running) applyChatInbox(await send("stopChatInbox", { chatId: id }));
          else if (live && live.runId) await cancelChatRun(id, live.runId);
          else if (active) await cancelBridgeRequest(active.requestId);
        } catch (error) { setChatRunCancelling(id, false); chatInbox(id).canceling = false; component.notice(error); }
        component.controls();
      },
      canMessageAction: function (value) { return !!value.message && !component.loading && !snapshot().chatReadOnly && !running() && !pending() &&
        !edit() && !deleting && !approval.pendingActivity() && (value.name !== "edit" || messageRole(value.message) === "user" && !RNAssistantAgentCommunication.sender(value.message)); },
      messageAction: async function (value) {
        if (!commands.canMessageAction(value)) return;
        if (value.name === "edit") {
          captureDraft(false); composer.startEdit(target, { messageId: messageId(value.message), index: value.index, text: messageContent(value.message) });
          node("chatInput").value = messageContent(value.message); component.render(); node("chatInput").focus(); return;
        }
        deleting = true; component.controls();
        try {
          var response = await send(value.name === "fork" ? "forkChat" : "deleteMessage", { chatId: id, id: messageId(value.message), index: value.index });
          if (value.name === "fork") { if (component.isCurrent()) applyChatState(response.state || response); }
          else if (component.isCurrent()) accept(response);
        } catch (error) { component.notice(error); }
        finally { deleting = false; component.render(); }
      },
      loadPrevious: loadPrevious,
      loadLatest: function () { return refresh({ latestMessages: true }); },
      compactContextButton: function () { return action("compactChatContext"); },
      addSelectionContextButton: function () { return addSelectionContext("full", component); },
      clearContext: async function () {
        try { if (applyContextResponse(await send("clearContext", { chatId: id }), owner)) {
          setContextManagerOpen(false, component); await refresh();
        } } catch (error) { component.notice(error); }
      },
      openDocumentButton: function () { return openActiveDocument(id); }
    };
    async function attachFiles(files) {
      if (!files.length || uploading || pending() || edit() || !id || snapshot().chatReadOnly || component.loading || window.state.bridgeUnavailable) return;
      uploading = true; component.controls();
      try { await stageChatResourceFiles(id, files); composer.restoreDraft(target, draft()); }
      finally { uploading = false; component.controls(); }
    }
    function renderUtilities() {
      renderOfficeChatHeader(component); approval.renderDock();
      renderChatRunNotice(component); renderAgentPlanDock(component); renderContextMeter(component);
      if (node("contextStrip").context !== snapshot().context) {
        node("contextStrip").context = snapshot().context; renderContext(component);
      }
      var controller = RNAssistantConversationPanel.selected() === component && window.RNAssistantHtmlWorkspaceAnnotations;
      renderConversationAttachments(component, draft(), async function (item) {
        try {
          if (!item.unavailable) await send("discardChatResourceDraft", { chatId: id, id: attachmentId(item) });
          var current = draft();
          if (current) saveOfficeDraft(id, current.text, current.attachments.filter(function (value) { return attachmentId(value) !== attachmentId(item); }), true);
          if (item.previewUrl) URL.revokeObjectURL(item.previewUrl); component.controls();
        } catch (error) { component.notice(error); }
      }, controller);
      node("workspaceChatControl").hidden = true; node("workspaceChatQuestions").hidden = true;
    }
    return { commands: commands, projection: function () { return owner; }, snapshot: snapshot, accept: accept, refresh: refresh,
      draft: draft, writeDraft: writeDraft, captureDraft: captureDraft, running: running, pending: pending, attachFiles: attachFiles,
      hasAttachments: function () { var saved = draft(); return !!(saved && saved.attachments.length); },
      pendingUnits: function () { return buildPendingInputUnits(component); }, renderUtilities: renderUtilities,
      presentation: function () { var view = snapshot().activeRunViewState; return { running: running(), readOnly: snapshot().chatReadOnly,
        canceling: !!(chatInbox(id).canceling || state.chatRuns[id] && state.chatRuns[id].cancelRequested),
        awaitingReply: view && view.lifecycle === "awaiting_user", awaitingApproval: !!approval.pendingActivity(), queue: runningMessageDelivery() === "Queue" }; },
      focus: function () {
        if (!id) return;
        prepareOfficeChatSelection(id, false);
        selectedOfficeProjection = owner; state.activeChatId = id;
        state.chatReadOnly = snapshot().chatReadOnly; state.chatDetailPending = component.loading; state.chatDetailError = component.loadError;
        state.draftAttachments = (draft() && draft().attachments || []).slice();
        renderChatSessions();
      },
      activate: function () { if (id) inbox.start(); },
      pollInbox: function () { return inbox.readNow(); },
      dispose: function () { if (selectedOfficeProjection === owner) selectedOfficeProjection = null; },
      inbox: function (previous, current) {
        if (!previous || previous.epoch !== current.epoch || previous.running !== current.running || chatInboxNeedsHistory(id)) refresh();
        component.render();
      }
    };
  }
  window.RNAssistantOfficeChatView = { create: create };
}());
