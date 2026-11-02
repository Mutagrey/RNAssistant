(function () {
  "use strict";
  function key(target) {
    return target && target.sessionId ? JSON.stringify([target.source, target.workspaceId || "", target.sessionId]) : "";
  }
  function create(nodes) {
    var sources = Object.create(null), owner = "office", selected = "", generation = 0, bound = false, disposed = false;
    var edits = new Map(), listeners = [], frames = new Set();
    function invalidate() {
      ++generation;
      frames.forEach(function (frame) { window.cancelAnimationFrame(frame); }); frames.clear();
    }
    function context() {
      var adapter = sources[owner], target = adapter && adapter.target ? adapter.target() : null;
      var next = owner + ":" + key(target);
      if (next !== selected) { selected = next; invalidate(); }
      return { owner: owner, target: target && Object.freeze(Object.assign({}, target)), adapter: adapter };
    }
    function capture() {
      var scope = context(), stamp = generation;
      scope.isCurrent = function () { context(); return !disposed && stamp === generation; };
      scope.invoke = function (name, value) {
        if (!scope.isCurrent()) return;
        var handler = scope.adapter && scope.adapter[name];
        if (handler) return handler(value, scope);
      };
      return scope;
    }
    function dispatch(name, value) { return capture().invoke(name, value); }
    function current(target) { return key(target) !== "" && key(context().target) === key(target); }
    function edit(target) { return edits.get(key(target)) || null; }
    function defer(action) {
      var scope = capture(), frame = window.requestAnimationFrame(function () {
        frames.delete(frame);
        if (scope.isCurrent() && nodes.form.isConnected !== false &&
            (!nodes.form.closest || !nodes.form.closest("[inert]"))) action();
      });
      frames.add(frame);
    }
    function startEdit(target, details) {
      if (!current(target)) return null;
      var input = nodes.input, previous = edit(target);
      if (previous) return null;
      invalidate();
      var value = Object.assign({}, details, { draft: input.value, selectionStart: input.selectionStart || 0,
        selectionEnd: input.selectionEnd || 0, scrollTop: input.scrollTop || 0, busy: false });
      edits.set(key(target), value); return value;
    }
    function cancelEdit(target, options) {
      options = options || {};
      var value = edit(target);
      if (!value) return null;
      edits.delete(key(target));
      if (!current(target)) return value;
      invalidate();
      if (options.restore === false) return value;
      nodes.input.value = value.draft;
      dispatch("input");
      defer(function () {
        var length = nodes.input.value.length;
        nodes.input.selectionStart = Math.min(value.selectionStart, length);
        nodes.input.selectionEnd = Math.min(Math.max(value.selectionEnd, nodes.input.selectionStart), length);
        nodes.input.scrollTop = value.scrollTop;
        if (options.focus) nodes.input.focus();
      });
      return value;
    }
    function listen(node, event, action) {
      if (!node) return;
      node.addEventListener(event, action);
      listeners.push(function () { node.removeEventListener(event, action); });
    }
    function bind() {
      if (bound) return;
      bound = true;
      function submit() { if (!nodes.send.disabled) dispatch("submit"); }
      listen(nodes.form, "submit", function (event) { event.preventDefault(); submit(); });
      listen(nodes.input, "input", function () {
        var value = edit(context().target);
        if (value) value.text = nodes.input.value;
        dispatch("input");
      });
      listen(nodes.input, "keydown", function (event) {
        if (event.key === "Escape" && !event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey) {
          if (edit(context().target) && nodes.cancelEdit && nodes.cancelEdit.disabled) event.preventDefault();
          else dispatch("escape", event);
        }
        if (!event.defaultPrevented && event.key === "Enter" && !event.isComposing &&
            !event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey) {
          event.preventDefault(); submit();
        }
      });
      listen(nodes.stop, "click", function () { dispatch("stop"); });
      listen(nodes.clear, "click", function () { dispatch("clear"); });
      listen(nodes.cancelEdit, "click", function () { dispatch("cancelEdit"); });
      listen(nodes.approve, "click", function () { dispatch("resolve", true); });
      listen(nodes.reject, "click", function () { dispatch("resolve", false); });
    }
    return { register: function (name, adapter) { sources[name] = adapter; },
      activate: function (name) { owner = name; context(); }, owner: function () { return owner; },
      capture: capture, dispatch: dispatch, sync: context, current: current, defer: defer,
      edit: edit, startEdit: startEdit, cancelEdit: cancelEdit, bind: bind,
      dispose: function () {
        if (disposed) return;
        disposed = true; invalidate(); listeners.forEach(function (remove) { remove(); });
        listeners = []; edits.clear(); sources = Object.create(null);
      }
    };
  }
  window.RNAssistantChatComposer = { create: create };
})();

