(function () {
  var htmlPreviewRefreshTimer = 0;

  function prop(source, pascal, camel, fallback) {
    source = source || {};
    return source[camel] !== undefined ? source[camel] : (source[pascal] !== undefined ? source[pascal] : fallback);
  }

  function workspace() {
    var current = state.htmlWorkspace || {};
    current.files = prop(current, "Files", "files", []) || [];
    current.dataSources = prop(current, "DataSources", "dataSources", []) || [];
    current.history = prop(current, "History", "history", []) || [];
    current.redoHistory = prop(current, "RedoHistory", "redoHistory", []) || [];
    current.activeFileId = prop(current, "ActiveFileId", "activeFileId", "") || "";
    state.htmlWorkspace = current;
    return current;
  }

  function files() {
    return workspace().files;
  }

  function dataSources() {
    return workspace().dataSources;
  }

  function historyItems() {
    return workspace().history || [];
  }

  function redoItems() {
    return workspace().redoHistory || [];
  }

  function fileId(file) {
    return prop(file, "Id", "id", prop(file, "Path", "path", ""));
  }

  function filePath(file) {
    return prop(file, "Path", "path", fileId(file));
  }

  function fileKind(file) {
    return (prop(file, "Kind", "kind", "") || "").toLowerCase();
  }

  function isScriptFile(file) {
    var kind = fileKind(file);
    return kind === "script" || kind === "js" || /\.js$/i.test(filePath(file));
  }

  function fileContent(file) {
    return prop(file, "Content", "content", "") || "";
  }

  function setFileContent(file, value) {
    if (!file) {
      return;
    }
    if (file.content !== undefined || file.Content === undefined) {
      file.content = value || "";
    } else {
      file.Content = value || "";
    }
  }

  function dataId(data) {
    return prop(data, "Id", "id", prop(data, "Name", "name", ""));
  }

  function dataName(data) {
    return prop(data, "Name", "name", dataId(data));
  }

  function dataJson(data) {
    return prop(data, "Json", "json", "{}") || "{}";
  }

  function setDataJson(data, value) {
    if (!data) {
      return;
    }
    if (data.json !== undefined || data.Json === undefined) {
      data.json = value || "{}";
    } else {
      data.Json = value || "{}";
    }
  }

  function selectionKey(type, id) {
    return (type || "") + ":" + (id || "");
  }

  function selectedKey() {
    return selectionKey(state.htmlWorkspaceSelection.type, state.htmlWorkspaceSelection.id);
  }

  function selectedItem() {
    var selection = state.htmlWorkspaceSelection || {};
    var id = selection.id || "";
    var result = null;
    if (selection.type === "data") {
      dataSources().forEach(function (item) {
        if (dataId(item) === id) {
          result = { type: "data", item: item };
        }
      });
      return result;
    }

    files().forEach(function (item) {
      if (fileId(item) === id) {
        result = { type: "file", item: item };
      }
    });
    return result;
  }

  function activeHtmlFile() {
    var activeId = workspace().activeFileId || "";
    var active = null;
    files().forEach(function (file) {
      if (!active && fileId(file) === activeId && fileKind(file) === "html") {
        active = file;
      }
    });
    if (active) {
      return active;
    }
    files().forEach(function (file) {
      if (!active && fileKind(file) === "html") {
        active = file;
      }
    });
    return active;
  }

  function ensureSelection() {
    if (selectedItem()) {
      return;
    }

    var active = activeHtmlFile();
    if (active) {
      state.htmlWorkspaceSelection = { type: "file", id: fileId(active) };
      return;
    }
    if (files().length) {
      state.htmlWorkspaceSelection = { type: "file", id: fileId(files()[0]) };
      return;
    }
    if (dataSources().length) {
      state.htmlWorkspaceSelection = { type: "data", id: dataId(dataSources()[0]) };
      return;
    }
    state.htmlWorkspaceSelection = { type: "file", id: "" };
  }

  function htmlPreviewEnabled() {
    var settings = state.settings || {};
    return !!(settings.AllowUnsafeHtmlArtifacts || settings.allowUnsafeHtmlArtifacts);
  }

  function syncHtmlEditorToState() {
    var selected = selectedItem();
    if (!selected) {
      return;
    }
    var value = typeof getCodeEditorValue === "function"
      ? getCodeEditorValue("htmlWorkspaceEditorInput")
      : ($("htmlWorkspaceEditorInput").value || "");
    if (selected.type === "data") {
      setDataJson(selected.item, value);
    } else {
      setFileContent(selected.item, value);
    }
  }

  function markHtmlWorkspaceDirty() {
    syncHtmlEditorToState();
    state.htmlWorkspaceDirty = true;
    updateHtmlWorkspaceStatus();
    scheduleHtmlWorkspacePreviewRefresh();
  }

  function scheduleHtmlWorkspacePreviewRefresh() {
    if (htmlPreviewRefreshTimer) {
      window.clearTimeout(htmlPreviewRefreshTimer);
    }
    htmlPreviewRefreshTimer = window.setTimeout(function () {
      htmlPreviewRefreshTimer = 0;
      renderHtmlWorkspacePreview();
    }, 160);
  }

  function updateHtmlWorkspaceStatus() {
    var status = $("htmlWorkspaceStatus");
    var save = $("saveHtmlWorkspaceButton");
    var selected = selectedItem();
    if (status) {
      if (state.bridgeUnavailable) {
        status.textContent = "Office bridge недоступен.";
      } else if (!files().length && !dataSources().length) {
        status.textContent = "HTML workspace пуст.";
      } else {
        status.textContent = (files().length || 0) + " file(s), " + (dataSources().length || 0) + " data source(s), " + historyItems().length + " undo, " + redoItems().length + " redo" + (state.htmlWorkspaceDirty ? " · не сохранено" : "");
      }
    }
    if (save) {
      save.disabled = state.bridgeUnavailable || !selected;
    }
    if ($("undoHtmlWorkspaceButton")) {
      $("undoHtmlWorkspaceButton").disabled = state.bridgeUnavailable || !historyItems().length;
      $("undoHtmlWorkspaceButton").title = historyItems().length
        ? "Вернуть: " + snapshotLabel(historyItems()[0])
        : "Нет предыдущих версий";
    }
    if ($("redoHtmlWorkspaceButton")) {
      $("redoHtmlWorkspaceButton").disabled = state.bridgeUnavailable || !redoItems().length;
      $("redoHtmlWorkspaceButton").title = redoItems().length
        ? "Повторить: " + snapshotLabel(redoItems()[0])
        : "Нет отмененных версий";
    }
  }

  function setHtmlWorkspaceMode(mode) {
    if (typeof syncCodeEditors === "function") {
      syncCodeEditors(["htmlWorkspaceEditorInput"]);
    }
    syncHtmlEditorToState();
    state.htmlWorkspaceMode = mode === "edit" ? "edit" : "preview";
    applyHtmlWorkspaceMode();
    if (state.htmlWorkspaceMode === "preview") {
      renderHtmlWorkspacePreview();
    }
  }

  function applyHtmlWorkspaceMode() {
    var mode = state.htmlWorkspaceMode === "edit" ? "edit" : "preview";
    Array.prototype.slice.call(document.querySelectorAll(".html-workspace-mode-button")).forEach(function (button) {
      button.classList.toggle("active", button.getAttribute("data-html-mode") === mode);
    });
    Array.prototype.slice.call(document.querySelectorAll(".html-workspace-view")).forEach(function (view) {
      view.classList.toggle("hidden", view.getAttribute("data-html-view") !== mode);
    });
    if (mode === "edit" && typeof refreshCodeEditors === "function") {
      refreshCodeEditors(["htmlWorkspaceEditorInput"]);
    }
  }

  function renderHtmlWorkspaceList() {
    var tree = $("htmlWorkspaceTree");
    if (!tree) {
      return;
    }
    var query = (($("htmlWorkspaceSearchInput") && $("htmlWorkspaceSearchInput").value) || "").trim().toLowerCase();
    tree.innerHTML = "";
    var rendered = 0;
    var workspaceFiles = files();
    rendered += renderFileGroup(tree, "Artifacts", "html-artifacts", workspaceFiles.filter(function (file) { return !isScriptFile(file); }), query);
    rendered += renderFileGroup(tree, "Scripts", "html-scripts", workspaceFiles.filter(isScriptFile), query);
    rendered += renderDataGroup(tree, "Data", "html-data", dataSources(), query);
    if (!rendered) {
      tree.appendChild(createResourceEmptyState(query ? "Ничего не найдено." : "Workspace пуст."));
    }
  }

  function matchesText(text, query) {
    return !query || String(text || "").toLowerCase().indexOf(query) >= 0;
  }

  function renderFileGroup(parent, label, key, items, query) {
    var count = 0;
    var group = createResourceGroup({ key: key, title: label, count: items.length });
    group.className += " html-workspace-group";
    var body = group.treeChildren || group;
    renderFileTreeRows(body, key, items.filter(function (file) {
      var text = [filePath(file), fileKind(file), fileContent(file)].join(" ");
      return matchesText(text, query);
    }), function (container, file) {
      container.appendChild(createResourceListItem({
        title: fileDisplayName(file),
        active: selectedKey() === selectionKey("file", fileId(file)),
        meta: fileMeta(file),
        tooltip: filePath(file) + " - " + (fileKind(file) || "file"),
        icon: fileListIcon(file),
        description: firstLine(fileContent(file)) || "HTML workspace file",
        compact: true,
        depth: 1,
        onClick: function () { selectHtmlWorkspaceItem("file", fileId(file)); }
      }));
      count += 1;
    });
    if (count) {
      parent.appendChild(group);
    }
    return count;
  }

  function renderDataGroup(parent, label, key, items, query) {
    var count = 0;
    var group = createResourceGroup({ key: key, title: label, count: items.length });
    group.className += " html-workspace-group";
    var body = group.treeChildren || group;
    items.forEach(function (data) {
      var text = [dataName(data), dataJson(data)].join(" ");
      if (!matchesText(text, query)) {
        return;
      }
      body.appendChild(createResourceListItem({
        title: dataName(data),
        active: selectedKey() === selectionKey("data", dataId(data)),
        meta: "data/*.json",
        icon: "JSON",
        description: firstLine(dataJson(data)) || "JSON data source",
        compact: true,
        depth: 1,
        onClick: function () { selectHtmlWorkspaceItem("data", dataId(data)); }
      }));
      count += 1;
    });
    if (count) {
      parent.appendChild(group);
    }
    return count;
  }

  function firstLine(value) {
    return String(value || "").split(/\r?\n/)[0].trim().slice(0, 140);
  }

  function renderFileTreeRows(parent, key, items, appendFile) {
    var tree = buildFileTree(items);
    renderFileTreeNode(parent, key, tree, appendFile);
  }

  function buildFileTree(items) {
    var root = { dirs: {}, files: [] };
    items.forEach(function (file) {
      var parts = String(filePath(file) || "").split("/").filter(function (part) { return !!part; });
      var node = root;
      while (parts.length > 1) {
        var dir = parts.shift();
        if (!node.dirs[dir]) {
          node.dirs[dir] = { name: dir, dirs: {}, files: [] };
        }
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
        key: key + ":dir:" + dirPathKey(dir, dirName),
        title: dirName,
        count: countTreeFiles(dir)
      });
      group.className += " resource-tree-subgroup";
      var body = group.treeChildren || group;
      parent.appendChild(group);
      renderFileTreeNode(body, key + "/" + dirName, dir, appendFile);
    });
    node.files.sort(function (left, right) {
      return filePath(left).localeCompare(filePath(right));
    }).forEach(function (file) {
      appendFile(parent, file);
    });
  }

  function dirPathKey(dir, fallback) {
    return fallback || (dir && dir.name) || "folder";
  }

  function countTreeFiles(node) {
    var count = (node.files || []).length;
    Object.keys(node.dirs || {}).forEach(function (key) {
      count += countTreeFiles(node.dirs[key]);
    });
    return count;
  }

  function fileDisplayName(file) {
    var parts = String(filePath(file) || "").split("/").filter(function (part) { return !!part; });
    return parts.length ? parts[parts.length - 1] : filePath(file);
  }

  function fileMeta(file) {
    return fileKind(file) || "file";
  }

  function fileListIcon(file) {
    var kind = fileKind(file);
    if (kind === "css") {
      return "CSS";
    }
    if (isScriptFile(file)) {
      return "JS";
    }
    return "HTML";
  }

  function snapshotLabel(snapshot) {
    return prop(snapshot || {}, "Label", "label", "HTML workspace snapshot");
  }

  function selectHtmlWorkspaceItem(type, id) {
    if (state.htmlWorkspaceDirty && selectedKey() !== selectionKey(type, id)) {
      window.alert("Сначала сохраните текущие изменения HTML workspace.");
      return;
    }
    state.htmlWorkspaceSelection = { type: type, id: id };
    renderHtmlWorkspace();
  }

  function selectedEditorValue(selected) {
    if (!selected) {
      return "";
    }
    return selected.type === "data" ? dataJson(selected.item) : fileContent(selected.item);
  }

  function renderHtmlWorkspaceEditor() {
    var selected = selectedItem();
    var empty = $("htmlWorkspaceEmptyState");
    var editor = $("htmlWorkspaceEditor");
    var title = $("htmlWorkspaceTitle");
    var meta = $("htmlWorkspaceMeta");
    var hasItems = !!selected;
    if (editor) {
      editor.classList.toggle("is-empty", !hasItems);
    }
    if (empty) {
      empty.classList.toggle("hidden", hasItems);
    }
    if (title) {
      title.textContent = selected
        ? (selected.type === "data" ? dataName(selected.item) : filePath(selected.item))
        : "HTML не выбран";
    }
    if (meta) {
      meta.textContent = selected
        ? (selected.type === "data" ? "JSON data source" : (fileKind(selected.item) || "file"))
        : "";
    }
    if (typeof setCodeEditorValue === "function") {
      setCodeEditorValue("htmlWorkspaceEditorInput", selectedEditorValue(selected));
    } else if ($("htmlWorkspaceEditorInput")) {
      $("htmlWorkspaceEditorInput").value = selectedEditorValue(selected);
    }
    renderHtmlWorkspacePreview();
  }

  function escapeScriptJson(value) {
    return value.replace(/<\/script/gi, "<\\/script");
  }

  function safeStyle(value) {
    return String(value || "").replace(/<\/style/gi, "<\\/style");
  }

  function safeScript(value) {
    return String(value || "").replace(/<\/script/gi, "<\\/script");
  }

  function dataScript() {
    var data = {};
    dataSources().forEach(function (source) {
      try {
        data[dataName(source)] = JSON.parse(dataJson(source));
      } catch (error) {
        data[dataName(source)] = null;
      }
    });
    return "<script>window.RNAssistantData=" + escapeScriptJson(JSON.stringify(data)) + ";</script>";
  }

  function cssBlock() {
    return files().filter(function (file) {
      return fileKind(file) === "css";
    }).map(function (file) {
      return "<style data-rn-path=\"" + encodeHtml(filePath(file)) + "\">\n" + safeStyle(fileContent(file)) + "\n</style>";
    }).join("\n");
  }

  function scriptBlock() {
    return files().filter(isScriptFile).map(function (file) {
      return "<script data-rn-path=\"" + encodeHtml(filePath(file)) + "\">\n" + safeScript(fileContent(file)) + "\n</script>";
    }).join("\n");
  }

  function previewViewportReset() {
    return "<style data-rn-preview-reset>html,body{min-height:100%;margin:0;}*,*::before,*::after{box-sizing:border-box;}</style>";
  }

  function encodeHtml(value) {
    return String(value || "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  function buildPreviewHtml() {
    var file = activeHtmlFile();
    var html = file ? fileContent(file) : "";
    var headInject = previewViewportReset() + "\n" + dataScript() + "\n" + cssBlock();
    var bodyInject = scriptBlock();
    if (!html.trim()) {
      html = "<div style=\"font-family:Segoe UI,Arial,sans-serif;padding:24px;color:#475467\">HTML workspace пуст.</div>";
    }
    if (/<html[\s>]/i.test(html)) {
      if (/<head[\s>]/i.test(html)) {
        html = html.replace(/<head([^>]*)>/i, function (match) { return match + "\n" + headInject; });
      } else {
        html = html.replace(/<html[^>]*>/i, function (match) { return match + "<head>" + headInject + "</head>"; });
      }
      if (!bodyInject) {
        return html;
      }
      if (/<\/body>/i.test(html)) {
        return html.replace(/<\/body>/i, bodyInject + "\n</body>");
      }
      if (/<\/html>/i.test(html)) {
        return html.replace(/<\/html>/i, bodyInject + "\n</html>");
      }
      return html + "\n" + bodyInject;
    }
    return "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" + headInject + "</head><body>" + html + "\n" + bodyInject + "</body></html>";
  }

  function renderHtmlWorkspacePreview() {
    var frame = $("htmlWorkspacePreviewFrame");
    var blocked = $("htmlWorkspacePreviewBlocked");
    if (!frame || !blocked) {
      return;
    }
    var enabled = htmlPreviewEnabled();
    blocked.classList.toggle("hidden", enabled);
    frame.classList.toggle("hidden", !enabled);
    if (!enabled) {
      frame.removeAttribute("src");
      return;
    }
    frame.src = "data:text/html;charset=utf-8," + encodeURIComponent(buildPreviewHtml());
  }

  function applyHtmlWorkspaceResponse(response) {
    response = response || {};
    state.htmlWorkspace = response.workspace || response.Workspace || { activeFileId: "", files: [], dataSources: [], history: [], redoHistory: [] };
    state.htmlWorkspaceDirty = false;
    renderHtmlWorkspace();
  }

  async function saveHtmlWorkspaceSelection() {
    var selected = selectedItem();
    if (!selected || state.bridgeUnavailable) {
      return;
    }
    syncHtmlEditorToState();
    setActivity("saving", "Сохраняю HTML workspace...");
    try {
      if (selected.type === "data") {
        applyHtmlWorkspaceResponse(await send("saveHtmlWorkspaceData", {
          chatId: state.activeChatId,
          name: dataName(selected.item),
          json: dataJson(selected.item)
        }));
      } else {
        applyHtmlWorkspaceResponse(await send("saveHtmlWorkspaceFile", {
          chatId: state.activeChatId,
          path: filePath(selected.item),
          kind: fileKind(selected.item),
          content: fileContent(selected.item),
          setActive: fileKind(selected.item) === "html"
        }));
      }
      log("HTML workspace сохранен.");
    } catch (error) {
      log(error.detail || error.message);
      window.alert(error.message || "HTML workspace не сохранен.");
    } finally {
      clearActivity();
    }
  }

  async function undoHtmlWorkspace() {
    if (state.bridgeUnavailable || !historyItems().length) {
      return;
    }
    if (state.htmlWorkspaceDirty && !window.confirm("Есть несохраненные изменения. Вернуть предыдущую версию?")) {
      return;
    }

    setActivity("restoring", "Восстанавливаю HTML workspace...");
    try {
      applyHtmlWorkspaceResponse(await send("restoreHtmlWorkspaceSnapshot", {
        chatId: state.activeChatId,
        snapshotId: prop(historyItems()[0], "Id", "id", "")
      }));
      log("HTML workspace восстановлен.");
    } catch (error) {
      log(error.detail || error.message);
      window.alert(error.message || "HTML workspace не восстановлен.");
    } finally {
      clearActivity();
    }
  }

  async function redoHtmlWorkspace() {
    if (state.bridgeUnavailable || !redoItems().length) {
      return;
    }
    if (state.htmlWorkspaceDirty && !window.confirm("Есть несохраненные изменения. Повторить отмененную версию?")) {
      return;
    }

    setActivity("restoring", "Повторяю HTML workspace...");
    try {
      applyHtmlWorkspaceResponse(await send("redoHtmlWorkspaceSnapshot", {
        chatId: state.activeChatId,
        snapshotId: prop(redoItems()[0], "Id", "id", "")
      }));
      log("HTML workspace redo выполнен.");
    } catch (error) {
      log(error.detail || error.message);
      window.alert(error.message || "HTML workspace redo не выполнен.");
    } finally {
      clearActivity();
    }
  }

  async function addHtmlWorkspaceFile(kind) {
    if (state.bridgeUnavailable) {
      return;
    }
    var fallback = kind === "css" ? "styles.css" : (kind === "script" ? "app.js" : "index.html");
    showHtmlWorkspaceCreate(kind, fallback);
  }

  function showHtmlWorkspaceCreate(kind, fallback) {
    state.htmlWorkspaceCreateKind = kind;
    var box = $("htmlWorkspaceCreateBox");
    var input = $("htmlWorkspaceCreateNameInput");
    if (!box || !input) {
      return;
    }

    box.classList.remove("hidden");
    input.value = fallback || "";
    input.placeholder = kind === "data" ? "Имя data source" : "Путь файла";
    input.focus();
    input.select();
  }

  function hideHtmlWorkspaceCreate() {
    state.htmlWorkspaceCreateKind = "";
    if ($("htmlWorkspaceCreateBox")) {
      $("htmlWorkspaceCreateBox").classList.add("hidden");
    }
  }

  async function confirmHtmlWorkspaceCreate() {
    if (state.bridgeUnavailable) {
      return;
    }
    var kind = state.htmlWorkspaceCreateKind || "html";
    var input = $("htmlWorkspaceCreateNameInput");
    var path = input ? input.value : "";
    if (!path || !path.trim()) {
      return;
    }
    if (kind === "data") {
      await createHtmlWorkspaceData(path.trim());
      return;
    }

    await createHtmlWorkspaceFile(kind, path.trim());
  }

  async function createHtmlWorkspaceFile(kind, path) {
    var content = kind === "css"
      ? "body {\n  font-family: Segoe UI, Arial, sans-serif;\n}\n"
      : (kind === "script"
        ? "(function () {\n  var data = window.RNAssistantData || {};\n  console.log(\"HTML workspace data\", data);\n}());\n"
        : "<!doctype html>\n<html>\n<head>\n  <meta charset=\"utf-8\">\n  <title>HTML Workspace</title>\n</head>\n<body>\n  <h1>HTML Workspace</h1>\n</body>\n</html>\n");
    setActivity("saving", "Создаю файл HTML workspace...");
    try {
      applyHtmlWorkspaceResponse(await send("saveHtmlWorkspaceFile", {
        chatId: state.activeChatId,
        path: path,
        kind: kind,
        content: content,
        setActive: kind === "html"
      }));
      state.htmlWorkspaceSelection = { type: "file", id: path.toLowerCase() };
      hideHtmlWorkspaceCreate();
      renderHtmlWorkspace();
    } catch (error) {
      log(error.detail || error.message);
      window.alert(error.message || "Файл не создан.");
    } finally {
      clearActivity();
    }
  }

  async function addHtmlWorkspaceData() {
    if (state.bridgeUnavailable) {
      return;
    }
    showHtmlWorkspaceCreate("data", "data");
  }

  async function createHtmlWorkspaceData(name) {
    setActivity("saving", "Создаю JSON data source...");
    try {
      applyHtmlWorkspaceResponse(await send("saveHtmlWorkspaceData", {
        chatId: state.activeChatId,
        name: name,
        json: "{\n  \"items\": []\n}\n"
      }));
      state.htmlWorkspaceSelection = { type: "data", id: name.toLowerCase() };
      hideHtmlWorkspaceCreate();
      renderHtmlWorkspace();
    } catch (error) {
      log(error.detail || error.message);
      window.alert(error.message || "Data source не создан.");
    } finally {
      clearActivity();
    }
  }

  function updateHtmlSidebarToggle() {
    var button = $("toggleHtmlSidebarButton");
    if (!button) {
      return;
    }
    var label = state.htmlWorkspaceSidebarHidden ? "Показать список" : "Скрыть список";
    button.setAttribute("title", label);
    button.setAttribute("aria-label", label);
  }

  function toggleHtmlWorkspaceSidebar() {
    state.htmlWorkspaceSidebarHidden = !state.htmlWorkspaceSidebarHidden;
    renderHtmlWorkspace();
  }

  function renderHtmlWorkspace() {
    workspace();
    ensureSelection();
    var layout = $("htmlWorkspaceLayout");
    if (layout) {
      layout.classList.toggle("is-sidebar-hidden", !!state.htmlWorkspaceSidebarHidden);
    }
    updateHtmlSidebarToggle();
    renderHtmlWorkspaceList();
    renderHtmlWorkspaceEditor();
    applyHtmlWorkspaceMode();
    updateHtmlWorkspaceStatus();
  }

  function bindHtmlWorkspaceActions() {
    $("htmlWorkspaceSearchInput").addEventListener("input", renderHtmlWorkspaceList);
    $("saveHtmlWorkspaceButton").addEventListener("click", saveHtmlWorkspaceSelection);
    $("undoHtmlWorkspaceButton").addEventListener("click", undoHtmlWorkspace);
    $("redoHtmlWorkspaceButton").addEventListener("click", redoHtmlWorkspace);
    $("toggleHtmlSidebarButton").addEventListener("click", toggleHtmlWorkspaceSidebar);
    $("addHtmlFileButton").addEventListener("click", function () { addHtmlWorkspaceFile("html"); });
    $("addCssFileButton").addEventListener("click", function () { addHtmlWorkspaceFile("css"); });
    $("addJsFileButton").addEventListener("click", function () { addHtmlWorkspaceFile("script"); });
    $("addHtmlDataButton").addEventListener("click", addHtmlWorkspaceData);
    $("confirmHtmlCreateButton").addEventListener("click", confirmHtmlWorkspaceCreate);
    $("cancelHtmlCreateButton").addEventListener("click", hideHtmlWorkspaceCreate);
    $("htmlWorkspaceCreateNameInput").addEventListener("keydown", function (event) {
      if (event.key === "Enter") {
        event.preventDefault();
        confirmHtmlWorkspaceCreate();
      } else if (event.key === "Escape") {
        event.preventDefault();
        hideHtmlWorkspaceCreate();
      }
    });
    Array.prototype.slice.call(document.querySelectorAll(".html-workspace-mode-button")).forEach(function (button) {
      button.addEventListener("click", function () {
        setHtmlWorkspaceMode(button.getAttribute("data-html-mode"));
      });
    });
  }

  window.renderHtmlWorkspace = renderHtmlWorkspace;
  window.bindHtmlWorkspaceActions = bindHtmlWorkspaceActions;
  window.saveHtmlWorkspaceSelection = saveHtmlWorkspaceSelection;
  window.markHtmlWorkspaceDirty = markHtmlWorkspaceDirty;
}());
