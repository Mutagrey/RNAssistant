var ATTACHMENT_MAX_FILES = 10;
var ATTACHMENT_MAX_FILE_BYTES = 2147483647;
var ATTACHMENT_MAX_TOTAL_BYTES = Number.MAX_SAFE_INTEGER;

function attachmentValue(item, pascal, camel, fallback) {
  item = item || {};
  return item[pascal] !== undefined ? item[pascal] : (item[camel] !== undefined ? item[camel] : fallback);
}

function attachmentId(item) { return attachmentValue(item, "Id", "id", ""); }
function attachmentName(item) { return attachmentValue(item, "FileName", "fileName", "Файл"); }
function attachmentKind(item) { return attachmentValue(item, "Kind", "kind", "file"); }
function attachmentSize(item) { return Number(attachmentValue(item, "Size", "size", 0) || 0); }

function formatAttachmentSize(bytes) {
  if (bytes < 1024) return bytes + " Б";
  if (bytes < 1024 * 1024) return Math.ceil(bytes / 1024) + " КБ";
  return (bytes / (1024 * 1024)).toFixed(1) + " МБ";
}

async function uploadChatResourceFile(chatId, file, signal) {
  var lease, draft, pendingRequestId, completed = false;
  var lifetime = new AbortController();
  var cancel = function () {
    lifetime.abort();
    if (pendingRequestId) cancelBridgeRequest(pendingRequestId).catch(function () {});
    if (lease && lease.leaseId)
      send("cancelChatResourceUpload", { chatId: chatId, leaseId: lease.leaseId }).catch(function () {});
  };
  if (signal) signal.addEventListener("abort", cancel, { once: true });
  window.addEventListener("pagehide", cancel, { once: true });
  function active() {
    if (lifetime.signal.aborted || signal && signal.aborted) throw new Error("RESOURCE_UPLOAD_CANCELLED");
  }
  try {
    active();
    var opening = send("beginChatResourceUpload", {
      chatId: chatId, fileName: file.name || ("clipboard-" + Date.now() + ".png"),
      contentType: file.type || "application/octet-stream", byteLength: file.size
    });
    pendingRequestId = opening.requestId;
    lease = await opening;
    pendingRequestId = null;
    active();
    await window.RNAssistantResourceUpload.write(lease, file, { maxBytes: ATTACHMENT_MAX_FILE_BYTES,
      signal: lifetime.signal, isCurrent: function () { return !(signal && signal.aborted); } });
    active();
    var finishing = send("completeChatResourceUpload", { chatId: chatId, leaseId: lease.leaseId });
    pendingRequestId = finishing.requestId;
    draft = await finishing;
    pendingRequestId = null;
    active();
    completed = true;
    return draft;
  } finally {
    if (signal) signal.removeEventListener("abort", cancel);
    window.removeEventListener("pagehide", cancel);
    if (!completed && lease && lease.leaseId) {
      // No retry of an uncertain chunk or completion. Cancelling releases transient
      // buffers; a known late draft is discarded through its existing owner.
      await send("cancelChatResourceUpload", { chatId: chatId, leaseId: lease.leaseId }).catch(function () {});
      if (draft && (draft.resource || draft.Resource))
        await send("discardChatResourceDraft", { chatId: chatId, id: attachmentId(draft.resource || draft.Resource) }).catch(function () {});
    }
  }
}

function chatResourceIngestionQueue() {
  state.chatResourceIngestions = state.chatResourceIngestions || {};
  return state.chatResourceIngestions;
}

function pendingChatResourceIngestion(chatId) {
  return chatId ? (chatResourceIngestionQueue()[chatId] || null) : null;
}

function draftAttachmentsForChat(chatId) {
  if (state.activeChatId === chatId) return state.draftAttachments || [];
  var draft = readOfficeDraft(chatId);
  return draft && draft.attachments ? draft.attachments : [];
}

function setAttachmentStatus(chatId, message, level) {
  state.chatAttachmentStatus = state.chatAttachmentStatus || {};
  if (message) state.chatAttachmentStatus[chatId] = { message: message, level: level };
  else delete state.chatAttachmentStatus[chatId];
  if (state.activeChatId === chatId) renderAttachmentStatus();
  if (message && level) log(message, level);
}

function renderAttachmentStatus() {
  var box = $("attachmentStatus");
  if (!box) return;
  var status = state.chatAttachmentStatus && state.chatAttachmentStatus[state.activeChatId];
  box.textContent = status ? status.message : "";
  box.hidden = !status;
  box.classList.toggle("is-error", !!(status && status.level));
}

