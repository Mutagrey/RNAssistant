var contextJsonText = "{}";

function clearContextJsonViewer() {
  var target = $("contextBox");
  if (target && window.RNAssistantViewerRegistry) {
    window.RNAssistantViewerRegistry.unmount(target);
  }
}

function mountContextJsonViewer() {
  var target = $("contextBox");
  var details = $("contextJsonDetails");
  var manager = $("contextManager");
  if (!target || !details || !details.open || !manager || manager.classList.contains("hidden")) {
    clearContextJsonViewer();
    return;
  }
  if (!window.RNAssistantViewerRegistry || !window.RNAssistantViewerRegistry.has("json")) {
    throw new Error("JSON viewer is unavailable.");
  }
  window.RNAssistantViewerRegistry.mount("json", target, {
    text: contextJsonText,
    completeness: "full",
    mode: "tree",
    onCopy: window.copyTextResult
  });
}

function renderContextJson(value) {
  contextJsonText = JSON.stringify(value || {}, null, 2);
  mountContextJsonViewer();
}

function createRemoveContextButton(note) {
  var button = document.createElement("button");
  button.type = "button";
  button.className = "context-chip-remove";
  button.title = "Убрать из контекста";
  button.setAttribute("aria-label", "Убрать из контекста");
  button.innerHTML = "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M18 6 6 18\"/><path d=\"m6 6 12 12\"/></svg>";
  button.addEventListener("click", function (event) {
    event.preventDefault();
    event.stopPropagation();
    removeContextItem(noteId(note));
  });
  return button;
}

function appendContextPopover(chip, note) {
  var popover = document.createElement("div");
  popover.className = "context-popover";

  var title = document.createElement("div");
  title.className = "context-popover-title";
  title.textContent = noteTitle(note);

  var meta = document.createElement("div");
  meta.className = "context-popover-meta";
  meta.textContent = noteHost(note) + " - " + noteKind(note) + (noteReference(note) ? " - " + noteReference(note) : "");

  var preview = document.createElement("div");
  preview.className = "context-popover-preview";
  preview.textContent = notePreview(note) || "Нет превью.";

  popover.appendChild(title);
  popover.appendChild(meta);
  popover.appendChild(preview);
  if (noteDetails(note)) {
    var details = document.createElement("div");
    details.className = "context-popover-details";
    details.textContent = noteDetails(note);
    popover.appendChild(details);
  }
  chip.appendChild(popover);
}

function positionContextPopover(chip) {
  var popover = chip.querySelector(".context-popover");
  if (!popover) {
    return;
  }

  var previousDisplay = popover.style.display;
  var previousVisibility = popover.style.visibility;
  popover.style.display = "block";
  popover.style.visibility = "hidden";

  var chipRect = chip.getBoundingClientRect();
  var popoverRect = popover.getBoundingClientRect();
  var gap = 8;
  var viewportPadding = 12;
  var maxLeft = Math.max(viewportPadding, window.innerWidth - popoverRect.width - viewportPadding);
  var left = Math.min(Math.max(chipRect.left, viewportPadding), maxLeft);
  var top = chipRect.top - popoverRect.height - gap;

  if (top < viewportPadding) {
    top = Math.min(chipRect.bottom + gap, window.innerHeight - popoverRect.height - viewportPadding);
  }

  popover.style.setProperty("--context-popover-left", left + "px");
  popover.style.setProperty("--context-popover-top", Math.max(viewportPadding, top) + "px");
  popover.style.display = previousDisplay;
  popover.style.visibility = previousVisibility;
}

function bindContextPopover(chip) {
  var update = function () {
    positionContextPopover(chip);
  };
  chip.addEventListener("pointerenter", update);
  chip.addEventListener("focusin", update);
}

function renderContextChips(notes) {
  var strip = $("contextStrip");
  var chips = $("contextChips");
  chips.innerHTML = "";
  strip.classList.toggle("hidden", notes.length === 0);

  notes.forEach(function (note) {
    var chip = document.createElement("div");
    chip.className = "context-chip";
    chip.tabIndex = 0;

    var main = document.createElement("div");
    main.className = "context-chip-main";

    var badge = document.createElement("span");
    badge.className = "context-chip-badge";
    badge.textContent = hostBadge(note);

    var title = document.createElement("span");
    title.className = "context-chip-title";
    title.textContent = noteTitle(note);

    main.appendChild(badge);
    main.appendChild(title);
    chip.appendChild(main);
    chip.appendChild(createRemoveContextButton(note));
    appendContextPopover(chip, note);
    bindContextPopover(chip);
    chips.appendChild(chip);
  });
}

function renderContextList(notes) {
  var list = $("contextList");
  var summary = $("contextSummary");
  if (!list || !summary) {
    return;
  }
  list.innerHTML = "";
  summary.textContent = notes.length
    ? notes.length + " вложений в контексте активного чата"
    : "Контекст пуст";

  notes.forEach(function (note) {
    var card = document.createElement("article");
    card.className = "context-card";

    var head = document.createElement("div");
    head.className = "context-card-head";

    var text = document.createElement("div");
    var title = document.createElement("div");
    title.className = "context-card-title";
    title.textContent = noteTitle(note);

    var meta = document.createElement("div");
    meta.className = "context-card-meta";
    meta.textContent = noteHost(note) + " - " + noteKind(note) + (noteReference(note) ? " - " + noteReference(note) : "");

    text.appendChild(title);
    text.appendChild(meta);
    head.appendChild(text);

    var remove = createRemoveContextButton(note);
    remove.classList.add("secondary");
    head.appendChild(remove);

    var preview = document.createElement("div");
    preview.className = "context-card-preview";
    preview.textContent = notePreview(note) || "Нет превью.";

    card.appendChild(head);
    card.appendChild(preview);
    list.appendChild(card);
  });
}