// Temporary adapter to the single mounted composer; the controller itself accepts explicit nodes/targets.
var currentChatComposer = null;
function chatComposer() {
  if (!currentChatComposer) {
    currentChatComposer = window.RNAssistantChatComposer.create({ form: $("chatForm"), input: $("chatInput"),
      send: $("sendButton"), stop: $("stopButton"), clear: $("clearInputButton"), cancelEdit: $("cancelMessageEditButton"),
      approve: $("workspaceChatApprove"), reject: $("workspaceChatReject") });
    if (typeof messageView === "function") messageView().composer = currentChatComposer;
  }
  return currentChatComposer;
}
function officeComposerTarget(id) { return { source: "office", sessionId: id === undefined ? state.activeChatId : id }; }
function workspaceComposerTarget(target) {
  return { source: "workspace", workspaceId: target && target.workspaceId, sessionId: target && target.sessionId };
}

function positionComposerTooltip(tooltip, trigger) {
  tooltip.style.setProperty("--composer-tooltip-shift", "0px");
  var container = trigger.closest(".chat-main");
  var top = container ? Math.max(12, container.getBoundingClientRect().top + 12) : 12;
  tooltip.style.maxHeight = Math.max(0, Math.min(420, trigger.getBoundingClientRect().top - top - 10)) + "px";
  var rect = tooltip.getBoundingClientRect();
  var shift = Math.max(12 - rect.left, Math.min(0, window.innerWidth - 12 - rect.right));
  tooltip.style.setProperty("--composer-tooltip-shift", shift + "px");
}

function bindComposerTooltip(wrap, trigger, tooltip, canShow) {
  function hide() { tooltip.hidden = true; }
  function show() {
    if (trigger.disabled || (canShow && !canShow())) return;
    Array.prototype.slice.call(document.querySelectorAll(".composer-tooltip")).forEach(function (other) {
      other.hidden = other !== tooltip;
    });
    positionComposerTooltip(tooltip, trigger);
  }
  wrap.addEventListener("pointerenter", show);
  wrap.addEventListener("pointerleave", hide);
  trigger.addEventListener("focus", show);
  trigger.addEventListener("blur", hide);
  window.addEventListener("resize", hide);
  document.addEventListener("pointerdown", function (event) {
    if (!wrap.contains(event.target)) hide();
  });
  document.addEventListener("keydown", function (event) {
    if (event.key === "Escape") hide();
  });
}

function renderChatModePicker() {
  if (window.RNAssistantWorkspaceChat && window.RNAssistantWorkspaceChat.owner() !== "office") {
    window.RNAssistantWorkspaceChat.refreshComposer(); return;
  }
  var button = $("chatPlanButton");
  if (!button) return;
  var plan = state.activeChatMode === "plan";
  var disabled = !!state.chatReadOnly || !!state.chatNavigationPending || !!state.initializePromise ||
    !!currentActiveSend() || window.RNAssistantConversationInput.isPending("office", { chatId: state.activeChatId }) || hasActiveMessageEdit() ||
    state.modeSaving || state.reasoningSaving || state.bridgeUnavailable ||
    state.chatDetailPending || state.chatDetailError || !state.activeChatId;
  button.classList.toggle("is-selected", plan);
  button.disabled = !!disabled;
  button.setAttribute("aria-pressed", String(plan));
}

