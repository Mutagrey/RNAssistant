(function () {
  "use strict";
  const escape = value => String(value == null ? "" : value).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  const icon = name => name === 'splitRight' || name === 'splitBelow' ? '<span class="demo-icon codicon-split ' + (name === 'splitRight' ? 'split-right' : 'split-below') + '" aria-hidden="true"></span>' : window.RNAssistantIcons.render(name, 'demo-icon');
  const button = (label, attrs, symbol, kind) => '<button type="button" class="button ' + (kind || "") + '" ' + attrs + '>' + (symbol ? icon(symbol) : "") + escape(label) + '</button>';
  const iconButton = (label, attrs, symbol) => '<button type="button" class="icon-button" aria-label="' + escape(label) + '" title="' + escape(label) + '" ' + attrs + '>' + icon(symbol) + '</button>';
  const markdown = text => DOMPurify.sanitize(marked.parse(text));
  const hydrate = (root = document) => root.querySelectorAll('[data-icon]').forEach(el => { el.innerHTML = icon(el.dataset.icon); });
  window.RnaDemo = { escape, icon, button, iconButton, markdown, hydrate, fixtures: window.PrototypeFixtures };
  hydrate();
}());