function attachmentIngestionBlockedReason() {
  if (state.bridgeUnavailable) return "Связь с приложением недоступна. Не удалось добавить файлы.";
  if (state.chatNavigationPending || state.initializePromise || state.chatDetailPending || state.chatDetailError)
    return "Дождитесь загрузки чата, затем добавьте файлы ещё раз.";
  if (!state.activeChatId) return "Откройте или создайте чат, чтобы добавить файлы.";
  if (hasActiveMessageEdit()) return "Завершите редактирование сообщения, затем добавьте файлы.";
  if (isPendingChatSubmit(state.activeChatId)) return "Дождитесь отправки сообщения, затем добавьте файлы.";
  return "";
}

async function stageChatResourceFiles(targetChatId, files) {
  var existingAttachments = draftAttachmentsForChat(targetChatId);
  var existingBytes = existingAttachments.reduce(function (sum, item) { return sum + attachmentSize(item); }, 0);
  if (existingAttachments.length + files.length > ATTACHMENT_MAX_FILES) {
    setAttachmentStatus(targetChatId, "Можно добавить не более 10 файлов.", "warning");
    return false;
  }
  if (files.some(function (file) { return file.size <= 0 || file.size > ATTACHMENT_MAX_FILE_BYTES; }) ||
      existingBytes + files.reduce(function (sum, file) { return sum + file.size; }, 0) > ATTACHMENT_MAX_TOTAL_BYTES) {
    setAttachmentStatus(targetChatId, "Файл должен быть непустым и поддерживаться текущим способом импорта.", "warning");
    return false;
  }
  try {
    for (var index = 0; index < files.length; index += 1) {
      var file = files[index];
      setAttachmentStatus(targetChatId, "Добавление " + (index + 1) + "/" + files.length + ": " + file.name + "…");
      var response = await uploadChatResourceFile(targetChatId, file);
      var attachment = response.resource || response.Resource;
      if (file.type.indexOf("image/") === 0) attachment.previewUrl = URL.createObjectURL(file);
      if (state.activeChatId === targetChatId) {
        state.draftAttachments.push(attachment);
        captureChatDraft(targetChatId, true);
        renderAttachmentDrafts();
      } else {
        var draft = readOfficeDraft(targetChatId);
        saveOfficeDraft(targetChatId, draft ? draft.text : "",
          (draft && draft.attachments || []).concat(attachment), true);
      }
    }
    setAttachmentStatus(targetChatId, "");
    return true;
  } catch (error) {
    setAttachmentStatus(targetChatId, "Не удалось добавить «" + file.name + "»: " + (error.detail || error.message), "error");
    return false;
  }
}

function ingestChatResourceFiles(files) {
  files = Array.prototype.slice.call(files || []);
  if (!files.length) return Promise.resolve(true);
  var targetChatId = state.activeChatId;
  var blocked = attachmentIngestionBlockedReason();
  if (blocked) {
    setAttachmentStatus(targetChatId, blocked, "warning");
    return Promise.resolve(false);
  }

  var queue = chatResourceIngestionQueue();
  var previous = queue[targetChatId] || Promise.resolve(true);
  var operation = previous.then(async function (previousSucceeded) {
    var currentSucceeded = await stageChatResourceFiles(targetChatId, files);
    return previousSucceeded !== false && currentSucceeded;
  });
  queue[targetChatId] = operation;
  setAttachmentStatus(targetChatId, "Подготовка файлов…");
  updateComposerInputState();

  return operation.then(function (succeeded) {
    if (queue[targetChatId] === operation) delete queue[targetChatId];
    if (state.activeChatId === targetChatId) updateComposerInputState();
    return succeeded;
  }, function (error) {
    if (queue[targetChatId] === operation) delete queue[targetChatId];
    if (state.activeChatId === targetChatId) updateComposerInputState();
    setAttachmentStatus(targetChatId, "Не удалось добавить файлы: " + (error.detail || error.message), "error");
    return false;
  });
}

async function removeDraftAttachment(item) {
  var targetChatId = state.activeChatId;
  if (!targetChatId || isPendingChatSubmit(targetChatId)) return;
  try {
    await send("discardChatResourceDraft", { chatId: targetChatId, id: attachmentId(item) });
  } catch (error) {
    log(error.detail || error.message, "error");
    return;
  }
  if (item.previewUrl) URL.revokeObjectURL(item.previewUrl);
  if (state.activeChatId === targetChatId) {
    state.draftAttachments = state.draftAttachments.filter(function (candidate) { return attachmentId(candidate) !== attachmentId(item); });
    captureChatDraft(targetChatId, true);
    renderAttachmentDrafts();
  } else {
    var draft = readOfficeDraft(targetChatId);
    if (draft) saveOfficeDraft(targetChatId, draft.text, draft.attachments.filter(function (candidate) {
      return attachmentId(candidate) !== attachmentId(item);
    }), true);
  }
}