function bindComposerPickers() {
  $("composerOpenMaterialsButton").addEventListener("click", function () { switchTab("artifacts"); });
  $("chatPlanButton").addEventListener("click", function () {
    if (this.disabled) return;
    if (window.RNAssistantWorkspaceChat && window.RNAssistantWorkspaceChat.owner() !== "office")
      window.RNAssistantWorkspaceChat.composerAction("togglePlan");
    else saveChatMode(state.activeChatMode === "plan" ? "agent" : "plan");
  });
  var pickers = [$("composerAddPicker"), $("chatModelPicker")].filter(Boolean);
  pickers.forEach(function (picker) {
    var trigger = picker.querySelector("summary");
    trigger.addEventListener("click", function (event) {
      if (picker.classList.contains("is-disabled")) event.preventDefault();
    });
    picker.addEventListener("toggle", function () {
      if (!picker.open) return;
      pickers.forEach(function (other) { if (other !== picker) other.open = false; });
    });
  });
  $("composerAddMenu").addEventListener("click", function (event) {
    var button = event.target.closest("button");
    if (!button || button.disabled) return;
    $("composerAddPicker").open = false;
    if (button.id !== "composerOpenMaterialsButton") $("composerAddButton").focus();
  });
  document.addEventListener("pointerdown", function (event) {
    pickers.forEach(function (picker) {
      if (picker.open && !picker.contains(event.target)) picker.open = false;
    });
  });
  document.addEventListener("keydown", function (event) {
    if (event.key !== "Escape") return;
    var opened = pickers.filter(function (picker) { return picker.open; })[0];
    if (opened) {
      event.preventDefault();
      opened.open = false;
      opened.querySelector("summary").focus();
    }
  });
}

function renderChatRunNotice() {
  var root = $("chatRunNotice");
  if (!root) return;
  var run = state.activeRunViewState;
  var runNotice = window.RNAssistantRunViewState.runNotice(run);
  var sendError = state.failedSend && state.failedSend.error;
  var title = runNotice ? runNotice.title : "Не удалось выполнить действие";
  var text = runNotice ? runNotice.text : String(sendError || "");
  var signature = JSON.stringify([state.activeChatId, run && run.runId, runNotice, text]);
  if (root.noticeSignature === signature) return;
  root.noticeSignature = signature;
  root.replaceChildren();
  root.hidden = !(runNotice || sendError);
  if (root.hidden) return;
  root.setAttribute("role", "alert");
  var notice = document.createElement("div");
  notice.className = "chat-notice-content" + (runNotice && runNotice.warning ? "" : " is-danger");
  if (window.RNAssistantIcons) notice.innerHTML = window.RNAssistantIcons.render("warning");
  var copy = document.createElement("div");
  copy.className = "chat-notice-copy";
  var heading = document.createElement("strong"); heading.textContent = title; copy.appendChild(heading);
  var description = document.createElement("p"); description.textContent = text; copy.appendChild(description);
  if (run && run.runId && typeof window.openRunJournal === "function") {
    var button = document.createElement("button"); button.type = "button";
    button.className = "agent-details-link"; button.textContent = "Подробности";
    var chatId = state.activeChatId, runId = run.runId;
    button.addEventListener("click", function () {
      if (state.activeChatId === chatId) window.openRunJournal({ chatId: chatId, runId: runId, filter: "problems" });
    });
    copy.appendChild(button);
  }
  notice.appendChild(copy); root.appendChild(notice);
}

// Domain owners supply capabilities and activity; one renderer owns the shared
// composer's labels, editing, reply prompt and stop presentation.
function renderComposerPresentation(view) {
  var form = $("chatForm"), input = $("chatInput"), send = $("sendButton"), stop = $("stopButton");
  form.classList.toggle("is-message-editing", !!view.editing);
  form.classList.toggle("is-preparing-attachments", !!view.preparing);
  $("messageEditBar").classList.toggle("hidden", !view.editing);
  $("messageEditBar").setAttribute("aria-hidden", String(!view.editing));
  $("cancelMessageEditButton").disabled = !view.editing || !!view.editBusy || !!view.running;
  $("composerReplyPrompt").hidden = !view.awaitingReply || !!view.editing;
  $("focusReplyButton").disabled = !!view.inputReadOnly;
  send.classList.remove("hidden");
  send.title = view.preparing ? "Подготовка вложения" : view.editing ? "Отправить заново" :
    view.running ? (view.queue ? "В очередь" : "Уточнить текущую задачу") :
    view.awaitingReply ? "Отправить ответ" : "Отправить";
  send.setAttribute("aria-label", send.title);
  stop.hidden = !view.running;
  stop.classList.toggle("hidden", !view.running);
  stop.disabled = !!view.canceling;
  stop.title = view.canceling ? "Останавливаю запрос" : "Остановить запрос";
  stop.setAttribute("aria-label", stop.title);
  input.readOnly = !!view.inputReadOnly || !!(view.editing && view.editBusy);
  input.setAttribute("aria-label", "Сообщение в чат");
  input.placeholder = view.readOnly ? "История чата · создайте новый чат…" :
    view.loading ? "Обновляю чат…" : view.loadError ? "Повторите загрузку чата…" :
    view.unavailable ? "Чат недоступен…" : view.noChat ? "Выберите или создайте чат…" :
    view.editing ? "Измените сообщение или отправьте его заново…" :
    view.running || view.awaitingApproval ? "Уточните задачу или добавьте сообщение…" :
    view.awaitingReply ? "Напишите ответ, чтобы продолжить…" : "Напишите сообщение…";
  $("clearInputButton").disabled = !!view.preparing || !!view.editBusy;
}

