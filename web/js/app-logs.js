function updateLogFilterCounts() {
  var box = $("logBox");
  var entries = box ? box.querySelectorAll(".log-entry") : [];
  var counts = { all: entries.length, error: 0, warning: 0, success: 0, info: 0 };
  Array.prototype.forEach.call(entries, function (entry) {
    var type = entry.dataset.logType;
    if (Object.prototype.hasOwnProperty.call(counts, type)) counts[type] += 1;
  });
  Array.prototype.forEach.call(document.querySelectorAll("[data-log-filter]"), function (button) {
    var count = button.querySelector(".log-filter-count");
    if (count) count.textContent = String(counts[button.dataset.logFilter] || 0);
  });
  if (box) box.classList.toggle("is-filter-empty", entries.length > 0 && (counts[state.logFilter] || 0) === 0);
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
}

function bindLogActions() {
  var clear = $("clearLogButton");
  if (clear) clear.addEventListener("click", function () {
    var box = $("logBox");
    if (box) box.textContent = "";
    updateLogFilterCounts();
  });
  Array.prototype.forEach.call(document.querySelectorAll("[data-log-filter]"), function (button) {
    button.addEventListener("click", function () {
      setLogFilter(button.dataset.logFilter);
    });
  });
  setLogFilter(state.logFilter);
}
