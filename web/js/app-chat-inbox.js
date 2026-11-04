// Durable input state is owned by the runtime. This cache is presentation only.
function chatInbox(chatId) {
  return (state.chatInboxes || {})[chatId || state.activeChatId] || { items: [], running: false, paused: false };
}
// Transient echoes bridge intake -> queue -> history. They never authorize delivery.
function chatInputEchoes(chatId) {
  state.chatInputEchoes = state.chatInputEchoes || {};
  return state.chatInputEchoes[chatId] || (state.chatInputEchoes[chatId] = []);
}
function reconcileChatInputReceipt(chatId, item, allowInsert) {
  var echoes = chatInputEchoes(chatId);
  var index = echoes.findIndex(function (echo) {
    return item.Id && echo.Id === item.Id || item.OperationId && echo.OperationId === item.OperationId;
  });
  var submission = (state.inboxSubmissions || {})[chatId];
  if (!submission || !item.OperationId || submission.OperationId !== item.OperationId) submission = null;
  var previous = index < 0 ? null : echoes[index];
  if (submission && submission.receipt && (!previous || submission.receipt.Revision > (previous.Revision || 0)))
    previous = submission.receipt;
  if ((!allowInsert && !previous && !submission) || (previous && previous.Revision > item.Revision)) return false;
  var receipt = Object.assign({}, previous || submission, item);
  delete receipt.localStatus;
  delete receipt.receipt;
  // Retain a terminal revision only while its submit acknowledgement can still arrive.
  if (submission) submission.receipt = receipt;
  var rendered = submission && submission.renderedId === receipt.Id || chatId === state.activeChatId && (state.messages || []).some(function (message) {
    return (message.Id || message.id) === receipt.Id;
  });
  if (receipt.Status === "Removed" || rendered) {
    if (index >= 0) echoes.splice(index, 1);
    return index >= 0;
  }
  var changed = index < 0 || !previous || previous.Revision !== receipt.Revision ||
    previous.Status !== receipt.Status || previous.localStatus;
  if (index >= 0) echoes[index] = receipt;
  else echoes.push(receipt);
  return !!changed;
}
function applyChatInbox(inbox, deferRender) {
  if (!inbox || !inbox.chatId) return;
  var component = window.RNAssistantConversationPanel && RNAssistantConversationPanel.office(inbox.chatId);
  state.chatInboxes = state.chatInboxes || {};
  var old = state.chatInboxes[inbox.chatId];
  var stale = old && old.epoch === inbox.epoch && old.revision > inbox.revision;
  var changed = false;
  if (!stale) {
    state.chatInboxes[inbox.chatId] = inbox;
    (inbox.items || []).forEach(function (item) {
      changed = reconcileChatInputReceipt(inbox.chatId, item, true) || changed;
    });
  }
  (inbox.receipts || []).forEach(function (item) {
    changed = reconcileChatInputReceipt(inbox.chatId, item, false) || changed;
  });
  if (inbox.acceptedInput) {
    var submission = (state.inboxSubmissions || {})[inbox.chatId];
    if (!stale || submission && submission.OperationId === inbox.acceptedInput.OperationId)
      changed = reconcileChatInputReceipt(inbox.chatId, inbox.acceptedInput, !stale) || changed;
  }
  if (stale) {
    if (changed && !deferRender && inbox.chatId === state.activeChatId) renderChatInbox();
    return;
  }
  var endedRun = inbox.running === false && !state.activeSends[inbox.chatId] &&
    state.chatRuns && state.chatRuns[inbox.chatId];
  if (endedRun) endChatRunTracking(inbox.chatId);
  if (inbox.chatId === state.activeChatId) {
    var announcer = component ? component.node("inboxAnnouncer") : !window.RNAssistantConversationPanel && $("inboxAnnouncer");
    if (announcer && (!old || old.revision !== inbox.revision)) announcer.textContent =
      "Ожидают отправки: " + (inbox.items || []).length + (inbox.paused ? ". Очередь приостановлена." : ".");
    renderSendControls();
    if (!deferRender && (!old || old.epoch !== inbox.epoch || old.revision !== inbox.revision ||
        old.hasPendingWait !== inbox.hasPendingWait || old.runPaused !== inbox.runPaused || old.phase !== inbox.phase || old.running !== inbox.running || old.paused !== inbox.paused || changed)) renderChatInbox();
  }
  if (!deferRender && (endedRun || !old || old.running !== inbox.running)) renderChatSessions();
  if (component && !deferRender) component.inbox(old, inbox);
}
function reconcileChatInputEchoes(chatId, messages) {
  state.chatInputEchoes = state.chatInputEchoes || {};
  state.chatInputEchoes[chatId] = chatInputEchoes(chatId).filter(function (item) {
    if (!item.Id) return true; // Only an intake receipt can resolve an uncertain local send.
    // The history page may predate queue consumption even within one response.
    // Absence from the queue is not evidence that its user bubble was rendered.
    var rendered = (messages || []).some(function (message) { return (message.Id || message.id) === item.Id; });
    var submission = (state.inboxSubmissions || {})[chatId];
    if (rendered && submission && submission.OperationId === item.OperationId) submission.renderedId = item.Id;
    return !rendered;
  });
}
function runningMessageDelivery() {
  return String((state.settings || {}).RunningMessageDelivery || "Steer").toLowerCase() === "queue" ? "Queue" : "Steer";
}
function inboxOperationId() {
  return "input-" + (window.crypto && window.crypto.randomUUID ? window.crypto.randomUUID() :
    Date.now().toString(36) + "-" + Math.random().toString(36).slice(2));
}
async function submitInboxMessage(text, attachments, chatId, delivery, answers, steerRunId, steerPendingId, component) {
  component = component || (window.RNAssistantConversationPanel && RNAssistantConversationPanel.office(chatId));
  if (window.RNAssistantConversationInput.isPending("office", { chatId: chatId })) return false;
  if (delivery === "Steer" && !answers && !steerRunId) delivery = "Queue";
  if (delivery !== "Steer" || answers) { steerRunId = null; steerPendingId = null; }
  if (!answers && readOfficeDraft(chatId) && readOfficeDraft(chatId).pendingVisualAnnotation) {
    setAttachmentStatus(chatId, "Завершите или удалите незавершённую аннотацию перед отправкой.", "warning");
    return false;
  }
  attachments = (attachments || []).slice();
  var releasePending = window.RNAssistantConversationInput.begin("office", { chatId: chatId });
  if (!releasePending) return false;
  renderSendControls();
  // Retain the identity across uncertain transport retries, including accepted-but-unacknowledged sends.
  var fingerprint = JSON.stringify([chatId, text, attachments.map(attachmentId), delivery,
    answers || null, steerRunId || null, steerPendingId || null]);
  var echoes = chatInputEchoes(chatId);
  state.inboxSubmissions = state.inboxSubmissions || {};
  var pending = state.inboxSubmissions[chatId];
  if (!pending || pending.fingerprint !== fingerprint) pending = echoes.find(function (item) {
    // Equal text after a successful ACK is a new user action. Only an unresolved
    // transport failure may reuse an older operation's idempotency key.
    return item.localStatus === "unconfirmed" && item.fingerprint === fingerprint;
  });
  if (!pending) {
    pending = { fingerprint: fingerprint, OperationId: inboxOperationId(), Text: text,
      Attachments: attachments.slice(), Delivery: delivery, Answers: answers,
      SteerRunId: steerRunId || null, SteerPendingId: steerPendingId || null };
    echoes.push(pending);
  }
  state.inboxSubmissions[chatId] = pending;
  pending.localStatus = "submitting";
  var submittedDraft = !answers && component ? component.captureDraft(false) : !answers && state.activeChatId === chatId ? captureChatDraft(chatId) : readOfficeDraft(chatId);
  var gate = window.RNAssistantConversationInput.create("office", { chatId: chatId },
    pending.OperationId, steerRunId);
  if (component) component.render({ forceScroll: true });
  if (state.activeChatId === chatId) renderChatInbox({ forceScroll: true });
  try {
    var inbox = await gate.submit(
      function () { return answers ? Promise.resolve() : window.RNAssistantChatDrafts.flushOffice(chatId); },
      function () { return send("submitChatInput", { chatId: chatId, operationId: pending.OperationId,
        text: text, resourceDraftIds: attachments.map(attachmentId), delivery: delivery,
        steerRunId: steerRunId, steerPendingId: steerPendingId, answers: answers }); },
      function (response) { return response; });
    pending.localStatus = "syncing";
    applyChatInbox(inbox);
    delete state.inboxSubmissions[chatId];
    if (!answers) {
      var previousDraft = component && component.isCurrent() ? component.captureDraft(false) : captureChatDraft(chatId);
      var acceptedDraft = acceptOfficeDraft(chatId, text, submittedDraft && submittedDraft.version, attachments);
      if (component) component.acceptDraft(previousDraft, acceptedDraft);
      else chatComposer().acceptDraft(officeComposerTarget(chatId), previousDraft, acceptedDraft);
      if (state.activeChatId === chatId) {
        state.draftAttachments = acceptedDraft ? acceptedDraft.attachments.slice() : [];
        if (typeof renderAttachmentDrafts === "function") renderAttachmentDrafts();
      }
    }
    if (state.activeChatId === chatId) {
      updateComposerInputState();
      clearSendError();
      if ($("inboxStatus")) $("inboxStatus").textContent = "";
    }
    if (chatInboxNeedsHistory(chatId)) refreshInboxChat(chatId);
    return true;
  } catch (error) {
    pending.localStatus = "unconfirmed";
    if (component) component.notice(error);
    log(error.detail || error.message, "error");
    var status = $("inboxStatus");
    if (status && state.activeChatId === chatId) status.textContent = "Не удалось подтвердить отправку. Текст сохранён — повторите отправку.";
    return false;
  } finally {
    releasePending();
    if (component) component.render();
    renderSendControls();
    if (state.activeChatId === chatId) renderChatInbox();
  }
}
function refreshInboxChat(chatId) {
  var component = window.RNAssistantConversationPanel && RNAssistantConversationPanel.office(chatId);
  return component ? component.refresh() : Promise.resolve(null);
}
async function changeInboxItem(action, item, text, component) {
  var state = component ? component.inboxState : window.state;
  var chatId = component ? component.target.sessionId : state.activeChatId;
  var editingDraft = state.inboxEditDraft;
  try {
    var response = await send(action, { chatId: chatId, inputId: item.Id, expectedRevision: item.Revision, text: text });
    if (state.inboxEditing === item.Id && state.inboxEditDraft === editingDraft) { state.inboxEditing = null; state.inboxEditDraft = null; }
    applyChatInbox(response);
    if (component) component.notice("");
    else if (state.activeChatId === chatId) $("inboxStatus").textContent = "";
  } catch (error) {
    if (component) { component.notice(error); await component.refresh(); }
    else { if (state.activeChatId === chatId) $("inboxStatus").textContent = error.detail || error.message; await pollChatInbox(); }
  }
}
function inboxButton(label, action) {
  var button = document.createElement("button");
  button.type = "button"; button.textContent = label; button.addEventListener("click", action);
  return button;
}
function renderChatInbox(options) {
  if (typeof renderMessages === "function") renderMessages(options);
}
function pollChatInbox() {
  var component = window.RNAssistantConversationPanel && RNAssistantConversationPanel.office(state.activeChatId);
  return component ? component.pollInbox() : Promise.resolve(null);
}
function chatInboxNeedsHistory(chatId) {
  var component = window.RNAssistantConversationPanel && RNAssistantConversationPanel.office(chatId);
  var messages = component ? component.snapshot().messages : chatId === state.activeChatId ? state.messages : [];
  return chatInputEchoes(chatId).some(function (item) {
    return item.Status === "Applied" && !(messages || []).some(function (message) { return messageId(message) === item.Id; });
  });
}
function pendingChatInputs() {
  return chatInputEchoes(state.activeChatId).filter(function (item) {
    return !item.Id || !(state.messages || []).some(function (message) { return (message.Id || message.id) === item.Id; });
  });
}
function pendingInputStatus(item, box) {
  if (item.localStatus === "submitting") return "Отправка…";
  if (item.localStatus === "unconfirmed") return "Отправка не подтверждена";
  if (!(box.items || []).some(function (pending) { return pending.Id === item.Id; })) return "Обновление истории…";
  if (item.Delivery === "Steer" && item.AcceptedRunId && (!box.running || box.runId && box.runId !== item.AcceptedRunId))
    return "Не доставлено в исходный запуск";
  if (box.paused && !box.running) return "В очереди · приостановлено";
  if (item.Status === "Delivering") return "Передаю агенту…";
  if (item.Delivery === "Steer" && box.running) return box.phase === "executing" ? "Применится после текущего действия" : "Ожидает применения";
  return box.activeRun ? "В очереди" : "Передаю агенту…";
}
function renderPendingInput(item, chatId, editing, component, source) {
  var state = component ? component.inboxState : window.state;
  var $ = component ? component.node : window.$;
  function renderChatInbox() { if (component) component.render(); else window.renderChatInbox(); }
  var box = source ? source.box : chatInbox(chatId);
  function changeInboxItem(action, value, text) { return source ? source.change(action, value, text) : window.changeInboxItem(action, value, text, component); }
  var node = document.createElement("article"); node.className = "message user pending inbox-message";
  node.setAttribute("aria-label", "Сообщение в очереди");
  var heading = document.createElement("div"); heading.className = "inbox-heading";
  var status = document.createElement("span"); status.className = "inbox-item-status";
  status.textContent = pendingInputStatus(item, box); heading.appendChild(status);
  node.appendChild(heading);
  if (window.RNAssistantAgentCommunication) RNAssistantAgentCommunication.appendSender(node, item);
  if (editing) {
    var draft = state.inboxEditDraft;
    var editor = document.createElement("textarea"); editor.value = draft.text; editor.rows = 3;
    editor.setAttribute("aria-label", "Текст сообщения в очереди");
    editor.addEventListener("input", function () { draft.text = editor.value; });
    node.appendChild(editor);
    var controls = document.createElement("div"); controls.className = "inbox-actions";
    var save = inboxButton("Сохранить", async function () {
      save.disabled = true; editor.readOnly = true;
      try { await changeInboxItem("editChatInput", { Id: item.Id, Revision: draft.revision }, editor.value); }
      finally { save.disabled = false; editor.readOnly = false; }
    });
    controls.appendChild(save);
    function cancel() { state.inboxEditing = null; state.inboxEditDraft = null; renderChatInbox(); }
    controls.appendChild(inboxButton("Отмена", cancel));
    editor.addEventListener("keydown", function (event) {
      if (event.key === "Escape") { event.preventDefault(); cancel(); }
    });
    node.appendChild(controls);
    window.requestAnimationFrame(function () { if (editor.isConnected) editor.focus({ preventScroll: true }); });
  } else {
    var body = document.createElement("div"); body.className = "markdown inbox-pending-text";
    body.textContent = item.Text || (item.Answers ? "Ответы на вопросы" : "Вложения"); node.appendChild(body);
    var actions = document.createElement("div"); actions.className = "inbox-actions";
    var queued = (box.items || []).some(function (pending) { return pending.Id === item.Id; });
    if (item.localStatus === "unconfirmed") actions.appendChild(inboxButton("Повторить отправку", function () {
      submitInboxMessage(item.Text, item.Attachments || [], chatId, item.Delivery, item.Answers,
        item.SteerRunId || item.AcceptedRunId, item.SteerPendingId || item.PendingId);
    }));
    if (queued && item.Status === "Pending" && !(window.RNAssistantAgentCommunication && RNAssistantAgentCommunication.sender(item))) {
      if (item.Delivery !== "Steer") actions.appendChild(inboxButton("Отправить сейчас", function () { changeInboxItem("steerChatInput", item); }));
      if (!item.QuestionSetId) actions.appendChild(inboxButton("Редактировать", function () {
        state.inboxEditing = item.Id;
        state.inboxEditDraft = { chatId: chatId, id: item.Id, text: item.Text, revision: item.Revision };
        renderChatInbox();
      }));
    }
    if (queued && (item.Status === "Pending" || box.paused && !box.running)) {
      actions.appendChild(inboxButton("Удалить", function () { changeInboxItem("removeChatInput", item); }));
    }
    if (source && source.appendDraft && item.Text && item.Delivery === "Steer" && item.AcceptedRunId &&
        (!box.running || box.runId && box.runId !== item.AcceptedRunId))
      actions.appendChild(inboxButton("Вставить в ввод", function () { source.appendDraft(item.Text); }));
    if (actions.childNodes.length) {
      var menu = document.createElement("details"); menu.className = "inbox-item-menu";
      menu.setAttribute("data-disclosure-key", "input-menu");
      var summary = document.createElement("summary"); summary.textContent = "…";
      summary.setAttribute("aria-label", "Действия с сообщением в очереди");
      menu.appendChild(summary); menu.appendChild(actions); heading.appendChild(menu);
      menu.addEventListener("toggle", function () {
        if (menu.open) menu.classList.toggle("opens-below",
          summary.getBoundingClientRect().top - $("messages").getBoundingClientRect().top < actions.offsetHeight + 8);
      });
      menu.addEventListener("keydown", function (event) {
        if (event.key === "Escape") { menu.open = false; summary.focus({ preventScroll: true }); }
      });
    }
  }
  if ((item.Attachments || []).length) {
    var files = document.createElement("small"); files.className = "inbox-files";
    files.textContent = item.Attachments.map(function (file) { return file.FileName || file.fileName || "Вложение"; }).join(" · ");
    node.appendChild(files);
  }
  return node;
}
async function controlSavedInboxRun(component, chatId, target, cancel) {
  var scope = component ? component.target : { source: "office", sessionId: chatId };
  if (!target || target.sessionId !== chatId || !target.runId || !Number.isSafeInteger(target.revision))
    throw new Error("Состояние запуска устарело. Обновите чат.");
  var command = Object.assign({}, scope), expected = Object.assign({}, target), receipt;
  if (cancel) {
    command.target = expected;
    receipt = await send("cancelAgentWait", command);
  } else {
    command.targets = [expected];
    var response = await send("resumeAgentWaits", command);
    receipt = response && Array.isArray(response.receipts) && response.receipts.length === 1 ? response.receipts[0] : null;
  }
  if (!receipt || receipt.sessionId !== expected.sessionId || receipt.runId !== expected.runId)
    throw new Error("Результат управления не подтверждён. Обновите чат.");
  if (cancel && ["Cancelled", "AlreadyStopped"].includes(receipt.status) || !cancel && receipt.status === "Requested") return;
  var message = receipt.status === "Changed" ? "Запуск изменился. Обновите чат." :
    receipt.status === "Unavailable" ? "Запуск сейчас недоступен для этой команды. Обновите чат." :
    "Результат управления не подтверждён. Обновите чат без повторения команды.";
  throw new Error(message + (receipt.detail ? " " + receipt.detail : ""));
}

