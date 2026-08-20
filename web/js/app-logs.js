var runtimeLogRefreshBusy = false;

function logsTabVisible() {
  var panel = $("tab-logs");
  return !!panel && panel.classList.contains("active");
}

function runtimeLogVisible() {
  var section = document.querySelector('[data-log-view-section="runtime"]');
  return logsTabVisible() && !!section && !section.classList.contains("hidden");
}

function switchLogView(name) {
  name = name === "runtime" ? "runtime" : "session";
  Array.prototype.slice.call(document.querySelectorAll(".log-view-button")).forEach(function (button) {
    var active = button.dataset.logView === name;
    button.classList.toggle("active", active);
    button.setAttribute("aria-selected", active ? "true" : "false");
  });
  Array.prototype.slice.call(document.querySelectorAll("[data-log-view-section]")).forEach(function (section) {
    section.classList.toggle("hidden", section.dataset.logViewSection !== name);
  });
  if (name === "runtime") refreshRuntimeLog();
}

async function refreshRuntimeLog() {
  if (runtimeLogRefreshBusy || state.bridgeUnavailable) return;
  runtimeLogRefreshBusy = true;
  try {
    var response = await send("getRuntimeLog", {});
    var content = response.content !== undefined ? response.content : (response.Content || "");
    var path = response.path !== undefined ? response.path : (response.Path || "");
    var box = $("runtimeLogBox");
    if (box) {
      var nearBottom = box.scrollHeight - box.scrollTop - box.clientHeight < 48;
      box.textContent = content;
      if (nearBottom) box.scrollTop = box.scrollHeight;
    }
    if ($("runtimeLogPath")) $("runtimeLogPath").textContent = path;
  } catch (error) {
    if ($("runtimeLogBox")) $("runtimeLogBox").textContent = "Runtime-журнал недоступен: " + error.message;
  } finally {
    runtimeLogRefreshBusy = false;
  }
}

async function clearRuntimeLog() {
  if (runtimeLogRefreshBusy || state.bridgeUnavailable) return;
  if (!window.confirm("Очистить runtime-журнал RNAssistant?")) return;
  runtimeLogRefreshBusy = true;
  try {
    var response = await send("clearRuntimeLog", {});
    if ($("runtimeLogBox")) $("runtimeLogBox").textContent = response.content || response.Content || "";
    if ($("runtimeLogPath")) $("runtimeLogPath").textContent = response.path || response.Path || "";
  } catch (error) {
    log(error.message);
  } finally {
    runtimeLogRefreshBusy = false;
  }
}

function bindLogActions() {
  Array.prototype.slice.call(document.querySelectorAll(".log-view-button")).forEach(function (button) {
    button.addEventListener("click", function () {
      switchLogView(button.dataset.logView);
    });
  });
  $("clearLogButton").addEventListener("click", function () {
    $("logBox").textContent = "";
  });
  $("refreshRuntimeLogButton").addEventListener("click", refreshRuntimeLog);
  $("clearRuntimeLogButton").addEventListener("click", clearRuntimeLog);
  window.setInterval(function () {
    if (runtimeLogVisible()) refreshRuntimeLog();
  }, 3000);
}
