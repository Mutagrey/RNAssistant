var state = {
  host: "",
  title: "",
  officeContext: null,
  settings: {},
  hasApiKey: false,
  tools: [],
  skills: [],
  context: {},
  contextUsage: {},
  chats: [],
  documents: [],
  chatSearch: "",
  collapsedChatDocuments: {},
  initializedChatDocuments: {},
  currentChatDocumentKey: "",
  chatSidebarHidden: false,
  activeChatId: "",
  activeChatModel: "",
  activeChatMode: "agent",
  activeChatHtmlMode: false,
  activeChatReasoning: false,
  bridgeToken: "",
  messages: [],
  artifacts: [],
  activeContextCheckpointId: "",
  activeHtmlArtifactId: "",
  activePlanArtifactId: "",
  agentPlanExpanded: {},
  draftAttachments: [],
  failedSend: null,
  activeSends: {},
  chatRuns: {},
  liveActivity: null,
  liveAgentRun: null,
  liveStreamContent: null,
  liveReasoning: "",
  liveReasoningComplete: false,
  liveStreamRenderPending: false,
  editingMessageId: "",
  editingMessageIndex: -1,
  editingText: "",
  editingBusy: false,
  editingDraftCaptured: false,
  editingDraftText: "",
  editingDraftSelectionStart: 0,
  editingDraftSelectionEnd: 0,
  editingDraftScrollTop: 0,
  modelCatalog: { configUrl: "", defaultModel: "", models: [], loaded: false, loading: false, error: "" },
  modelSaving: false,
  reasoningSaving: false,
  bridgeUnavailable: false,
  selectedToolIndex: -1,
  selectedSkillIndex: -1,
  selectedPromptIndex: -1,
  selectedInstructionKind: "prompt",
  promptEditorMode: "edit",
  toolEditorPage: "main",
  toolSchemaMode: "form",
  toolPipelineMode: "form",
  vbaEditorMode: "edit",
  vbaEditorDirty: false,
  htmlWorkspaceMode: "preview",
  htmlWorkspaceSelection: { type: "file", id: "" },
  htmlWorkspaceSidebarHidden: false,
  htmlWorkspaceDirty: false,
  htmlWorkspaceCreateKind: "",
  htmlWorkspace: { activeFileId: "", files: [], dataSources: [], history: [], redoHistory: [] },
  collapsedResourceGroups: {},
  promptDrafts: {},
  toolsPath: "",
  skillsPath: "",
  vba: { modules: [], backups: [], selectedModule: "" },
  pending: {},
  seq: 1,
  focusReportTimer: null,
  lastReportedWantsKeyboard: null,
  highlightLog: {},
  highlightRetryScheduled: false,
  highlightRetryAttempts: 0,
  highlightLoadLogged: false,
  syncTimer: null
};

function $(id) {
  return document.getElementById(id);
}

window.RNAssistantHost = {
  blurComposer: function () {
    var active = document.activeElement;
    var chatInput = $("chatInput");
    if (chatInput) {
      chatInput.blur();
    }
    if (active && active !== document.body && typeof active.blur === "function") {
      active.blur();
    }
  },
  refreshContext: function () {
    refreshContext();
  },
  refreshState: function () {
    initialize();
  },
  runQuickAction: function (action) {
    runQuickAction(action);
  }
};

function log(message) {
  var box = $("logBox");
  if (!box) {
    return;
  }
  var line = new Date().toISOString() + " " + message;
  box.textContent += line + "\n";
  box.scrollTop = box.scrollHeight;
}

function logOnce(message) {
  if (state.highlightLog[message]) {
    return;
  }
  state.highlightLog[message] = true;
  log(message);
}

function setControlBusy(target, busy) {
  var control = typeof target === "string" ? $(target) : target;
  if (!control) return;
  if (busy) {
    control.dataset.busyWasDisabled = control.disabled ? "1" : "0";
    control.disabled = true;
    control.classList.add("is-busy");
    control.setAttribute("aria-busy", "true");
    return;
  }
  control.classList.remove("is-busy");
  control.removeAttribute("aria-busy");
  control.disabled = control.dataset.busyWasDisabled === "1";
  delete control.dataset.busyWasDisabled;
}

function send(type, payload) {
  var id = String(state.seq++);
  var promise = new Promise(function (resolve, reject) {
    if (!window.chrome || !window.chrome.webview) {
      reject(new Error("WebView bridge is not available."));
      return;
    }

    state.pending[id] = { resolve: resolve, reject: reject, type: type, payload: payload || {} };
    window.chrome.webview.postMessage({ id: id, type: type, bridgeToken: state.bridgeToken || null, payload: payload || {} });
  });
  promise.requestId = id;
  return promise;
}

function cancelBridgeRequest(requestId) {
  if (!requestId) {
    return Promise.resolve({ cancelled: false });
  }

  return send("cancelRequest", { requestId: requestId });
}

function cancelChatRun(chatId, runId) {
  if (!chatId || !runId) return Promise.resolve({ cancelled: false });
  return send("cancelChatRun", { chatId: chatId, runId: runId });
}

function recordChatRunActivityState(chatId, activity) {
  if (!chatId || !activity) return;
  var run = state.chatRuns[chatId] = state.chatRuns[chatId] || { activities: [], stream: "" };
  if (typeof recordActivityTimeline === "function") {
    return recordActivityTimeline(run.activities, activity);
  }
  var copy = cloneActivity(activity);
  run.activities.push(copy);
  return copy;
}