function renderContext(skipUsageEstimate) {
  if (typeof isPanelActive === "function" && !isPanelActive("chat")) return;
  var notes = contextNotes();
  renderContextChips(notes);
  renderContextList(notes);
  renderContextJson(state.context || {});
  if (!skipUsageEstimate) {
    updateEstimatedContextUsage();
  }
  renderContextMeter();
}

async function syncActiveChatState() {
  await synchronizeChatState(true);
}

function applyContextResponse(response, expectedChatId) {
  if (expectedChatId && state.activeChatId !== expectedChatId) return false;
  var context = response && (response.context || response.Context) || response;
  if (!context || typeof context !== "object") return false;
  state.context = context;
  renderContext();
  return true;
}

async function refreshContext() {
  try {
    await syncActiveChatState();
  } catch (error) {
    log(error.detail || error.message, "error");
  }
}

async function addSelectionContext(mode) {
  var targetChatId = state.activeChatId;
  if (!targetChatId) return;
  setControlBusy("addSelectionContextButton", true);
  try {
    if (document.activeElement && typeof document.activeElement.blur === "function") {
      document.activeElement.blur();
    }
    reportFocusState();
    applyContextResponse(
      await send("addSelectionContext", { chatId: targetChatId, mode: mode || "full" }),
      targetChatId);
    if (state.activeChatId === targetChatId) await syncActiveChatState();
    log("Выделение добавлено в контекст.");
  } catch (error) {
    log(error.detail || error.message, "error");
  } finally {
    setControlBusy("addSelectionContextButton", false);
  }
}

async function addTextContext(kind, title, reference, text, details) {
  var targetChatId = state.activeChatId;
  if (!targetChatId) return false;
  var applied = applyContextResponse(await send("addTextContext", {
    chatId: targetChatId,
    kind: kind,
    title: title,
    reference: reference,
    text: text,
    detailsJson: typeof details === "string" ? details : JSON.stringify(details || {})
  }), targetChatId);
  if (state.activeChatId === targetChatId) await syncActiveChatState();
  return applied;
}

async function addSelectedToolContextToContext() {
  syncSelectedToolFromEditor();
  var skill = state.tools[state.selectedToolIndex];
  var context = selectedToolContext();
  if (!skill || !context) {
    return false;
  }

  await addTextContext(
    "tool_definition",
    "Tool: " + (skill.Id || "tool"),
    "tool:" + (skill.Id || "tool"),
    context,
    {
      type: "tool_definition",
      id: skill.Id || ""
    });
  log("Контекст инструмента добавлен в чат.");
  return true;
}

async function removeContextItem(id) {
  if (!id) {
    return;
  }

  try {
    var targetChatId = state.activeChatId;
    var applied = applyContextResponse(
      await send("removeContextItem", { chatId: targetChatId, id: id }),
      targetChatId);
    if (applied && !contextNotes().length) setContextManagerOpen(false);
    if (state.activeChatId === targetChatId) await syncActiveChatState();
    log("Элемент контекста удален.");
  } catch (error) {
    log(error.detail || error.message, "error");
  }
}

function setContextManagerOpen(open) {
  var panel = $("contextManager");
  var button = $("openContextTabButton");
  if (!panel) {
    return;
  }

  panel.classList.toggle("hidden", !open);
  if (button) {
    button.classList.toggle("active", !!open);
    button.setAttribute("aria-expanded", open ? "true" : "false");
  }
  if (open) mountContextJsonViewer();
  else clearContextJsonViewer();
}

function toggleContextManager() {
  var panel = $("contextManager");
  setContextManagerOpen(panel ? panel.classList.contains("hidden") : true);
}

function bindContextActions() {
  $("contextJsonDetails").addEventListener("toggle", function () {
    if ($("contextJsonDetails").open) mountContextJsonViewer();
    else clearContextJsonViewer();
  });
  $("openContextTabButton").addEventListener("click", toggleContextManager);
  $("closeContextManagerButton").addEventListener("click", function () { setContextManagerOpen(false); });
  $("addSelectionContextButton").addEventListener("click", function () { addSelectionContext("full"); });
  $("clearContextButton").addEventListener("click", async function () {
    var targetChatId = state.activeChatId;
    if (!targetChatId) return;
    setControlBusy("clearContextButton", true);
    try {
      var applied = applyContextResponse(
        await send("clearContext", { chatId: targetChatId }),
        targetChatId);
      if (applied) setContextManagerOpen(false);
      if (state.activeChatId === targetChatId) await syncActiveChatState();
      log("Контекст очищен.");
    } catch (error) {
      log(error.message, "error");
    } finally {
      setControlBusy("clearContextButton", false);
    }
  });
  document.addEventListener("keydown", function (event) {
    var panel = $("contextManager");
    if (event.key === "Escape" && panel && !panel.classList.contains("hidden")) {
      setContextManagerOpen(false);
    }
  });
}
