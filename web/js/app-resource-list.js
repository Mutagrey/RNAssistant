function createResourceListItem(options) {
  options = options || {};

  var item = document.createElement("button");
  item.type = "button";
  item.className = "tool-list-item" + (options.active ? " active" : "");

  var top = document.createElement("div");
  top.className = "tool-list-top";

  var title = document.createElement("div");
  title.className = "tool-list-title";
  title.textContent = options.title || "";
  top.appendChild(title);

  var badge = document.createElement("div");
  badge.className = "tool-list-badge " + (options.enabled === false ? "is-disabled" : "is-enabled");
  badge.textContent = options.enabled === false ? "выкл" : "вкл";
  top.appendChild(badge);

  var meta = document.createElement("div");
  meta.className = "tool-list-meta";
  meta.textContent = options.meta || "";

  var description = document.createElement("div");
  description.className = "tool-list-desc";
  description.textContent = options.description || "";

  item.appendChild(top);
  item.appendChild(meta);
  item.appendChild(description);

  if (typeof options.onClick === "function") {
    item.addEventListener("click", options.onClick);
  }

  return item;
}

function createResourceEmptyState(text) {
  var node = document.createElement("div");
  node.className = "tool-list-empty";
  node.textContent = text;
  return node;
}

function renderResourceList(options) {
  options = options || {};

  var list = $(options.listId);
  if (!list) {
    return;
  }

  var search = $(options.searchInputId);
  var query = ((search && search.value) || "").trim().toLowerCase();
  var items = options.items || [];
  list.innerHTML = "";

  if (!items.length) {
    options.setSelectedIndex(-1);
    list.appendChild(createResourceEmptyState(options.emptyText || "Список пуст."));
    options.renderEditor();
    return;
  }

  var selectedIndex = options.getSelectedIndex();
  if (selectedIndex < 0 || selectedIndex >= items.length) {
    selectedIndex = 0;
    options.setSelectedIndex(selectedIndex);
  }

  var rendered = 0;
  items.forEach(function (item, index) {
    if (query && !options.matches(item, query)) {
      return;
    }

    list.appendChild(createResourceListItem({
      title: options.title(item),
      enabled: options.enabled(item),
      active: index === selectedIndex,
      meta: options.meta(item),
      description: options.description(item),
      onClick: function () {
        options.syncEditor();
        options.setSelectedIndex(index);
        options.renderList();
      }
    }));
    rendered += 1;
  });

  if (!rendered) {
    list.appendChild(createResourceEmptyState(options.noResultsText || "Ничего не найдено."));
  }

  options.renderEditor();
}
