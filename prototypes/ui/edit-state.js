(function () {
  "use strict";
  var P = window.Prototype, fields = {}, references = [], templateData;
  function templateState() { return JSON.stringify([P.state.mappings, P.state.parameters]); }
  function owner(key) { return /^tool(?:-|$)/.test(key) ? "tool" : key; }
  function current() {
    var s = P.state, route = s.route;
    if (route.startsWith("vba/")) return "vba:" + s.vbaModule;
    if (route.startsWith("materials/")) return P.materialKey();
    if (route.startsWith("projects/")) return "template";
    if (route === "library/prompt") return "prompt";
    return /library\/(tool|schema|implementation|test|docs|manage)$/.test(route) ? "tool" : "skill";
  }
  function fieldValue(name) { return P.state.fields[name] == null ? fields[name].saved : P.state.fields[name]; }
  function codeChanged(key) { return P.state.drafts[key] != null && P.state.drafts[key] !== P.state.saved[key]; }
  function fieldChanged(key) { return Object.keys(fields).some(function (name) { return fields[name].owner === key && fieldValue(name) !== fields[name].saved; }); }
  function pending(key) {
    key = owner(key || current());
    return Object.keys(P.state.drafts).some(function (k) { return owner(k) === key && codeChanged(k); }) || fieldChanged(key) ||
      (key === "skill" && JSON.stringify(references) !== JSON.stringify(P.state.references)) ||
      (key === "template" && templateData !== templateState());
  }
  function sync() {
    var s = P.state, key = current();
    Object.keys(s.drafts).forEach(function (k) { s.dirty[k] = codeChanged(k); });
    var owners = new Set([key, "template", "skill"]);
    Object.keys(s.drafts).forEach(function (k) { owners.add(owner(k)); });
    Object.keys(fields).forEach(function (name) { owners.add(fields[name].owner); });
    owners.forEach(function (k) { s.dirty[k] = pending(k); });
    document.querySelectorAll('[data-action="save-editor"], [data-action="discard-editor"]').forEach(function (button) { button.hidden = !pending(key); });
    document.querySelectorAll('[data-action="save-template"]').forEach(function (button) { button.hidden = !s.dirty.template; });
    document.querySelectorAll('[data-dirty-status]').forEach(function (label) {
      var editor = label.closest('.editor'), source = editor && editor.querySelector('textarea[data-draft]');
      label.closest('.editor-status').hidden = !source;
      if (!source) return;
      var sourceKey = source.dataset.draft;
      var format = /^vba:|tool-code/.test(sourceKey) ? "VBA" : /schema|template|json/.test(sourceKey) ? "JSON" : /\.js$/.test(sourceKey) ? "JavaScript" : /\.css$/.test(sourceKey) ? "CSS" : /\.html$/.test(sourceKey) ? "HTML" : "Markdown";
      label.textContent = format + (pending(label.dataset.dirtyStatus) ? " · Есть изменения" : "");
    });
    if (P.updateSettingsControls) P.updateSettingsControls();
  }
  P.edits = {
    init: function () { fields = {}; references = P.state.references.slice(); templateData = templateState(); },
    key: current,
    pending: pending,
    rememberCode: function (key, initial) { if (P.state.saved[key] == null) P.state.saved[key] = initial; },
    capture: function (root) {
      if (!/^library\/(all|skill|prompt|tool)$/.test(P.state.route)) return;
      root.querySelectorAll('[data-field]').forEach(function (el) {
        var name = el.dataset.field;
        if (!fields[name]) fields[name] = { owner: current(), saved: el.type === "checkbox" ? el.checked : el.value };
      });
    },
    sync: sync,
    commit: function (key) {
      key = owner(key || current()); var s = P.state;
      Object.keys(s.drafts).filter(function (k) { return owner(k) === key; }).forEach(function (k) { s.saved[k] = s.drafts[k]; s.dirty[k] = false; });
      Object.keys(fields).filter(function (name) { return fields[name].owner === key; }).forEach(function (name) { fields[name].saved = fieldValue(name); });
      if (key === "skill") references = s.references.slice();
      if (key === "template") templateData = templateState();
      s.dirty[key] = false;
    },
    discard: function (key) {
      key = owner(key || current()); var s = P.state;
      Object.keys(s.drafts).filter(function (k) { return owner(k) === key; }).forEach(function (k) { if (s.saved[k] != null) s.drafts[k] = s.saved[k]; else delete s.drafts[k]; s.dirty[k] = false; });
      Object.keys(fields).filter(function (name) { return fields[name].owner === key; }).forEach(function (name) { s.fields[name] = fields[name].saved; });
      if (key === "skill") s.references = references.slice();
      if (key === "template") { var data = JSON.parse(templateData); s.mappings = data[0]; s.parameters = data[1]; }
      s.dirty[key] = false;
    }
  };
}());
