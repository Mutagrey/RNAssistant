(function () {
  'use strict';
  var rail = document.querySelector('.section-rail');
  var card = document.createElement('div');
  var title = document.createElement('strong');
  var body = document.createElement('div');
  var renderers = Object.create(null), current = null, hideTimer = null, bound = false;
  card.className = 'rail-hover-card';
  card.hidden = true;
  title.className = 'rail-hover-title';
  title.id = 'railHoverTitle';
  card.setAttribute('aria-labelledby', title.id);
  body.className = 'rail-hover-body';
  card.append(title, body);
  document.body.appendChild(card);

  function cancelHide() { if (hideTimer) clearTimeout(hideTimer); hideTimer = null; }
  function hide() { cancelHide(); current = null; card.hidden = true; body.replaceChildren(); }
  function action(label, glyph, onClick) {
    var node = document.createElement('button'), caption = document.createElement('span');
    node.type = 'button'; node.innerHTML = glyph || ''; caption.textContent = label;
    node.appendChild(caption); node.setAttribute('aria-label', label); node.addEventListener('click', onClick);
    return node;
  }
  function scheduleHide() {
    cancelHide();
    hideTimer = setTimeout(function () {
      if (!card.contains(document.activeElement) && current !== document.activeElement) hide();
    }, 180);
  }
  function position() {
    if (!current || card.hidden) return;
    var anchor = current.getBoundingClientRect();
    var width = card.getBoundingClientRect().width, height = card.getBoundingClientRect().height;
    card.style.left = Math.max(4, Math.min(innerWidth - width - 4, anchor.right + 6)) + 'px';
    card.style.top = Math.max(4, Math.min(innerHeight - height - 4, anchor.top + (anchor.height - height) / 2)) + 'px';
  }
  function show(button) {
    if (!button || !rail.contains(button)) return;
    cancelHide();
    if (current === button && !card.hidden) return;
    current = button;
    card.dataset.kind = button.dataset.railPopover || '';
    title.textContent = button.dataset.railLabel || button.getAttribute('aria-label') || '';
    body.replaceChildren();
    var renderer = renderers[button.dataset.railPopover];
    card.setAttribute('role', renderer ? 'group' : 'tooltip');
    card.hidden = false;
    position();
    if (renderer) Promise.resolve(renderer(button, body, function () { return current === button; })).then(position, function (error) {
      if (current !== button) return;
      body.textContent = error.message;
      position();
    });
  }
  function buttonFrom(event) { return event.target.closest && event.target.closest('.section-rail .rail-button'); }
  function bind() {
    if (bound || !rail) return;
    bound = true;
    rail.querySelectorAll('.rail-button[title]').forEach(function (button) {
      button.dataset.railLabel = button.dataset.railLabel || button.getAttribute('aria-label') || button.title;
      button.removeAttribute('title');
    });
    rail.addEventListener('pointerover', function (event) {
      var button = buttonFrom(event);
      if (button && !button.contains(event.relatedTarget)) show(button);
    });
    rail.addEventListener('pointerout', function (event) {
      if (current && current.contains(event.target) && !current.contains(event.relatedTarget)) scheduleHide();
    });
    rail.addEventListener('focusin', function (event) { show(buttonFrom(event)); });
    rail.addEventListener('focusout', scheduleHide);
    rail.addEventListener('keydown', function (event) {
      if (event.key !== 'Tab' || event.shiftKey || buttonFrom(event) !== current || card.hidden) return;
      var first = body.querySelector('button:not([disabled])');
      if (first) { event.preventDefault(); first.focus(); }
    });
    rail.addEventListener('click', function (event) { if (buttonFrom(event)) hide(); });
    card.addEventListener('pointerenter', cancelHide);
    card.addEventListener('pointerleave', scheduleHide);
    card.addEventListener('focusin', cancelHide);
    card.addEventListener('focusout', scheduleHide);
    card.addEventListener('keydown', function (event) {
      if (event.key !== 'Tab' || !current) return;
      var actions = body.querySelectorAll('button:not([disabled])');
      if (!actions.length) return;
      if (event.shiftKey && document.activeElement === actions[0]) {
        event.preventDefault(); current.focus();
      } else if (!event.shiftKey && document.activeElement === actions[actions.length - 1]) {
        var buttons = Array.from(rail.querySelectorAll('.rail-button'));
        var next = buttons[buttons.indexOf(current) + 1];
        if (next) { event.preventDefault(); hide(); next.focus(); }
      }
    });
    document.addEventListener('keydown', function (event) {
      if (event.key === 'Escape' && !card.hidden) { var anchor = current; hide(); if (anchor) anchor.focus(); }
    });
    document.addEventListener('pointerdown', function (event) {
      if (!card.hidden && !card.contains(event.target) && !rail.contains(event.target)) hide();
    });
    window.addEventListener('resize', position);
    rail.addEventListener('scroll', position, { passive: true });
  }
  window.RNAssistantRailPopovers = { bind: bind, hide: hide, position: position, action: action,
    register: function (kind, renderer) { renderers[kind] = renderer; } };
}());
