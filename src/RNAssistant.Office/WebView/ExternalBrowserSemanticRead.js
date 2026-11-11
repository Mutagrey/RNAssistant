(function () {
  "use strict";
  // This object exists only in the isolated world; page script cannot obtain its bindings.
  if (globalThis.__rnaBrowser) return globalThis.__rnaBrowser.read();
  var observation = null, prepared = null, blocked = false, navigating = false;
  function visual(method) {
    // Decoration cannot confer authority or change a verified operation's outcome.
    try {
      var view = globalThis.__rnaBrowserVisual;
      return view && view[method].apply(view, Array.prototype.slice.call(arguments, 1));
    } catch (error) { return false; }
  }
  function control() { return globalThis.__rnaBrowserControl ? globalThis.__rnaBrowserControl.snapshot() : { manual: true, epoch: 0 }; }
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
  function matches(view, role, name) {
    return view.nodes.reduce(function (found, node, index) {
      if (node.role === role && node.name === name) found.push(index);
      return found;
    }, []);
  }
  function check(request) {
    if (window.top !== window || !/^https?:$/.test(location.protocol)) fail('browser_action_invalid');
    if (blocked) fail('browser_effect_unknown');
    if (control().manual) fail('browser_user_control');
    if (!observation || observation.used || observation.interaction !== control().epoch) fail('browser_observation_stale');
    var role = request.action === 'fill' ? 'textbox' : request.action === 'click' ? 'button' : request.action === 'follow' ? 'link' : null;
    if (!role || !request.name || request.name.length > 160 ||
        role === 'textbox' && (typeof request.value !== 'string' || request.value.length > 1024) ||
        role === 'button' && (!request.expectedText || request.expectedText.length > 160)) fail('browser_action_invalid');
    var current = capture(), previous = matches(observation, role, request.name), found = matches(current, role, request.name);
    if (observation.truncated || current.truncated) fail('browser_observation_incomplete');
    if (previous.length !== 1 || found.length !== 1) fail('browser_element_ambiguous');
    var element = current.elements[found[0]];
    if (!element || element !== observation.elements[previous[0]] || !element.isConnected) fail('browser_element_stale');
    if (role === 'link') {
      if (element.tagName !== 'A' || !current.destinations[found[0]] || element.hasAttribute('download')) fail('browser_element_not_interactable');
      if (current.destinations[found[0]] !== observation.destinations[previous[0]]) fail('browser_element_stale');
      var destination = new URL(element.href);
      if (!/^https?:$/.test(destination.protocol) || destination.username || destination.password) fail('browser_action_invalid');
    }
    if (current.nodes[found[0]].disabled || role === 'textbox' && (element.readOnly ||
        element.tagName !== 'TEXTAREA' && !(element.tagName === 'INPUT' && /^(text|search|email|url|tel)$/.test(element.type)))) fail('browser_element_not_interactable');
    var rect = element.getBoundingClientRect(), x = rect.x + rect.width / 2, y = rect.y + rect.height / 2;
    var top = document.elementFromPoint(x, y);
    if (rect.width <= 0 || rect.height <= 0 || !top || !(top === element || element.contains(top))) fail('browser_element_not_interactable');
    if (role === 'button' && current.nodes.some(function (node) { return node.name === request.expectedText; })) fail('browser_verification_already_present');
    return element;
  }
  var api = {
    invalidate: function () { observation = null; prepared = null; },
    read: function () {
      observation = capture(); observation.observationId = Array.from(crypto.getRandomValues(new Uint8Array(16)),
        function (value) { return value.toString(16).padStart(2, '0'); }).join('');
      observation.interaction = control().epoch; observation.used = false; prepared = null;
      return { observationId: observation.observationId, title: document.title.slice(0, 160),
        nodes: observation.nodes, visited: observation.visited, truncated: observation.truncated };
    },
    prepare: function (id, preparationId, request) {
      try {
        if (control().manual) fail('browser_user_control');
        if (!observation || observation.observationId !== id) fail('browser_observation_stale');
        var element = check(request);
        prepared = { id: preparationId, request: request, element: element,
          before: request.action === 'fill' ? element.value : null };
        return { effect: 'VerifiedNoChange', code: 'browser_prepared' };
      } catch (error) { prepared = null; return { effect: 'Rejected', code: error.message }; }
    },
    prepareLink: function (id, preparationId, name) {
      var result = api.prepare(id, preparationId, { action: 'follow', name: name });
      if (result.effect !== 'VerifiedNoChange') return { prepared: false, code: result.code };
      prepared.url = prepared.element.href;
      return { prepared: true, url: prepared.url };
    },
    showAction: function (id, visible) {
      try {
        if (!prepared || prepared.id !== id) fail('browser_preparation_stale');
        var element = check(prepared.request);
        return visual('show', id, element, prepared.request.action === 'follow' ? 'click' : prepared.request.action, visible) || 0;
      } catch (error) { visual('hide'); return 0; }
    },
    hideAction: function () { visual('hide'); return true; },
    completeAction: function (id, effect) { visual('finish', id, effect); return true; },
    follow: function (id, approvedUrl) {
      try {
        if (!prepared || prepared.id !== id || prepared.request.action !== 'follow') fail('browser_preparation_stale');
        var element = check(prepared.request);
        if (element !== prepared.element || element.href !== prepared.url || element.href !== approvedUrl) fail('browser_element_stale');
        visual('dispatch', id, element, 'click');
        return api.navigate(observation.observationId, approvedUrl);
      } catch (error) { return { started: false, code: error.message }; }
    },
    navigate: function (id, url) {
      var attempted = false;
      try {
        if (control().manual) fail('browser_user_control');
        if (blocked) fail('browser_effect_unknown');
        if (!observation || observation.used || observation.observationId !== id || observation.interaction !== control().epoch)
          fail('browser_observation_stale');
        var target = new URL(url);
        if (!/^https?:$/.test(target.protocol) || target.username || target.password) fail('browser_action_invalid');
        observation.used = true; prepared = null;
        if (target.href === location.href) return { started: false, unchanged: true, code: 'browser_verified' };
        blocked = true; navigating = true; attempted = true;
        location.assign(target.href);
        return { started: true };
      } catch (error) { return { started: attempted, code: error.message }; }
    },
    completeNavigation: function () {
      if (!navigating) return false;
      navigating = false; blocked = false; observation = null; prepared = null; return true;
    },
    act: async function (id) {
      var plan = prepared; prepared = null;
      function result(effect, code) { return { effect: effect, code: code }; }
      try {
        if (control().manual) fail('browser_user_control');
        if (!plan || plan.id !== id) fail('browser_preparation_stale');
        var element = check(plan.request);
        if (element !== plan.element || plan.request.action === 'fill' && element.value !== plan.before) fail('browser_element_stale');
      } catch (error) { return result('Rejected', error.message); }
      observation.used = true;
      if (plan.request.action === 'fill' && element.value === plan.request.value)
        return result('VerifiedNoChange', 'browser_verified');
      visual('dispatch', id, element, plan.request.action);
      var interaction = control().epoch;
      blocked = true; // Any interruption from here leaves effects unknown and mutations disabled.
      try {
        if (plan.request.action === 'fill') {
          var prototype = element.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
          Object.getOwnPropertyDescriptor(prototype, 'value').set.call(element, plan.request.value);
          element.dispatchEvent(new Event('input', { bubbles: true }));
          element.dispatchEvent(new Event('change', { bubbles: true }));
          await Promise.resolve();
          if (!element.isConnected || element.value !== plan.request.value) fail('browser_verification_failed');
        } else {
          element.click();
          var deadline = performance.now() + 1000;
          while (true) {
            if (control().manual || control().epoch !== interaction) fail('browser_user_control');
            var after = capture();
            if (!after.truncated && after.nodes.filter(function (node) { return node.name === plan.request.expectedText; }).length === 1) break;
            if (performance.now() >= deadline) fail('browser_verification_failed');
            await new Promise(function (resolve) { setTimeout(resolve, 50); });
          }
        }
        if (control().manual || control().epoch !== interaction) fail('browser_user_control');
        blocked = false;
        return result('VerifiedChange', 'browser_verified');
      } catch (error) { return result('Unknown', 'browser_verification_failed'); }
    }
  };
  Object.defineProperty(globalThis, '__rnaBrowser', { value: api });
  return api.read();
}())
