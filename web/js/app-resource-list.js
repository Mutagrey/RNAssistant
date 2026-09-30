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
    var icon = document.createElement("span");
    icon.className = "tool-list-icon";
    icon.textContent = options.icon;
    top.appendChild(icon);
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
