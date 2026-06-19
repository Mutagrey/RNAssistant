var state = {
  host: "",
  title: "",
  settings: {},
  tools: [],
  skills: [],
  context: {},
  contextUsage: {},
  chats: [],
  activeChatId: "",
  activeChatModel: "",
  messages: [],
  failedSend: null,
  liveActivity: null,
  modelCatalog: { configUrl: "", defaultModel: "", models: [], loaded: false, loading: false, error: "" },
  modelSaving: false,
  selectedToolIndex: -1,
  selectedSkillIndex: -1,
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

  state.activity = { visible: true, phase: phase || "working", message: message || "Working..." };
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
  return new Promise(function (resolve, reject) {
    var id = String(state.seq++);
    state.pending[id] = { resolve: resolve, reject: reject, type: type };
    window.chrome.webview.postMessage({ id: id, type: type, payload: payload || {} });
  });
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

window.chrome.webview.addEventListener("message", function (event) {
  var response = event.data;
  if (typeof response === "string") {
    response = JSON.parse(response);
  }
  if (response && response.type === "progress") {
    var progress = response.payload || {};
    var progressPending = state.pending[response.id];
    setActivity(progress.phase || "working", progress.message || "Working...");
    if (progressPending && progressPending.type === "sendChat") {
      state.liveActivity = normalizeProgressActivity(progress);
      renderMessages();
    }
    log("[" + (progress.phase || "working") + "] " + (progress.message || "Working..."));
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
    pending.reject(error);
  }
});