function renderComposerInputState(hasContent) {
  $("chatForm").classList.toggle("has-input", !!hasContent);
  $("clearInputButton").hidden = !$("chatInput").value.trim();
  resizeComposerInput($("chatInput"));
}

function renderSendControls() {
  if (window.RNAssistantWorkspaceChat && window.RNAssistantWorkspaceChat.owner() !== "office") return;
  renderChatRunNotice();
  var navigating = !!(state.chatNavigationPending || state.initializePromise);
  var detailUnavailable = !!(state.chatDetailPending || state.chatDetailError);
  var activeSend = currentActiveSend();
  var isEditing = hasActiveMessageEdit();
  var isSending = !!activeSend;
  var isPreparingAttachments = !isSending && !!state.activeChatId &&
    window.RNAssistantConversationInput.isPending("office", { chatId: state.activeChatId });
  var isCanceling = isSending && !!activeSend.canceling;
  var approvalPending = !isEditing && typeof pendingAgentApprovalActivity === "function" && !!pendingAgentApprovalActivity();
  var awaitingReply = !!state.activeChatId && !navigating && !detailUnavailable &&
    !isSending && !isPreparingAttachments && !isEditing && !state.bridgeUnavailable &&
    !!state.activeRunViewState && state.activeRunViewState.lifecycle === "awaiting_user";
  var currentDocumentAvailable = typeof activeChatUsesCurrentDocument !== "function" || activeChatUsesCurrentDocument();

  if ($("newChatButton")) $("newChatButton").disabled = navigating || !!state.bridgeUnavailable;

  renderComposerPresentation({ editing: isEditing, editBusy: isEditing && officeMessageEdit().busy,
    preparing: isPreparingAttachments, running: isSending, canceling: isCanceling,
    awaitingReply: awaitingReply, awaitingApproval: approvalPending,
    readOnly: state.chatReadOnly, loading: navigating || state.chatDetailPending,
    loadError: !!state.chatDetailError, unavailable: state.bridgeUnavailable, noChat: !state.activeChatId,
    inputReadOnly: state.chatReadOnly || navigating || detailUnavailable || isPreparingAttachments ||
      state.modeSaving || state.reasoningSaving || state.bridgeUnavailable || !state.activeChatId,
    queue: isSending && (runningMessageDelivery() === "Queue" || typeof exactOfficeSteerTarget !== "function" ||
      !exactOfficeSteerTarget(state.activeChatId).runId) });
  setComposerPickerDisabled($("composerAddPicker"), navigating || detailUnavailable ||
    isPreparingAttachments || isEditing || state.bridgeUnavailable || !state.activeChatId);
  renderChatModePicker();
  if (typeof renderChatModelPicker === "function") {
    renderChatModelPicker();
  }
  if (typeof renderReasoningToggle === "function") {
    renderReasoningToggle();
  }
  if (navigating || detailUnavailable || isPreparingAttachments) {
    if (typeof setComposerPickerDisabled === "function") {
      setComposerPickerDisabled($("chatModelPicker"), true);
    }
    if ($("chatReasoningToggle")) $("chatReasoningToggle").disabled = true;
  }
  if ($("addSelectionContextButton")) {
    $("addSelectionContextButton").disabled = navigating || detailUnavailable || isPreparingAttachments || isEditing || state.bridgeUnavailable || !currentDocumentAvailable;
  }
  if ($("attachFileButton")) {
    $("attachFileButton").disabled = navigating || isPreparingAttachments || isEditing || state.bridgeUnavailable || !state.activeChatId;
  }
  if (typeof renderContextMeterAvailability === "function") {
    renderContextMeterAvailability();
  }
  updateComposerInputState();
}

