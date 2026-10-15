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
    var announcer = $("inboxAnnouncer");
    if (announcer && (!old || old.revision !== inbox.revision)) announcer.textContent =
      "Ожидают отправки: " + (inbox.items || []).length + (inbox.paused ? ". Очередь приостановлена." : ".");
    renderSendControls();
    if (!deferRender && (!old || old.epoch !== inbox.epoch || old.revision !== inbox.revision ||
        old.phase !== inbox.phase || old.running !== inbox.running || old.paused !== inbox.paused || changed)) renderChatInbox();
  }
  if (!deferRender && (endedRun || !old || old.running !== inbox.running)) renderChatSessions();
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
function retainUnsentInputAttachments(drafts, sent) {
  return (drafts || []).filter(function (item) {
    var submitted = sent.some(function (attachment) { return attachmentId(attachment) === attachmentId(item); });
    if (submitted && item.previewUrl) URL.revokeObjectURL(item.previewUrl);
    return !submitted;
  });
}
async function submitInboxMessage(text, attachments, chatId, delivery, answers) {
  if (isPendingChatSubmit(chatId)) return;
  attachments = (attachments || []).slice();
  setPendingChatSubmit(chatId, true);
  renderSendControls();
  // Retain the identity across uncertain transport retries, including accepted-but-unacknowledged sends.
  var fingerprint = JSON.stringify([chatId, text, attachments.map(attachmentId), delivery, answers || null]);
  var echoes = chatInputEchoes(chatId);
  state.inboxSubmissions = state.inboxSubmissions || {};
  var pending = state.inboxSubmissions[chatId];
  if (!pending || pending.fingerprint !== fingerprint) pending = echoes.find(function (item) { return item.fingerprint === fingerprint; });
  if (!pending) {
    pending = { fingerprint: fingerprint, OperationId: inboxOperationId(), Text: text,
      Attachments: attachments.slice(), Delivery: delivery, Answers: answers };
    echoes.push(pending);
  }
  state.inboxSubmissions[chatId] = pending;
  pending.localStatus = "submitting";
  var submittedDraft = state.activeChatId === chatId ? $("chatInput").value : null;
  if (state.activeChatId === chatId) renderChatInbox({ forceScroll: true });
  try {
    var inbox = await send("submitChatInput", { chatId: chatId, operationId: pending.OperationId,
      text: text, resourceDraftIds: attachments.map(attachmentId), delivery: delivery, answers: answers });
    pending.localStatus = "syncing";
    applyChatInbox(inbox);
    delete state.inboxSubmissions[chatId];
    if (state.activeChatId === chatId) {
      if (!answers && $("chatInput").value === submittedDraft && submittedDraft.trim() === text) setChatInputText("", false);
      state.draftAttachments = retainUnsentInputAttachments(state.draftAttachments, attachments);
      if (typeof renderAttachmentDrafts === "function") renderAttachmentDrafts();
      if (typeof captureChatDraft === "function") captureChatDraft(chatId);
      clearSendError();
      if ($("inboxStatus")) $("inboxStatus").textContent = "";
    } else if (!answers) {
      var draft = state.chatDrafts && state.chatDrafts[chatId];
      if (draft) {
        if (draft.text === submittedDraft) draft.text = "";
        draft.attachments = retainUnsentInputAttachments(draft.attachments, attachments);
        if (!draft.text && !draft.attachments.length) delete state.chatDrafts[chatId];
      }
    }
    if (chatInboxNeedsHistory(chatId)) refreshInboxChat(chatId);
    return true;
  } catch (error) {
    pending.localStatus = "unconfirmed";
    log(error.detail || error.message, "error");
    var status = $("inboxStatus");
    if (status && state.activeChatId === chatId) status.textContent = "Не удалось подтвердить отправку. Текст сохранён — повторите отправку.";
    return false;
  } finally {
    setPendingChatSubmit(chatId, false);
    renderSendControls();
    if (state.activeChatId === chatId) renderChatInbox();
  }
}
async function refreshInboxChat(chatId) {
  state.inboxChatRefreshing = state.inboxChatRefreshing || {};
  if (state.inboxChatRefreshing[chatId]) return;
  state.inboxChatRefreshing[chatId] = true;
  try {
    if (state.activeChatId === chatId) applyChatStateForChat(await loadChatState(chatId), chatId);
  } catch (error) { log(error.message, "error"); }
  finally { delete state.inboxChatRefreshing[chatId]; }
}
async function changeInboxItem(action, item, text) {
  var chatId = state.activeChatId;
  var editingDraft = state.inboxEditDraft;
  try {
    var response = await send(action, { chatId: chatId, inputId: item.Id, expectedRevision: item.Revision, text: text });
    if (state.inboxEditing === item.Id && state.inboxEditDraft === editingDraft) { state.inboxEditing = null; state.inboxEditDraft = null; }
    applyChatInbox(response);
    if (state.activeChatId === chatId) $("inboxStatus").textContent = "";
  } catch (error) {
    if (state.activeChatId === chatId) $("inboxStatus").textContent = error.detail || error.message;
    await pollChatInbox();
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
async function pollChatInbox() {
  if (!state.activeChatId || state.bridgeUnavailable || state.inboxPolling || state.chatNavigationPending) return;
  var chatId = state.activeChatId;
  state.inboxPolling = true;
  try {
    var previous = chatInbox(chatId);
    // Prioritize missing echoes so a large pending queue cannot starve terminal receipts.
    var queuedIds = new Set((previous.items || []).map(function (item) { return item.Id; }));
    var echoes = chatInputEchoes(chatId).filter(function (item) { return item.OperationId; });
    var operationIds = echoes.filter(function (item) { return !queuedIds.has(item.Id); })
      .concat(echoes.filter(function (item) { return queuedIds.has(item.Id); }))
      .map(function (item) { return item.OperationId; }).slice(0, 128);
    var inbox = await send("getChatInbox", { chatId: chatId, receiptOperationIds: operationIds });
    applyChatInbox(inbox);
    var current = chatInbox(chatId);
    if (current.epoch !== previous.epoch || current.running !== previous.running ||
        current.phase !== previous.phase || chatInboxNeedsHistory(chatId)) refreshInboxChat(chatId);
  } catch (_) { /* The normal bridge banner reports disconnection; keep the saved projection. */ }
  finally { state.inboxPolling = false; }
}
function chatInboxNeedsHistory(chatId) {
  return chatInputEchoes(chatId).some(function (item) {
    return item.Status === "Applied" && (chatId !== state.activeChatId || !(state.messages || []).some(function (message) {
      return (message.Id || message.id) === item.Id;
    }));
  });
}
function initChatInbox() {
  if (state.inboxInitialized) return;
  state.inboxInitialized = true;
  document.addEventListener("click", function (event) {
    document.querySelectorAll(".inbox-item-menu[open]").forEach(function (itemMenu) {
      if (!itemMenu.contains(event.target)) itemMenu.open = false;
    });
  });
  window.setInterval(function () {
    if (document.visibilityState !== "hidden" && (!state.chatInboxes || !state.chatInboxes[state.activeChatId] || chatInbox().running || (chatInbox().items || []).length || pendingChatInputs().length || currentActiveSend())) pollChatInbox();
  }, 1200);
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
  if (box.paused && !box.running) return "В очереди · приостановлено";
  if (item.Status === "Delivering") return "Передаю агенту…";
  if (item.Delivery === "Steer" && box.running) return box.phase === "executing" ? "Применится после текущего действия" : "Ожидает применения";
  return "В очереди";
}
function renderPendingInput(item, chatId, editing) {
  var node = document.createElement("article"); node.className = "message user pending inbox-message";
  node.setAttribute("aria-label", "Сообщение в очереди");
  var heading = document.createElement("div"); heading.className = "inbox-heading";
  var status = document.createElement("span"); status.className = "inbox-item-status";
  status.textContent = pendingInputStatus(item, chatInbox(chatId)); heading.appendChild(status);
  node.appendChild(heading);
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
    var queued = (chatInbox(chatId).items || []).some(function (pending) { return pending.Id === item.Id; });
    if (item.localStatus === "unconfirmed") actions.appendChild(inboxButton("Повторить отправку", function () {
      submitInboxMessage(item.Text, item.Attachments || [], chatId, item.Delivery, item.Answers);
    }));
    if (queued && item.Status === "Pending") {
      if (item.Delivery !== "Steer") actions.appendChild(inboxButton("Отправить сейчас", function () { changeInboxItem("steerChatInput", item); }));
      if (!item.QuestionSetId) actions.appendChild(inboxButton("Редактировать", function () {
        state.inboxEditing = item.Id;
        state.inboxEditDraft = { chatId: chatId, id: item.Id, text: item.Text, revision: item.Revision };
        renderChatInbox();
      }));
    }
    if (queued && (item.Status === "Pending" || chatInbox(chatId).paused && !chatInbox(chatId).running)) {
      actions.appendChild(inboxButton("Удалить", function () { changeInboxItem("removeChatInput", item); }));
    }
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
function buildPendingInputUnits() {
  var chatId = state.activeChatId, box = chatInbox(), items = pendingChatInputs(), units = [];
  if (box.paused && (box.items || []).length) units.push({ key: "live:inbox-paused", signature: JSON.stringify([box.pauseReason, box.running]), render: function () {
    var note = document.createElement("div"); note.className = "inbox-pause";
    var reason = document.createElement("span"); reason.textContent = box.pauseReason || "Очередь приостановлена"; note.appendChild(reason);
    note.appendChild(inboxButton("Продолжить очередь", async function () {
      try { applyChatInbox(await send("resumeChatInbox", { chatId: chatId })); }
      catch (error) { if (state.activeChatId === chatId) $("inboxStatus").textContent = error.message; }
    }));
    return note;
  } });
  return units.concat(items.map(function (item) {
    var editing = state.inboxEditing === item.Id && state.inboxEditDraft && state.inboxEditDraft.chatId === chatId;
    var statusText = pendingInputStatus(item, box);
    return { key: "live:input:" + (item.OperationId || item.Id),
      // Polling must not replace a focused editor or its selection. Save uses its captured revision.
      signature: JSON.stringify(editing ? [item.Id, "editing"] : [item,
        (box.items || []).some(function (pending) { return pending.Id === item.Id; }), box.paused && !box.running]),
      actionsSignature: statusText,
      refreshActions: function (node) { node.querySelector(".inbox-item-status").textContent = statusText; },
      render: function () { return renderPendingInput(item, chatId, editing); } };
  }));
}
