// Durable input state is owned by the runtime. This cache is presentation only.
function chatInbox(chatId) {
  return (state.chatInboxes || {})[chatId || state.activeChatId] || { items: [], running: false, paused: false };
}
function applyChatInbox(inbox) {
  if (!inbox || !inbox.chatId) return;
  state.chatInboxes = state.chatInboxes || {};
  var old = state.chatInboxes[inbox.chatId];
  if (old && old.epoch === inbox.epoch && old.revision > inbox.revision) return;
  state.chatInboxes[inbox.chatId] = inbox;
  if (inbox.chatId === state.activeChatId) {
    if (old && old.running && !inbox.running && !state.activeSends[inbox.chatId]) endChatRunTracking(inbox.chatId);
    renderChatInbox();
    var announcer = $("inboxAnnouncer");
    if (announcer && (!old || old.revision !== inbox.revision)) announcer.textContent =
      "Ожидают отправки: " + (inbox.items || []).length + (inbox.paused ? ". Очередь приостановлена." : ".");
    renderSendControls();
    if ((!old || old.revision !== inbox.revision) && typeof renderMessages === "function") renderMessages();
  }
}
function runningMessageDelivery() {
  return String((state.settings || {}).RunningMessageDelivery || "Steer").toLowerCase() === "queue" ? "Queue" : "Steer";
}
function inboxOperationId() {
  return "input-" + (window.crypto && window.crypto.randomUUID ? window.crypto.randomUUID() :
    Date.now().toString(36) + "-" + Math.random().toString(36).slice(2));
}
async function submitInboxMessage(text, attachments, chatId, delivery) {
  setPendingChatSubmit(chatId, true);
  renderSendControls();
  // Retain the identity across uncertain transport retries, including accepted-but-unacknowledged sends.
  var fingerprint = JSON.stringify([chatId, text, attachments.map(attachmentId), delivery]);
  var pending = state.inboxSubmission;
  if (!pending || pending.fingerprint !== fingerprint) pending = { fingerprint: fingerprint, id: inboxOperationId() };
  state.inboxSubmission = pending;
  try {
    var inbox = await send("submitChatInput", { chatId: chatId, operationId: pending.id,
      text: text, resourceDraftIds: attachments.map(attachmentId), delivery: delivery });
    applyChatInbox(inbox);
    if (state.activeChatId === chatId) {
      setChatInputText("", false);
      clearDraftAttachments();
      clearSendError();
      if ($("inboxStatus")) $("inboxStatus").textContent = "";
    }
    state.inboxSubmission = null;
    refreshInboxChat(chatId);
  } catch (error) {
    log(error.detail || error.message, "error");
    var status = $("inboxStatus");
    if (status) status.textContent = "Не удалось подтвердить отправку. Текст сохранён — повторите отправку.";
  } finally {
    setPendingChatSubmit(chatId, false);
    renderSendControls();
  }
}
async function refreshInboxChat(chatId) {
  try {
    if (state.activeChatId === chatId) applyChatStateForChat(await loadChatState(chatId), chatId);
  } catch (error) { log(error.message, "error"); }
}
async function changeInboxItem(action, item, text) {
  var chatId = state.activeChatId;
  try {
    var response = await send(action, { chatId: chatId, inputId: item.Id, expectedRevision: item.Revision, text: text });
    if (state.inboxEditing === item.Id) { state.inboxEditing = null; state.inboxEditDraft = null; }
    applyChatInbox(response);
    $("inboxStatus").textContent = "";
  } catch (error) {
    $("inboxStatus").textContent = error.detail || error.message;
    await pollChatInbox();
  }
}
function inboxButton(label, action) {
  var button = document.createElement("button");
  button.type = "button"; button.textContent = label; button.addEventListener("click", action);
  return button;
}
function renderChatInbox() {
  var root = $("chatInbox");
  if (!root) return;
  var box = chatInbox(), items = (box.items || []).filter(function (item) { return box.paused || item.Delivery !== "Steer"; });
  root.hidden = !items.length;
  // Preserve the edit field and its focus while background status polling continues.
  var signature = JSON.stringify([state.activeChatId, box.epoch, box.revision, box.phase, box.paused, box.pauseReason, state.inboxExpanded, state.inboxEditing]);
  if (root.inboxSignature === signature) return;
  root.inboxSignature = signature;
  root.replaceChildren();
  if (!items.length) return;
  var header = document.createElement("div"); header.className = "inbox-heading";
  var title = document.createElement("strong"); title.textContent = "Ожидают отправки · " + items.length; header.appendChild(title);
  if (box.paused) {
    var resume = inboxButton("Продолжить очередь", async function () {
      try { applyChatInbox(await send("resumeChatInbox", { chatId: state.activeChatId })); }
      catch (error) { $("inboxStatus").textContent = error.message; }
    });
    header.appendChild(resume);
  }
  root.appendChild(header);
  if (box.paused) {
    var reason = document.createElement("p"); reason.className = "inbox-pause";
    reason.textContent = box.pauseReason || "Очередь приостановлена"; root.appendChild(reason);
  }
  (state.inboxExpanded ? items : items.slice(0, 3)).forEach(function (item) {
    var card = document.createElement("article"); card.className = "inbox-card";
    var status = document.createElement("span"); status.className = "inbox-item-status";
    status.textContent = item.Status === "Delivering" ? (box.paused && !box.running ? "Не завершено · очередь приостановлена" : "Передаю агенту…") : item.Delivery === "Steer" && box.running
      ? (box.phase === "executing" ? "Применится после текущего действия" : "Ожидает применения") : "В очереди";
    card.appendChild(status);
    if (state.inboxEditing === item.Id) {
      var draft = state.inboxEditDraft || { id: item.Id, text: item.Text, revision: item.Revision };
      state.inboxEditDraft = draft;
      var editor = document.createElement("textarea"); editor.value = draft.text; editor.rows = 3;
      editor.addEventListener("input", function () { draft.text = editor.value; });
      editor.setAttribute("aria-label", "Текст сообщения в очереди"); card.appendChild(editor);
      var controls = document.createElement("div"); controls.className = "inbox-actions";
      controls.appendChild(inboxButton("Сохранить", function () { changeInboxItem("editChatInput", { Id: item.Id, Revision: draft.revision }, editor.value); }));
      controls.appendChild(inboxButton("Отмена", function () { state.inboxEditing = null; state.inboxEditDraft = null; renderChatInbox(); }));
      card.appendChild(controls);
      window.requestAnimationFrame(function () { editor.focus(); });
    } else {
      var text = document.createElement("p"); text.className = "inbox-text"; text.textContent = item.Text || "Вложения";
      text.title = item.Text || "Вложения"; card.appendChild(text);
      if ((item.Attachments || []).length) {
        var files = document.createElement("small"); files.className = "inbox-files";
        files.textContent = item.Attachments.map(function (a) { return a.FileName; }).join(" · "); card.appendChild(files);
      }
      if (item.Status === "Pending") {
        var actions = document.createElement("div"); actions.className = "inbox-actions";
        if (item.Delivery !== "Steer") actions.appendChild(inboxButton("Отправить сейчас", function () { changeInboxItem("steerChatInput", item); }));
        actions.appendChild(inboxButton("Редактировать", function () { state.inboxEditing = item.Id; state.inboxEditDraft = { id: item.Id, text: item.Text, revision: item.Revision }; renderChatInbox(); }));
        actions.appendChild(inboxButton("Удалить", function () { changeInboxItem("removeChatInput", item); }));
        card.appendChild(actions);
      } else if (box.paused && !box.running) {
        card.appendChild(inboxButton("Удалить", function () { changeInboxItem("removeChatInput", item); }));
      }
    }
    root.appendChild(card);
  });
  if (items.length > 3) root.appendChild(inboxButton(state.inboxExpanded ? "Свернуть" : "Ещё " + (items.length - 3), function () {
    state.inboxExpanded = !state.inboxExpanded; renderChatInbox();
  }));
}
async function pollChatInbox() {
  if (!state.activeChatId || state.bridgeUnavailable || state.inboxPolling || state.chatNavigationPending) return;
  var chatId = state.activeChatId;
  state.inboxPolling = true;
  try {
    var previous = chatInbox(chatId);
    var inbox = await send("getChatInbox", { chatId: chatId });
    applyChatInbox(inbox);
    if (inbox.revision !== previous.revision || inbox.running !== previous.running) refreshInboxChat(chatId);
  } catch (_) { /* The normal bridge banner reports disconnection; keep the saved projection. */ }
  finally { state.inboxPolling = false; }
}
function initChatInbox() {
  if (state.inboxInitialized) return;
  state.inboxInitialized = true;
  ["Steer", "Queue"].forEach(function (delivery) {
    $("send" + delivery + "Button").addEventListener("click", function () {
      $("sendDeliveryMenu").open = false;
      submitChatInput(delivery);
    });
  });
  document.addEventListener("click", function (event) {
    var menu = $("sendDeliveryMenu"); if (menu.open && !menu.contains(event.target)) menu.open = false;
  });
  document.addEventListener("keydown", function (event) {
    if (event.key === "Escape" && $("sendDeliveryMenu").open) {
      $("sendDeliveryMenu").open = false; $("sendDeliveryMenu").querySelector("summary").focus();
    }
  });
  window.setInterval(function () {
    if (document.visibilityState !== "hidden" && (!state.chatInboxes || !state.chatInboxes[state.activeChatId] || chatInbox().running || (chatInbox().items || []).length || currentActiveSend())) pollChatInbox();
  }, 1200);
}

function pendingSteerInputs() {
  var box = chatInbox();
  if (box.paused) return [];
  return (box.items || []).filter(function (item) {
    return item.Delivery === "Steer" && !(state.messages || []).some(function (message) { return (message.Id || message.id) === item.Id; });
  });
}
function buildPendingInputUnits() {
  return pendingSteerInputs().map(function (item) {
    return { key: "live:input:" + item.Id, signature: JSON.stringify([item, chatInbox().phase]), render: function () {
      var node = document.createElement("article"); node.className = "message user pending";
      var body = document.createElement("div"); body.className = "markdown inbox-pending-text";
      body.textContent = item.Text || "Вложения"; node.appendChild(body);
      if ((item.Attachments || []).length) {
        var files = document.createElement("small"); files.textContent = item.Attachments.map(function (file) { return file.FileName; }).join(" · ");
        node.appendChild(files);
      }
      var status = document.createElement("div"); status.className = "inbox-item-status";
      status.textContent = chatInbox().phase === "executing" ? "Применится после текущего действия" : "Ожидает применения";
      node.appendChild(status);
      return node;
    } };
  });
}
