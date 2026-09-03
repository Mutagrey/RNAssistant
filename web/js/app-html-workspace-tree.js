(function () {
  "use strict";

  function matchesText(text, query) {
    return !query || String(text || "").toLowerCase().indexOf(query) >= 0;
  }

  function firstLine(value) {
    return String(value || "").split(/\r?\n/)[0].trim().slice(0, 140);
  }

  function bindingValue(binding, pascal, camel, fallback) {
    binding = binding || {};
    return binding[camel] !== undefined ? binding[camel] : (binding[pascal] !== undefined ? binding[pascal] : fallback);
  }

  function isScriptFile(file) {
    return file.kind === "script" || file.kind === "js" || /\.js$/i.test(file.path);
  }

  function isStyleFile(file) {
    return file.kind === "css" || /\.css$/i.test(file.path);
  }

  function isHtmlFile(file) {
    return file.kind === "html" || /\.html?$/i.test(file.path);
  }

  function fileCategory(file) {
    if (isHtmlFile(file)) return "html-pages";
    if (isStyleFile(file)) return "html-styles";
    if (isScriptFile(file)) return "html-scripts";
    return "html-files";
  }

  function itemKey(type, id) {
    return "item::" + String(type || "") + "::" + String(id || "");
  }

  function groupKey(key) {
    return "group::" + String(key || "");
  }

  function isGroupExpanded(key) {
    return !(state.collapsedResourceGroups && state.collapsedResourceGroups[key] === true);
  }

  function setGroupExpanded(key, expanded) {
    state.collapsedResourceGroups = state.collapsedResourceGroups || {};
    state.collapsedResourceGroups[key] = !expanded;
  }

  function tooltip(title, meta, description) {
    return [title, meta, description].filter(function (part) { return !!part; }).join(" — ");
  }

  function treeIconKind(kind) {
    kind = String(kind || "").toLowerCase();
    if (kind === "script") return "js";
    if (kind === "data") return "json";
    if (kind === "attachment") return "file";
    if (kind === "plan_document") return "plan";
    if (["html", "css", "js", "json", "markdown", "chart", "image", "audio", "file", "plan"].indexOf(kind) >= 0) return kind;
    return "system";
  }

  function artifactCategory(artifact) {
    if (artifact && artifact.category) return artifact.category;
    var kind = String(artifact && artifact.kind || "").toLowerCase();
    if (["attachment", "image", "audio", "file"].indexOf(kind) >= 0) return "files";
    if (kind === "chart") return "generated";
    if (["task_list", "compaction", "tool_result"].indexOf(kind) >= 0) return "system";
    return "authored";
  }

  function groupNode(key, title, count, children, iconKind, selectable) {
    var node = {
      key: groupKey(key),
      groupKey: key,
      title: title,
      meta: String(count),
      tooltip: title + " — " + count,
      iconKind: iconKind || "folder",
      expanded: isGroupExpanded(key),
      children: children
    };
    if (selectable) {
      node.itemType = "collection";
      node.itemId = key;
    }
    return node;
  }

  function itemNode(type, id, title, meta, description, kind, deletable) {
    return {
      key: itemKey(type, id),
      itemType: type,
      itemId: String(id || ""),
      title: title,
      meta: meta,
      tooltip: tooltip(title, meta, description),
      iconKind: treeIconKind(kind),
      deletable: !!deletable
    };
  }

  function buildFileTree(items) {
    var root = { dirs: {}, files: [] };
    items.forEach(function (file) {
      var parts = String(file.path || "").split("/").filter(function (part) { return !!part; });
      if (parts.length > 10) parts = parts.slice(0, 9).concat(parts[parts.length - 1]);
      var node = root;
      while (parts.length > 1) {
        var dir = parts.shift();
        if (!node.dirs[dir]) node.dirs[dir] = { name: dir, dirs: {}, files: [] };
        node = node.dirs[dir];
      }
      node.files.push(file);
    });
    return root;
  }

  function countTreeFiles(node) {
    var count = node.files.length;
    Object.keys(node.dirs).forEach(function (key) { count += countTreeFiles(node.dirs[key]); });
    return count;
  }

  function fileDisplayName(file) {
    var parts = String(file.path || "").split("/").filter(function (part) { return !!part; });
    return parts.length ? parts[parts.length - 1] : file.path;
  }

  function fileTreeNodes(key, node) {
    var result = [];
    Object.keys(node.dirs).sort().forEach(function (dirName) {
      var dir = node.dirs[dirName];
      var storageKey = key + ":dir:" + dirName;
      result.push(groupNode(storageKey, dirName, countTreeFiles(dir), fileTreeNodes(key + "/" + dirName, dir), "folder"));
    });
    node.files.sort(function (left, right) {
      return String(left.path || "").localeCompare(String(right.path || ""));
    }).forEach(function (file) {
      result.push(itemNode(
        "file",
        file.id,
        fileDisplayName(file),
        file.kind || "file",
        [file.path, firstLine(file.content) || "HTML workspace file"].filter(function (part) { return !!part; }).join(" — "),
        isStyleFile(file) ? "css" : (isScriptFile(file) ? "js" : (isHtmlFile(file) ? "html" : "file")),
        true
      ));
    });
    return result;
  }

  function fileGroup(label, key, items, query) {
    var matched = items.filter(function (file) {
      return matchesText([file.path, file.kind, file.content].join(" "), query);
    });
    if (!matched.length) return null;
    return groupNode(key, label, matched.length, fileTreeNodes(key, buildFileTree(matched)), "folder");
  }

  function dataGroup(items, query) {
    var children = [];
    items.forEach(function (data) {
      var binding = data.binding || null;
      var sourceTool = bindingValue(binding, "ToolId", "toolId", "");
      var status = bindingValue(binding, "Status", "status", "ready");
      var lastError = bindingValue(binding, "LastError", "lastError", "");
      var refreshPolicy = bindingValue(binding, "RefreshPolicy", "refreshPolicy", "manual");
      if (!matchesText([data.name, data.json, sourceTool, status, lastError].join(" "), query)) return;
      var meta = binding ? sourceTool + " · " + status : "data/*.json · static";
      var description = binding
        ? (lastError || (refreshPolicy === "on_preview" ? "Обновляется при открытии" : "Обновляется вручную"))
        : (firstLine(data.json) || "JSON data source");
      children.push(itemNode("data", data.id, data.name, meta, description, "json", true));
    });
    return children.length ? groupNode("html-data", "Данные", children.length, children, "json") : null;
  }

  function dependencyGroup(items, query) {
    var children = [];
    items.forEach(function (dependency) {
      var status = dependency.loaded ? "загружено" : "недоступно";
      if (!matchesText([
        dependency.title, dependency.path, dependency.version, status, dependency.description
      ].join(" "), query)) return;
      children.push({
        key: itemKey("dependency", dependency.id),
        title: dependency.title || dependency.path,
        meta: "ECharts " + dependency.version + " · " + status + " · только чтение",
        tooltip: tooltip(dependency.path, status, dependency.description),
        iconKind: "js",
        deletable: false
      });
    });
    return children.length ? groupNode("html-dependencies", "Зависимости", children.length, children, "folder") : null;
  }

  function artifactGroup(label, key, items, query, selectionType) {
    var matched = items.filter(function (artifact) {
      return matchesText([artifact.title, artifact.kind, artifact.mimeType, artifact.relativePath, artifact.text].join(" "), query);
    });
    if (!matched.length) return null;
    var children = matched.sort(function (left, right) {
      return String(left.title || "").localeCompare(String(right.title || ""));
    }).map(function (artifact) {
      return itemNode(
        selectionType,
        artifact.id,
        artifact.title,
        artifact.meta,
        firstLine(artifact.text) || artifact.relativePath || artifact.mimeType,
        selectionType === "plan" ? "plan" : artifact.kind,
        selectionType === "plan"
      );
    });
    return groupNode(key, label, children.length, children,
      selectionType === "plan" ? "plan" : "folder", true);
  }

  function selectedKey(options) {
    var selected = options.selected || {};
    if (!selected.type || !selected.id) return "";
    if (selected.type === "collection") return groupKey(selected.id);
    return itemKey(selected.type, selected.id);
  }

  function render(options) {
    options = options || {};
    var root = options.root;
    if (!root) return 0;
    var adapter = window.RNAssistantTreeAdapter;
    if (!adapter || typeof adapter.mount !== "function") {
      root.replaceChildren();
      var error = document.createElement("div");
      error.className = "rn-tree-status is-error";
      error.textContent = "TreeAdapter недоступен.";
      root.appendChild(error);
      return 0;
    }

    var query = String(options.query || "").trim().toLowerCase();
    var files = options.files || [];
    var dataSources = options.dataSources || [];
    var dependencies = options.dependencies || [];
    var artifacts = options.artifacts || [];
    var htmlChildren = [];

    [
      ["Страницы", "html-pages"],
      ["Стили", "html-styles"],
      ["Скрипты", "html-scripts"],
      ["Файлы", "html-files"]
    ].forEach(function (entry) {
      var members = files.filter(function (file) { return fileCategory(file) === entry[1]; });
      var node = fileGroup(entry[0], entry[1], members, query);
      if (node) htmlChildren.push(node);
    });
    var data = dataGroup(dataSources, query);
    if (data) htmlChildren.push(data);
    var dependency = dependencyGroup(dependencies, query);
    if (dependency) htmlChildren.push(dependency);

    var nodes = [];
    if (htmlChildren.length) {
      var htmlCount = htmlChildren.reduce(function (sum, node) { return sum + Number(node.meta || 0); }, 0);
      nodes.push(groupNode("artifacts:html", "HTML workspace", htmlCount, htmlChildren, "html"));
    }

    [
      artifactGroup("Планы", "artifact-plans", options.plans || [], query, "plan"),
      artifactGroup("Документы", "artifact-authored", artifacts.filter(function (artifact) {
        return artifactCategory(artifact) === "authored";
      }), query, "artifact"),
      artifactGroup("Файлы и медиа", "artifact-files", artifacts.filter(function (artifact) {
        return artifactCategory(artifact) === "files";
      }), query, "artifact"),
      artifactGroup("Созданные снимки", "artifact-generated", artifacts.filter(function (artifact) {
        return artifactCategory(artifact) === "generated";
      }), query, "artifact"),
      artifactGroup("Служебные данные", "artifact-system", artifacts.filter(function (artifact) {
        return artifactCategory(artifact) === "system";
      }), query, "artifact")
    ].forEach(function (node) { if (node) nodes.push(node); });

    var mounted = adapter.mount(root, {
      nodes: nodes,
      selectedKey: selectedKey(options),
      emptyText: query ? "Ничего не найдено." : "Артефактов пока нет.",
      limits: { maxNodes: 1800, maxDepth: 12 },
      onActivate: function (item) {
        return typeof options.onSelect === "function" ? options.onSelect(item.type, item.id) : true;
      },
      onDelete: function (item) {
        if (typeof options.onDelete === "function") options.onDelete(item.type, item.id);
      },
      onToggle: setGroupExpanded
    });
    return mounted.count;
  }

  window.RNAssistantHtmlWorkspaceTree = { render: render };
}());
