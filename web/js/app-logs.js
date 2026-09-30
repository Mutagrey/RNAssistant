var logFilterCounts = { all: 0, error: 0, warning: 0, success: 0, info: 0 };

function updateLogFilterCounts() {
  var box = $("logBox");
  Array.prototype.forEach.call(document.querySelectorAll("[data-log-filter]"), function (button) {
    var count = button.querySelector(".log-filter-count");
    if (count) count.textContent = String(logFilterCounts[button.dataset.logFilter] || 0);
  });
  if (box) box.classList.toggle("is-filter-empty",
    logFilterCounts.all > 0 && (logFilterCounts[state.logFilter] || 0) === 0);
}

function recordLogEntry(entry, removed) {
  logFilterCounts.all += 1;
  if (Object.prototype.hasOwnProperty.call(logFilterCounts, entry.dataset.logType))
    logFilterCounts[entry.dataset.logType] += 1;
  if (removed) {
    logFilterCounts.all -= 1;
    if (Object.prototype.hasOwnProperty.call(logFilterCounts, removed.dataset.logType))
      logFilterCounts[removed.dataset.logType] -= 1;
  }
  updateLogFilterCounts();
}

function isLogNearBottom(box) {
  return box.scrollHeight - box.clientHeight - box.scrollTop <= 32;
}

function updateLogScrollButton() {
  var box = $("logBox");
  var button = $("logScrollBottomButton");
  if (!box || !button) return;
  var visible = !isLogNearBottom(box);
  button.classList.toggle("is-visible", visible);
  button.setAttribute("aria-hidden", visible ? "false" : "true");
  button.tabIndex = visible ? 0 : -1;
}

function setLogFilter(filter) {
  var allowed = { all: true, error: true, warning: true, success: true, info: true };
  state.logFilter = allowed[filter] ? filter : "all";
  Array.prototype.forEach.call(document.querySelectorAll("[data-log-filter]"), function (button) {
    var active = button.dataset.logFilter === state.logFilter;
    button.classList.toggle("active", active);
    button.setAttribute("aria-pressed", active ? "true" : "false");
  });
  Array.prototype.forEach.call(document.querySelectorAll("#logBox .log-entry"), function (entry) {
    entry.hidden = state.logFilter !== "all" && entry.dataset.logType !== state.logFilter;
  });
  updateLogFilterCounts();
  updateLogScrollButton();
}

function bindLogActions() {
  var clear = $("clearLogButton");
  if (clear) clear.addEventListener("click", function () {
    var box = $("logBox");
    if (box) box.textContent = "";
    logFilterCounts = { all: 0, error: 0, warning: 0, success: 0, info: 0 };
    updateLogFilterCounts();
    updateLogScrollButton();
  });
  var box = $("logBox");
  if (box) box.addEventListener("scroll", updateLogScrollButton, { passive: true });
  var bottom = $("logScrollBottomButton");
  if (bottom) bottom.addEventListener("click", function () {
    if (box) box.scrollTop = box.scrollHeight;
    updateLogScrollButton();
  });
  Array.prototype.forEach.call(document.querySelectorAll("[data-log-filter]"), function (button) {
    button.addEventListener("click", function () {
      setLogFilter(button.dataset.logFilter);
    });
  });
  setLogFilter(state.logFilter);
}
