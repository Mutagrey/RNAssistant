(function () {
  "use strict";

  function escape(value) {
    return String(value == null ? "" : value).replace(/[&<>"']/g, function (character) {
      return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[character];
    });
  }

  function render(items, options) {
    options = options || {};
    var attribute = options.dataAttribute || "data-go";
    if (!/^data-[a-z-]+$/.test(attribute)) throw new Error("Invalid tab action attribute");
    return '<nav class="rna-editor-tabs ' + escape(options.className || "") +
      '" aria-label="' + escape(options.label || "Разделы экрана") + '">' +
      items.map(function (item) {
        var selected = item.value === options.selected;
        return '<button type="button" class="' + escape(options.buttonClass || "") +
          (selected ? " active" : "") + '" ' + attribute + '="' + escape(item.value) + '"' +
          (selected ? ' aria-current="page"' : "") + '>' + escape(item.label) + '</button>';
      }).join("") + '</nav>';
  }

  var presets = {
    diagnostics: { className: "diagnostics-tabs", buttonClass: "diagnostics-tab-button",
      label: "Разделы диагностики", dataAttribute: "data-diagnostics-tab", selected: "runtime-log",
      items: [{ label: "Ход работы", value: "runtime-log" }, { label: "История чата", value: "trajectory" },
        { label: "Проверки и данные", value: "overview" }, { label: "Протокол модели", value: "model-lab" }] },
    skill: { className: "skill-page-tabs", buttonClass: "skill-page-button",
      label: "Раздел навыка", dataAttribute: "data-skill-page", selected: "instruction",
      items: [{ label: "Инструкция", value: "instruction" }, { label: "Файлы", value: "files" }] },
    tool: { className: "tool-page-tabs", buttonClass: "tool-page-button",
      label: "Раздел инструмента", dataAttribute: "data-tool-page", selected: "main",
      items: [{ label: "Описание", value: "main" }, { label: "Параметры", value: "schema" },
        { label: "Код", value: "implementation" }, { label: "Проверка", value: "test" },
        { label: "Документация", value: "docs" }] },
    vba: { className: "vba-mode-toggle", buttonClass: "vba-mode-button",
      label: "Раздел VBA", dataAttribute: "data-vba-mode", selected: "edit",
      items: [{ label: "Код", value: "edit" }, { label: "Изменения", value: "diff" },
        { label: "Запуск", value: "run" }, { label: "История", value: "history" }] }
  };

  window.RNAssistantEditorTabs = Object.freeze({ render: render });
  document.querySelectorAll("[data-rna-editor-tabs]").forEach(function (mount) {
    var preset = presets[mount.getAttribute("data-rna-editor-tabs")];
    if (!preset) throw new Error("Unknown editor tabs preset");
    mount.outerHTML = render(preset.items, preset);
  });
}());