function attachmentCard(item, removable, lifecycle) {
  lifecycle = lifecycle || (removable ? "draft" : "committed");
  var card = document.createElement("div");
  card.className = (removable ? "attachment-draft" : "message-attachment") + " lifecycle-" + lifecycle;
  var thumb = document.createElement("div");
  thumb.className = "attachment-thumb";
  if (item.previewUrl) {
    var image = document.createElement("img");
    image.src = item.previewUrl;
    image.alt = "";
    thumb.appendChild(image);
  } else {
    thumb.textContent = attachmentKind(item) === "image" ? "IMG" : attachmentKind(item);
  }
  card.appendChild(thumb);
  var copy = document.createElement("div");
  copy.className = "attachment-copy";
  var name = document.createElement("div");
  name.className = "attachment-name";
  name.textContent = attachmentName(item);
  copy.appendChild(name);
  var meta = document.createElement("div");
  meta.className = "attachment-meta";
  var warning = attachmentValue(item, "ExtractionWarning", "extractionWarning", "");
  var lifecycleLabels = { draft: "Не отправлено", preparing: "Подготовка", committed: "Оригинал" };
  meta.textContent = (lifecycleLabels[lifecycle] || lifecycleLabels.committed) + " · " +
    formatAttachmentSize(attachmentSize(item)) + (warning ? " · требуется внимание" : "");
  if (warning) meta.title = warning;
  copy.appendChild(meta);
  card.appendChild(copy);
  if (removable) {
    var remove = document.createElement("button");
    remove.type = "button";
    remove.className = "attachment-remove";
    remove.title = "Удалить вложение";
    remove.textContent = "×";
    remove.addEventListener("click", function () { removeDraftAttachment(item); });
    card.appendChild(remove);
  }
  return card;
}

function renderAttachmentDrafts() {
  renderAttachmentStatus();
  var box = $("attachmentDrafts");
  if (!box) return;
  box.innerHTML = "";
  box.classList.toggle("hidden", !state.draftAttachments.length);
  state.draftAttachments.forEach(function (item) { box.appendChild(attachmentCard(item, true, "draft")); });
  updateComposerInputState();
}

function attachmentTransferFiles(transfer) {
  if (!transfer) return [];
  var files = Array.prototype.slice.call(transfer.files || []);
  return files.length ? files : Array.prototype.slice.call(transfer.items || [])
    .filter(function (item) { return item.kind === "file"; })
    .map(function (item) { return item.getAsFile(); })
    .filter(function (file) { return !!file; });
}

function attachmentTransferHasFiles(transfer) {
  return !!transfer && (Array.prototype.indexOf.call(transfer.types || [], "Files") >= 0 ||
    !!(transfer.files && transfer.files.length) ||
    Array.prototype.some.call(transfer.items || [], function (item) { return item.kind === "file"; }));
}

function bindAttachmentActions() {
  var button = $("attachFileButton");
  var input = $("attachmentFileInput");
  var composer = $("chatForm");
  var dropZone = $("chatDropZone");
  var dragDepth = 0;
  function clearDrag() { dragDepth = 0; composer.classList.remove("is-dragging"); }
  button.addEventListener("click", function () { input.click(); });
  input.addEventListener("change", function () {
    ingestChatResourceFiles(input.files);
    input.value = "";
  });
  ["dragenter", "dragover"].forEach(function (name) {
    dropZone.addEventListener(name, function (event) {
      if (!attachmentTransferHasFiles(event.dataTransfer)) return;
      event.preventDefault();
      if (name === "dragenter") dragDepth += 1;
      var allowed = !attachmentIngestionBlockedReason();
      event.dataTransfer.dropEffect = allowed ? "copy" : "none";
      composer.classList.toggle("is-dragging", allowed);
    });
  });
  dropZone.addEventListener("dragleave", function () {
    dragDepth = Math.max(0, dragDepth - 1);
    if (!dragDepth) clearDrag();
  });
  dropZone.addEventListener("drop", function (event) {
    clearDrag();
    if (!attachmentTransferHasFiles(event.dataTransfer)) return;
    event.preventDefault();
    var files = attachmentTransferFiles(event.dataTransfer);
    if (files.length) ingestChatResourceFiles(files);
    else setAttachmentStatus(state.activeChatId, "Не удалось получить файлы для добавления.", "warning");
  });
  // A file dropped outside the chat must not navigate away and lose the draft.
  ["dragover", "drop"].forEach(function (name) {
    document.addEventListener(name, function (event) {
      if (attachmentTransferHasFiles(event.dataTransfer)) event.preventDefault();
      if (name === "drop") clearDrag();
    });
  });
  window.addEventListener("dragend", clearDrag);
  $("chatInput").addEventListener("paste", function (event) {
    var files = attachmentTransferFiles(event.clipboardData);
    if (files && files.length) {
      event.preventDefault();
      ingestChatResourceFiles(files);
    }
  });
  renderAttachmentDrafts();
}
