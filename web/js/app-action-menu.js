(function () {
  "use strict";

  function escape(value) {
    return String(value == null ? "" : value).replace(/[&<>"']/g, function (character) {
      return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[character];
    });
  }

  function icon(name) { return window.RNAssistantNavigationTree.icon(name); }

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
    document.querySelectorAll(".rna-action-menu[open]").forEach(function (menu) {
      if (menu !== except) menu.removeAttribute("open");
    });
  }

  document.addEventListener("toggle", function (event) {
    var menu = event.target;
    if (menu.matches && menu.matches(".rna-action-menu") && menu.open) closeAll(menu);
  }, true);
  document.addEventListener("click", function (event) {
    var menu = event.target.closest(".rna-action-menu");
    if (!menu) closeAll();
    else if (event.target.closest(".rna-action-menu-body > button")) menu.removeAttribute("open");
  });
  document.addEventListener("keydown", function (event) {
    if (event.key === "Escape") closeAll();
  });

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

  window.RNAssistantActionMenu = Object.freeze({ render: render, closeAll: closeAll });
  document.querySelectorAll("[data-rna-action-menu]").forEach(function (mount) {
    var preset = presets[mount.getAttribute("data-rna-action-menu")];
    if (!preset) throw new Error("Unknown action menu preset");
    mount.outerHTML = render(preset.items, preset);
  });
}());