function updateComposerInputState() {
  if (window.RNAssistantWorkspaceChat && window.RNAssistantWorkspaceChat.owner() !== "office") return;
  var input = $("chatInput");
  var hasText = !!(input && input.value.trim());
  var hasAttachments = !!(state.draftAttachments && state.draftAttachments.length);
  var hasPendingAttachments = !!(state.chatResourceIngestions && state.activeChatId &&
    state.chatResourceIngestions[state.activeChatId]);

  renderComposerInputState(hasText || hasAttachments || hasPendingAttachments);
  updateSendButtonAvailability(hasText || hasAttachments || hasPendingAttachments);
}

function updateSendButtonAvailability(hasContent) {
  if (window.RNAssistantWorkspaceChat && window.RNAssistantWorkspaceChat.owner() !== "office") return;
  var sendButton = $("sendButton");
  if (!sendButton) {
    return;
  }

  var editingTarget = hasActiveMessageEdit() ? findEditingMessage() : null;
  var canSaveEdit = !!editingTarget && canSaveMessageEdit(editingTarget.message, editingTarget.index);
  var unfinishedAnnotation = readOfficeDraft(state.activeChatId);
  sendButton.disabled = state.chatReadOnly ||
    !!state.chatNavigationPending || !!state.initializePromise ||
    !!state.chatDetailPending || !!state.chatDetailError ||
    !!state.activeChatId && window.RNAssistantConversationInput.isPending("office", { chatId: state.activeChatId }) ||
    state.modelSaving ||
    state.modeSaving ||
    state.reasoningSaving ||
    state.bridgeUnavailable ||
    !state.activeChatId ||
    !!(unfinishedAnnotation && unfinishedAnnotation.pendingVisualAnnotation && !hasActiveMessageEdit()) ||
    (hasActiveMessageEdit() ? !canSaveEdit : !hasContent);
}

function resizeChatInput() {
  resizeComposerInput($("chatInput"));
}

function resizeComposerInput(input) {
  if (!input) {
    return;
  }

  input.style.height = "auto";
  var styles = window.getComputedStyle(input);
  var fontSize = parseFloat(styles.fontSize) || 14;
  var lineHeight = parseFloat(styles.lineHeight) || (fontSize * 1.45);
  var verticalChrome =
    (parseFloat(styles.paddingTop) || 0) +
    (parseFloat(styles.paddingBottom) || 0) +
    (parseFloat(styles.borderTopWidth) || 0) +
    (parseFloat(styles.borderBottomWidth) || 0);
  var minHeight = Math.ceil((lineHeight * 2) + verticalChrome);
  var maxHeight = Math.ceil((lineHeight * 6) + verticalChrome);
  var contentHeight = Math.max(input.scrollHeight, minHeight);

  input.style.height = Math.min(contentHeight, maxHeight) + "px";
  input.style.overflowY = contentHeight > maxHeight ? "auto" : "hidden";
}

function setChatInputText(text, shouldFocus, saveDraft) {
  if (window.RNAssistantWorkspaceChat && window.RNAssistantWorkspaceChat.owner() !== "office") {
    if (saveDraft !== false && state.activeChatId && !hasActiveMessageEdit())
      saveOfficeDraft(state.activeChatId, text || "", state.draftAttachments || [], true);
    return;
  }
  var input = $("chatInput");
  if (!input) {
    return;
  }

  input.value = text || "";
  var edit = chatComposer().edit(officeComposerTarget());
  if (edit) edit.text = input.value;
  if (saveDraft !== false && !hasActiveMessageEdit()) captureChatDraft(state.activeChatId, true);
  updateComposerInputState();
  window.requestAnimationFrame(resizeChatInput);
  if (shouldFocus) {
    input.focus();
  }
}