function isKeyboardElement(element) {
  if (!element) {
    return false;
  }

  var tag = (element.tagName || "").toLowerCase();
  if (element.isContentEditable || tag === "textarea" || tag === "select") {
    return true;
  }

  if (tag !== "input") {
    return false;
  }

  return ["button", "checkbox", "color", "file", "hidden", "image", "radio", "range", "reset", "submit"].indexOf((element.type || "text").toLowerCase()) === -1;
}

function reportFocusState() {
  if (!window.chrome || !window.chrome.webview) {
    return;
  }

  var selection = window.getSelection ? window.getSelection() : null;
  var hasSelection = !!(selection && !selection.isCollapsed && String(selection).length > 0);
  var wantsKeyboard = document.hasFocus() && (isKeyboardElement(document.activeElement) || hasSelection);
  if (state.lastReportedWantsKeyboard === wantsKeyboard) {
    return;
  }
  state.lastReportedWantsKeyboard = wantsKeyboard;
  window.chrome.webview.postMessage({
    type: "focusState",
    payload: {
      wantsKeyboard: wantsKeyboard
    }
  });
}

function scheduleFocusStateReport() {
  if (state.focusReportTimer) {
    window.clearTimeout(state.focusReportTimer);
  }

  state.focusReportTimer = window.setTimeout(reportFocusState, 0);
}

if (window.chrome && window.chrome.webview) {
  window.chrome.webview.addEventListener("message", function (event) {
    var response = event.data;
    if (typeof response === "string") {
      response = JSON.parse(response);
    }
    if (response && response.type === "progress") {
      var progress = response.payload || {};
      var progressPending = state.pending[response.id];
      var isChatProgress = progressPending && (progressPending.type === "sendChat" || progressPending.type === "confirmAgentTool" || progressPending.type === "editMessage");
      var progressChatId = progress.chatId || progress.ChatId || (progressPending && progressPending.payload && progressPending.payload.chatId) || "";
      var progressRunId = progress.runId || progress.RunId || "";
      if (progressChatId) {
        state.chatRuns[progressChatId] = state.chatRuns[progressChatId] || { activities: [], stream: "" };
        state.chatRuns[progressChatId].runId = progressRunId;
        state.chatRuns[progressChatId].phase = progress.phase || progress.Phase || "working";
      }
      var contentDelta = progress.contentDelta || progress.ContentDelta || "";
      var reasoningDelta = progress.reasoningDelta || progress.ReasoningDelta || "";
      var reasoningComplete = !!(progress.reasoningComplete || progress.ReasoningComplete);
      var hasReasoningProgress = !!(reasoningDelta || reasoningComplete);
      if (contentDelta && isChatProgress) {
        if (progressChatId) state.chatRuns[progressChatId].stream = (state.chatRuns[progressChatId].stream || "") + contentDelta;
        if (progressChatId !== state.activeChatId) { renderChatSessions(); return; }
        state.liveStreamContent = progressChatId
          ? state.chatRuns[progressChatId].stream
          : (state.liveStreamContent || "") + contentDelta;
        if (progressChatId) {
          state.liveAgentRun = state.chatRuns[progressChatId].activities;
          if (!state.liveActivity && state.liveAgentRun && state.liveAgentRun.length) {
            state.liveActivity = state.liveAgentRun[state.liveAgentRun.length - 1];
          }
        }
        if (typeof scheduleLiveStreamRender === "function") {
          scheduleLiveStreamRender();
        } else {
          renderMessages();
        }
        return;
      }
      if (hasReasoningProgress && isChatProgress) {
        var reasoningRun = progressChatId ? state.chatRuns[progressChatId] : null;
        if (reasoningRun) {
          if (reasoningDelta && reasoningRun.reasoningComplete) reasoningRun.reasoning = "";
          reasoningRun.reasoning = (reasoningRun.reasoning || "") + reasoningDelta;
          if (reasoningRun.reasoning.length > 24000) reasoningRun.reasoning = reasoningRun.reasoning.substring(0, 24000);
          reasoningRun.reasoningComplete = reasoningComplete;
        }
        if (progressChatId === state.activeChatId) {
          state.liveReasoning = reasoningRun ? reasoningRun.reasoning : reasoningDelta;
          state.liveReasoningComplete = reasoningRun ? !!reasoningRun.reasoningComplete : reasoningComplete;
          if (typeof scheduleLiveStreamRender === "function") scheduleLiveStreamRender();
          else renderMessages();
        } else {
          renderChatSessions();
        }
        return;
      }
      if (isChatProgress) {
        var normalizedActivity = normalizeProgressActivity(progress);
        var storedActivity = recordChatRunActivityState(progressChatId, normalizedActivity);
        if (progressChatId === state.activeChatId) {
          state.liveActivity = storedActivity || normalizedActivity;
          state.liveAgentRun = state.chatRuns[progressChatId].activities;
        }
      }
      if (progressChatId !== state.activeChatId) { renderChatSessions(); return; }
      if (isChatProgress) {
        renderMessages();
      }
      log("[" + (progress.phase || "working") + "] " + (progress.message || "Выполняю..."));
      return;
    }
    if (response && response.type === "chatState") {
      applyChatCatalogState(response.payload || {});
      return;
    }
    var pending = state.pending[response.id];
    if (!pending) {
      return;
    }
    delete state.pending[response.id];
    if (response.ok) {
      pending.resolve(response.payload);
    } else {
      var error = new Error(response.error || "Bridge error");
      error.detail = response.errorDetail || response.error || "";
      error.cancelled = !!response.cancelled;
      pending.reject(error);
    }
  });
}
