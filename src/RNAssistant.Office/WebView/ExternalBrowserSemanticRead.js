(function () {
  "use strict";
  // This object exists only in the isolated world; page script cannot obtain its bindings.
  if (globalThis.__rnaBrowser) return globalThis.__rnaBrowser.read();
  var observation = null, prepared = null, interaction = 0, blocked = false;
  ['pointerdown', 'keydown', 'input', 'change'].forEach(function (type) {
    document.addEventListener(type, function (event) { if (event.isTrusted) interaction++; }, true);
  });
  function capture() {
  // Never deliver input values, URLs, HTML or page script state.
  var nodes = [], elements = [], visited = 0, characters = 0, truncated = false, deadline = performance.now() + 100;
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
    value = value.slice(0, 160).replace(/\s+/g, " ").trim();
    if (!value && role === "text") return;
    if (nodes.length >= 160 || characters + value.length > 16000) { truncated = true; throw stop; }
    var entry = { role: role, name: value, disabled: !!element && (element.matches(":disabled") || element.disabled === true || element.getAttribute("aria-disabled") === "true") };
    if (role === "checkbox" || role === "radio") {
      var checked = element.getAttribute("aria-checked");
      if (element.indeterminate || checked === "mixed") entry.checked = null;
      else if (checked === "true" || checked === "false") entry.checked = checked === "true";
      else if (typeof element.checked === "boolean") entry.checked = element.checked;
    }
    nodes.push(entry); elements.push(element); characters += value.length;
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
    if (!root || hidden(root)) return { nodes: nodes, elements: elements, visited: visited, truncated: false };
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
  return { nodes: nodes, elements: elements, visited: visited, truncated: truncated };
  }
  function fail(code) { throw new Error(code); }
  function matches(view, role, name) {
    return view.nodes.reduce(function (found, node, index) {
      if (node.role === role && node.name === name) found.push(index);
      return found;
    }, []);
  }
  function check(request) {
    if (location.href !== 'https://rna-browser-probe.invalid/index.html' || window.top !== window) fail('browser_probe_only');
    if (blocked) fail('browser_effect_unknown');
    if (!observation || observation.used || observation.interaction !== interaction) fail('browser_observation_stale');
    var role = request.action === 'fill' ? 'textbox' : request.action === 'click' ? 'button' : null;
    if (!role || !request.name || request.name.length > 160 ||
        role === 'textbox' && (typeof request.value !== 'string' || request.value.length > 1024) ||
        role === 'button' && (!request.expectedText || request.expectedText.length > 160)) fail('browser_action_invalid');
    var current = capture(), previous = matches(observation, role, request.name), found = matches(current, role, request.name);
    if (observation.truncated || current.truncated) fail('browser_observation_incomplete');
    if (previous.length !== 1 || found.length !== 1) fail('browser_element_ambiguous');
    var element = current.elements[found[0]];
    if (!element || element !== observation.elements[previous[0]] || !element.isConnected) fail('browser_element_stale');
    if (current.nodes[found[0]].disabled || role === 'textbox' && (element.readOnly ||
        element.tagName !== 'TEXTAREA' && !(element.tagName === 'INPUT' && /^(text|search|email|url|tel)$/.test(element.type)))) fail('browser_element_not_interactable');
    var rect = element.getBoundingClientRect(), x = rect.x + rect.width / 2, y = rect.y + rect.height / 2;
    var top = document.elementFromPoint(x, y);
    if (rect.width <= 0 || rect.height <= 0 || !top || !(top === element || element.contains(top))) fail('browser_element_not_interactable');
    if (role === 'button' && current.nodes.some(function (node) { return node.name === request.expectedText; })) fail('browser_verification_already_present');
    return element;
  }
  var api = {
    read: function () {
      observation = capture(); observation.observationId = Array.from(crypto.getRandomValues(new Uint8Array(16)),
        function (value) { return value.toString(16).padStart(2, '0'); }).join('');
      observation.interaction = interaction; observation.used = false; prepared = null;
      return { observationId: observation.observationId, nodes: observation.nodes, visited: observation.visited, truncated: observation.truncated };
    },
    prepare: function (id, preparationId, request) {
      try {
        if (!observation || observation.observationId !== id) fail('browser_observation_stale');
        var element = check(request);
        prepared = { id: preparationId, request: request, element: element,
          before: request.action === 'fill' ? element.value : null };
        return { effect: 'VerifiedNoChange', code: 'browser_prepared' };
      } catch (error) { prepared = null; return { effect: 'Rejected', code: error.message }; }
    },
    act: function (id) {
      var plan = prepared; prepared = null;
      try {
        if (!plan || plan.id !== id) fail('browser_preparation_stale');
        var element = check(plan.request);
        if (element !== plan.element || plan.request.action === 'fill' && element.value !== plan.before) fail('browser_element_stale');
      } catch (error) { return { effect: 'Rejected', code: error.message }; }
      observation.used = true;
      if (plan.request.action === 'fill' && element.value === plan.request.value)
        return { effect: 'VerifiedNoChange', code: 'browser_verified' };
      blocked = true; // Any interruption from here leaves effects unknown and mutations disabled.
      try {
        if (plan.request.action === 'fill') {
          var prototype = element.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
          Object.getOwnPropertyDescriptor(prototype, 'value').set.call(element, plan.request.value);
          element.dispatchEvent(new Event('input', { bubbles: true }));
          element.dispatchEvent(new Event('change', { bubbles: true }));
          if (!element.isConnected || element.value !== plan.request.value) fail('browser_verification_failed');
        } else {
          element.click();
          var after = capture();
          if (after.truncated || after.nodes.filter(function (node) { return node.name === plan.request.expectedText; }).length !== 1)
            fail('browser_verification_failed');
        }
        blocked = false;
        return { effect: 'VerifiedChange', code: 'browser_verified' };
      } catch (error) { return { effect: 'Unknown', code: 'browser_verification_failed' }; }
    }
  };
  Object.defineProperty(globalThis, '__rnaBrowser', { value: api });
  return api.read();
}())
