function createRemoveContextButton(note) {
  var button = document.createElement("button");
  button.type = "button";
  button.className = "context-chip-remove";
  button.title = "Remove context";
  button.setAttribute("aria-label", "Remove context");
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
  preview.textContent = notePreview(note) || "No preview.";

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
    chips.appendChild(chip);
  });
}

function renderVbaContextToggle() {
  var button = $("toggleVbaContextButton");
  if (!button) {
    return;
  }

  var active = vbaContextNotes().length > 0;
  button.classList.toggle("active", active);
  button.setAttribute("aria-pressed", active ? "true" : "false");
  button.title = active ? "Detach VBA project context" : "Attach VBA project context";
}

function renderContextList(notes) {
  var list = $("contextList");
  var summary = $("contextSummary");
  list.innerHTML = "";
  summary.textContent = notes.length
    ? notes.length + " context attachment(s) belong to the active chat and will be included in its next model request."
    : "No context in this chat. Add a selection from the Office right-click menu or the composer button.";

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
    preview.textContent = notePreview(note) || "No preview.";

    card.appendChild(head);
    card.appendChild(preview);
    list.appendChild(card);
  });
}

function renderContext(skipUsageEstimate) {
  var notes = contextNotes();
  renderContextChips(notes);
  renderContextList(notes);
  $("contextBox").textContent = JSON.stringify(state.context || {}, null, 2);
  renderVbaContextToggle();
  if (!skipUsageEstimate) {
    updateEstimatedContextUsage();
  }
  renderContextMeter();
}

async function syncActiveChatState() {
  applyChatState(await send("listChats", {}));
}

async function refreshContext() {
  try {
    await syncActiveChatState();
  } catch (error) {
    log(error.detail || error.message);
  }
}

async function addSelectionContext(mode) {
  setActivity("context", "Добавляю выделение в контекст...");
  try {
    if (document.activeElement && typeof document.activeElement.blur === "function") {
      document.activeElement.blur();
    }
    reportFocusState();
    await send("addSelectionContext", { chatId: state.activeChatId, mode: mode || "full" });
    await syncActiveChatState();
    log("Selection added to context.");
  } catch (error) {
    log(error.detail || error.message);
  } finally {
    clearActivity();
  }
}

async function addTextContext(kind, title, reference, text, details) {
  await send("addTextContext", {
    chatId: state.activeChatId,
    kind: kind,
    title: title,
    reference: reference,
    text: text,
    detailsJson: typeof details === "string" ? details : JSON.stringify(details || {})
  });
  await syncActiveChatState();
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
  log("Tool context added to chat context.");
  return true;
}

async function ensureVbaContextAttached() {
  if (vbaContextNotes().length > 0) {
    return;
  }

  await addVbaContext();
}

async function addVbaContext() {
  setActivity("context", "Добавляю VBA в контекст...");
  try {
    await send("addVbaContext", {
      chatId: state.activeChatId,
      maxChars: Number($("vbaContextLimitInput").value || 30000)
    });
    await syncActiveChatState();
    log("VBA context added.");
  } finally {
    clearActivity();
  }
}

async function toggleVbaContext() {
  var notes = vbaContextNotes();
  try {
    if (notes.length) {
      for (var i = 0; i < notes.length; i += 1) {
        await send("removeContextItem", { chatId: state.activeChatId, id: noteId(notes[i]) });
      }
      await syncActiveChatState();
      log("VBA context removed.");
      return;
    }

    await addVbaContext();
  } catch (error) {
    log(error.detail || error.message);
  }
}

async function removeContextItem(id) {
  if (!id) {
    return;
  }

  try {
    await send("removeContextItem", { chatId: state.activeChatId, id: id });
    await syncActiveChatState();
    log("Context item removed.");
  } catch (error) {
    log(error.detail || error.message);
  }
}

function bindContextActions() {
  $("openContextTabButton").addEventListener("click", function () { switchTab("context"); });
  $("addSelectionContextButton").addEventListener("click", function () { addSelectionContext("full"); });
  $("toggleVbaContextButton").addEventListener("click", toggleVbaContext);
  $("clearContextButton").addEventListener("click", async function () {
    setActivity("clearing", "Очищаю контекст...");
    try {
      await send("clearContext", { chatId: state.activeChatId });
      await syncActiveChatState();
      log("Context cleared.");
    } catch (error) {
      log(error.message);
    } finally {
      clearActivity();
    }
  });
}
