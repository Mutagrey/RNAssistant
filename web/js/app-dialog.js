(function () {
  "use strict";
  // A transient presentation only: callers own validation, commands and their outcomes.
  var active = null, sequence = 0;
  function open(options) {
    if (active) active.close(false);
    var origin = document.activeElement, disposed = false, pending = false, failed = false, composing = false;
    var events = new AbortController(), prefix = "rna-dialog-" + (++sequence);
    var root = document.createElement("dialog"); root.className = "rna-dialog"; root.tabIndex = -1;
    root.setAttribute("aria-labelledby", prefix + "-title"); root.setAttribute("aria-modal", "true");
    var form = document.createElement("form"); form.noValidate = true;
    var title = document.createElement("h2"); title.id = prefix + "-title"; title.textContent = options.title;
    form.appendChild(title);
    if (options.message) {
      var description = document.createElement("p"); description.id = prefix + "-description";
      description.textContent = options.message; form.appendChild(description);
      root.setAttribute("aria-describedby", description.id);
    }
    var input = null;
    if (options.label) {
      var field = document.createElement("label"); field.className = "rna-dialog-field";
      var caption = document.createElement("span"); caption.textContent = options.label;
      input = document.createElement(options.multiline ? "textarea" : "input");
      if (options.multiline) input.rows = 8; else input.type = "text";
      input.className = "rna-form-control compact";
      if (options.maximumLength) input.maxLength = options.maximumLength;
      input.value = options.value || ""; input.autocomplete = "off";
      field.append(caption, input); form.appendChild(field);
    }
    var status = document.createElement("p"); status.id = prefix + "-status";
    status.className = "rna-dialog-status"; status.hidden = true; status.setAttribute("role", "status");
    if (input) input.setAttribute("aria-describedby", status.id);
    var actions = document.createElement("div"); actions.className = "rna-dialog-actions";
    var cancel = document.createElement("button"); cancel.type = "button";
    cancel.className = "button compact"; cancel.textContent = "Отмена";
    var submit = document.createElement("button"); submit.type = "submit";
    submit.className = "button compact " + (options.danger ? "danger-soft" : "primary");
    submit.textContent = options.submitLabel;
    actions.append(cancel, submit); form.append(status, actions); root.appendChild(form);

    function on(node, name, handler) { node.addEventListener(name, handler, { signal: events.signal }); }
    function message(text, error) {
      status.setAttribute("role", error ? "alert" : "status");
      status.classList.toggle("is-error", !!error); status.textContent = text; status.hidden = !text;
    }
    // As with ActionMenu, size in viewport coordinates before converting through CSS zoom.
    function fit() {
      if (disposed) return;
      var styles = getComputedStyle(root);
      // offsetWidth rounds to integers and can make ResizeObserver oscillate at 125%.
      var scale = root.getBoundingClientRect().width / Number.parseFloat(styles.width) || 1;
      var inset = Number.parseFloat(styles.getPropertyValue("--space-4"));
      root.style.maxWidth = Math.max(0, innerWidth / scale - 2 * inset) + "px";
      root.style.maxHeight = Math.max(0, innerHeight / scale - 2 * inset) + "px";
      var bounds = root.getBoundingClientRect();
      root.style.left = Math.max(inset, (innerWidth - bounds.width) / (2 * scale)) + "px";
      root.style.top = Math.max(inset, (innerHeight - bounds.height) / (2 * scale)) + "px";
    }
    function close(restoreFocus) {
      if (disposed) return;
      disposed = true; events.abort(); observer.disconnect();
      // Removal exits the top layer without the native close() restoring an obsolete target.
      root.remove(); if (active === handle) active = null;
      if (options.onClose) options.onClose();
      if (restoreFocus !== false) {
        var target = options.restoreFocus ? options.restoreFocus() : origin;
        if (target && target.isConnected && !target.disabled && target.getClientRects().length)
          target.focus({ preventScroll: true });
      }
    }
    async function commit(event) {
      event.preventDefault(); event.stopPropagation();
      if (disposed || pending || failed || composing) return;
      var value = input ? input.value : true;
      var validation = options.validate && options.validate(value);
      if (validation) {
        message(validation, true);
        if (input) { input.setAttribute("aria-invalid", "true"); input.focus(); }
        fit(); return;
      }
      pending = true; submit.disabled = true; if (input) input.disabled = true;
      root.setAttribute("aria-busy", "true"); cancel.textContent = "Закрыть";
      message("Операция выполняется. Закрытие окна её не отменяет.", false);
      root.focus({ preventScroll: true }); fit();
      try {
        await options.onSubmit(value);
        if (!disposed) close(true);
      } catch (error) {
        if (disposed) return;
        failed = true; pending = false; root.removeAttribute("aria-busy");
        // A rejected/lost mutation is never resubmitted from this dialog.
        if (input) { input.disabled = false; input.readOnly = true; }
        message(error.detail || error.message || "Не удалось выполнить действие.", true);
        cancel.focus({ preventScroll: true }); fit();
      }
    }
    on(form, "submit", commit);
    on(cancel, "click", function () { close(true); });
    on(root, "cancel", function (event) { event.preventDefault(); if (!composing) close(true); });
    on(root, "keydown", function (event) {
      if (event.key === "Enter" && (event.isComposing || event.keyCode === 229 || event.repeat)) {
        event.preventDefault(); event.stopPropagation(); return;
      }
      if (event.key === "Escape" && !event.isComposing && !composing) {
        event.preventDefault(); event.stopPropagation(); close(true); return;
      }
      if (event.key === "Tab") {
        var controls = Array.from(root.querySelectorAll("input:not(:disabled), textarea:not(:disabled), button:not(:disabled)"));
        var index = controls.indexOf(document.activeElement);
        if (index < 0 || event.shiftKey && index === 0 || !event.shiftKey && index === controls.length - 1) {
          event.preventDefault(); controls[event.shiftKey ? controls.length - 1 : 0].focus();
        }
      }
    });
    if (input) {
      on(input, "compositionstart", function () { composing = true; });
      on(input, "compositionend", function () { composing = false; });
      on(input, "input", function () { if (!failed) { input.removeAttribute("aria-invalid"); message("", false); } });
    }
    var observer = new ResizeObserver(fit), handle = { close: close };
    active = handle; document.body.appendChild(root); root.showModal(); fit();
    observer.observe(root); observer.observe(document.documentElement);
    on(window, "resize", fit); on(window, "pagehide", function () { close(false); });
    if (input) { input.focus(); input.select(); } else cancel.focus();
    return handle;
  }
  window.RNAssistantDialog = Object.freeze({ open: open });
}());
