(function () {
  "use strict";
  // One component implementation. DockWorkspace owns instances and placement;
  // adapters own addressed commands, while existing services own runs and drafts.
  var template, serial = 0, selected = null, ready = false;
  function key(target) {
    return target.sessionId ? "chat:" + encodeURIComponent(JSON.stringify([target.source, target.workspaceId || "", target.sessionId])) : "conversation";
  }
  function route(target) { return Object.assign({ view: "conversation" }, target); }
  function find(target) { return RNAssistantDockWorkspace.getView(key(target)); }
  function office(id) { return id ? find({ source: "office", sessionId: id }) : null; }
  function prepare() {
    if (template) return;
    template = document.getElementById("chatDropZone").cloneNode(true);
  }
  function create(target) {
    prepare();
    var root = template.cloneNode(true), nodes = Object.create(null), prefix = "chat-view-" + (++serial) + "-";
    var address = Object.freeze(target.source === "workspace"
      ? { source: "workspace", workspaceId: target.workspaceId, sessionId: target.sessionId }
      : { source: "office", sessionId: target.sessionId });
    var disposed = false, adapter, communication, listeners = new AbortController(), fullPending = false, questionsSignature = null, headerTimer = null;
    [root].concat(Array.from(root.querySelectorAll("[id]"))).forEach(function (node) {
      var id = node.id; nodes[id] = node; node.dataset.chatNode = id; node.id = prefix + id;
    });
    root.querySelectorAll("[aria-controls], [aria-describedby], [for]").forEach(function (node) {
      ["aria-controls", "aria-describedby", "for"].forEach(function (attribute) {
        if (node.hasAttribute(attribute)) node.setAttribute(attribute, node.getAttribute(attribute).split(" ")
          .map(function (id) { return nodes[id] ? prefix + id : id; }).join(" "));
      });
    });
    function node(id) { return nodes[id] || null; }
    root.classList.add("conversation-panel"); root.hidden = false;
    root.dataset.conversation = JSON.stringify(address);
    node("chatForm").dataset.owner = address.source === "office" ? "office" : "project";
    var view = RNAssistantConversationView.get(node("messages"), {
      root: root,
      button: node("chatScrollBottomButton"), clear: function () {
        releaseMessageMediaThumbnails(node("messages")); clearMarkdownEnhancements(node("messages"));
      }
    });
    view.begin(address); view.bind();
    var composer = RNAssistantChatComposer.create({ form: node("chatForm"), input: node("chatInput"),
      send: node("sendButton"), stop: node("stopButton"), clear: node("clearInputButton"), cancelEdit: node("cancelMessageEditButton"),
      });
    view.composer = composer;
    var component = { target: address, route: route(address), element: root, view: view, composer: composer, node: node,
      inboxState: { inboxEditing: null, inboxEditDraft: null },
      loading: !!address.sessionId, loadError: "", hasSnapshot: false, error: "", isCurrent: function () { return !disposed; },
      loadFailed: function (error) {
        if (disposed) return;
        component.loading = false; component.loadError = error.detail || error.message || String(error);
        render();
      },
      snapshot: function () { return adapter.snapshot(); },
      transcript: function () { return adapter.transcript(); },
      draft: function () { return adapter.draft(); },
      refresh: function (options) { return Promise.resolve(adapter.refresh(options)); },
      accept: function (snapshot, options) { return adapter.accept(snapshot, options); },
      pendingUnits: function () { return adapter.pendingUnits ? adapter.pendingUnits() : []; },
      pollInbox: function () { return adapter.pollInbox ? adapter.pollInbox() : component.refresh(); },
      actionsSignature: function () { return JSON.stringify([composer.preferences().disabled, !!composer.edit(address), adapter.running(), state.bridgeUnavailable]); },
      captureDraft: function (changed) { return adapter.captureDraft(changed); },
      restoreDraft: function () { composer.restoreDraft(address, adapter.draft()); renderControls(); },
      notice: function (error) { if (!disposed) { component.error = error ? error.detail || error.message || String(error) : ""; renderControls(); } },
      render: render, controls: renderControls,
      stream: function (full) {
        if (disposed) return;
        fullPending = fullPending || full;
        view.schedule("stream", function () {
          if (fullPending) { fullPending = false; render(); }
          else if (!renderStreamingMessages(true, component)) render();
        });
      },
      focus: function () {
        if (disposed || selected === component) return;
        RNAssistantChatGroups.dismissDialog();
        selected = component;
        if (adapter.focus) adapter.focus();
        RNAssistantWorkspaceNavigation.select(address, true);
        renderControls();
        if (window.RNAssistantChatInspector) { RNAssistantChatInspector.mount(address.source === "office" ? "office" : "project"); RNAssistantChatInspector.render(); }
      },
      activate: function () { view.setVisible(true, render); render(); if (adapter.activate) adapter.activate(); },
      deactivate: function () { view.setVisible(false, render); stopHeaderTimer(); RNAssistantActionMenu.closeWithin(root); },
      layout: function () { if (!disposed) resizeComposerInput(node("chatInput")); },
      dispose: function (options) {
        if (disposed) return;
        if (!options || options.captureDraft !== false) adapter.captureDraft(false);
        disposed = true;
        stopHeaderTimer(); RNAssistantActionMenu.closeWithin(root);
        if (communication) communication.dispose();
        if (selected === component) selected = null;
        if (window.RNAssistantChatInspector) RNAssistantChatInspector.render();
        if (window.RNAssistantModelContext) RNAssistantModelContext.sync();
        listeners.abort(); if (adapter.dispose) adapter.dispose(); view.dispose();
        if (window.RNAssistantViewerRegistry) RNAssistantViewerRegistry.unmount(node("contextBox"));
      }
    };
    function on(element, event, fn) { if (element) element.addEventListener(event, fn, { signal: listeners.signal }); }
    function edit() { return composer.edit(address); }
    var common = {
      target: function () { return address; },
      input: function () { if (!edit()) adapter.captureDraft(true); renderControls(); },
      clear: function () { node("chatInput").value = ""; common.input(); node("chatInput").focus(); },
      cancelEdit: function () { composer.cancelEdit(address, { focus: true }); render(); },
      escape: function (event) { if (edit()) { event.preventDefault(); common.cancelEdit(); } },
      appendDraft: function (text) {
        composer.appendDraft(address, { read: adapter.draft, write: adapter.writeDraft }, text); renderControls();
      },
      refresh: render, preferenceChanged: renderControls, preferenceFailed: component.notice,
      preferenceUnavailable: component.notice
    };
    adapter = address.source === "office" ? RNAssistantOfficeChatView.create(component) : RNAssistantWorkspaceChat.createPanelAdapter(component);
    composer.register(address.source, Object.assign(common, adapter.commands)); composer.activate(address.source); composer.bind();
    component.approval = RNAssistantAgentApproval.create({
      state: Object.defineProperty({}, "activeRunViewState", { get: adapter.runView }), node: node,
      currentActiveSend: function () { return adapter.running() || adapter.pending(); },
      primaryText: function (pending) { return pending.toolName || "Действие"; },
      confirm: function (pendingId, runId) { return composer.dispatch("resolve", { pendingId: pendingId, runId: runId, approve: true }); },
      cancel: function (pendingId, runId) { return composer.dispatch("resolve", { pendingId: pendingId, runId: runId, approve: false }); }
    });
    composer.restoreDraft(address, adapter.draft());
    on(root, "pointerdown", component.focus); on(root, "focusin", component.focus);
    on(node("focusReplyButton"), "click", function () { node("chatInput").focus(); });
    on(node("chatPlanButton"), "click", function () { composer.setPreference("mode", composer.preferences().mode === "plan" ? "agent" : "plan"); });
    on(node("chatReasoningToggle"), "click", function () { composer.setPreference("reasoning", !composer.preferences().reasoning); });
    on(node("chatModelPicker"), "toggle", function () {
      if (node("chatModelPicker").open) {
        renderChatModelPicker(true, component);
        if (!state.modelCatalog.loaded && !state.modelCatalog.loading) loadModelCatalog(false).then(renderControls);
      }
    });
    var pickers = [node("composerAddPicker"), node("chatModelPicker")];
    pickers.forEach(function (picker) {
      on(picker.querySelector("summary"), "click", function (event) {
        if (picker.classList.contains("is-disabled")) event.preventDefault();
        else if (communication) communication.closeRecovery();
      });
      on(picker, "toggle", function () { if (picker.open) pickers.forEach(function (other) { if (other !== picker) other.open = false; }); });
    });
    on(document, "pointerdown", function (event) { pickers.forEach(function (picker) { if (!picker.contains(event.target)) picker.open = false; }); });
    on(root, "keydown", function (event) {
      if (event.key !== "Escape" || event.defaultPrevented) return;
      var picker = pickers.find(function (item) { return item.open; });
      if (picker) { picker.open = false; picker.querySelector("summary").focus(); event.preventDefault(); }
      else if (!node("contextManager").classList.contains("hidden")) { setContextManagerOpen(false, component); event.preventDefault(); }
    });
    on(node("attachFileButton"), "click", function () { node("attachmentFileInput").click(); });
    on(node("attachmentFileInput"), "change", function () { adapter.attachFiles(Array.from(this.files)); this.value = ""; });
    on(root, "dragover", function (event) { if (attachmentTransferHasFiles(event.dataTransfer)) event.preventDefault(); });
    on(root, "drop", function (event) { var files = attachmentTransferFiles(event.dataTransfer); if (files.length) { event.preventDefault(); adapter.attachFiles(files); } });
    on(node("chatInput"), "paste", function (event) { var files = attachmentTransferFiles(event.clipboardData); if (files.length) { event.preventDefault(); adapter.attachFiles(files); } });
    ["addSelectionContextButton", "addTextContextButton", "compactContextButton", "openDocumentButton"].forEach(function (id) {
      on(node(id), "click", function () { if (adapter.commands[id]) adapter.commands[id](); });
    });
    on(node("composerOpenMaterialsButton"), "click", function () { component.focus(); switchTab("artifacts"); });
    on(node("resumeAgentWaitsButton"), "click", function (event) {
      event.stopPropagation(); node("composerAddPicker").open = false;
      communication.openRecovery(node("composerAddButton"));
    });
    on(node("composerAddMenu"), "click", function (event) { if (event.target.closest("button")) node("composerAddPicker").open = false; });
    bindComposerTooltip(node("contextMeterWrap"), node("contextMeter"), node("contextMeterTooltip"), function () { return true; }, listeners.signal);
    on(node("contextMeter"), "click", function () { RNAssistantChatInspector.openUsage(address, view.scope(), node("contextMeter")); });
    function activityTarget() {
      var run = adapter.runView();
      return run && run.runId ? { owner: address.source === "office" ? "office" : "project",
        workspaceId: address.workspaceId, chatId: address.sessionId, runId: run.runId } : null;
    }
    function openActivity(target, scope, trigger) {
      if (scope.isCurrent() && target) RNAssistantChatInspector.openActivity(target, trigger);
    }
    on(node("chatActivityButton"), "click", function () { openActivity(activityTarget(), view.scope(), node("chatActivityButton")); });
    var chatMenu = RNAssistantActionMenu.createTrigger("Действия чата"); node("chatbarActions").appendChild(chatMenu);
    on(chatMenu, "click", function () {
      var scope = view.scope(), target = activityTarget(), items = [];
      if (!node("chatActivityButton").getClientRects().length) items.push({ label: "Ход работы", icon: "clock", disabled: !target,
        onSelect: function () { openActivity(target, scope, chatMenu); } });
      items.push({ label: "Обновить историю", icon: "refresh", onSelect: function () { if (scope.isCurrent()) component.refresh(); } });
      RNAssistantActionMenu.open(chatMenu, items);
    });
    on(node("openContextTabButton"), "click", function () { setContextManagerOpen(node("contextManager").classList.contains("hidden"), component); });
    on(node("closeContextManagerButton"), "click", function () { setContextManagerOpen(false, component); });
    on(node("contextJsonDetails"), "toggle", function () { if (this.open) mountContextJsonViewer(component); else clearContextJsonViewer(component); });
    on(node("clearContextButton"), "click", function () { composer.dispatch("clearContext"); });
    on(node("chatLoadRetry"), "click", function () { component.refresh(); });
    on(window, "resize", component.layout);
    function renderControls() {
      if (disposed) return;
      if (!view.canRender()) return;
      var presentation = adapter.presentation(), editing = edit();
      var disabled = component.loading || !!component.loadError || state.bridgeUnavailable || !address.sessionId;
      var preparing = adapter.pending();
      renderComposerPresentation(Object.assign({}, presentation, { editing: !!editing, editBusy: editing && editing.busy,
        loading: component.loading, loadError: component.loadError, unavailable: state.bridgeUnavailable, noChat: !address.sessionId,
        preparing: preparing, inputReadOnly: disabled || presentation.readOnly || !!(editing && editing.busy) }), node);
      var content = !!node("chatInput").value.trim() || adapter.hasAttachments();
      node("sendButton").disabled = disabled || preparing || presentation.readOnly || presentation.sendBlocked || !content || !!(editing && editing.busy) || !!(adapter.draft() && adapter.draft().pendingVisualAnnotation);
      renderComposerInputState(content, node);
      setComposerPickerDisabled(node("composerAddPicker"), disabled || preparing || !!editing);
      var preferences = composer.preferences(), plan = node("chatPlanButton");
      plan.classList.toggle("is-selected", preferences.mode === "plan"); plan.disabled = preferences.disabled;
      plan.setAttribute("aria-pressed", String(preferences.mode === "plan"));
      renderChatModelPicker(false, component);
      renderReasoningControl(node("chatReasoningToggle"), preferences.reasoning, preferences.disabled,
        effectiveModelSupportsReasoning(preferences.selected || settingsModel()));
      node("inboxStatus").textContent = component.error;
      if (adapter.renderUtilities) adapter.renderUtilities();
      component.approval.renderDock();
      renderQuestions();
      renderHeader();
    }
    function stopHeaderTimer() { if (headerTimer !== null) clearTimeout(headerTimer); headerTimer = null; }
    function renderHeader() {
      stopHeaderTimer();
      if (disposed || !view.canRender()) return;
      var header = adapter.header(), run = adapter.runView(), running = adapter.running();
      function text(id, value) { if (node(id).textContent !== value) node(id).textContent = value; }
      text("activeChatTitle", header.title);
      text("activeChatSubtitle", header.label + (header.unavailable ? " · недоступно" : ""));
      node("activeChatSubtitle").title = [header.label, header.detail].filter(Boolean).join("\n");
      var labels = { running: "Выполняю", awaiting_confirmation: "Нужно разрешение", awaiting_user: "Нужен ответ",
        completed: "Завершено", failed: "Ошибка", cancelled: "Остановлено" };
      var status = component.loading ? "Загрузка…" : run ? labels[run.lifecycle] || "" : running ? "Выполняю" : "";
      var started = run && Date.parse(run.startedUtc);
      if (running && Number.isFinite(started)) status += " · " + formatElapsedTime(Math.max(0, Date.now() - started));
      text("chatRunStatus", status); node("chatRunStatus").title = run && run.currentAction || status;
      node("chatActivityButton").disabled = !activityTarget();
      if (running) headerTimer = setTimeout(renderHeader, 1000);
    }
    function renderQuestions() {
      var pending = adapter.questions(), host = node("chatQuestions");
      var signature = JSON.stringify(pending && [pending.runId, pending.data, pending.activity, pending.message && messageId(pending.message)]);
      if (signature === questionsSignature) return;
      questionsSignature = signature; host.replaceChildren(); host.hidden = !pending;
      if (!pending) return;
      var presentation = createMessagePresentation(view, adapter.snapshot(), composer.capture());
      if (pending.data) renderQuestionCards(host, pending.data, { runId: pending.runId }, presentation);
      else appendQuestionCards(host, pending.activity, { presentation: presentation, message: pending.message });
    }
    function render(options) {
      if (disposed || !view.canRender()) return;
      var status = component.loading ? "Загрузка чата…" : component.loadError;
      node("chatLoadStatus").classList.toggle("hidden", !status);
      node("chatLoadStatusText").textContent = status;
      node("chatLoadRetry").hidden = !component.loadError || component.loading;
      if (!component.hasSnapshot && address.sessionId) {
        if (node("messages").childNodes.length) view.reset();
      } else renderMessages(options, component);
      renderControls();
    }
    communication = RNAssistantAgentCommunication.mount(component);
    component.communication = communication;
    component.projection = adapter.projection;
    component.progress = adapter.progress;
    component.inbox = adapter.inbox;
    component.acceptDraft = function (before, after) { composer.acceptDraft(address, before, after); renderControls(); };
    if (address.sessionId && ready) component.refresh(); else render();
    return component;
  }
  function register(workspace) {
    prepare(); bindAttachmentActions();
    workspace.registerConversation({ title: "Чат", icon: "chat", discovery: "primary", discoveryTitle: "Открыть чат", order: 50, discover: function () { RNAssistantWorkspaceNavigation.focusSearch(); return true; }, section: "chat", menu: true, perObject: true,
      maximumInstances: 12, renderer: "onlyWhenVisible", key: key, create: create,
      titleFor: function (target) {
        if (target.source === "office") {
          var summary = (state.chats || []).find(function (chat) { return chatId(chat) === target.sessionId; });
          return summary ? chatTitle(summary) : "Чат";
        }
        return "Чат проекта";
      },
      accepts: function (target) { return !!target && target.view === "conversation" && typeof target.sessionId === "string" &&
        (target.source === "office" || target.source === "workspace" && typeof target.workspaceId === "string" && !!target.workspaceId); }
    });
  }
  window.RNAssistantConversationPanel = { register: register, create: create, key: key, find: find, office: office,
    ready: function () {
      ready = true;
      RNAssistantDockWorkspace.eachView("conversation", function (component) {
        if (component.communication) component.communication.rebind();
        if (component.target.sessionId && (!component.projection().snapshot() || !component.snapshot().sessionRevision && component.target.source === "office")) component.refresh();
      });
    },
    closeDeleted: function (target) {
      var component = find(target);
      if (component) component.dispose({ captureDraft: false });
      RNAssistantDockWorkspace.close(key(target));
    },
    selected: function () { return selected; },
    open: function (target, options) { return RNAssistantDockWorkspace.openTarget(route(target), options); },
    each: function (callback) { RNAssistantDockWorkspace.eachView("conversation", callback); }
  };
}());
