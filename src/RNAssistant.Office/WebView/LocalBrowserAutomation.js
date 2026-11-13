(function () {
  "use strict";
  if (globalThis.__rnaLocalBrowser) return true;
  var observation, preparation, epoch = 0, geometryEpoch = 0;
  function capture() {
  // Never deliver input values, link destinations, HTML or page script state.
  var nodes = [], elements = [], destinations = [], visited = 0, characters = 0, truncated = false, deadline = performance.now() + 100;
  var privateSelector = 'script,style,template,noscript,iframe,object,embed,canvas,svg,[hidden],[aria-hidden="true"],[inert],[data-private],[data-sensitive],[contenteditable]:not([contenteditable="false"]),input[type="hidden"],input[type="password"]';
  var stop = {};
  function budget() { if (visited >= 3000 || performance.now() > deadline) { truncated = true; throw stop; } visited++; }
  function hidden(element) {
    if (element.matches(privateSelector)) return true;
    var style = getComputedStyle(element);
    return style.display === "none" || style.visibility === "hidden";
  }
  function smallText(element) {
    var value = "", walker = document.createTreeWalker(element, NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT, {
      acceptNode: function (node) {
        budget();
        return node.nodeType === 1 && (hidden(node) || /^(INPUT|TEXTAREA|SELECT)$/.test(node.tagName)) ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT;
      }
    });
    while (value.length < 160 && walker.nextNode()) if (walker.currentNode.nodeType === 3) value += walker.currentNode.nodeValue.slice(0, 160 - value.length);
    return value.replace(/\s+/g, " ").trim();
  }
  function name(element, role) {
    var label = element.getAttribute("aria-label");
    if (label) return label.slice(0, 160);
    if (element.labels && element.labels.length) {
      var source = element.labels[0];
      if (!source.closest(privateSelector)) return smallText(source);
    }
    return /^(textbox|checkbox|radio|combobox)$/.test(role) ? "" : smallText(element);
  }
  function add(role, value, element) {
    var maximum = role === 'text' ? 2000 : 160;
    if (value.length > maximum) truncated = true;
    value = value.slice(0, maximum).replace(/\s+/g, " ").trim();
    if (!value && role === "text") return;
    if (nodes.length >= 160 || characters + value.length > 16000) { truncated = true; throw stop; }
    var entry = { role: role, name: value, disabled: !!element && (element.matches(":disabled") || element.disabled === true || element.getAttribute("aria-disabled") === "true") };
    if (role === "checkbox" || role === "radio") {
      var checked = element.getAttribute("aria-checked");
      if (element.indeterminate || checked === "mixed") entry.checked = null;
      else if (checked === "true" || checked === "false") entry.checked = checked === "true";
      else if (typeof element.checked === "boolean") entry.checked = element.checked;
    }
    nodes.push(entry); elements.push(element);
    var rawHref = role === 'link' && element.tagName === 'A' ? element.getAttribute('href') : null;
    var href = rawHref != null && rawHref.length <= 8192 ? element.href : null;
    destinations.push(href && href.length <= 8192 ? href : null);
    characters += value.length;
  }
  function role(element) {
    var explicit = element.getAttribute("role");
    if (/^(button|link|heading|textbox|checkbox|radio|combobox)$/.test(explicit)) return explicit;
    var tag = element.tagName;
    if (tag === "BUTTON" || tag === "INPUT" && /^(button|submit|reset)$/.test(element.type)) return "button";
    if (tag === "A" && element.hasAttribute("href")) return "link";
    if (/^H[1-6]$/.test(tag)) return "heading";
    if (tag === "SELECT") return "combobox";
    if (tag === "TEXTAREA") return "textbox";
    if (tag === "INPUT") return /^(checkbox|radio)$/.test(element.type) ? element.type : "textbox";
    return null;
  }
  try {
    var root = document.body;
    if (!root || hidden(root)) return { nodes: nodes, elements: elements, destinations: destinations, visited: visited, truncated: false };
    var represented = new WeakSet(), walker = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT, {
      acceptNode: function (node) {
        budget();
        if (node.nodeType === 1 && hidden(node)) return NodeFilter.FILTER_REJECT;
        if (node.parentElement && represented.has(node.parentElement)) return NodeFilter.FILTER_REJECT;
        return NodeFilter.FILTER_ACCEPT;
      }
    });
    while (walker.nextNode()) {
      var node = walker.currentNode;
      if (node.nodeType === 3) add("text", node.nodeValue, null);
      else {
        var kind = role(node);
        if (kind) { add(kind, name(node, kind), node); represented.add(node); }
      }
    }
  } catch (error) { if (error !== stop) throw error; }
  return { nodes: nodes, elements: elements, destinations: destinations, visited: visited, truncated: truncated };
  }

  function fail(code) { throw new Error(code); }
  function fingerprint(view) { return JSON.stringify(view.nodes); }
  function point(element) {
    if (!element || !element.isConnected || element.disabled || element.closest('[data-private],[data-sensitive],iframe,[contenteditable]:not([contenteditable="false"])') || element.matches('input[type="password"],input[type="file"],input[type="hidden"]')) fail('browser_private_or_stale');
    var box = element.getBoundingClientRect(), x = box.x + box.width / 2, y = box.y + box.height / 2;
    if (!box.width || !box.height || x < 0 || y < 0 || x >= innerWidth || y >= innerHeight) fail('browser_target_outside_viewport');
    var top = document.elementFromPoint(x, y);
    if (top !== element && !element.contains(top)) fail('browser_target_occluded');
    return { x: x, y: y };
  }
  function find(view, name, roles) {
    var matches = view.nodes.map(function (node, i) { return { node: node, element: view.elements[i] }; })
      .filter(function (item) { return item.node.name === name && item.element && (!roles || roles.indexOf(item.node.role) >= 0); });
    if (matches.length !== 1) fail('browser_target_ambiguous');
    return matches[0].element;
  }
  var api = {
    invalidate: function () { epoch++; observation = preparation = null; },
    read: function (id) {
      var view = capture(); observation = { id: id, fingerprint: fingerprint(view), epoch: epoch }; preparation = null;
      return { observationId: id, title: document.title.slice(0, 160), nodes: view.nodes, visited: view.visited, truncated: view.truncated };
    },
    prepare: function (observationId, id, request) {
      if (!observation || observation.id !== observationId || observation.epoch !== epoch) fail('browser_observation_stale');
      var view = capture(); if (view.truncated || fingerprint(view) !== observation.fingerprint) fail('browser_document_changed');
      var roles = request.action === 'fill' ? ['textbox'] : request.action === 'select' ? ['combobox'] : request.action === 'check' ? ['checkbox', 'radio'] : request.action === 'navigate' ? ['link'] : null;
      var element = find(view, request.name, roles), position = point(element), destination = request.toName ? point(find(view, request.toName)) : null;
      if (request.action === 'fill' && element.readOnly) fail('browser_read_only');
      if (request.expectedText && view.nodes.some(function (node) { return node.name === request.expectedText; })) fail('browser_expected_text_present');
      var option = -1;
      if (request.action === 'select') {
        if (element.tagName !== 'SELECT' || element.multiple) fail('browser_select_unsupported');
        var choices = Array.from(element.options).map(function (value, index) { return { value: value, index: index }; }).filter(function (value) { return value.value.text.trim() === request.value && !value.value.disabled; });
        if (choices.length !== 1 || choices[0].index > 100) fail('browser_option_ambiguous'); option = choices[0].index;
      }
      preparation = { id: id, request: request, element: element, position: position, destination: destination, epoch: epoch,
        fingerprint: observation.fingerprint, beforeValue: element.value, checked: element.checked, scrollX: scrollX, scrollY: scrollY,
        elementScrollX: element.scrollLeft, elementScrollY: element.scrollTop, option: option };
      if (globalThis.__rnaActionView) __rnaActionView.show(id, element, request.action, true);
      return { x: position.x, y: position.y, toX: destination && destination.x, toY: destination && destination.y,
        option: option, href: request.action === 'navigate' ? element.href : null,
        satisfied: request.action === 'check' && element.checked === request.checked };
    },
    admit: function (id) {
      var value = preparation; if (!value || value.id !== id || value.epoch !== epoch) fail('browser_preparation_stale');
      var position = point(value.element);
      if (Math.abs(position.x - value.position.x) > 1 || Math.abs(position.y - value.position.y) > 1 || fingerprint(capture()) !== value.fingerprint) fail('browser_layout_changed');
      if (value.destination) { var next = point(find(capture(), value.request.toName)); if (Math.abs(next.x - value.destination.x) > 1 || Math.abs(next.y - value.destination.y) > 1) fail('browser_layout_changed'); }
      observation = null; return true;
    },
    dispatch: function (id) {
      api.admit(id); if (globalThis.__rnaActionView) __rnaActionView.dispatch(id, preparation.element, preparation.request.action); return true;
    },
    verify: function (id, finalRead) {
      var value = preparation;
      if (!value || value.id !== id || value.epoch !== epoch) return { effect: 'Unknown', code: 'browser_readback_unavailable' };
      var request = value.request, element = value.element, view = capture(), verified = false;
      if (!element.isConnected && !request.expectedText) { if (finalRead !== false) preparation = null; return { effect: 'Unknown', code: 'browser_readback_unavailable' }; }
      if (request.action === 'fill') verified = element.value === request.value;
      else if (request.action === 'check') verified = element.checked === request.checked;
      else if (request.action === 'select') verified = element.selectedIndex === value.option;
      else if (request.expectedText) verified = !view.truncated && view.nodes.some(function (node) { return node.name === request.expectedText; });
      else if (request.action === 'hover') verified = element.matches(':hover');
      else if (request.action === 'scroll') verified = scrollX !== value.scrollX || scrollY !== value.scrollY || element.scrollLeft !== value.elementScrollX || element.scrollTop !== value.elementScrollY;
      else verified = !view.truncated && fingerprint(view) !== value.fingerprint || element.value !== value.beforeValue || element.checked !== value.checked;
      if (verified || finalRead !== false) preparation = null;
      return { effect: verified ? 'VerifiedChange' : 'Unknown', code: verified ? 'browser_verified' : 'browser_effect_unknown' };
    },
    privacy: function () {
      var nodes = document.querySelectorAll('*'); if (nodes.length > 3000 || Array.from(nodes).some(function (node) { return !!node.shadowRoot; })) return { safe: false };
      if (document.querySelector('iframe,object,embed,canvas,[data-private],[data-sensitive],input,textarea,select,[contenteditable]:not([contenteditable="false"])')) return { safe: false };
      return { safe: true, width: innerWidth, height: innerHeight, scale: devicePixelRatio, epoch: geometryEpoch };
    }
  };
  var queued = false;
  function changed() {
    geometryEpoch++;
    if (queued || typeof globalThis.__rnaViewportChanged !== 'function') return;
    queued = true; requestAnimationFrame(function () { queued = false; __rnaViewportChanged('viewport'); });
  }
  function authored(node) { return !(node.nodeType === 1 ? node : node.parentElement)?.closest('[data-rna-browser-action-view]'); }
  new MutationObserver(function (changes) {
    if (changes.some(function (value) {
      return value.type === 'childList' ? Array.from(value.addedNodes).concat(Array.from(value.removedNodes)).some(authored) : authored(value.target);
    })) changed();
  }).observe(document.documentElement, { subtree: true, childList: true, characterData: true, attributes: true, attributeFilter: ['class', 'style', 'hidden', 'src', 'width', 'height'] });
  addEventListener('scroll', changed, true); addEventListener('resize', changed);
  Object.defineProperty(globalThis, '__rnaLocalBrowser', { value: api });
  return true;
}());