function buildPendingInputUnits(component, source) {
  var state = component ? component.inboxState : window.state;
  var chatId = component ? component.target.sessionId : state.activeChatId, box = source ? source.box : chatInbox(chatId), units = [];
  var history = component ? component.snapshot().messages : state.messages;
  var items = (source ? box.items : chatInputEchoes(chatId)).filter(function (item) { return !item.Id || !(history || []).some(function (message) { return messageId(message) === item.Id; }); });
  if ((box.paused || box.runPaused) && ((box.items || []).length || box.hasPendingWait)) units.push({ key: "live:inbox-paused", transient: true,
    signature: JSON.stringify([box.pauseReason, box.running, box.hasPendingWait, box.runPaused, box.waitTarget, !!state.inboxRunControlBusy]), render: function () {
    var note = document.createElement("div"); note.className = "inbox-pause";
    var reason = document.createElement("span");
    reason.textContent = box.runPaused ? "Выполнение приостановлено" : box.pauseReason || (box.hasPendingWait ? "Ожидание сохранено" : "Очередь приостановлена");
    note.appendChild(reason);
    async function control(cancel) {
      if (state.inboxRunControlBusy) return;
      state.inboxRunControlBusy = true;
      if (component) { component.notice(""); component.render(); } else renderChatInbox();
      try {
        if (box.hasPendingWait) await controlSavedInboxRun(component, chatId, box.waitTarget, cancel);
        else if (source) await source.resume();
        else applyChatInbox(await send("resumeChatInbox", { chatId: chatId }));
      } catch (error) {
        if (component) component.notice(error); else if (window.state.activeChatId === chatId) $("inboxStatus").textContent = error.message;
      } finally {
        try {
          if (component && component.isCurrent()) {
            if (component.target.source === "office") await component.pollInbox();
            await component.refresh();
          } else if (!component && window.state.activeChatId === chatId) await pollChatInbox();
        } catch (error) { if (component) component.notice(error); }
        state.inboxRunControlBusy = false;
        if (component && component.isCurrent()) component.render(); else if (!component) renderChatInbox();
      }
    }
    var resume = inboxButton(box.runPaused ? "Продолжить" : box.hasPendingWait ? "Продолжить ожидание" : "Продолжить очередь", function () { return control(false); });
    resume.disabled = !!state.inboxRunControlBusy || box.hasPendingWait && !box.waitTarget;
    note.appendChild(resume);
    if (box.hasPendingWait && !box.running) {
      var stop = inboxButton("Остановить", function () { return control(true); });
      stop.disabled = !!state.inboxRunControlBusy || !box.waitTarget;
      note.appendChild(stop);
    }
    return note;
  } });
  return units.concat(items.map(function (item) {
    var editing = state.inboxEditing === item.Id && state.inboxEditDraft && state.inboxEditDraft.chatId === chatId;
    var statusText = pendingInputStatus(item, box);
    return { key: "live:input:" + (item.OperationId || item.Id), transient: true,
      // Polling must not replace a focused editor or its selection. Save uses its captured revision.
      signature: JSON.stringify(editing ? [item.Id, "editing"] : [item,
        (box.items || []).some(function (pending) { return pending.Id === item.Id; }), box.paused && !box.running, box.runId]),
      actionsSignature: statusText,
      refreshActions: function (node) { node.querySelector(".inbox-item-status").textContent = statusText; },
      render: function () { return renderPendingInput(item, chatId, editing, component, source); } };
  }));
}
