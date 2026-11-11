(function () {
  "use strict";
  // Runs in an isolated world. Never read input values, URLs, HTML or page script state.
  var nodes = [], visited = 0, characters = 0, truncated = false, deadline = performance.now() + 100;
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
    var entry = { role: role, name: value, disabled: !!element && (element.disabled === true || element.getAttribute("aria-disabled") === "true") };
    if (role === "checkbox" || role === "radio") {
      var checked = element.getAttribute("aria-checked");
      if (element.indeterminate || checked === "mixed") entry.checked = null;
      else if (checked === "true" || checked === "false") entry.checked = checked === "true";
      else if (typeof element.checked === "boolean") entry.checked = element.checked;
    }
    nodes.push(entry); characters += value.length;
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
    if (!root || hidden(root)) return { nodes: nodes, visited: visited, truncated: false };
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
  return { nodes: nodes, visited: visited, truncated: truncated };
}())
