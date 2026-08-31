var state = {
  appVersion: "",
  host: "",
  title: "",
  officeContext: null,
  settings: {},
  hasApiKey: false,
  hasHistorySecret: false,
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
  activeChatReasoning: false,
  bridgeToken: "",
  messages: [],
  artifacts: [],
  artifactLibrary: { sessionRevision: 0, heads: [] },
  activeContextCheckpointId: "",
  activeHtmlArtifactId: "",
  activeTaskListArtifactId: "",
  activePlanDocumentArtifactId: "",
  agentPlanExpanded: {},
  draftAttachments: [],
  failedSend: null,
  activeSends: {},
  chatRuns: {},
  chatProjectionRevisions: {},
  activeRunViewState: null,
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
  modeSaving: false,
  reasoningSaving: false,
  modelDiagnostics: null,
  modelDiagnosticsTimer: null,
  modelDiagnosticsLocalStart: 0,
  bridgeUnavailable: false,
  selectedToolIndex: -1,
  selectedSkillIndex: -1,
  toolLibraryBaseline: "",
  toolLibraryBaselineItems: [],
  toolLibraryDirty: false,
  skillLibraryBaseline: "",
  skillLibraryBaselineItems: [],
  skillLibraryDirty: false,
  selectedPromptIndex: -1,
  selectedInstructionKind: "prompt",
  promptEditorMode: "edit",
  toolEditorPage: "main",
  toolSchemaMode: "form",
  vbaEditorMode: "edit",
  vbaEditorDirty: false,
  htmlWorkspaceMode: "preview",
  htmlWorkspaceSelection: { type: "file", id: "" },
  htmlWorkspaceSidebarHidden: false,
  vbaSidebarHidden: false,
  htmlWorkspaceDirty: false,
  htmlWorkspaceCreateKind: "",
  htmlWorkspace: { activeFileId: "", files: [], dataSources: [], history: [], redoHistory: [], redoBranches: [], recovery: { status: "empty", canMutate: true, candidates: [] } },
  collapsedResourceGroups: {},
  promptDrafts: {},
  toolsPath: "",
  skillsPath: "",
  vba: { modules: [], backups: [], selectedModule: "" },
  pending: {},
  seq: 1,
  focusReportTimer: null,
  lastReportedWantsKeyboard: null,
  logFilter: "all",
  highlightLog: {},
  highlightRetryScheduled: false,
  highlightRetryAttempts: 0,
  highlightLoadLogged: false,
  syncTimer: null,
  chatSyncPromise: null,
  initializePromise: null,
  chatNavigationVersion: 0,
  chatStateApplyVersion: 0
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

function resolveLogType(message, type) {
  var normalizedType = String(type || "").toLowerCase();
  if (normalizedType === "error" || normalizedType === "warning" || normalizedType === "success" || normalizedType === "info") {
    return normalizedType;
  }

  var text = String(message || "").toLowerCase();
  if (/\b(error|failed|failure|fail|exception|fatal|denied|invalid|unavailable|timeout|refused)\b|ошиб|не удалось|недоступ|сбой|отказ|неверн|запрещ/.test(text)) {
    return "error";
  }
  if (/\b(warn|warning|cancelled|canceled|missing|not loaded|not bundled|limit)\b|предупреж|отмен|лимит|не загруж|не найден|исправьте|не более/.test(text)) {
    return "warning";
  }
  if (/\b(ok|success|saved|loaded|completed|finished|created|updated|deleted|cleared|copied|opened|selected|enabled|disabled|confirmed|restored|initialized|added|activated|recorded)\b|сохран|загруж|заверш|создан|обнов|удал|очищ|скопирован|открыт|выбран|включ|выключ|подтверж|восстанов|инициализ|добавлен|актив|переименован|выполнен/.test(text)) {
    return "success";
  }
  return "info";
}

function log(message, type) {
  var box = $("logBox");
  if (!box) {
    return;
  }
  var resolvedType = resolveLogType(message, type);
  var entry = document.createElement("span");
  entry.className = "log-entry log-entry-" + resolvedType;
  entry.dataset.logType = resolvedType;
  entry.hidden = state.logFilter !== "all" && state.logFilter !== resolvedType;

  var time = document.createElement("span");
  time.className = "log-entry-time";
  time.textContent = new Date().toISOString();

  var text = document.createElement("span");
  text.className = "log-entry-message";
  text.textContent = String(message === null || message === undefined ? "" : message);

  entry.appendChild(time);
  entry.appendChild(document.createTextNode(" "));
  entry.appendChild(text);
  box.appendChild(entry);
  if (typeof updateLogFilterCounts === "function") {
    updateLogFilterCounts();
  }
  box.scrollTop = box.scrollHeight;
}

function logOnce(message, type) {
  if (state.highlightLog[message]) {
    return;
  }
  state.highlightLog[message] = true;
  log(message, type);
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
  var run = state.chatRuns[chatId] = state.chatRuns[chatId] || { activities: [], stream: "", streamResetPending: false, reasoningResetPending: false };
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
    if (response && response.type === "modelDiagnostics") {
      if (typeof handleModelDiagnosticsUpdate === "function") {
        handleModelDiagnosticsUpdate(response.payload || {});
      }
      return;
    }
    if (response && response.type === "progress") {
      var progress = response.payload || {};
      var progressPending = state.pending[response.id];
      var isChatProgress = progressPending && (progressPending.type === "sendChat" || progressPending.type === "confirmAgentTool" || progressPending.type === "editMessage");
      var progressChatId = progress.chatId || progress.ChatId || (progressPending && progressPending.payload && progressPending.payload.chatId) || "";
      var progressRunId = progress.runId || progress.RunId || "";
      if (progressChatId && isChatProgress) {
        state.chatRuns[progressChatId] = state.chatRuns[progressChatId] || { activities: [], stream: "", streamResetPending: false, reasoningResetPending: false };
        state.chatRuns[progressChatId].runId = progressRunId;
        state.chatRuns[progressChatId].phase = progress.phase || progress.Phase || "working";
      }
      var contentReset = !!(progress.contentReset || progress.ContentReset);
      var reasoningReset = !!(progress.reasoningReset || progress.ReasoningReset);
      var contentDelta = progress.contentDelta || progress.ContentDelta || "";
      var reasoningDelta = progress.reasoningDelta || progress.ReasoningDelta || "";
      var reasoningComplete = !!(progress.reasoningComplete || progress.ReasoningComplete);
      var hasReasoningProgress = !!(reasoningDelta || reasoningComplete);
      if ((contentReset || reasoningReset) && isChatProgress) {
        // Keep the previous attempt visible until the next attempt has real output to replace it.
        var resetRun = progressChatId ? state.chatRuns[progressChatId] : null;
        if (resetRun) {
          if (contentReset) resetRun.streamResetPending = true;
          if (reasoningReset) resetRun.reasoningResetPending = true;
        }
        if (!contentDelta && !hasReasoningProgress) return;
      }
      if (contentDelta && isChatProgress) {
        var contentRun = progressChatId ? state.chatRuns[progressChatId] : null;
        var replaceContent = !!(contentRun && contentRun.streamResetPending);
        var firstContentDelta = !progressChatId || replaceContent || !contentRun.stream;
        if (contentRun) {
          contentRun.stream = replaceContent ? contentDelta : (contentRun.stream || "") + contentDelta;
          contentRun.streamResetPending = false;
          if (contentRun.reasoningResetPending) {
            contentRun.reasoning = "";
            contentRun.reasoningComplete = false;
            contentRun.reasoningResetPending = false;
          }
        }
        if (progressChatId !== state.activeChatId) { renderChatSessions(); return; }
        state.liveStreamContent = progressChatId
          ? contentRun.stream
          : (state.liveStreamContent || "") + contentDelta;
        if (contentRun && !contentRun.reasoning) resetLiveReasoning();
        if (progressChatId) {
          state.liveAgentRun = state.chatRuns[progressChatId].activities;
          if (!state.liveActivity && state.liveAgentRun && state.liveAgentRun.length) {
            state.liveActivity = state.liveAgentRun[state.liveAgentRun.length - 1];
          }
        }
        if (firstContentDelta) {
          renderMessages();
        } else if (typeof scheduleLiveStreamRender === "function") {
          scheduleLiveStreamRender();
        } else {
          renderMessages();
        }
        return;
      }
      if (hasReasoningProgress && isChatProgress) {
        var reasoningRun = progressChatId ? state.chatRuns[progressChatId] : null;
        if (reasoningRun) {
          if (reasoningRun.reasoningResetPending) {
            reasoningRun.reasoning = "";
            reasoningRun.reasoningComplete = false;
            reasoningRun.reasoningResetPending = false;
          }
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
        var activityPhase = String(progress.phase || progress.Phase || "").toLowerCase();
        if ((activityPhase === "acting" || activityPhase === "tool_running") && progressChatId) {
          state.chatRuns[progressChatId].stream = "";
          state.chatRuns[progressChatId].streamResetPending = false;
          state.chatRuns[progressChatId].reasoningResetPending = false;
          if (progressChatId === state.activeChatId) state.liveStreamContent = null;
        }
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
      applyPushedChatState(response);
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
