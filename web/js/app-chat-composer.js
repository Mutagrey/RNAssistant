(function () {
  "use strict";
  function key(target) {
    return target && target.sessionId ? JSON.stringify([target.source, target.workspaceId || "", target.sessionId]) : "";
  }
  function create(nodes) {
    var sources = Object.create(null), owner = "office", selected = "", generation = 0, bound = false, disposed = false;
    var edits = new Map(), listeners = [], frames = new Set(), preferenceWrites = new Set(), draftBinding = null;
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
      scope.generation = stamp;
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
    function preferenceBusy(target) { return preferenceWrites.has(key(target || context().target)); }
    function inputTarget(target) { return target.source === "office" ? { chatId: target.sessionId } : target; }
    function preferences() {
      var scope = capture(), values = scope.invoke("preferences") || {};
      return Object.assign({}, values, { disabled: !scope.target || !scope.target.sessionId || !scope.isCurrent() ||
        !scope.adapter || !scope.adapter.preferenceService || !!values.disabled || preferenceBusy(scope.target) ||
        window.RNAssistantConversationInput.isPending(scope.target.source, inputTarget(scope.target)) });
    }
    async function setPreference(name, value) {
      var scope = capture(), values = preferences();
      if (values.disabled || ["model", "mode", "reasoning"].indexOf(name) < 0) return false;
      if (values[name === "model" ? "selected" : name] === value) return true;
      var service = scope.adapter.preferenceService(scope), id = key(scope.target);
      var release = window.RNAssistantConversationInput.begin(scope.target.source, inputTarget(scope.target));
      if (!release) return false;
      var change = Object.freeze({ name: name, value: value });
      preferenceWrites.add(id);
      async function reconcile(binding) {
        try {
          var currentService = binding.adapter.preferenceService(binding);
          var response = await currentService.read();
          if (binding.isCurrent()) currentService.apply(response, null);
        }
        catch (error) { binding.invoke("preferenceUnavailable", error); }
      }
      try {
        scope.invoke("preferenceChanged");
        var response = await service.write(change);
        return scope.isCurrent() && service.apply(response, change) !== false;
      } catch (error) {
        if (error.resultUncertain && scope.isCurrent()) await reconcile(scope);
        scope.invoke("preferenceFailed", error);
        return false;
      } finally {
        // A new visit to A must read its current state, not adopt the old A reply.
        if (!scope.isCurrent() && !disposed && current(scope.target)) await reconcile(capture());
        release(); preferenceWrites.delete(id);
        if (!disposed && current(scope.target)) dispatch("preferenceChanged");
      }
    }
    function edit(target) { return edits.get(key(target)) || null; }
    function draftText(draft) { return draft ? draft.text : ""; }
    function restoreDraft(target, draft) {
      if (disposed) return false;
      var actual = context().target;
      if (!target || !actual || target.source !== actual.source || (target.workspaceId || "") !== (actual.workspaceId || "") ||
          (target.sessionId || "") !== (actual.sessionId || "")) return false;
      var id = key(target), editing = edit(target);
      if (draftBinding && draftBinding.key === id && draftBinding.record === draft) return false;
      draftBinding = { key: id, record: draft };
      var text = editing ? editing.text : draftText(draft);
      if (nodes.input.value !== text) nodes.input.value = text;
      return true;
    }
    function captureDraft(target, write) {
      if (disposed || !current(target)) return null;
      var editing = edit(target), draft = write(editing ? editing.draft : nodes.input.value);
      draftBinding = { key: key(target), record: draft };
      return draft;
    }
    function acceptDraft(target, before, after) {
      if (disposed || before === after) return false;
      var editing = edit(target);
      if (editing && editing.draft === draftText(before)) editing.draft = draftText(after);
      if (!current(target) || !draftBinding || draftBinding.key !== key(target) || draftBinding.record !== before) return false;
      draftBinding.record = after;
      if (!editing && nodes.input.value === draftText(before)) nodes.input.value = draftText(after);
      return true;
    }
    function appendDraft(target, store, line) {
      var before = store.read(), text = draftText(before), editing = edit(target);
      if (!disposed && current(target) && draftBinding && draftBinding.key === key(target) && draftBinding.record === before)
        text = editing ? editing.draft : nodes.input.value;
      var saved = store.write(text.trim() ? text.replace(/\s*$/, "") + "\n" + line : line);
      if (editing && !disposed) editing.draft = draftText(saved);
      restoreDraft(target, saved);
      return saved;
    }
    function defer(action) {
      var scope = capture(), frame = window.requestAnimationFrame(function () {
        frames.delete(frame);
        if (scope.isCurrent() && nodes.form && nodes.form.isConnected !== false &&
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
      preferences: preferences, setPreference: setPreference, preferenceBusy: preferenceBusy,
      captureDraft: captureDraft, restoreDraft: restoreDraft, acceptDraft: acceptDraft, appendDraft: appendDraft,
      edit: edit, startEdit: startEdit, cancelEdit: cancelEdit, bind: bind,
      dispose: function () {
        if (disposed) return;
        disposed = true; invalidate(); listeners.forEach(function (remove) { remove(); });
        listeners = []; edits.clear(); preferenceWrites.clear(); draftBinding = null; sources = Object.create(null);
      }
    };
  }
  window.RNAssistantChatComposer = { create: create };
})();

// Selected-surface compatibility only; mounted views always receive their own composer.
var inactiveChatComposer = null;
function chatComposer() {
  var component = window.RNAssistantConversationPanel && RNAssistantConversationPanel.selected();
  if (component) return component.composer;
  return inactiveChatComposer || (inactiveChatComposer = RNAssistantChatComposer.create({}));
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

function bindComposerTooltip(wrap, trigger, tooltip, canShow, signal) {
  var listenerOptions = signal ? { signal: signal } : undefined;
  function hide() { tooltip.hidden = true; }
  function show() {
    if (trigger.disabled || (canShow && !canShow())) return;
    Array.prototype.slice.call(document.querySelectorAll(".composer-tooltip")).forEach(function (other) {
      other.hidden = other !== tooltip;
    });
    positionComposerTooltip(tooltip, trigger);
  }
  wrap.addEventListener("pointerenter", show, listenerOptions);
  wrap.addEventListener("pointerleave", hide, listenerOptions);
  trigger.addEventListener("focus", show, listenerOptions);
  trigger.addEventListener("blur", hide, listenerOptions);
  window.addEventListener("resize", hide, listenerOptions);
  document.addEventListener("pointerdown", function (event) {
    if (!wrap.contains(event.target)) hide();
  }, listenerOptions);
  document.addEventListener("keydown", function (event) {
    if (event.key === "Escape") hide();
  }, listenerOptions);
}

function renderChatModePicker() {
  var button = $("chatPlanButton");
  if (!button) return;
  var selection = chatComposer().preferences(), plan = selection.mode === "plan";
  button.classList.toggle("is-selected", plan);
  button.disabled = selection.disabled;
  button.setAttribute("aria-pressed", String(plan));
}

function renderChatRunNotice(component) {
  var $ = component ? component.node : window.$;
  var state = component ? component.snapshot() : window.state;
  var root = $("chatRunNotice");
  if (!root) return;
  var workspace = component && component.target.source === "workspace";
  var run = workspace ? RNAssistantRunViewState.normalize(state.state && (state.state.runViewState || state.state.RunViewState)) : state.activeRunViewState;
  var runNotice = workspace ? RNAssistantWorkspaceChat.runNotice(state) : RNAssistantRunViewState.runNotice(run);
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
      if (component ? component.isCurrent() : state.activeChatId === chatId) window.openRunJournal(workspace ? { owner: component.contextOwner || "project", workspaceId: component.target.workspaceId, sessionId: component.target.sessionId, runId: runId, filter: "problems" } : { chatId: chatId, runId: runId, filter: "problems" });
    });
    copy.appendChild(button);
  }
  notice.appendChild(copy); root.appendChild(notice);
}

// Domain owners supply capabilities and activity; one renderer owns the shared
// composer's labels, editing, reply prompt and stop presentation.
function renderComposerPresentation(view, node) {
  var $ = node || window.$;
  var form = $("chatForm"), input = $("chatInput"), send = $("sendButton"), stop = $("stopButton");
  form.classList.toggle("is-message-editing", !!view.editing);
  form.classList.toggle("is-preparing-attachments", !!view.preparing);
  form.classList.toggle("is-running", !!view.running);
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

function renderComposerInputState(hasContent, node) {
  var $ = node || window.$;
  $("chatForm").classList.toggle("has-input", !!hasContent);
  $("clearInputButton").hidden = !$("chatInput").value.trim();
  resizeComposerInput($("chatInput"));
}

function renderSendControls() {
  var component = window.RNAssistantConversationPanel && RNAssistantConversationPanel.selected();
  if (component) component.controls();
}
function updateComposerInputState() { renderSendControls(); }

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
  var pane = input.closest(".chat-main");
  var availableHeight = pane && pane.clientHeight || window.innerHeight;
  var maxHeight = Math.max(minHeight, Math.min(
    Math.ceil((lineHeight * 6) + verticalChrome), Math.floor(availableHeight * 0.2)));
  var contentHeight = input.value ? Math.max(input.scrollHeight, minHeight) : minHeight;

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
