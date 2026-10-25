(function () {
  "use strict";

  function attach(root, options) {
    var bar = document.createElement("div");
    bar.className = "preview-surface-bar";
    bar.hidden = true;
    var panelHead = document.createElement("div");
    panelHead.className = "preview-surface-panel-head";
    var section = document.createElement("div");
    section.className = "preview-surface-section";
    panelHead.appendChild(section);
    var resourceRow = document.createElement("div");
    resourceRow.className = "preview-surface-resource-row";
    var location = document.createElement("span");
    location.className = "preview-surface-location";
    var title = document.createElement("strong");
    title.className = "preview-surface-title";
    var path = document.createElement("small");
    path.className = "preview-surface-path";
    location.append(title, path);
    var status = document.createElement("span");
    status.className = "preview-surface-status";
    status.setAttribute("role", "status");
    var actions = document.createElement("div");
    actions.className = "preview-surface-actions";
    var reload = options.reloadButton || document.createElement("button");
    var currentLabel = null;
    var reloadLabel = options.reloadLabel || "Обновить предпросмотр";
    reload.setAttribute("aria-label", reloadLabel);
    reload.title = reloadLabel;
    if (!options.reloadButton) {
      reload.type = "button";
      reload.className = "icon-btn";
      reload.innerHTML = window.RNAssistantIcons.render("refresh");
      reload.addEventListener("click", function () {
        try {
          var result = options.reload();
          if (result && typeof result.catch === "function") result.catch(function (error) {
            fail(currentLabel, error && error.message);
          });
        } catch (error) { fail(currentLabel, error && error.message); }
      });
    }
    actions.appendChild(reload);
    resourceRow.append(location, status, actions);
    bar.append(panelHead, resourceRow);
    root.insertBefore(bar, root.firstChild);

    var pending = null;
    function detach() {
      if (!pending) return;
      pending.frame.removeEventListener("load", pending.load);
      pending.frame.removeEventListener("error", pending.error);
      pending = null;
    }
    function setStatus(message, error) {
      status.textContent = message || "";
      status.title = message || "";
      status.dataset.error = error ? "true" : "false";
      status.setAttribute("role", error ? "alert" : "status");
    }
    function show(label, message) {
      bar.hidden = false;
      currentLabel = label;
      var value = label && typeof label === "object" ? label : { title: label };
      title.textContent = value.title || "Предпросмотр";
      path.textContent = value.path || "";
      location.title = title.textContent + (path.textContent ? " · " + path.textContent : "");
      path.hidden = !path.textContent;
      setStatus(message, false);
      reload.disabled = false;
    }
    function fail(label, message) {
      detach();
      show(label, "");
      setStatus(String(message || "Предпросмотр недоступен.").slice(0, 500), true);
    }
    function watch(frame, label) {
      detach();
      show(label, "Загрузка…");
      var current = {
        frame: frame,
        load: function () { if (pending === current) { detach(); setStatus("", false); } },
        error: function () { if (pending === current) { detach(); setStatus("Не удалось загрузить предпросмотр.", true); } }
      };
      pending = current;
      frame.addEventListener("load", current.load);
      frame.addEventListener("error", current.error);
    }
    return {
      actions: actions,
      watch: watch,
      waiting: function (label, message) { detach(); show(label, message || "Загрузка…"); },
      fail: fail,
      clear: function () { if (bar.hidden && !pending) return; detach(); bar.hidden = true; currentLabel = null; title.textContent = ""; path.textContent = ""; location.title = ""; path.hidden = true; setStatus("", false); reload.disabled = true; }
    };
  }

  window.RNAssistantPreviewSurface = { attach: attach };
}());
