var runtimeLogRefreshBusy = false;

function logsTabVisible() {
  var panel = $("tab-logs");
  return !!panel && panel.classList.contains("active");
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
  $("clearLogButton").addEventListener("click", function () {
    $("logBox").textContent = "";
  });
  $("refreshRuntimeLogButton").addEventListener("click", refreshRuntimeLog);
  $("clearRuntimeLogButton").addEventListener("click", clearRuntimeLog);
  window.setInterval(function () {
    if (logsTabVisible()) refreshRuntimeLog();
  }, 3000);
}
