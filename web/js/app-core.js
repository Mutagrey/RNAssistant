var state = {
  host: "",
  title: "",
  officeContext: null,
  settings: {},
  tools: [],
  skills: [],
  context: {},
  contextUsage: {},
  chats: [],
  activeChatId: "",
  activeChatModel: "",
  activeChatHtmlMode: false,
  bridgeToken: "",
  messages: [],
  failedSend: null,
  activeSend: null,
  liveActivity: null,
  liveAgentRun: null,
  modelCatalog: { configUrl: "", defaultModel: "", models: [], loaded: false, loading: false, error: "" },
  modelSaving: false,
  bridgeUnavailable: false,
  selectedToolIndex: -1,
  selectedSkillIndex: -1,
  selectedPromptIndex: -1,
  promptEditorMode: "preview",
  vbaEditorMode: "preview",
  htmlWorkspaceMode: "preview",
  htmlWorkspaceSelection: { type: "file", id: "" },
  htmlWorkspaceSidebarHidden: false,
  htmlWorkspaceDirty: false,
  htmlWorkspace: { activeFileId: "", files: [], dataSources: [], history: [] },
  collapsedResourceGroups: {},
  promptDrafts: {},
  toolsPath: "",
  skillsPath: "",
  vba: { modules: [], backups: [], selectedModule: "" },
  activity: { visible: false, phase: "", message: "" },
  pending: {},
  seq: 1,
  focusReportTimer: null,
  highlightLog: {},
  highlightRetryScheduled: false,
  highlightRetryAttempts: 0,
  highlightLoadLogged: false
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

function setActivity(phase, message) {
  var status = $("activityStatus");
  var text = $("activityText");
  if (!status || !text) {
    return;
  }

  state.activity = { visible: true, phase: phase || "working", message: message || "Выполняю..." };
  status.classList.remove("hidden");
  status.dataset.phase = state.activity.phase;
  text.textContent = state.activity.message;
}

function clearActivity() {
  var status = $("activityStatus");
  if (!status) {
    return;
  }

  state.activity = { visible: false, phase: "", message: "" };
  status.classList.add("hidden");
  status.removeAttribute("data-phase");
}

function showHelp() {
  var modal = $("helpModal");
  if (modal) {
    modal.classList.remove("hidden");
  }
}

function hideHelp() {
  var modal = $("helpModal");
  if (modal) {
    modal.classList.add("hidden");
  }
}

function send(type, payload) {
  var id = String(state.seq++);
  var promise = new Promise(function (resolve, reject) {
    if (!window.chrome || !window.chrome.webview) {
      reject(new Error("WebView bridge is not available."));
      return;
    }

    state.pending[id] = { resolve: resolve, reject: reject, type: type };
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
  window.chrome.webview.postMessage({
    type: "focusState",
    payload: {
      wantsKeyboard: document.hasFocus() && (isKeyboardElement(document.activeElement) || hasSelection)
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
      setActivity(progress.phase || "working", progress.message || "Выполняю...");
      if (progressPending && progressPending.type === "sendChat") {
        state.liveActivity = normalizeProgressActivity(progress);
        if (typeof recordLiveAgentActivity === "function") {
          recordLiveAgentActivity(state.liveActivity);
        }
        renderMessages();
      }
      log("[" + (progress.phase || "working") + "] " + (progress.message || "Выполняю..."));
      return;
    }
    if (response && response.type === "chatState") {
      applyChatState(response.payload || {});
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
