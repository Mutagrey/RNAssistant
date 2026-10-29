(function () {
  "use strict";
  var selectedSessions = new Map();
  var activeOwner = "office", sharedComposer = null, actions = Object.create(null), actionsBound = false;
  var workspaceMessageEdit = null;
  var officeComposerAnchor = null;
  var viewNodes = { Messages: "messages", Form: "chatForm", Input: "chatInput", Send: "sendButton", Stop: "stopButton",
    Notice: "workspaceChatNotice", Control: "workspaceChatControl", Pending: "workspaceChatPending",
    Approve: "workspaceChatApprove", Reject: "workspaceChatReject", Questions: "workspaceChatQuestions" };
  function node(name) { return viewNodes[name] ? document.getElementById(viewNodes[name]) : null; }
  function registerActions(owner, handlers) {
    if (owner !== "office" && owner !== "project" && owner !== "plugin") throw new Error("Unknown chat owner: " + owner);
    actions[owner] = handlers;
  }
  function dispatch(name, value) {
    var handler = actions[activeOwner] && actions[activeOwner][name];
    if (handler) return handler(value);
  }
  function canMessageAction(name, message, index) {
    if (activeOwner === "office") return false;
    var handler = actions[activeOwner] && actions[activeOwner].canMessageAction;
    return !!handler && !!handler(name, message, index);
  }
  function messageAction(name, message, index) {
    if (!canMessageAction(name, message, index)) return;
    return dispatch("messageAction", { name: name, message: message, index: index });
  }
  function paintMessageEdit() {
    var editing = activeOwner !== "office" && !!workspaceMessageEdit;
    var form = node("Form"), bar = document.getElementById("messageEditBar");
    form.classList.toggle("is-message-editing", editing);
    bar.classList.toggle("hidden", !editing);
    bar.setAttribute("aria-hidden", String(!editing));
    document.getElementById("cancelMessageEditButton").disabled = !editing;
    node("Send").title = editing ? "Отправить заново" : "Отправить";
    node("Send").setAttribute("aria-label", editing ? "Отправить заново" : "Отправить");
  }
  function cancelWorkspaceMessageEdit(restore) {
    if (!workspaceMessageEdit) return;
    var prior = workspaceMessageEdit;
    workspaceMessageEdit = null;
    if (restore !== false && activeOwner === prior.owner) {
      node("Input").value = prior.draft;
      node("Input").dispatchEvent(new Event("input", { bubbles: true }));
    }
    paintMessageEdit();
  }
  function startWorkspaceMessageEdit(message) {
    if (activeOwner === "office" || !message || !message.Id) return;
    var snapshot = dispatch("snapshot"), workspaceId = field(field(snapshot, "project"), "workspaceId");
    var sessionId = field(snapshot, "sessionId"), revision = field(field(snapshot, "state"), "revision");
    if (!workspaceId || !sessionId || !Number.isSafeInteger(revision)) return;
    if (sharedComposer) sharedComposer.draftVersion();
    workspaceMessageEdit = { owner: activeOwner, workspaceId: workspaceId, sessionId: sessionId,
      messageId: message.Id, revision: revision, draft: node("Input").value };
    node("Input").value = messageContent(message);
    node("Input").dispatchEvent(new Event("input", { bubbles: true }));
    paintMessageEdit();
    node("Input").focus();
  }
  function currentMessageEdit() {
    if (!workspaceMessageEdit || activeOwner !== workspaceMessageEdit.owner) return null;
    var snapshot = dispatch("snapshot");
    return field(field(snapshot, "project"), "workspaceId") === workspaceMessageEdit.workspaceId &&
      field(snapshot, "sessionId") === workspaceMessageEdit.sessionId ? workspaceMessageEdit : null;
  }
  function bindActions() {
    if (actionsBound) return;
    actionsBound = true;
    var form = node("Form"), input = node("Input");
    form.addEventListener("submit", function (event) { event.preventDefault(); dispatch("submit"); });
    input.addEventListener("input", function () { dispatch("input"); });
    input.addEventListener("keydown", function (event) {
      if (event.key === "Escape") dispatch("escape", event);
      if (!event.defaultPrevented && event.key === "Enter" && !event.isComposing &&
          !event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey) {
        event.preventDefault(); dispatch("submit");
      }
    });
    node("Stop").addEventListener("click", function () { dispatch("stop"); });
    document.getElementById("clearInputButton").addEventListener("click", function () {
      if (activeOwner === "office") dispatch("clear");
      else { input.value = ""; input.dispatchEvent(new Event("input", { bubbles: true })); input.focus(); }
    });
    node("Approve").addEventListener("click", function () { dispatch("resolve", true); });
    node("Reject").addEventListener("click", function () { dispatch("resolve", false); });
    document.getElementById("cancelMessageEditButton").addEventListener("click", function () {
      if (activeOwner !== "office") { cancelWorkspaceMessageEdit(true); dispatch("refresh"); }
    });
  }
  function mount(owner, container) {
    var view = document.getElementById("workspaceChatView");
    var list = document.getElementById("messages");
    var form = document.getElementById("chatForm"), input = document.getElementById("chatInput");
    if (!view || !list || !form || !input || (owner !== "office" && owner !== "project" && owner !== "plugin")) return false;
    if (owner !== "office" && !container) return false;
    var changed = activeOwner !== owner;
    if (changed && workspaceMessageEdit) cancelWorkspaceMessageEdit(false);
    if (changed && activeOwner !== "office") dispatch("suspend");
    if (activeOwner === "office" && owner !== "office") {
      if (!officeComposerAnchor) {
        officeComposerAnchor = document.createComment("Office composer");
        form.parentNode.insertBefore(officeComposerAnchor, form);
      }
      suspendOfficeTranscript();
      captureChatDraft(state.activeChatId, false);
      list._chatSignature = null;
      delete list.dataset.session;
    }
    if (changed) ["composerAddPicker", "chatModelPicker"].forEach(function (id) {
      var picker = document.getElementById(id);
      if (picker) picker.open = false;
    });
    if (changed && activeOwner !== "office" && sharedComposer) sharedComposer.select(null);
    var transcriptMount = document.getElementById(owner === "office" ? "officeChatTranscriptMount" : "workspaceChatTranscriptMount");
    if (list.parentNode !== transcriptMount) {
      if (owner === "office") transcriptMount.insertBefore(list, document.getElementById("chatScrollBottomButton"));
      else transcriptMount.appendChild(list);
      changed = true;
    }
    activeOwner = owner;
    form.dataset.owner = owner;
    document.getElementById("attachmentDrafts").hidden = owner !== "office";
    var pluginDrafts = document.getElementById("pluginAnnotationDrafts");
    if (pluginDrafts) pluginDrafts.hidden = owner !== "plugin" || pluginDrafts.childElementCount === 0;
    form.classList.toggle("project-chat-composer", owner !== "office");
    if (owner === "office") {
      form.title = "";
      if (form.parentNode !== document.getElementById("chatDropZone"))
        document.getElementById("chatDropZone").insertBefore(form, officeComposerAnchor);
      document.getElementById("stopButton").hidden = false;
      input.setAttribute("aria-label", "Сообщение в чат Office");
      if (changed) {
        var editText = hasActiveMessageEdit() ? state.editingText : null;
        restoreChatDraft(state.activeChatId);
        if (editText !== null) setChatInputText(editText || "", false, false);
      }
      renderSendControls();
      list.setAttribute("aria-label", "Сообщения чата");
      window.RNAssistantChatInspector.mount(owner);
      return changed;
    }
    if (view.parentNode !== container) container.appendChild(view);
    if (form.parentNode !== view) view.appendChild(form);
    form.classList.remove("is-message-editing", "is-preparing-attachments", "is-dragging");
    document.getElementById("stopButton").classList.remove("hidden");
    document.getElementById("sendButton").title = "Отправить";
    document.getElementById("sendButton").setAttribute("aria-label", "Отправить");
    input.readOnly = false;
    if (changed && sharedComposer) sharedComposer.select(null);
    view.dataset.owner = owner;
    view.classList.add("chat-main");
    view.classList.remove("plugin-workspace-chat-view");
    input.placeholder = owner === "plugin" ? "Опишите изменение плагина" : "Спросите о проекте…";
    input.setAttribute("aria-label", "Сообщение в чат проекта");
    list.setAttribute("aria-label", "Сообщения чата проекта");
    if (sharedComposer) sharedComposer.refresh();
    paintMessageEdit();
    window.RNAssistantChatInspector.mount(owner);
    return changed;
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
  function latestSnapshot(current, incoming) {
    if (!current || field(current, "sessionId") !== field(incoming, "sessionId") ||
        field(field(current, "project"), "workspaceId") !== field(field(incoming, "project"), "workspaceId")) return incoming;
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
    var currentStart = field(current, "messageStartIndex"), incomingStart = field(incoming, "messageStartIndex");
    var currentTotal = field(current, "messageTotalCount"), incomingTotal = field(incoming, "messageTotalCount");
    if (Number.isSafeInteger(previousRevision) && previousRevision === nextRevision &&
        currentStart === incomingStart && currentTotal === incomingTotal &&
        Array.isArray(current.messages) && Array.isArray(incoming.messages) &&
        (current.messages.length > incoming.messages.length ||
          current.messages.length === incoming.messages.length && current.messages.some(function (message, index) {
            return field(message, "id") !== field(incoming.messages[index], "id");
          }))) incoming.messages = current.messages;
    if (Number.isSafeInteger(currentStart) && Number.isSafeInteger(incomingStart) &&
        currentStart < incomingStart && Number.isSafeInteger(currentTotal) &&
        Number.isSafeInteger(incomingTotal) && incomingTotal >= currentTotal &&
        Array.isArray(current.messages) && current.messages.length >= incomingStart - currentStart) {
      incoming.messages = current.messages.slice(0, incomingStart - currentStart).concat(incoming.messages || []);
      incoming.messageStartIndex = currentStart;
    }
    return incoming;
  }
  function prependMessages(current, page) {
    var start = field(current, "messageStartIndex"), total = field(current, "messageTotalCount");
    var revision = field(field(current, "state"), "revision");
    if (!current || !page || field(page, "chatId") !== field(current, "sessionId") ||
        field(page, "sessionRevision") !== revision || field(page, "totalCount") !== total ||
        field(page, "startIndex") + (page.messages || []).length !== start)
      throw new Error("История изменилась. Обновите чат и повторите загрузку.");
    current.messages = page.messages.concat(current.messages || []);
    current.messageStartIndex = page.startIndex;
    return current;
  }
  function createRunObserver(read, changed, failed, progressChanged) {
    var target = null, selected = null, generation = 0, lastPushAt = 0, polling = false;
    var observer = window.RNAssistantConversationObserver.create({
      current: function () { return target ? { workspaceId: target.workspaceId,
        sessionId: target.sessionId, generation: generation } : null; },
      read: read, changed: changed, failed: failed, interval: 10000,
      shouldPoll: function () { return !lastPushAt || Date.now() - lastPushAt >= 30000; }
    });
    return { select: function (snapshot, pending) {
      var workspaceId = field(field(snapshot, "project"), "workspaceId"), sessionId = field(snapshot, "sessionId");
      var observe = !!(workspaceId && sessionId &&
        (pending || runStatus(field(snapshot, "state")) === "running"));
      selected = snapshot;
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
      if (!target || target.workspaceId !== field(payload, "workspaceId") ||
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
  function renderMessages(list, chat, emptyOptions) {
    chat = chat || { messages: [], state: {} };
    var key = JSON.stringify([activeOwner, field(field(chat, "project"), "workspaceId") || "", chat.sessionId || chat.pluginId || ""]), changed = list.dataset.session !== key;
    var following = changed || list.scrollHeight - list.scrollTop - list.clientHeight < 64;
    var snapshot = field(chat, "state"), status = runStatus(snapshot), live = field(snapshot, "modelProgress");
    if (status !== "running" || field(live, "runId") !== field(snapshot, "runId")) live = null;
    var start = field(chat, "messageStartIndex") || 0;
    var messages = chat.messages || [];
    var actionMessage = messages.find(visibleMessage);
    var historyActions = !!actionMessage && canMessageAction("fork", actionMessage, 0);
    var signature = JSON.stringify([chat.messages || [], live, field(snapshot, "pendingInputs"), status, start, field(chat, "messageTotalCount"), historyActions, emptyOptions]);
    if (!changed && list._chatSignature === signature) return;
    var scroll = list.scrollTop, height = list.scrollHeight;
    var oldStart = changed ? null : Number(list.dataset.startIndex);
    if (changed) { list.replaceChildren(); list._workspaceEntries = Object.create(null); }
    list.dataset.session = key; list.dataset.startIndex = String(start); list._chatSignature = signature;
    var units = [];
    if (start > 0) {
      appendMessageUnit(units, "history:older", String(start), function () {
        var more = document.createElement("button");
        more.type = "button"; more.className = "chat-history-more";
        more.textContent = "Показать предыдущие сообщения";
        more.onclick = function () {
          more.disabled = true;
          Promise.resolve(dispatch("loadPrevious")).finally(function () { more.disabled = false; });
        };
        return more;
      });
    }
    var visible = messages.filter(visibleMessage);
    if (!visible.length && !start && !live) {
      appendMessageUnit(units, "empty", JSON.stringify(emptyOptions || {}), function () { return renderChatEmptyState(emptyOptions || (!field(chat, "sessionId")
        ? { title: "Чатов пока нет", text: "Нажмите «+», чтобы создать чат." }
        : { title: "С чего начнём?", text: "Опишите задачу или задайте вопрос." })); });
    }
    var liveAttached = false;
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
        appendSavedRunUnit(units, run, true, String(historyActions), run.live ? "live:workspace-model" : null,
          run.live ? (function (progress) {
            return function (node) { paintLiveModelOutput(node.querySelector(".agent-stream-step"), progress); };
          }(liveProgress)) : null);
      }, function (message, index) {
        appendSavedMessageUnit(units, message, index, true, String(historyActions));
      });
    if (live && !liveAttached) {
      var liveRun = { items: [], workspace: true, live: true, workspaceProgress: liveProgress };
      appendMessageUnit(units, "live:workspace-model", String(liveRunId), function () {
        return renderAgentRunArticle(liveRun);
      }, "", null, function (node) {
        paintLiveModelOutput(node.querySelector(".agent-stream-step"), liveProgress);
      });
    }
    (field(snapshot, "pendingInputs") || []).forEach(function (pending) {
      var waiting = status === "running" && pending.RunId === field(snapshot, "runId");
      appendMessageUnit(units, "input:" + pending.Id, JSON.stringify([pending, waiting]), function () {
        var article = document.createElement("article"); article.className = "message user pending inbox-message";
        article.dataset.inputId = pending.Id;
        var status = document.createElement("div"); status.className = "inbox-item-status";
        status.textContent = pending.RunId === field(snapshot, "runId") ? "Уточнение сохранено, но ещё не применено" :
          "Уточнение из предыдущего запуска не применено";
        var body = document.createElement("div"); body.className = "markdown inbox-pending-text"; body.textContent = pending.Text;
        article.append(status, body);
        if (!waiting) {
          var restore = document.createElement("button"); restore.type = "button"; restore.textContent = "Вставить в ввод";
          restore.onclick = function () { if (sharedComposer) sharedComposer.append({
            workspaceId: field(field(chat, "project"), "workspaceId"), sessionId: field(chat, "sessionId") }, pending.Text); };
          article.appendChild(restore);
        }
        return article;
      });
    });
    list._workspaceEntries = reconcileMessageUnits(list, units, false, list._workspaceEntries || Object.create(null));
    list._workspaceLiveRunId = liveRunId || null;
    list.scrollTop = !changed && Number.isSafeInteger(oldStart) && start < oldStart
      ? scroll + list.scrollHeight - height : following ? list.scrollHeight : scroll;
  }
  function paintProgress(list, chat) {
    var snapshot = field(chat, "state"), preview = field(snapshot, "modelProgress");
    var runId = field(snapshot, "runId");
    var key = JSON.stringify([activeOwner, field(field(chat, "project"), "workspaceId") || "", field(chat, "sessionId") || ""]);
    var entry = list && list._workspaceEntries && list._workspaceEntries["live:workspace-model"];
    if (!entry || !entry.node || entry.node.parentNode !== list || list.dataset.session !== key ||
        list._workspaceLiveRunId !== runId || runStatus(snapshot) !== "running" ||
        field(preview, "runId") !== runId) return false;
    var section = entry.node.querySelector(".agent-stream-step");
    if (!section) return false;
    var following = list.scrollHeight - list.scrollTop - list.clientHeight < 64;
    paintLiveModelOutput(section, { content: field(preview, "message"), reasoning: field(preview, "reasoning"),
      reasoningTruncated: field(preview, "reasoningTruncated"), retrying: field(preview, "retrying"), showStatus: true });
    if (following) list.scrollTop = list.scrollHeight;
    return true;
  }
  function renderWorkspaceControls(chat, submitQuestions) {
    if (activeOwner === "office") return;
    var snapshot = field(chat, "state") || {}, pending = field(chat, "pendingId");
    node("Stop").hidden = runStatus(snapshot) !== "running";
    node("Control").hidden = !pending;
    node("Pending").textContent = pending ? (field(chat, "pendingTool") || "Действие") + "\n" +
      (field(chat, "pendingArguments") || "") : "";
    var preview = field(chat, "patchPreview");
    if (pending && preview && window.RNAssistantRunChanges) {
      node("Pending").textContent = "Предложенные изменения";
      window.RNAssistantRunChanges.renderPrepared(node("Pending"), preview);
    }
    var input = field(snapshot, "userInput"), set = field(input, "questionSet"), container = node("Questions");
    if (!set) { container.hidden = true; container.replaceChildren(); return; }
    window.RNAssistantQuestions.render(container, set, {
      scope: "workspace:" + field(field(chat, "project"), "workspaceId") + ":" + field(chat, "sessionId"),
      runId: field(input, "runId"), submit: submitQuestions
    });
  }
  function bindComposer(form, options) {
    if (sharedComposer) { if (options && options.onFiles) sharedComposer.attachFiles(options); return sharedComposer; }
    var input = form.querySelector("textarea");
    var current = null, loaded = null, mode = "agent", reasoning = false, locked = false;
    var draftStore = window.RNAssistantChatDrafts;
    function sameTarget(left, right) {
      return !!left && !!right && left.workspaceId === right.workspaceId && left.sessionId === right.sessionId;
    }
    function save() {
      if (workspaceMessageEdit) return;
      if (current) loaded = draftStore.saveWorkspace(current, { text: input.value, mode: mode, reasoning: reasoning });
    }
    function showOfficeContext(snapshot) {
      if (activeOwner !== "project") { form.title = ""; return; }
      var office = field(snapshot, "office"), status = runStatus(snapshot);
      var activeRun = status === "running" || status === "awaiting_confirmation", pinned = office && activeRun;
      form.title = pinned ? "Office: " + field(office, "host") + " · " + (field(office, "documentTitle") || "документ") :
        activeRun ? "Office: документ не привязан" : "Office: при отправке";
    }
    var clear = document.getElementById("clearInputButton");
    var picker = document.getElementById("composerAddPicker"), plan = document.getElementById("chatPlanButton");
    var fileAction = document.getElementById("attachFileButton"), fileInput = document.createElement("input");
    var modelPicker = document.getElementById("chatModelPicker"), modelLabel = document.getElementById("chatModelButtonLabel");
    var modelMenu = document.getElementById("chatModelMenu"), think = document.getElementById("chatReasoningToggle");
    var fileTarget = null, fileOptions = null;
    fileInput.id = "workspaceFileInput"; fileInput.type = "file"; fileInput.multiple = true; fileInput.hidden = true;
    picker.appendChild(fileInput);
    fileInput.onchange = function () {
      var files = Array.from(fileInput.files || []), target = fileTarget;
      fileInput.value = "";
      if (files.length && target && fileOptions) fileOptions.onFiles(files, target);
    };
    function attachFiles(nextOptions) {
      if (!nextOptions || !nextOptions.onFiles) return;
      fileOptions = nextOptions;
      if (sharedComposer) update();
    }
    function pickFiles() {
      if (activeOwner !== "project" || locked || !current || !fileOptions ||
          fileOptions.canAttachFiles && !fileOptions.canAttachFiles()) return;
      fileTarget = fileOptions.fileTarget ? fileOptions.fileTarget() : current;
      picker.open = false; fileInput.click();
    }
    function renderWorkspaceModelMenu() {
      if (modelMenu.dataset.owner === "workspace") return;
      modelMenu.replaceChildren();
      var settingsAction = document.createElement("button"); settingsAction.type = "button";
      settingsAction.className = "composer-picker-item composer-model-item";
      settingsAction.textContent = "Настройки модели";
      settingsAction.onclick = function () { modelPicker.open = false; switchTab("settings"); };
      modelMenu.appendChild(settingsAction);
      modelMenu.dataset.owner = "workspace";
    }
    function update() {
      if (activeOwner === "office") return;
      clear.hidden = !input.value; resizeComposerInput(input);
      setComposerPickerDisabled(picker, locked || !current);
      plan.classList.toggle("is-selected", mode === "plan"); plan.setAttribute("aria-pressed", String(mode === "plan")); plan.disabled = locked;
      fileAction.disabled = locked || !current || !fileOptions ||
        activeOwner !== "project" || !!(fileOptions.canAttachFiles && !fileOptions.canAttachFiles());
      think.setAttribute("aria-pressed", String(reasoning)); think.title = reasoning ? "Reasoning включён" : "Reasoning выключен";
      think.setAttribute("aria-label", reasoning ? "Выключить reasoning" : "Включить reasoning");
      think.classList.toggle("active", reasoning); think.disabled = locked;
      modelLabel.textContent = field(state.settings, "model") || "Модель";
      modelPicker.title = "Модель из общих настроек";
      setComposerPickerDisabled(modelPicker, locked || !current);
      modelMenu.modelRenderState = null;
      renderWorkspaceModelMenu();
    }
    input.addEventListener("input", function () { if (activeOwner !== "office") { save(); update(); } });
    window.addEventListener("resize", update);
    attachFiles(options);
    update();
    sharedComposer = {
      attachFiles: attachFiles,
      refresh: update,
      pickFiles: pickFiles,
      togglePlan: function () { if (!locked) { mode = mode === "plan" ? "agent" : "plan"; save(); update(); } },
      toggleReasoning: function () { if (!locked) { reasoning = !reasoning; save(); update(); } },
      select: function (target, snapshot) {
        showOfficeContext(snapshot);
        var next = target && target.workspaceId && target.sessionId
          ? { workspaceId: target.workspaceId, sessionId: target.sessionId } : null;
        var same = sameTarget(current, next);
        if (workspaceMessageEdit && (!same || draftStore.readWorkspace(next) !== loaded))
          cancelWorkspaceMessageEdit(false);
        current = next;
        var saved = draftStore.readWorkspace(next);
        if (same && saved === loaded) { update(); return; }
        loaded = saved;
        input.value = saved ? saved.text : ""; mode = saved ? saved.mode : field(snapshot, "mode") || "agent";
        reasoning = saved ? saved.reasoning : !!field(field(snapshot, "modelConfiguration"), "reasoningEnabled"); update();
      },
      options: function () { return { Mode: mode, ReasoningEnabled: reasoning }; },
      prepareInput: function (target, text, edit, activeRun) {
        var options = activeRun ? { SteerRunId: field(activeRun, "runId"), Mode: field(activeRun, "mode"),
          ReasoningEnabled: !!field(field(activeRun, "modelConfiguration"), "reasoningEnabled") } : this.options();
        return draftStore.prepareWorkspaceInput(target, text, Object.assign({}, options, edit ?
          { EditMessageId: edit.messageId, ExpectedRevision: edit.revision } : {}));
      },
      draftVersion: function () {
        if (!current) return null;
        var saved = draftStore.readWorkspace(current);
        if (saved !== loaded || !saved || saved.text !== input.value || saved.mode !== mode || saved.reasoning !== reasoning) save();
        return loaded.version;
      },
      lock: function (value) { locked = value; update(); },
      append: function (target, line) {
        if (sameTarget(current, target)) {
          var latest = draftStore.readWorkspace(current);
          if (latest !== loaded) this.select(target);
          input.value = input.value.trim() ? input.value.replace(/\s*$/, "") + "\n" + line : line;
          input.dispatchEvent(new Event("input", { bubbles: true }));
          return;
        }
        var saved = draftStore.readWorkspace(target) || { text: "", mode: "agent", reasoning: false };
        draftStore.saveWorkspace(target, { text: saved.text.trim() ? saved.text.replace(/\s*$/, "") + "\n" + line : line,
          mode: saved.mode, reasoning: saved.reasoning });
      },
      accepted: function (target, text, version, request) {
        var previous = draftStore.readWorkspace(target);
        var cleared = draftStore.acceptWorkspaceInput(target, request, text, version);
        if (cleared && activeOwner !== "office" && sameTarget(current, target) && loaded === previous) {
          if (currentMessageEdit()) workspaceMessageEdit.draft = "";
          else input.value = "";
          loaded = cleared; update();
        }
      },
      acceptedEdit: function (target, request) {
        draftStore.acknowledgeWorkspaceInput(target, request);
        if (activeOwner !== "office" && currentMessageEdit() && sameTarget(current, target)) cancelWorkspaceMessageEdit(true);
      }
    };
    return sharedComposer;
  }
  window.RNAssistantWorkspaceChat = { renderMessages: renderMessages, renderWorkspaceControls: renderWorkspaceControls, bindComposer: bindComposer,
    mount: mount, owner: function () { return activeOwner; }, node: node, messages: function () { return node("Messages"); },
    registerActions: registerActions, bindActions: bindActions,
    canMessageAction: canMessageAction, messageAction: messageAction,
    startMessageEdit: startWorkspaceMessageEdit, cancelMessageEdit: cancelWorkspaceMessageEdit,
    currentMessageEdit: currentMessageEdit,
    composerAction: function (name) { if (sharedComposer && activeOwner !== "office" && sharedComposer[name]) sharedComposer[name](); },
    refreshComposer: function () { if (sharedComposer) sharedComposer.refresh(); },
    snapshot: function () { return dispatch("snapshot"); }, runStatus: runStatus,
    rememberSession: rememberSession, selectedSession: function (workspaceId) { return selectedSessions.get(workspaceId); },
    latestSnapshot: latestSnapshot, prependMessages: prependMessages,
    createRunObserver: createRunObserver, paintProgress: paintProgress,
    acceptProgress: function (payload) { dispatch("progress", payload); },
    readOnly: readOnly, runNotice: runNotice };
}());
