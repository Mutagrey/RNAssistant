(function () {
  var htmlPreviewRefreshTimer = 0;
  var htmlPreview = window.RNAssistantHtmlWorkspacePreview;
  var workspaceArtifacts = window.RNAssistantHtmlWorkspaceArtifacts;

  window.addEventListener("message", function (event) {
    var frame = $("htmlWorkspacePreviewFrame");
    var data = event.data || {};
    if (!frame || event.source !== frame.contentWindow || data.type !== "rnassistant-html-fetch") return;
    var payload = data.request || {};
    function reply(ok, value) {
      if (frame.contentWindow) frame.contentWindow.postMessage({ type: "rnassistant-html-fetch-result", requestId: data.requestId, ok: ok, value: value }, "*");
    }
    function execute() {
      return send("htmlFetch", payload).then(function (response) { reply(true, response); });
    }
    execute().catch(function (error) {
      var message = error.detail || error.message || "HTTP request failed";
      var origin = "";
      try { origin = new URL(payload.url).origin; } catch (ignore) {}
      var deniedOrigin = String(message).match(/not allowed:\s*(https?:\/\/[^\s]+)/i);
      if (deniedOrigin) origin = deniedOrigin[1].replace(/[.,;]+$/, "");
      if (origin && /not allowed|не разреш/i.test(message) && window.confirm("HTML workspace запрашивает доступ к сети:\n" + origin + "\n\nРазрешить этот origin?")) {
        send("allowHtmlNetworkOrigin", { origin: origin }).then(function () {
          var list = state.settings.HtmlNetworkAllowedOrigins || state.settings.htmlNetworkAllowedOrigins || [];
          if (list.indexOf(origin) < 0) list.push(origin);
          state.settings.HtmlNetworkAllowedOrigins = list;
          return execute();
        }).catch(function (allowError) { reply(false, allowError.detail || allowError.message); });
        return;
      }
      reply(false, message);
    });
  });

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

  function artifactId(artifact) {
    return prop(artifact, "Id", "id", "");
  }

  function artifactKind(artifact) {
    return String(prop(artifact, "Kind", "kind", "file") || "file").toLowerCase();
  }

  function artifactTitle(artifact) {
    return prop(artifact, "Title", "title", "Артефакт") || "Артефакт";
  }

  function artifactRevision(artifact) {
    return Number(prop(artifact, "Revision", "revision", 1) || 1);
  }

  function artifactInlineText(artifact) {
    return prop(artifact, "InlineText", "inlineText", "") || "";
  }

  function setArtifactInlineText(artifact, value) {
    if (!artifact) return;
    if (artifact.inlineText !== undefined || artifact.InlineText === undefined) artifact.inlineText = value || "";
    else artifact.InlineText = value || "";
  }

  function artifactById(id) {
    return (state.artifacts || []).filter(function (artifact) { return artifactId(artifact) === id; })[0] || null;
  }

  function planJson(artifact) {
    if (!artifact || artifactKind(artifact) !== "plan") return null;
    try { return JSON.parse(artifactInlineText(artifact)); } catch (error) { return null; }
  }

  function planStableId(artifact) {
    var plan = planJson(artifact);
    return plan && (plan.id || plan.Id) || artifactId(artifact);
  }

  function latestPlanArtifacts() {
    var latest = {};
    (state.artifacts || []).forEach(function (artifact) {
      if (artifactKind(artifact) !== "plan") return;
      var id = planStableId(artifact);
      if (!latest[id] || artifactRevision(artifact) > artifactRevision(latest[id])) latest[id] = artifact;
    });
    return Object.keys(latest).map(function (id) { return latest[id]; });
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

  function isStyleFile(file) {
    return fileKind(file) === "css" || /\.css$/i.test(filePath(file));
  }

  function isHtmlFile(file) {
    return fileKind(file) === "html" || /\.html?$/i.test(filePath(file));
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
    if (selection.type === "plan" || selection.type === "artifact") {
      var artifact = artifactById(id);
      return artifact ? { type: selection.type, item: artifact } : null;
    }
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

    if (state.activePlanArtifactId && artifactById(state.activePlanArtifactId)) {
      state.htmlWorkspaceSelection = { type: "plan", id: state.activePlanArtifactId };
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
    if ((state.artifacts || []).length) {
      state.htmlWorkspaceSelection = { type: "artifact", id: artifactId(state.artifacts[0]) };
      return;
    }
    state.htmlWorkspaceSelection = { type: "file", id: "" };
  }

  function syncHtmlEditorToState() {
    var selected = selectedItem();
    if (!selected) {
      return;
    }
    var value = typeof getCodeEditorValue === "function"
      ? getCodeEditorValue("htmlWorkspaceEditorInput")
      : ($("htmlWorkspaceEditorInput").value || "");
    if (selected.type === "plan") {
      setArtifactInlineText(selected.item, value);
    } else if (selected.type === "artifact") {
      return;
    } else if (selected.type === "data") {
      setDataJson(selected.item, value);
    } else {
      setFileContent(selected.item, value);
    }
  }

  function markHtmlWorkspaceDirty() {
    var selected = selectedItem();
    if (!selected || selected.type === "artifact") return;
    syncHtmlEditorToState();
    state.htmlWorkspaceDirty = true;
    updateHtmlWorkspaceStatus();
    scheduleHtmlWorkspacePreviewRefresh();
  }

  function confirmDiscardHtmlWorkspaceChanges(action) {
    if (!state.htmlWorkspaceDirty) {
      return true;
    }
    return window.confirm(
      "В артефакте есть несохраненные изменения. " +
      (action || "Продолжить") +
      " и потерять их?"
    );
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
    if ($("addPlanButton")) $("addPlanButton").disabled = !!state.bridgeUnavailable;
    ["addHtmlFileButton", "addCssFileButton", "addJsFileButton", "addHtmlDataButton"].forEach(function (id) {
      if ($(id)) $(id).disabled = !!state.bridgeUnavailable;
    });
    if (status) {
      if (state.bridgeUnavailable) {
        status.textContent = "Office bridge недоступен.";
      } else if (!files().length && !dataSources().length && !(state.artifacts || []).length) {
        status.textContent = "Артефактов пока нет.";
      } else {
        status.textContent = (state.artifacts || []).length + " артефактов · " + files().length + " файлов · " + dataSources().length + " наборов данных" + (state.htmlWorkspaceDirty ? " · не сохранено" : "");
      }
    }
    if (save) {
      save.disabled = state.bridgeUnavailable || !selected || selected.type === "artifact" || !state.htmlWorkspaceDirty;
      save.title = "Сохранить изменения (Ctrl+S)";
    }
    if ($("deleteHtmlWorkspaceButton")) {
      $("deleteHtmlWorkspaceButton").disabled = state.bridgeUnavailable || !selected || selected.type === "artifact";
      $("deleteHtmlWorkspaceButton").title = selected
        ? (selected.type === "plan" ? "Удалить план" : "Удалить выбранный файл или источник данных")
        : "Выберите артефакт";
    }
    if ($("undoHtmlWorkspaceButton")) {
      $("undoHtmlWorkspaceButton").classList.toggle("hidden", !!selected && (selected.type === "plan" || selected.type === "artifact"));
      $("undoHtmlWorkspaceButton").disabled = state.bridgeUnavailable || !historyItems().length;
      $("undoHtmlWorkspaceButton").title = historyItems().length
        ? "Вернуть: " + snapshotLabel(historyItems()[0])
        : "Нет предыдущих версий";
    }
    if ($("redoHtmlWorkspaceButton")) {
      $("redoHtmlWorkspaceButton").classList.toggle("hidden", !!selected && (selected.type === "plan" || selected.type === "artifact"));
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
    var htmlRoot = createResourceGroup({ key: "artifacts:html", title: "HTML", count: workspaceFiles.length + dataSources().length });
    var htmlRendered = 0;
    htmlRoot.className += " artifact-root-group";
    htmlRendered += renderFileGroup(htmlRoot.treeChildren, "Страницы", "html-pages", workspaceFiles.filter(isHtmlFile), query);
    htmlRendered += renderFileGroup(htmlRoot.treeChildren, "Стили", "html-styles", workspaceFiles.filter(isStyleFile), query);
    htmlRendered += renderFileGroup(htmlRoot.treeChildren, "Скрипты", "html-scripts", workspaceFiles.filter(isScriptFile), query);
    htmlRendered += renderFileGroup(htmlRoot.treeChildren, "Файлы", "html-files", workspaceFiles.filter(function (file) {
      return !isHtmlFile(file) && !isStyleFile(file) && !isScriptFile(file);
    }), query);
    htmlRendered += renderDataGroup(htmlRoot.treeChildren, "Данные", "html-data", dataSources(), query);
    if (htmlRendered) {
      tree.appendChild(htmlRoot);
      rendered += htmlRendered;
    }
    rendered += renderArtifactGroup(tree, "Планы", "artifact-plans", latestPlanArtifacts(), query, "plan");
    rendered += renderArtifactGroup(tree, "Вложения", "artifact-attachments", (state.artifacts || []).filter(function (artifact) {
      return ["attachment", "image", "file"].indexOf(artifactKind(artifact)) >= 0;
    }), query, "artifact");
    rendered += renderArtifactGroup(tree, "Другие", "artifact-other", (state.artifacts || []).filter(function (artifact) {
      return ["plan", "attachment", "image", "file", "html_workspace"].indexOf(artifactKind(artifact)) < 0;
    }), query, "artifact");
    if (!rendered) {
      tree.appendChild(createResourceEmptyState(query ? "Ничего не найдено." : "Артефактов пока нет."));
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

  function renderArtifactGroup(parent, label, key, items, query, selectionType) {
    var matched = (items || []).filter(function (artifact) {
      return matchesText([
        artifactTitle(artifact),
        artifactKind(artifact),
        prop(artifact, "MimeType", "mimeType", ""),
        prop(artifact, "RelativePath", "relativePath", ""),
        artifactInlineText(artifact)
      ].join(" "), query);
    });
    if (!matched.length) return 0;
    var group = createResourceGroup({ key: key, title: label, count: matched.length });
    group.className += " artifact-root-group";
    matched.sort(function (left, right) { return artifactTitle(left).localeCompare(artifactTitle(right)); }).forEach(function (artifact) {
      var kind = artifactKind(artifact);
      var meta = kind === "plan" ? workspaceArtifacts.planSummary(artifact) : workspaceArtifacts.typeLabel(kind);
      group.treeChildren.appendChild(createResourceListItem({
        title: artifactTitle(artifact),
        active: selectedKey() === selectionKey(selectionType, artifactId(artifact)),
        meta: meta,
        description: firstLine(artifactInlineText(artifact)) || prop(artifact, "RelativePath", "relativePath", "") || prop(artifact, "MimeType", "mimeType", ""),
        compact: true,
        depth: 1,
        onClick: function () { selectHtmlWorkspaceItem(selectionType, artifactId(artifact)); }
      }));
    });
    parent.appendChild(group);
    return matched.length;
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
      window.alert("Сначала сохраните изменения текущего артефакта.");
      return;
    }
    state.htmlWorkspaceSelection = { type: type, id: id };
    renderHtmlWorkspace();
  }

  function selectedEditorValue(selected) {
    if (!selected) {
      return "";
    }
    if (selected.type === "plan") {
      try { return JSON.stringify(JSON.parse(artifactInlineText(selected.item)), null, 2); }
      catch (error) { return artifactInlineText(selected.item); }
    }
    if (selected.type === "artifact") return artifactInlineText(selected.item);
    return selected.type === "data" ? dataJson(selected.item) : fileContent(selected.item);
  }

  function renderHtmlWorkspaceEditor() {
    var selected = selectedItem();
    var empty = $("htmlWorkspaceEmptyState");
    var editor = $("htmlWorkspaceEditor");
    var title = $("htmlWorkspaceTitle");
    var meta = $("htmlWorkspaceMeta");
    var hasItems = !!selected;
    var isPlan = !!selected && selected.type === "plan";
    var isArtifact = !!selected && selected.type === "artifact";
    if (editor) {
      editor.classList.toggle("is-empty", !hasItems);
    }
    if (empty) {
      empty.classList.toggle("hidden", hasItems);
    }
    if (title) {
      title.textContent = selected
        ? (isPlan || isArtifact ? artifactTitle(selected.item) : (selected.type === "data" ? dataName(selected.item) : filePath(selected.item)))
        : "Артефакт не выбран";
    }
    if (meta) {
      meta.textContent = selected
        ? (isPlan ? "План · JSON · v" + artifactRevision(selected.item) : (isArtifact ? workspaceArtifacts.typeLabel(artifactKind(selected.item)) + " · только чтение" : (selected.type === "data" ? "JSON data source" : (fileKind(selected.item) || "file"))))
        : "";
    }
    var previewButton = document.querySelector('.html-workspace-mode-button[data-html-mode="preview"]');
    var editButton = document.querySelector('.html-workspace-mode-button[data-html-mode="edit"]');
    if (previewButton) previewButton.textContent = isPlan ? "План" : "Просмотр";
    if (editButton) {
      editButton.textContent = isPlan ? "JSON" : "Код";
      editButton.classList.toggle("hidden", isArtifact);
    }
    if (isArtifact) state.htmlWorkspaceMode = "preview";
    if ($("saveHtmlWorkspaceButton")) $("saveHtmlWorkspaceButton").classList.toggle("hidden", isArtifact);
    if ($("deleteHtmlWorkspaceButton")) $("deleteHtmlWorkspaceButton").classList.toggle("hidden", isArtifact);
    if (typeof setCodeEditorValue === "function") {
      setCodeEditorValue("htmlWorkspaceEditorInput", selectedEditorValue(selected));
    } else if ($("htmlWorkspaceEditorInput")) {
      $("htmlWorkspaceEditorInput").value = selectedEditorValue(selected);
    }
    if (typeof setCodeEditorReadOnly === "function") setCodeEditorReadOnly("htmlWorkspaceEditorInput", isArtifact);
    renderHtmlWorkspacePreview();
  }

  function renderHtmlWorkspacePreview() {
    var frame = $("htmlWorkspacePreviewFrame");
    var detail = $("artifactDetailPreview");
    if (!frame || !detail) {
      return;
    }
    var selected = selectedItem();
    var special = selected && (selected.type === "plan" || selected.type === "artifact");
    frame.classList.toggle("hidden", !!special);
    detail.classList.toggle("hidden", !special);
    if (special) {
      workspaceArtifacts.renderDetail(detail, selected, selectedEditorValue(selected));
      frame.removeAttribute("src");
      frame.srcdoc = "";
      return;
    }
    detail.replaceChildren();
    frame.removeAttribute("src");
    frame.srcdoc = htmlPreview.build({
      activeFileId: workspace().activeFileId,
      dataSources: dataSources(),
      files: files()
    });
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
    try {
      if (selected.type === "plan") {
        await savePlanArtifact(selected.item);
        return;
      } else if (selected.type === "data") {
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
      log("Артефакт сохранён.");
    } catch (error) {
      log(error.detail || error.message);
      window.alert(error.message || "Артефакт не сохранён.");
    }
  }

  async function refreshArtifactsAfterPlanChange(planId) {
    var response = await send("selectChat", { chatId: state.activeChatId });
    if (typeof applyChatState === "function") applyChatState(response);
    var latest = latestPlanArtifacts().filter(function (artifact) { return planStableId(artifact) === planId; })[0] || null;
    state.htmlWorkspaceSelection = latest
      ? { type: "plan", id: artifactId(latest) }
      : { type: "file", id: "" };
    state.htmlWorkspaceDirty = false;
    renderHtmlWorkspace();
  }

  async function savePlanArtifact(artifact) {
    var plan = workspaceArtifacts.validatePlanDraft(artifact);
    var result = await send("runTool", {
      toolId: "common.plan_update",
      arguments: { id: plan.id, goal: plan.goal, steps: plan.steps },
      dryRun: false
    });
    if (!(result && (result.Success === true || result.success === true))) {
      throw new Error(result && (result.Message || result.message) || "План не сохранён.");
    }
    await refreshArtifactsAfterPlanChange(plan.id);
    log("План сохранён как новая версия.");
  }

  async function deleteHtmlWorkspaceSelection() {
    var selected = selectedItem();
    if (!selected || state.bridgeUnavailable) {
      return;
    }

    var label = selected.type === "plan" ? artifactTitle(selected.item) : (selected.type === "data" ? dataName(selected.item) : filePath(selected.item));
    var warning = selected.type === "plan"
      ? "Удалить план «" + label + "» и все его версии?"
      : "Удалить «" + label + "» из HTML? Удаление можно отменить через Undo.";
    if (state.htmlWorkspaceDirty) {
      warning = "Есть несохраненные изменения. " + warning;
    }
    if (!window.confirm(warning)) {
      return;
    }

    try {
      if (selected.type === "plan") {
        var planId = planStableId(selected.item);
        var result = await send("runTool", { toolId: "common.plan_delete", arguments: { id: planId }, dryRun: false });
        if (!(result && (result.Success === true || result.success === true))) throw new Error(result && (result.Message || result.message) || "План не удалён.");
        await refreshArtifactsAfterPlanChange(planId);
        log("План удалён: " + label);
        return;
      }
      var response = selected.type === "data"
        ? await send("deleteHtmlWorkspaceData", {
          chatId: state.activeChatId,
          name: dataName(selected.item)
        })
        : await send("deleteHtmlWorkspaceFile", {
          chatId: state.activeChatId,
          path: filePath(selected.item)
        });
      state.htmlWorkspaceSelection = { type: "file", id: "" };
      applyHtmlWorkspaceResponse(response);
      log("Удалено из HTML: " + label);
    } catch (error) {
      log(error.detail || error.message);
      window.alert(error.message || "Элемент HTML workspace не удален.");
    }
  }

  async function undoHtmlWorkspace() {
    if (state.bridgeUnavailable || !historyItems().length) {
      return;
    }
    if (state.htmlWorkspaceDirty && !window.confirm("Есть несохраненные изменения. Вернуть предыдущую версию?")) {
      return;
    }

    try {
      applyHtmlWorkspaceResponse(await send("restoreHtmlWorkspaceSnapshot", {
        chatId: state.activeChatId,
        snapshotId: prop(historyItems()[0], "Id", "id", "")
      }));
      log("HTML workspace восстановлен.");
    } catch (error) {
      log(error.detail || error.message);
      window.alert(error.message || "HTML workspace не восстановлен.");
    }
  }

  async function redoHtmlWorkspace() {
    if (state.bridgeUnavailable || !redoItems().length) {
      return;
    }
    if (state.htmlWorkspaceDirty && !window.confirm("Есть несохраненные изменения. Повторить отмененную версию?")) {
      return;
    }

    try {
      applyHtmlWorkspaceResponse(await send("redoHtmlWorkspaceSnapshot", {
        chatId: state.activeChatId,
        snapshotId: prop(redoItems()[0], "Id", "id", "")
      }));
      log("HTML workspace redo выполнен.");
    } catch (error) {
      log(error.detail || error.message);
      window.alert(error.message || "HTML workspace redo не выполнен.");
    }
  }

  async function addHtmlWorkspaceFile(kind) {
    if (state.bridgeUnavailable) {
      return;
    }
    var fallback = kind === "css" ? "styles.css" : (kind === "script" ? "app.js" : "index.html");
    showHtmlWorkspaceCreate(kind, fallback);
  }

  async function addPlan() {
    if (state.bridgeUnavailable) return;
    try {
      var result = await send("runTool", {
        toolId: "common.plan_create",
        arguments: {
          goal: "Новый план",
          steps: [{ id: "step_1", text: "Опишите первый шаг", status: "pending" }]
        },
        dryRun: false
      });
      if (!(result && (result.Success === true || result.success === true))) throw new Error(result && (result.Message || result.message) || "План не создан.");
      var payload = {};
      try { payload = JSON.parse(result.DataJson || result.dataJson || "{}"); } catch (ignore) {}
      var plan = payload.plan || payload.Plan || {};
      await refreshArtifactsAfterPlanChange(plan.id || plan.Id || "");
      state.htmlWorkspaceMode = "preview";
      renderHtmlWorkspace();
      log("План создан.");
    } catch (error) {
      log(error.detail || error.message);
      window.alert(error.message || "План не создан.");
    }
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
    }
  }

  async function addHtmlWorkspaceData() {
    if (state.bridgeUnavailable) {
      return;
    }
    showHtmlWorkspaceCreate("data", "data");
  }

  async function createHtmlWorkspaceData(name) {
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
    $("deleteHtmlWorkspaceButton").addEventListener("click", deleteHtmlWorkspaceSelection);
    $("undoHtmlWorkspaceButton").addEventListener("click", undoHtmlWorkspace);
    $("redoHtmlWorkspaceButton").addEventListener("click", redoHtmlWorkspace);
    $("toggleHtmlSidebarButton").addEventListener("click", toggleHtmlWorkspaceSidebar);
    $("addPlanButton").addEventListener("click", addPlan);
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
  window.confirmDiscardHtmlWorkspaceChanges = confirmDiscardHtmlWorkspaceChanges;
}());
