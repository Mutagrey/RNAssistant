var ATTACHMENT_MAX_FILES = 10;
var ATTACHMENT_MAX_FILE_BYTES = 20 * 1024 * 1024;
var ATTACHMENT_MAX_TOTAL_BYTES = 50 * 1024 * 1024;

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

function fileToBase64(file) {
  return new Promise(function (resolve, reject) {
    var reader = new FileReader();
    reader.onload = function () {
      var value = String(reader.result || "");
      resolve(value.substring(value.indexOf(",") + 1));
    };
    reader.onerror = function () { reject(reader.error || new Error("Не удалось прочитать файл.")); };
    reader.readAsDataURL(file);
  });
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
  var draft = chatDraftStore()[chatId];
  return draft && draft.attachments ? draft.attachments : [];
}

async function stageChatResourceFiles(targetChatId, files) {
  var existingAttachments = draftAttachmentsForChat(targetChatId);
  var existingBytes = existingAttachments.reduce(function (sum, item) { return sum + attachmentSize(item); }, 0);
  if (existingAttachments.length + files.length > ATTACHMENT_MAX_FILES) {
    log("Можно добавить не более 10 файлов.", "warning");
    return false;
  }
  if (files.some(function (file) { return file.size <= 0 || file.size > ATTACHMENT_MAX_FILE_BYTES; }) ||
      existingBytes + files.reduce(function (sum, file) { return sum + file.size; }, 0) > ATTACHMENT_MAX_TOTAL_BYTES) {
    log("Лимит: 20 МБ на файл и 50 МБ на сообщение.", "warning");
    return false;
  }
  try {
    for (var index = 0; index < files.length; index += 1) {
      var file = files[index];
      var response = await send("stageChatResource", {
        chatId: targetChatId,
        fileName: file.name || ("clipboard-" + Date.now() + ".png"),
        contentType: file.type || "application/octet-stream",
        base64: await fileToBase64(file)
      });
      var attachment = response.resource || response.Resource;
      if (file.type.indexOf("image/") === 0) attachment.previewUrl = URL.createObjectURL(file);
      if (state.activeChatId === targetChatId) {
        state.draftAttachments.push(attachment);
        renderAttachmentDrafts();
      } else {
        var drafts = chatDraftStore();
        var draft = drafts[targetChatId] || { text: "", attachments: [] };
        draft.attachments = draft.attachments || [];
        draft.attachments.push(attachment);
        drafts[targetChatId] = draft;
      }
    }
    return true;
  } catch (error) {
    log(error.detail || error.message, "error");
    return false;
  }
}

function ingestChatResourceFiles(files) {
  files = Array.prototype.slice.call(files || []);
  if (!files.length) return Promise.resolve(true);
  var targetChatId = state.activeChatId;
  if (!targetChatId || currentActiveSend() || state.bridgeUnavailable || state.pendingChatSubmitId === targetChatId) {
    return Promise.resolve(false);
  }

  var queue = chatResourceIngestionQueue();
  var previous = queue[targetChatId] || Promise.resolve(true);
  var operation = previous.then(async function (previousSucceeded) {
    var currentSucceeded = await stageChatResourceFiles(targetChatId, files);
    return previousSucceeded !== false && currentSucceeded;
  });
  queue[targetChatId] = operation;
  updateComposerInputState();

  return operation.then(function (succeeded) {
    if (queue[targetChatId] === operation) delete queue[targetChatId];
    if (state.activeChatId === targetChatId) updateComposerInputState();
    return succeeded;
  }, function (error) {
    if (queue[targetChatId] === operation) delete queue[targetChatId];
    if (state.activeChatId === targetChatId) updateComposerInputState();
    log(error.detail || error.message, "error");
    return false;
  });
}

async function removeDraftAttachment(item) {
  var targetChatId = state.activeChatId;
  if (!targetChatId || currentActiveSend() || state.pendingChatSubmitId === targetChatId) return;
  try {
    await send("discardChatResourceDraft", { chatId: targetChatId, id: attachmentId(item) });
  } catch (error) {
    log(error.detail || error.message, "error");
    return;
  }
  if (item.previewUrl) URL.revokeObjectURL(item.previewUrl);
  if (state.activeChatId === targetChatId) {
    state.draftAttachments = state.draftAttachments.filter(function (candidate) { return attachmentId(candidate) !== attachmentId(item); });
    renderAttachmentDrafts();
  } else {
    var draft = chatDraftStore()[targetChatId];
    if (draft && draft.attachments) {
      draft.attachments = draft.attachments.filter(function (candidate) { return attachmentId(candidate) !== attachmentId(item); });
    }
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
  var box = $("attachmentDrafts");
  if (!box) return;
  box.innerHTML = "";
  box.classList.toggle("hidden", !state.draftAttachments.length);
  state.draftAttachments.forEach(function (item) { box.appendChild(attachmentCard(item, true, "draft")); });
  updateComposerInputState();
}

function clearDraftAttachments() {
  state.draftAttachments.forEach(function (item) { if (item.previewUrl) URL.revokeObjectURL(item.previewUrl); });
  state.draftAttachments = [];
  if (state.activeChatId) delete chatDraftStore()[state.activeChatId];
  renderAttachmentDrafts();
}

function bindAttachmentActions() {
  var button = $("attachFileButton");
  var input = $("attachmentFileInput");
  var composer = $("chatForm");
  button.addEventListener("click", function () { input.click(); });
  input.addEventListener("change", function () {
    ingestChatResourceFiles(input.files);
    input.value = "";
  });
  ["dragenter", "dragover"].forEach(function (name) {
    composer.addEventListener(name, function (event) {
      event.preventDefault();
      composer.classList.add("is-dragging");
    });
  });
  ["dragleave", "drop"].forEach(function (name) {
    composer.addEventListener(name, function (event) {
      event.preventDefault();
      composer.classList.remove("is-dragging");
      if (name === "drop") ingestChatResourceFiles(event.dataTransfer.files);
    });
  });
  $("chatInput").addEventListener("paste", function (event) {
    var clipboard = event.clipboardData;
    var files = clipboard && clipboard.files ? Array.prototype.slice.call(clipboard.files) : [];
    if (!files.length && clipboard && clipboard.items) {
      files = Array.prototype.slice.call(clipboard.items)
        .filter(function (item) { return item.kind === "file"; })
        .map(function (item) { return item.getAsFile(); })
        .filter(function (file) { return !!file; });
    }
    if (files && files.length) {
      event.preventDefault();
      ingestChatResourceFiles(files);
    }
  });
  renderAttachmentDrafts();
}
