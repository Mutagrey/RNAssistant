(function () {
  'use strict';
  var rail = document.querySelector('.section-rail');
  var card = document.createElement('div');
  var title = document.createElement('strong');
  var body = document.createElement('div');
  var renderers = Object.create(null), current = null, hideTimer = null, showTimer = null, bound = false;
  var generation = 0, restoringFocus = false, offset = 6, inset = 8;
  card.className = 'rail-hover-card';
  card.id = 'railHoverCard';
  card.hidden = true;
  title.className = 'rail-hover-title';
  title.id = 'railHoverTitle';
  card.setAttribute('aria-labelledby', title.id);
  body.className = 'rail-hover-body';
  card.append(title, body);
  document.body.appendChild(card);

  function cancelHide() { if (hideTimer) clearTimeout(hideTimer); hideTimer = null; }
  function hide() {
    cancelHide(); clearTimeout(showTimer); ++generation;
    if (current) { current.removeAttribute('aria-describedby'); current.removeAttribute('aria-controls'); current.removeAttribute('aria-expanded'); }
    current = null; card.hidden = true; body.replaceChildren();
  }
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
    card.style.left = Math.max(inset, Math.min(innerWidth - width - inset, anchor.right + offset)) + 'px';
    card.style.top = Math.max(inset, Math.min(innerHeight - height - inset, anchor.top + (anchor.height - height) / 2)) + 'px';
  }
  function show(button) {
    if (!button || !rail.contains(button)) return;
    cancelHide(); clearTimeout(showTimer);
    if (current === button && !card.hidden) return;
    hide();
    current = button;
    var version = ++generation;
    card.dataset.kind = button.dataset.railPopover || '';
    title.textContent = button.dataset.railLabel || button.getAttribute('aria-label') || '';
    title.removeAttribute('title');
    body.replaceChildren();
    var renderer = renderers[button.dataset.railPopover];
    card.setAttribute('role', renderer ? 'group' : 'tooltip');
    if (renderer) { button.setAttribute('aria-controls', card.id); button.setAttribute('aria-expanded', 'true'); }
    else button.setAttribute('aria-describedby', card.id);
    card.hidden = false;
    position();
    if (renderer) Promise.resolve(renderer(button, body, function () { return generation === version; }, title)).then(function () {
      if (generation === version) position();
    }, function (error) {
      if (generation !== version) return;
      body.textContent = error.message;
      position();
    });
  }
  function buttonFrom(event) { return event.target.closest && event.target.closest('.section-rail .rail-button'); }
  function bind() {
    if (bound || !rail) return;
    bound = true;
    var tokens = getComputedStyle(document.documentElement);
    offset = Number.parseFloat(tokens.getPropertyValue('--menu-offset'));
    inset = Number.parseFloat(tokens.getPropertyValue('--space-2'));
    rail.querySelectorAll('.rail-button[title]').forEach(function (button) {
      button.dataset.railLabel = button.dataset.railLabel || button.getAttribute('aria-label') || button.title;
      button.removeAttribute('title');
    });
    rail.addEventListener('pointerover', function (event) {
      if (event.pointerType === 'touch') return;
      var button = buttonFrom(event);
      if (button && !button.contains(event.relatedTarget)) {
        clearTimeout(showTimer); cancelHide();
        if (current && current !== button) hide();
        showTimer = setTimeout(function () { show(button); }, 160);
      }
    });
    rail.addEventListener('pointerout', function (event) {
      var button = buttonFrom(event);
      if (button && !button.contains(event.relatedTarget)) clearTimeout(showTimer);
      if (current && current.contains(event.target) && !current.contains(event.relatedTarget)) scheduleHide();
    });
    rail.addEventListener('focusin', function (event) { if (!restoringFocus) show(buttonFrom(event)); });
    rail.addEventListener('focusout', scheduleHide);
    rail.addEventListener('keydown', function (event) {
      if (!['Tab', 'ArrowRight', 'ArrowDown'].includes(event.key) || event.shiftKey || buttonFrom(event) !== current || card.hidden) return;
      var first = body.querySelector('button:not([disabled])');
      if (first) { event.preventDefault(); first.focus(); }
    });
    rail.addEventListener('click', function (event) { if (buttonFrom(event)) hide(); });
    card.addEventListener('pointerenter', cancelHide);
    card.addEventListener('pointerleave', scheduleHide);
    card.addEventListener('focusin', cancelHide);
    card.addEventListener('focusout', scheduleHide);
    card.addEventListener('keydown', function (event) {
      if (!current) return;
      var actions = Array.from(body.querySelectorAll('button:not([disabled])'));
      if (!actions.length) return;
      if (['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) {
        var index = actions.indexOf(document.activeElement);
        index = event.key === 'Home' ? 0 : event.key === 'End' ? actions.length - 1 : (index + (event.key === 'ArrowUp' ? -1 : 1) + actions.length) % actions.length;
        event.preventDefault(); actions[index].focus(); return;
      }
      if (event.key !== 'Tab') return;
      if (event.shiftKey && document.activeElement === actions[0]) {
        event.preventDefault(); current.focus();
      } else if (!event.shiftKey && document.activeElement === actions[actions.length - 1]) {
        var buttons = Array.from(rail.querySelectorAll('.rail-button'));
        var next = buttons[buttons.indexOf(current) + 1];
        if (next) { event.preventDefault(); hide(); next.focus(); }
        else hide();
      }
    });
    document.addEventListener('keydown', function (event) {
      if (event.key === 'Escape' && !card.hidden) {
        event.preventDefault(); var anchor = current; hide();
        restoringFocus = true; if (anchor) anchor.focus(); restoringFocus = false;
      }
    });
    document.addEventListener('pointerdown', function (event) {
      if (!card.hidden && !card.contains(event.target) && !rail.contains(event.target)) hide();
    });
    window.addEventListener('resize', position);
    rail.addEventListener('scroll', position, { passive: true });
    window.addEventListener('pagehide', hide);
  }
  window.RNAssistantRailPopovers = { bind: bind, hide: hide, position: position, action: action,
    register: function (kind, renderer) { renderers[kind] = renderer; } };
}());
