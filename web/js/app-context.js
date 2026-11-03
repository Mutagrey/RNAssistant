var contextJsonText = "{}";

function clearContextJsonViewer(component) {
  var $ = component ? component.node : window.$;
  var target = $("contextBox");
  if (target && window.RNAssistantViewerRegistry) {
    window.RNAssistantViewerRegistry.unmount(target);
  }
}

function mountContextJsonViewer(component) {
  var $ = component ? component.node : window.$;
  var target = $("contextBox");
  var details = $("contextJsonDetails");
  var manager = $("contextManager");
  if (!target || !details || !details.open || !manager || manager.classList.contains("hidden")) {
    clearContextJsonViewer(component);
    return;
  }
  if (!window.RNAssistantViewerRegistry || !window.RNAssistantViewerRegistry.has("json")) {
    throw new Error("JSON viewer is unavailable.");
  }
  window.RNAssistantViewerRegistry.mount("json", target, {
    text: component ? target.contextJsonText || "{}" : contextJsonText,
    completeness: "full",
    mode: "tree",
    onCopy: window.copyTextResult
  });
}

function renderContextJson(value, component) {
  var text = JSON.stringify(value || {}, null, 2);
  if (component) component.node("contextBox").contextJsonText = text; else contextJsonText = text;
  mountContextJsonViewer(component);
}

function createRemoveContextButton(note, component) {
  var button = document.createElement("button");
  button.type = "button";
  button.className = "context-chip-remove";
  button.title = "Убрать из контекста";
  button.setAttribute("aria-label", "Убрать из контекста");
  button.innerHTML = window.RNAssistantIcons.render("close");
  button.addEventListener("click", function (event) {
    event.preventDefault();
    event.stopPropagation();
    removeContextItem(noteId(note), component);
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

function renderContextChips(notes, component) {
  var $ = component ? component.node : window.$;
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
    chip.appendChild(createRemoveContextButton(note, component));
    appendContextPopover(chip, note);
    bindContextPopover(chip);
    chips.appendChild(chip);
  });
}

function renderContextList(notes, component) {
  var $ = component ? component.node : window.$;
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

    var remove = createRemoveContextButton(note, component);
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

function renderContext(component) {
  if (!component && typeof isPanelActive === "function" && !isPanelActive("chat")) return;
  var context = component ? component.snapshot().context || {} : state.context || {};
  var notes = (context.Notes || context.notes || []).filter(Boolean);
  renderContextChips(notes, component);
  renderContextList(notes, component);
  renderContextJson(context, component);
  renderContextMeter(component);
  if (!component && window.RNAssistantChatInspector) window.RNAssistantChatInspector.render();
}

async function syncActiveChatState() {
  await synchronizeChatState(true);
}

function applyContextResponse(response, projection) {
  var component = projection && window.RNAssistantConversationPanel && RNAssistantConversationPanel.office(projection.target.sessionId);
  if (component && component.projection() === projection && projection.isCurrent()) {
    projection.snapshot().context = response && (response.context || response.Context) || response;
    renderContext(component); return true;
  }
  if (!projection || projection !== officeChatProjection() || !projection.isCurrent() ||
      projection.target.sessionId !== state.activeChatId) return false;
  var context = response && (response.context || response.Context) || response;
  if (!context || typeof context !== "object") return false;
  projection.snapshot().context = context;
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

async function addSelectionContext(mode, component) {
  var projection = component ? component.projection() : officeChatProjection();
  if (!projection) return;
  var targetChatId = projection.target.sessionId;
  var button = component ? component.node("addSelectionContextButton") : $("addSelectionContextButton");
  button.disabled = true;
  try {
    if (document.activeElement && typeof document.activeElement.blur === "function") {
      document.activeElement.blur();
    }
    reportFocusState();
    if (component ? component.snapshot().activeRunViewState && ["running", "awaiting_confirmation"].includes(component.snapshot().activeRunViewState.lifecycle) :
        currentActiveSend() || (typeof pendingAgentApprovalActivity === "function" && pendingAgentApprovalActivity())) {
      var staged = await send("stageSelectionInput", { chatId: targetChatId, mode: mode || "full" });
      var attachment = staged.resource || staged.Resource;
      var draft = readOfficeDraft(targetChatId);
      saveOfficeDraft(targetChatId, draft ? draft.text : "", (draft && draft.attachments || []).concat(attachment), true);
      if (component) component.controls();
      log("Снимок выделения прикреплён к следующему сообщению.");
      return;
    }
    var applied = applyContextResponse(
      await send("addSelectionContext", { chatId: targetChatId, mode: mode || "full" }),
      projection);
    if (applied) {
      if (component) await component.refresh(); else await syncActiveChatState();
      if (projection === officeChatProjection()) log("Выделение добавлено в контекст.");
    }
  } catch (error) {
    if (component) component.notice(error); else if (projection === officeChatProjection()) log(error.detail || error.message, "error");
  } finally {
    button.disabled = false;
  }
}

async function addTextContext(role, kind, title, reference, text, details) {
  var projection = officeChatProjection();
  if (!projection) return false;
  var targetChatId = projection.target.sessionId;
  var applied = applyContextResponse(await send("addTextContext", {
    chatId: targetChatId,
    role: role,
    kind: kind,
    title: title,
    reference: reference,
    text: text,
    detailsJson: typeof details === "string" ? details : JSON.stringify(details || {})
  }), projection);
  if (applied) await syncActiveChatState();
  return applied;
}

async function removeContextItem(id, component) {
  if (!id) {
    return;
  }

  var projection = component ? component.projection() : officeChatProjection();
  if (!projection) return;
  try {
    var targetChatId = projection.target.sessionId;
    var applied = applyContextResponse(
      await send("removeContextItem", { chatId: targetChatId, id: id }),
      projection);
    if (applied && !contextNotes().length) setContextManagerOpen(false);
    if (applied) {
      if (component) await component.refresh(); else await syncActiveChatState();
      if (projection === officeChatProjection()) log("Элемент контекста удален.");
    }
  } catch (error) {
    if (projection === officeChatProjection()) log(error.detail || error.message, "error");
  }
}

function setContextManagerOpen(open, component) {
  var $ = component ? component.node : window.$;
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
  if (open) mountContextJsonViewer(component);
  else clearContextJsonViewer(component);
}

function toggleContextManager() {
  var panel = $("contextManager");
  setContextManagerOpen(panel ? panel.classList.contains("hidden") : true);
}
