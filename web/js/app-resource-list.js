function createResourceIcon(kind) {
  var paths = {
    prompt: '<path d="M7 4h8l3 3v13H7z"/><path d="M15 4v3h3M10 11h5M10 15h5"/>',
    skill: '<path d="m12 3 1.8 6.2L20 11l-6.2 1.8L12 19l-1.8-6.2L4 11l6.2-1.8z"/><path d="m19 18 .5 1.5L21 20l-1.5.5L19 22l-.5-1.5L17 20l1.5-.5z"/>',
    tool: '<path d="M14 5a5 5 0 0 0-6 6L3 16l5 5 5-5a5 5 0 0 0 6-6l-3 3-3-3z"/>',
    host: '<rect x="3" y="5" width="18" height="14" rx="2"/><path d="M3 10h18"/>',
    section: '<path d="M3 7a2 2 0 0 1 2-2h5l2 2h7a2 2 0 0 1 2 2v10H3z"/>'
  };
  var icon = document.createElement("span");
  icon.className = "resource-tree-icon resource-tree-icon-" + (paths[kind] ? kind : "section");
  icon.setAttribute("aria-hidden", "true");
  icon.innerHTML = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round">' + (paths[kind] || paths.section) + '</svg>';
  return icon;
}

function createResourceListItem(options) {
  options = options || {};

  var hasMeta = !!options.meta;
  var item = document.createElement("button");
  item.type = "button";
  item.className = "tool-list-item"
    + (options.active ? " active" : "")
    + (options.compact ? " is-compact" : "")
    + (options.icon ? " has-icon" : "")
    + (hasMeta ? " has-meta" : "")
    + (typeof options.enabled === "boolean" ? " has-badge" : "");
  item.style.setProperty("--tree-depth", String(Math.max(0, Number(options.depth || 0))));
  item.title = options.tooltip || [options.title, options.meta].filter(function (part) { return !!part; }).join(" - ");

  var top = document.createElement("div");
  top.className = "tool-list-top";

  if (options.icon) {
    top.appendChild(createResourceIcon(options.icon));
  }

  var title = document.createElement("div");
  title.className = "tool-list-title";
  title.textContent = options.title || "";
  title.title = options.title || "";
  top.appendChild(title);

  if (typeof options.enabled === "boolean") {
    var badge = document.createElement("div");
    var enabledText = options.enabled ? "Включено" : "Отключено";
    badge.className = "tool-list-badge " + (options.enabled ? "is-enabled" : "is-disabled");
    badge.title = enabledText;
    badge.setAttribute("aria-label", enabledText);
    top.appendChild(badge);
  }

  var meta = document.createElement("div");
  meta.className = "tool-list-meta";
  meta.textContent = options.meta || "";
  meta.title = options.meta || "";

  var description = document.createElement("div");
  description.className = "tool-list-desc";
  description.textContent = options.description || "";

  item.appendChild(top);
  item.appendChild(meta);
  if (!options.compact && options.description) {
    item.appendChild(description);
  }

  if (typeof options.onClick === "function") {
    item.addEventListener("click", options.onClick);
  }

  return item;
}

function createResourceGroup(options) {
  options = options || {};
  var key = options.key || options.title || "group";
  var collapsed = state.collapsedResourceGroups && state.collapsedResourceGroups[key] === true;
  var details = document.createElement("details");
  details.className = "resource-tree-group";
  details.open = !collapsed;

  var summary = document.createElement("summary");
  summary.className = "resource-tree-group-title";
  if (options.icon) summary.appendChild(createResourceIcon(options.icon));
  var title = document.createElement("span");
  title.textContent = options.title || "";
  title.title = options.title || "";
  summary.appendChild(title);
  if (options.count !== undefined) {
    var count = document.createElement("em");
    count.textContent = String(options.count);
    summary.appendChild(count);
  }
  details.appendChild(summary);
  var children = document.createElement("div");
  children.className = "resource-tree-group-children";
  details.appendChild(children);
  details.treeChildren = children;
  details.addEventListener("toggle", function () {
    state.collapsedResourceGroups = state.collapsedResourceGroups || {};
    state.collapsedResourceGroups[key] = !details.open;
  });
  return details;
}

function createResourceEmptyState(text) {
  var node = document.createElement("div");
  node.className = "tool-list-empty";
  node.textContent = text;
  return node;
}
