(function () {
  "use strict";

  function escape(value) {
    return String(value == null ? "" : value).replace(/[&<>"']/g, function (character) {
      return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[character];
    });
  }

  function icon(name) { return window.RNAssistantIcons.render(name); }
  var popup = null, popupTrigger = null;

  function closePopup(restoreFocus) {
    if (!popup) return;
    popup.remove(); popup = null;
    if (popupTrigger) {
      popupTrigger.setAttribute("aria-expanded", "false");
      if (restoreFocus && popupTrigger.isConnected) popupTrigger.focus({ preventScroll: true });
    }
    popupTrigger = null;
  }

  // Portalled variant for headers inside Dockview's clipped tab container.
  function open(trigger, items, options) {
    if (trigger === popupTrigger) { closePopup(true); return; }
    closeAll(); options = options || {};
    popupTrigger = trigger; popup = document.createElement("div");
    popup.className = "rna-action-menu-popup rna-action-menu-body" + (options.className ? " " + options.className : "");
    popup.setAttribute("role", options.role === "dialog" ? "dialog" : "menu"); popup.tabIndex = -1;
    popup.setAttribute("aria-label", options.label || trigger.getAttribute("aria-label") || "Действия");
    var menu = popup;
    function button(item, key) {
      var control = document.createElement(item.informational ? "div" : "button");
      if (item.informational) control.className = "rna-action-menu-info"; else control.type = "button";
      control.dataset.menuKey = item.key || key;
      var check = typeof item.checked === "boolean";
      if (item.danger) control.classList.add("is-danger");
      if (!item.informational) { control.setAttribute("role", check ? "menuitemcheckbox" : "menuitem"); control.setAttribute("aria-label", item.label); control.disabled = !!item.disabled; }
      if (check) control.setAttribute("aria-checked", String(item.checked));
      control.innerHTML = (check ? icon("check") : item.iconHtml || (item.icon === false ? "" : icon(item.icon || "right"))) + '<span class="rna-action-menu-label">' + escape(item.label) + '</span>';
      if (item.status) {
        control.classList.add("has-detail"); if (check || item.icon !== false) control.classList.add("has-icon");
        var status = document.createElement("small"); status.textContent = item.status; control.appendChild(status);
        control.title = item.label + " · " + item.status;
      }
      if (!item.informational) control.addEventListener("click", function (event) {
        // A retained action may replace its own DOM before document.click runs.
        event.stopPropagation();
        if (!check && !item.keepOpen) closePopup(true); item.onSelect();
      });
      return control;
    }
    function render(values) {
      var active = menu.contains(document.activeElement) && document.activeElement.dataset.menuKey, scroll = menu.scrollTop;
      menu.replaceChildren();
      values.forEach(function (item, index) {
        if (item.separator) { menu.appendChild(document.createElement("hr")); return; }
        var control = button(item, String(index));
        if (item.secondary) {
          var row = document.createElement("div"); row.className = "rna-action-menu-row"; row.setAttribute("role", "none");
          var secondary = button(item.secondary, String(index) + ":secondary");
          secondary.className = "rna-action-menu-secondary"; secondary.title = item.secondary.label;
          row.append(control, secondary); menu.appendChild(row);
        } else menu.appendChild(control);
      });
      if (active) {
        var controls = Array.from(menu.querySelectorAll("button:not(:disabled)"));
        var next = controls.find(function (item) { return item.dataset.menuKey === active; }) || controls[0] || trigger;
        next.focus({ preventScroll: true });
      }
      menu.scrollTop = scroll;
    }
    render(items);
    document.body.appendChild(popup); trigger.setAttribute("aria-expanded", "true");
    function position() {
      var rect = trigger.getBoundingClientRect(), styles = getComputedStyle(menu);
      var scale = menu.getBoundingClientRect().width / menu.offsetWidth || 1;
      var inset = Number.parseFloat(styles.getPropertyValue("--space-2")) * scale;
      var offset = Number.parseFloat(styles.getPropertyValue("--menu-offset")) * scale;
      menu.style.maxWidth = "min(var(--menu-max-width, 100vw), " + Math.max(0, (innerWidth - 2 * inset) / scale) + "px)";
      menu.style.maxHeight = Math.max(0, (innerHeight - 2 * inset) / scale) + "px";
      var bounds = menu.getBoundingClientRect();
      var left = options.align === "start" ? rect.left : rect.right - bounds.width;
      menu.style.left = Math.max(inset, Math.min(left, innerWidth - bounds.width - inset)) / scale + "px";
      var top = options.placement === "top" ? rect.top - bounds.height - offset : rect.bottom + offset;
      menu.style.top = Math.max(inset, Math.min(top, innerHeight - bounds.height - inset)) / scale + "px";
    }
    position();
    var first = popup.querySelector("button:not(:disabled)"); if (first) first.focus(); else if (options.role === "dialog") popup.focus();
    return { update: function (values) { if (popup === menu) { render(values); position(); } },
      close: function () { if (popup === menu) closePopup(false); },
      isOpen: function () { return popup === menu; } };
  }

  function createTrigger(label) {
    var button = document.createElement("button"); button.type = "button"; button.className = "icon-btn";
    button.innerHTML = icon("more"); button.setAttribute("aria-label", label); button.title = label;
    button.setAttribute("aria-haspopup", "menu"); button.setAttribute("aria-expanded", "false"); return button;
  }

  // Reuse existing command handlers when a view already owns DOM-backed commands.
  function openButtons(trigger, root) {
    open(trigger, Array.from(root.children).filter(function (item) {
      return item.tagName === "BUTTON" && !item.hidden && !item.classList.contains("hidden");
    }).map(function (item) { return { label: item.textContent.trim(), disabled: item.disabled,
      icon: item.dataset.menuIcon || (item.querySelector("[data-rna-icon]") || { dataset: {} }).dataset.rnaIcon,
      onSelect: function () { item.click(); } }; }));
  }
  function render(items, options) {
    options = options || {};
    var label = options.label || "Дополнительные действия";
    return '<details class="popover rna-action-menu"' + (options.id ? ' id="' + escape(options.id) + '"' : "") +
      '><summary class="rna-action-menu-trigger" aria-label="' + escape(label) + '" title="' + escape(label) + '">' +
      icon("more") + '</summary><div class="popover-menu rna-action-menu-body">' + items.map(function (item) {
        if (item === "-" || item.separator) return '<hr>';
        if (item.type === "checkbox") return '<label class="rna-action-menu-check"><input type="checkbox" id="' +
          escape(item.id) + '"><span>' + escape(item.label) + '</span></label>';
        var action = item.action ? ' data-' + (item.route ? "go" : "action") + '="' + escape(item.action) + '"' : "";
        return '<button type="button"' + (item.id ? ' id="' + escape(item.id) + '"' : "") + action +
          (item.className ? ' class="' + escape(item.className) + '"' : "") + '>' + icon(item.icon || "right") +
          '<span class="rna-action-menu-label">' + escape(item.label) + '</span></button>';
      }).join("") + '</div></details>';
  }

  function closeAll(except) {
    closePopup(false);
    document.querySelectorAll(".rna-action-menu[open]").forEach(function (menu) {
      if (menu !== except) menu.removeAttribute("open");
    });
  }

  document.addEventListener("toggle", function (event) {
    var menu = event.target;
    if (menu.matches && menu.matches(".rna-action-menu") && menu.open) closeAll(menu);
  }, true);
  document.addEventListener("click", function (event) {
    if (popup && (popup.contains(event.target) || popupTrigger && popupTrigger.contains(event.target))) return;
    var menu = event.target.closest(".rna-action-menu");
    if (!menu) closeAll();
    else if (event.target.closest(".rna-action-menu-body > button")) menu.removeAttribute("open");
  });
  document.addEventListener("keydown", function (event) {
    if (popup) {
      var buttons = Array.from(popup.querySelectorAll("button:not(:disabled)")), index = buttons.indexOf(document.activeElement), target;
      if (event.key === "Escape") { closePopup(true); event.preventDefault(); return; }
      if (event.key === "ArrowDown") target = buttons[(index + 1) % buttons.length];
      else if (event.key === "ArrowUp") target = buttons[(index - 1 + buttons.length) % buttons.length];
      else if (event.key === "Home") target = buttons[0];
      else if (event.key === "End") target = buttons[buttons.length - 1];
      else if (event.key === "Tab") target = buttons[(index + (event.shiftKey ? -1 : 1) + buttons.length) % buttons.length];
      if (target) { event.preventDefault(); target.focus(); }
    } else if (event.key === "Escape") closeAll();
  });
  window.addEventListener("resize", function () { closePopup(false); });
  window.addEventListener("pagehide", function () { closePopup(false); });

  var presets = {
    tool: { id: "toolActionsMenu", label: "Другие действия с инструментом", items: [
      { id: "cloneToolButton", label: "Создать копию", icon: "copy" },
      { type: "checkbox", id: "toolEnabledInput", label: "Включён" },
      { id: "deleteToolButton", label: "Удалить инструмент", icon: "trash", className: "danger-text" }
    ] },
    skill: { id: "skillActionsMenu", label: "Другие действия с навыком", items: [
      { id: "skillPreviewToggleButton", label: "Просмотреть Markdown", icon: "book" },
      { id: "cloneSkillButton", label: "Создать копию", icon: "copy" },
      { id: "copySkillContextButton", label: "Скопировать текст навыка", icon: "copy" },
      { id: "deleteSkillButton", label: "Удалить навык", icon: "trash", className: "danger-text" }
    ] },
    prompt: { id: "promptActionsMenu", label: "Другие действия с промптом", items: [
      { id: "resetCurrentPromptButton", label: "Вернуть встроенный текст", icon: "refresh" }
    ] },
    vba: { id: "vbaActionsMenu", label: "Другие действия с модулем", items: [
      { id: "discardVbaChangesButton", label: "Отменить изменения", icon: "refresh" },
      { id: "downloadVbaModuleButton", label: "Скачать копию", icon: "download" }
    ] }
  };

  window.RNAssistantActionMenu = Object.freeze({ createTrigger: createTrigger, render: render, open: open, openButtons: openButtons, closeAll: closeAll,
    treeActions: function (label, actions) {
      return [{ title: label, menu: true, iconHtml: icon("more"), onClick: function (node, button) {
        open(button, actions.filter(function (action) { return !action.hidden; }).map(function (action) {
          return { label: action.title, iconHtml: action.iconHtml, danger: action.danger, disabled: action.disabled,
            onSelect: function () { action.onClick(node, button); } };
        }), { label: label });
      } }];
    },
    closeWithin: function (root) { if (popupTrigger && root.contains(popupTrigger)) closePopup(false); }
  });
  document.querySelectorAll("[data-rna-action-menu]").forEach(function (mount) {
    var preset = presets[mount.getAttribute("data-rna-action-menu")];
    if (!preset) throw new Error("Unknown action menu preset");
    mount.outerHTML = render(preset.items, preset);
  });
}());
