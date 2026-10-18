(function () {
  "use strict";

  function escape(value) {
    return String(value == null ? "" : value).replace(/[&<>"']/g, function (character) {
      return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[character];
    });
  }

  function render(model) {
    model = model || {};
    return '<div class="form-grid tool-form skill-editor-fields rna-skill-fields" id="skillForm">' +
      '<label>ID навыка<input id="skillIdInput" data-field="skillId" value="' + escape(model.id || "") + '" placeholder="common.review_contract"></label>' +
      '<label>Приложение<select id="skillHostInput" data-field="skillHost">' +
      ["Common", "Excel", "Word", "PowerPoint", "Outlook"].map(function (host) {
        return '<option' + (host === (model.host || "Common") ? ' selected' : '') + '>' + host + '</option>';
      }).join("") + '</select></label>' +
      '<label class="wide">Описание навыка<textarea id="skillDescriptionInput" data-field="skillDescription" rows="1" placeholder="Когда агенту использовать этот навык">' + escape(model.description || "") + '</textarea></label>' +
      '<label class="checkline skill-enabled-field"><input type="checkbox" id="skillEnabledInput" data-field="skillEnabled"' +
      (model.enabled ? ' checked' : '') + '> <span>Доступен агенту</span></label></div>';
  }

  window.RNAssistantSkillFieldsView = { render: render };
  var productMount = document.querySelector('[data-rna-skill-form]');
  if (productMount) productMount.outerHTML = render();
}());
