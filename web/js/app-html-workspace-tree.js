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

  function isSelected(options, type, id) {
    var selected = options.selected || {};
    return String(selected.type || "") === String(type || "") && String(selected.id || "") === String(id || "");
  }

  function select(options, type, id) {
    if (typeof options.onSelect === "function") options.onSelect(type, id);
  }

  function artifactVisuals() {
    return window.RNAssistantArtifactVisuals || null;
  }

  function resourceIconSvg(kind) {
    var visuals = artifactVisuals();
    return visuals && typeof visuals.iconSvg === "function" ? visuals.iconSvg(kind) : "";
  }

  function appendTreeItem(parent, itemOptions, action) {
    var row = document.createElement("div");
    row.className = "resource-tree-item-row" + (action ? " has-action" : "");
    var item = createResourceListItem(itemOptions);
    item.classList.add("resource-tree-item-main");
    item.setAttribute("role", "treeitem");
    row.appendChild(item);
    if (action) {
      var button = document.createElement("button");
      button.type = "button";
      button.className = "resource-tree-item-action" + (action.danger ? " is-danger" : "");
      button.title = action.title || "Действие";
      button.setAttribute("aria-label", button.title);
      button.innerHTML = iconSvg(action.icon || "trash");
      button.addEventListener("click", function (event) {
        event.preventDefault();
        event.stopPropagation();
        action.onClick();
      });
      row.appendChild(button);
    }
    parent.appendChild(row);
    return row;
  }

  function renderFileGroup(parent, label, key, items, query, options) {
    var count = 0;
    var group = createResourceGroup({ key: key, title: label, count: items.length });
    group.className += " html-workspace-group";
    var body = group.treeChildren || group;
    var matched = items.filter(function (file) {
      return matchesText([file.path, file.kind, file.content].join(" "), query);
    });
    renderFileTreeNode(body, key, buildFileTree(matched), function (container, file) {
      appendTreeItem(container, {
        title: fileDisplayName(file),
        active: isSelected(options, "file", file.id),
        meta: file.kind || "file",
        tooltip: file.path + " - " + (file.kind || "file"),
        icon: fileListIcon(file),
        iconHtml: resourceIconSvg(file.kind),
        description: firstLine(file.content) || "HTML workspace file",
        compact: true,
        depth: 1,
        onClick: function () { select(options, "file", file.id); }
      }, {
        title: "Удалить " + file.path,
        icon: "trash",
        danger: true,
        onClick: function () { options.onDelete("file", file.id); }
      });
      count += 1;
    });
    if (count) parent.appendChild(group);
    return count;
  }

  function renderDataGroup(parent, items, query, options) {
    var count = 0;
    var group = createResourceGroup({ key: "html-data", title: "Данные", count: items.length });
    group.className += " html-workspace-group";
    var body = group.treeChildren || group;
    items.forEach(function (data) {
      var binding = data.binding || null;
      var sourceTool = bindingValue(binding, "ToolId", "toolId", "");
      var status = bindingValue(binding, "Status", "status", "ready");
      var lastError = bindingValue(binding, "LastError", "lastError", "");
      var refreshPolicy = bindingValue(binding, "RefreshPolicy", "refreshPolicy", "manual");
      if (!matchesText([data.name, data.json, sourceTool, status, lastError].join(" "), query)) return;
      appendTreeItem(body, {
        title: data.name,
        active: isSelected(options, "data", data.id),
        meta: binding ? sourceTool + " · " + status : "data/*.json · static",
        icon: "JSON",
        iconHtml: resourceIconSvg("json"),
        description: binding ? (lastError || (refreshPolicy === "on_preview" ? "Обновляется при открытии" : "Обновляется вручную")) : (firstLine(data.json) || "JSON data source"),
        compact: true,
        depth: 1,
        onClick: function () { select(options, "data", data.id); }
      }, {
        title: "Удалить " + data.name,
        icon: "trash",
        danger: true,
        onClick: function () { options.onDelete("data", data.id); }
      });
      count += 1;
    });
    if (count) parent.appendChild(group);
    return count;
  }

  function renderArtifactGroup(parent, label, key, items, query, selectionType, options) {
    var matched = items.filter(function (artifact) {
      return matchesText([artifact.title, artifact.kind, artifact.mimeType, artifact.relativePath, artifact.text].join(" "), query);
    });
    if (!matched.length) return 0;
    var group = createResourceGroup({ key: key, title: label, count: matched.length });
    group.className += " artifact-root-group";
    var body = group.treeChildren || group;
    matched.sort(function (left, right) { return left.title.localeCompare(right.title); }).forEach(function (artifact) {
      appendTreeItem(body, {
        title: artifact.title,
        active: isSelected(options, selectionType, artifact.id),
        meta: artifact.meta,
        description: firstLine(artifact.text) || artifact.relativePath || artifact.mimeType,
        iconHtml: resourceIconSvg(artifact.kind),
        compact: true,
        depth: 1,
        onClick: function () { select(options, selectionType, artifact.id); }
      }, selectionType === "plan" ? {
        title: "Удалить план " + artifact.title,
        icon: "trash",
        danger: true,
        onClick: function () { options.onDelete(selectionType, artifact.id); }
      } : null);
    });
    parent.appendChild(group);
    return matched.length;
  }

  function buildFileTree(items) {
    var root = { dirs: {}, files: [] };
    items.forEach(function (file) {
      var parts = String(file.path || "").split("/").filter(function (part) { return !!part; });
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

  function renderFileTreeNode(parent, key, node, appendFile) {
    Object.keys(node.dirs).sort().forEach(function (dirName) {
      var dir = node.dirs[dirName];
      var group = createResourceGroup({
        key: key + ":dir:" + dirName,
        title: dirName,
        count: countTreeFiles(dir)
      });
      group.className += " resource-tree-subgroup";
      var body = group.treeChildren || group;
      parent.appendChild(group);
      renderFileTreeNode(body, key + "/" + dirName, dir, appendFile);
    });
    node.files.sort(function (left, right) { return left.path.localeCompare(right.path); }).forEach(function (file) {
      appendFile(parent, file);
    });
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

  function fileListIcon(file) {
    if (file.kind === "css") return "CSS";
    return isScriptFile(file) ? "JS" : "HTML";
  }

  function render(options) {
    options = options || {};
    var tree = options.root;
    if (!tree) return 0;
    var query = String(options.query || "").trim().toLowerCase();
    var files = options.files || [];
    var dataSources = options.dataSources || [];
    var artifacts = options.artifacts || [];
    tree.innerHTML = "";

    var rendered = 0;
    var htmlRoot = createResourceGroup({ key: "artifacts:html", title: "HTML workspace", count: files.length + dataSources.length });
    htmlRoot.className += " artifact-root-group";
    var htmlBody = htmlRoot.treeChildren || htmlRoot;
    var htmlRendered = 0;
    htmlRendered += renderFileGroup(htmlBody, "Страницы", "html-pages", files.filter(isHtmlFile), query, options);
    htmlRendered += renderFileGroup(htmlBody, "Стили", "html-styles", files.filter(isStyleFile), query, options);
    htmlRendered += renderFileGroup(htmlBody, "Скрипты", "html-scripts", files.filter(isScriptFile), query, options);
    htmlRendered += renderFileGroup(htmlBody, "Файлы", "html-files", files.filter(function (file) {
      return !isHtmlFile(file) && !isStyleFile(file) && !isScriptFile(file);
    }), query, options);
    htmlRendered += renderDataGroup(htmlBody, dataSources, query, options);
    if (htmlRendered) {
      tree.appendChild(htmlRoot);
      rendered += htmlRendered;
    }

    rendered += renderArtifactGroup(tree, "Планы", "artifact-plans", options.plans || [], query, "plan", options);
    rendered += renderArtifactGroup(tree, "Созданные", "artifact-created", artifacts.filter(function (artifact) {
      return ["markdown", "chart"].indexOf(artifact.kind) >= 0;
    }), query, "artifact", options);
    rendered += renderArtifactGroup(tree, "Файлы", "artifact-attachments", artifacts.filter(function (artifact) {
      return ["attachment", "image", "audio", "file"].indexOf(artifact.kind) >= 0;
    }), query, "artifact", options);
    rendered += renderArtifactGroup(tree, "Служебные", "artifact-system", artifacts.filter(function (artifact) {
      return ["plan_document", "task_list", "markdown", "chart", "attachment", "image", "audio", "file", "html_workspace"].indexOf(artifact.kind) < 0;
    }), query, "artifact", options);
    if (!rendered) tree.appendChild(createResourceEmptyState(query ? "Ничего не найдено." : "Артефактов пока нет."));
    return rendered;
  }

  window.RNAssistantHtmlWorkspaceTree = { render: render };
}());
