(function () {
  "use strict";
  if (!document.getElementById("artifactCatalog")) return;
  var generation = 0, previewGeneration = 0, pending = false, response = null, next = null, chat = null, previewUrl = null;
  var typedPreview = null;
  var sequencePreview = null;
  var selected = null, selectedRevision = null, collapsed = {}, searchTimer, loading = false, overviewRequested = true;
  var overviewLoading = false, overviewError = "";
  var overviewSort = { key: "updated", descending: true };
  function el(id) { return document.getElementById("artifactCatalog" + id); }
  function node(tag, text, className) {
    var result = document.createElement(tag); result.textContent = text || "";
    if (className) result.className = className;
    return result;
  }
  function status(text) { el("Status").textContent = text || ""; el("PreviewStatus").textContent = text || ""; }
  function icon(item) {
    var suffix = String(item.title || "").split(".").pop().toLowerCase();
    if (item.kind === "file" || item.kind === "attachment") return suffix === "json" ? "braces" : "file";
    return ({ html_workspace: "chart", markdown: "file", plan_document: "layers", task_list: "check",
      sequence: "layers" })[item.kind] || item.kind;
  }
  function overviewFormat(item) {
    var suffix = String(item.title || "").split(".").pop().toLowerCase();
    if (item.kind === "file" || item.kind === "attachment") return ({ json: "JSON", pdf: "PDF", txt: "Текст", md: "Markdown" })[suffix] || "Файл";
    return ({ html_workspace: "HTML", markdown: "Markdown", plan_document: "План", task_list: "Задачи",
      image: "Изображение", sequence: "Коллекция" })[item.kind] || item.kind || "Файл";
  }
  function overviewOwner(item) {
    return item.linked ? "Этот чат" : item.documentId === response.documentId ? "В документе" : item.documentTitle || "Другой документ";
  }
  function overviewDate(value) {
    var date = new Date(value);
    if (Number.isNaN(date.getTime())) return "—";
    var today = new Date(), midnight = new Date(today.getFullYear(), today.getMonth(), today.getDate());
    var dateMidnight = new Date(date.getFullYear(), date.getMonth(), date.getDate());
    if (dateMidnight.getTime() === midnight.getTime()) return date.toLocaleTimeString("ru-RU", { hour: "2-digit", minute: "2-digit" });
    if (dateMidnight.getTime() === midnight.getTime() - 86400000) return "Вчера";
    return date.toLocaleDateString("ru-RU", { day: "numeric", month: "short" });
  }
  function current(id, nav) { return id === state.activeChatId && nav === state.chatNavigationVersion; }
  function operationId() { return Array.from(crypto.getRandomValues(new Uint8Array(16)), function (n) { return n.toString(16).padStart(2, "0"); }).join(""); }
  function render() {
    if (typeof renderHtmlWorkspace === "function") renderHtmlWorkspace();
    renderOverview();
  }
  function renderOverview() {
    var overview = el("Overview"), rows = el("OverviewRows");
    if (!overview) return;
    var createMenu = document.getElementById("artifactCreateMenu");
    if (createMenu) {
      var menuHome = overviewRequested ? overview.querySelector(".artifact-overview-heading") :
        document.querySelector("#tab-artifacts .artifact-catalog-toolbar .tree-toolbar");
      if (menuHome && createMenu.parentNode !== menuHome) {
        createMenu.open = false;
        menuHome.appendChild(createMenu);
      }
      createMenu.classList.toggle("artifact-overview-create", overviewRequested);
      createMenu.querySelector("summary").className = overviewRequested ? "button primary" : "icon-btn";
    }
    overview.hidden = !overviewRequested;
    if (overviewRequested) el("All").setAttribute("aria-current", "page");
    else el("All").removeAttribute("aria-current");
    if (!overviewRequested) return;
    $("htmlWorkspaceEditor").hidden = true;
    el("Preview").hidden = true;
    rows.replaceChildren();
    document.querySelectorAll("#artifactCatalogOverviewTable [data-artifact-sort-head]").forEach(function (head) {
      var key = head.getAttribute("data-artifact-sort-head");
      if (key === overviewSort.key) head.setAttribute("aria-sort", overviewSort.descending ? "descending" : "ascending");
      else head.removeAttribute("aria-sort");
    });
    var items = !overviewLoading && !overviewError && response && response.items || [];
    items.forEach(function (item) {
      var row = document.createElement("tr"), name = document.createElement("td"), open = node("button", "", "rna-table-link artifact-overview-link");
      open.type = "button";
      open.innerHTML = RNAssistantNavigationTree.icon(icon(item));
      open.appendChild(node("span", item.title));
      name.appendChild(open); row.appendChild(name);
      row.appendChild(node("td", overviewFormat(item)));
      var ownerCell = node("td", ""); ownerCell.appendChild(node("small",
        overviewOwner(item)));
      row.appendChild(ownerCell);
      var dateCell = node("td", ""); dateCell.appendChild(node("small", overviewDate(item.updatedUtc)));
      row.appendChild(dateCell);
      row.tabIndex = 0;
      row.setAttribute("aria-label", "Открыть материал «" + item.title + "»");
      row.addEventListener("click", function () { select(item.resourceUri); });
      row.addEventListener("keydown", function (event) {
        if (event.target !== row || event.key !== "Enter" && event.key !== " ") return;
        event.preventDefault(); select(item.resourceUri);
      });
      rows.appendChild(row);
    });
    el("OverviewTable").hidden = !items.length;
    el("OverviewEmpty").hidden = !!items.length;
    el("OverviewEmpty").textContent = overviewError || (overviewLoading ? "Загрузка материалов…" :
      state.activeChatId ? "По этим условиям материалов нет." : "Откройте чат документа, чтобы увидеть материалы.");
  }
  function clearPreview() {
    typedPreview = null;
    if (sequencePreview) sequencePreview.destroy();
    sequencePreview = null;
    el("PreviewBody").querySelectorAll(".artifact-viewer-host, .collection-page").forEach(function (target) {
      if (window.RNAssistantViewerRegistry) window.RNAssistantViewerRegistry.unmount(target);
    });
    if (window.RNAssistantViewerRegistry) window.RNAssistantViewerRegistry.unmount(el("PreviewBody"));
    previewGeneration++; el("PreviewBody").replaceChildren(); el("Preview").hidden = true;
    el("Preview").classList.remove("is-media-preview");
    delete el("PreviewBody").dataset.productViewer;
    $("htmlWorkspaceEditor").hidden = false;
    if (previewUrl) URL.revokeObjectURL(previewUrl); previewUrl = null;
  }
  function renderSequence(text) {
    var source = JSON.parse(text), items = source && source.items;
    if (!Array.isArray(items) || !items.length || items.length > 10000) throw new Error("Коллекция не содержит доступных элементов.");
    var body = el("PreviewBody"), page = node("div", "", "collection-page product-viewer");
    body.dataset.productViewer = "collection";
    function itemTitle(value, index) { return String(value && (value.region || value.title || value.name) || "Элемент " + (index + 1)); }
    function amount(value) {
      return typeof value === "number" && Number.isFinite(value)
        ? (value / 1000000).toLocaleString("ru-RU", { maximumFractionDigits: 2 }) + " млн ₽" : "";
    }
    function select(index) {
      var value = items[index], title = itemTitle(value, index), lines = ["# " + title];
      if (value && typeof value === "object") {
        Object.keys(value).forEach(function (key) {
          if (key === "region" || key === "title" || key === "name") return;
          var label = { revenue: "Выручка", changePercent: "Изменение к прошлому периоду", status: "Статус" }[key] || key;
          var content = key === "revenue" ? amount(value[key]) : key === "changePercent" && typeof value[key] === "number"
            ? value[key].toLocaleString("ru-RU") + "%" :
            typeof value[key] === "string" ? value[key] : JSON.stringify(value[key]);
          lines.push("**" + label.replace(/[\\*_`]/g, "\\$&") + "** — " + String(content).replace(/[\\*_`]/g, "\\$&") + ".");
        });
      } else lines.push(String(value));
      window.RNAssistantViewerRegistry.mount("markdown", page, {
        text: lines.join("\n\n"), fullText: lines.join("\n\n"), complete: true, sourceComplete: true,
        onCopy: window.copyTextResult
      });
    }
    var rail = window.RNAssistantSequenceViewer.createRail({
      title: "Элементы", ariaLabel: "Элементы коллекции", count: items.length,
      currentIndex: 0, orientation: "horizontal", className: "collection-rail",
      showJump: false, showNumbers: false, itemExtent: 156,
      getItem: function (index) { return items[index]; },
      itemLabel: function (index, value) { return itemTitle(value, index); },
      onSelect: select,
      renderItem: function (index, value, preview) {
        var title = node("strong", itemTitle(value, index));
        var summary = value && (amount(value.revenue) || value.status) || "";
        preview.append(title, node("span", String(summary)));
        return "ready";
      }
    });
    sequencePreview = rail;
    body.append(rail.element, page);
    select(0);
  }
  function refreshViewer() {
    if (!typedPreview || el("Preview").hidden || !current(typedPreview.chatId, typedPreview.navigation)) return;
    var body = el("PreviewBody"), currentPreview = typedPreview;
    body.querySelectorAll(".artifact-viewer-host").forEach(function (target) {
      window.RNAssistantViewerRegistry.unmount(target);
    });
    body.replaceChildren(); body.dataset.productViewer = currentPreview.kind;
    var viewer = window.RNAssistantHtmlWorkspaceViewerActions.artifactViewerState(currentPreview.uri);
    if (viewer && viewer.status === "error") {
      showRawMedia(currentPreview.title, currentPreview.mime, currentPreview.url,
        "Просмотр страниц недоступен: " + (viewer.message || "ошибка чтения") + ". Открыт исходный файл.");
      typedPreview = null;
      return;
    }
    var artifact = { Kind: currentPreview.kind, Title: currentPreview.title,
      ResourceUri: currentPreview.uri, MimeType: currentPreview.mime };
    if (!window.RNAssistantHtmlWorkspaceArtifacts.appendTypedViewer(body, artifact,
      window.RNAssistantHtmlWorkspaceViewerActions)) {
      status("Просмотр этого формата недоступен.");
    }
  }
  function showRawMedia(title, mime, url, reason) {
    var body = el("PreviewBody");
    body.replaceChildren(); body.dataset.productViewer = /^image\//i.test(mime) ? "image" : "pdf";
    if (reason) body.appendChild(node("p", reason, "artifact-viewer-fallback-note"));
    if (/^image\/(?:jpeg|png|gif|webp)(?:;|$)/i.test(mime)) {
      var target = node("div", "", "artifact-viewer-host artifact-typed-viewer product-viewer");
      body.appendChild(target);
      window.RNAssistantViewerRegistry.mount("image", target, {
        title: title, mimeType: mime, data: { objectUrl: url }
      });
    } else if (/^image\//i.test(mime)) {
      var image = document.createElement("img"); image.src = url; image.alt = title; body.appendChild(image);
    } else {
      var pdf = document.createElement("iframe"); pdf.src = url; pdf.title = title; body.appendChild(pdf);
    }
  }
  function show(scope) {
    if (!canMutate()) return false;
    if (scope) el("Scope").value = scope;
    if (scope) {
      selected = null; selectedRevision = null; overviewRequested = true; clearPreview(); actions();
    }
    load();
    return true;
  }
  function backToOverview() {
    if (!canMutate()) return false;
    selected = null; selectedRevision = null; overviewRequested = true;
    clearPreview(); actions(); render();
    return true;
  }
  function editor(artifact, preserveOverview) {
    if (!preserveOverview) overviewRequested = false;
    selectedRevision = null;
    clearPreview();
    if (response) {
      var selection = state.htmlWorkspaceSelection || {};
      var id = selection.type === "file" || selection.type === "data" ? state.activeHtmlArtifactId : selection.id;
      var uri = artifact && (artifact.ResourceUri || artifact.resourceUri);
      selected = response.items.find(function (item) {
        var local = localArtifact(item);
        return uri ? item.resourceUri === uri : local && (local.Id || local.id) === id;
      }) || null;
    }
    actions(); renderOverview();
  }
  function sync() {
    if (chat !== state.activeChatId) {
      chat = state.activeChatId; generation++; response = null; selected = null; selectedRevision = null; next = null;
      overviewRequested = true; overviewError = ""; overviewLoading = false;
      clearPreview(); actions();
      if (typeof isPanelActive === "function" && isPanelActive("artifacts")) load();
    } else if (response && !loading && !pending && state.chatProjectionRevisions &&
        state.chatProjectionRevisions[chat] > response.sessionRevision &&
        typeof isPanelActive === "function" && isPanelActive("artifacts")) {
      load();
    }
  }
  async function load(cursor) {
    var id = state.activeChatId, nav = state.chatNavigationVersion, version = ++generation;
    if (!id) { response = null; selected = null; selectedRevision = null; overviewError = ""; overviewLoading = false;
      clearPreview(); actions(); status("Откройте или создайте чат документа."); render(); return; }
    chat = id; loading = true; overviewLoading = !cursor; overviewError = "";
    status("Загрузка…"); el("More").hidden = true; renderOverview();
    try {
      var result = await send("listArtifactCatalog", { chatId: id, scope: el("Scope").value, query: $("artifactNavSearch").value,
        kind: el("Kind").value, documentId: "", sortBy: overviewSort.key, sortDescending: overviewSort.descending,
        cursor: cursor || null });
      if (!current(id, nav) || version !== generation) return;
      if (result.chatId !== id) throw new Error("Получено дерево другого чата.");
      if (cursor && response) result.items = response.items.concat(result.items);
      response = result; next = result.nextCursor;
      if (selected) {
        selected = result.items.find(function (item) { return item.resourceUri === selected.resourceUri; }) || null;
        if (!selected) selectedRevision = null;
        if (!selected && !state.htmlWorkspaceDirty) {
          if (el("Preview").hidden) editor(null, true);
          if (!selected) { clearPreview(); if (!overviewRequested) emptyPreview(); }
        }
      }
      el("More").hidden = !next;
      status(result.items.length ? "" : "Артефакты не найдены.");
      if (!selected && el("Preview").hidden) editor(null, true);
      actions(); render();
    } catch (error) {
      if (current(id, nav) && version === generation) {
        overviewError = error.detail || error.message || "Не удалось загрузить материалы.";
        status(overviewError);
      }
    } finally {
      if (version === generation) { loading = false; overviewLoading = false; renderOverview(); }
    }
  }
  function emptyPreview() {
    $("htmlWorkspaceEditor").hidden = true; el("Preview").hidden = false;
    el("PreviewTitle").textContent = "Выберите артефакт";
    el("PreviewBody").appendChild(node("p", "Выберите файл или проект в дереве слева."));
  }
  function canMutate() {
    if (pending) return false;
    if (state.htmlWorkspaceDirty) { status("Сохраните или отмените правки перед изменением артефактов."); return false; }
    return true;
  }
  async function mutate(command, args, button) {
    if (!canMutate()) return;
    var id = state.activeChatId, nav = state.chatNavigationVersion, edit = state.htmlWorkspaceEditVersion || 0;
    pending = true; actions(); if (button) button.disabled = true;
    try {
      var result = await send(command, args);
      if (!current(id, nav) || edit !== (state.htmlWorkspaceEditVersion || 0)) return;
      applyChatStateForChat(result, id); await load(); return true;
    } catch (error) {
      if (current(id, nav)) { await load(); status(error.detail || error.message); }
    }
    finally { pending = false; actions(); }
  }
  async function changeLink(id, revision, uri, detached, button) {
    if (id !== state.activeChatId) return;
    return mutate("changeArtifactLink", { chatId: id, expectedSessionRevision: revision, resourceUri: uri, detached: detached }, button);
  }
  async function copyItem(item, button, listed) {
    var copied = await mutate("copyArtifact", { chatId: listed.chatId, expectedSessionRevision: listed.sessionRevision,
      documentId: item.documentId, resourceUri: item.resourceUri, operationId: operationId() }, button);
    if (copied) { editor(); render(); }
    return copied;
  }
  function remove(item, listed, button) {
    if (!canMutate()) return;
    if (!window.confirm("Удалить «" + item.title + "» из документа «" + item.documentTitle + "»?\n\nАртефакт исчезнет из библиотеки и всех чатов этого документа. Его нельзя будет подключить или редактировать. Ссылки в истории и сохранённые старые версии останутся.")) return;
    return mutate("deleteDocumentArtifact", { chatId: listed.chatId, expectedSessionRevision: listed.sessionRevision,
      resourceUri: item.resourceUri, confirmed: true }, button);
  }
  function treeAction(actionId, uri) {
    var listed = response;
    var item = listed && listed.items.find(function (entry) { return entry.resourceUri === uri; });
    if (!item || item.documentId !== listed.documentId) return false;
    if (actionId === "detach" && item.linked) {
      return changeLink(listed.chatId, listed.sessionRevision, uri, true);
    }
    if (actionId === "delete-document" && item.canDelete) return remove(item, listed);
    return false;
  }
  function actions() {
    var root = el("Actions"); root.replaceChildren(); root.hidden = !selected || overviewRequested;
    if (!selected || !response) return;
    var item = selected, listed = response, id = state.activeChatId, nav = state.chatNavigationVersion;
    function action(parent, label, iconName, fn, disabled, className) {
      var b = node("button", label, className); b.type = "button"; b.disabled = pending || !!disabled;
      b.insertAdjacentHTML("afterbegin", RNAssistantIcons.render(iconName));
      b.addEventListener("click", function () { if (!b.disabled && current(id, nav)) fn(b); }); parent.appendChild(b);
    }
    var heading = node("div", "", "artifact-detail-heading");
    heading.appendChild(node("span", "Материалы / " + (item.linked ? "Этот чат" :
      item.documentId === listed.documentId ? "В документе" : item.documentTitle || "Все документы")));
    heading.appendChild(node("h2", item.title + (selectedRevision ? " · v" + selectedRevision.revision : "")));
    root.appendChild(heading);
    var menu = node("details", "", "html-workspace-menu artifact-action-menu");
    var summary = node("summary", "⋯");
    summary.setAttribute("aria-label", "Действия с материалом"); menu.appendChild(summary);
    var options = node("div", "", "html-workspace-menu-items"); menu.appendChild(options);
    var artifact = localArtifact(item);
    var head = artifact && window.RNAssistantArtifactVisuals && window.RNAssistantArtifactVisuals.libraryHead(artifact);
    if (window.RNAssistantArtifactVersions) window.RNAssistantArtifactVersions.append(options, head, selectedRevision, function (revision) {
      if (selected !== item || response !== listed || !current(id, nav) || !canMutate()) return false;
      if (revision.resourceUri === item.resourceUri) { select(item.resourceUri); return true; }
      selectedRevision = revision;
      actions();
      preview(Object.assign({}, item, { resourceUri: revision.resourceUri, title: revision.title || item.title }), revision);
      render();
      return true;
    });
    if (selectedRevision) {
      options.appendChild(node("span", "Только просмотр", "artifact-version-readonly"));
      action(options, "К текущей версии", "rotateLeft", function () { select(item.resourceUri); });
      if (item.canTransfer) action(options, "Скачать v" + selectedRevision.revision, "download", function () {
        download(Object.assign({}, item, { resourceUri: selectedRevision.resourceUri }));
      });
      root.appendChild(menu);
      return;
    }
    if (item.documentId === listed.documentId) {
      action(options, item.linked ? "Убрать из чата" : "Подключить к чату", item.linked ? "minus" : "link", async function (b) {
        if (await changeLink(id, listed.sessionRevision, item.resourceUri, item.linked, b)) {
          if (selected && !item.linked) select(selected.resourceUri);
          else if (selected) preview(selected);
        }
      }, !item.linked && item.availabilityIssue);
    }
    if (item.canTransfer) {
      action(options, "Независимая копия", "copy", function (b) { copyItem(item, b, listed); });
      action(options, item.kind === "html_workspace" ? "Экспорт проекта" : "Скачать", "download", function () { download(item); });
    }
    if (item.canDelete) action(options, "Удалить из документа…", "trash", function (b) { remove(item, listed, b); }, false, "danger-soft");
    if (options.children.length) root.appendChild(menu);
  }
  function localArtifact(item) {
    return (state.artifacts || []).find(function (a) { return (a.ResourceUri || a.resourceUri) === item.resourceUri; });
  }
  async function select(uri) {
    if (!canMutate() || !response) return false;
    var item = response.items.find(function (value) { return value.resourceUri === uri; });
    if (!item) return false;
    overviewRequested = false;
    selected = item; selectedRevision = null; clearPreview(); actions();
    if (item.availabilityIssue) {
      $("htmlWorkspaceEditor").hidden = true; el("Preview").hidden = false; el("PreviewTitle").textContent = item.title;
      el("PreviewBody").appendChild(node("p", "Артефакт недоступен. Можно убрать ссылку из чата или удалить артефакт из документа.")); render(); return true;
    }
    var id = state.activeChatId, nav = state.chatNavigationVersion;
    if (item.linked && item.documentId === response.documentId) {
      if ((item.kind === "html_workspace" || item.kind === "plan_document") && !item.selected) {
        if (!await changeLink(id, response.sessionRevision, uri, false) || !current(id, nav)) return false;
        item = selected || item;
      }
      var artifact = localArtifact(item);
      if (artifact && typeof window.openArtifactResource === "function") {
        state.htmlWorkspaceMode = "preview"; window.openArtifactResource(artifact); render(); return true;
      }
    }
    preview(item); render(); return true;
  }
  function tree(nodes, htmlChildren, options) {
    if (!response) return null;
    var byId = {}, chatGroup = null, documentGroup = null, otherGroup = null, htmlGroups = {};
    function makeGroup(key, title) {
      return { key: key, groupKey: key, title: title, iconKind: null, expanded: !collapsed[key], children: [] };
    }
    function owner(item) {
      if (item.linked) return chatGroup || (chatGroup = makeGroup("catalog-owner::chat", "Этот чат"));
      if (item.documentId && item.documentId !== response.documentId) {
        return otherGroup || (otherGroup = makeGroup("catalog-owner::all", "Все документы"));
      }
      return documentGroup || (documentGroup = makeGroup("catalog-owner::document", "В документе"));
    }
    response.items.forEach(function (item) {
      var artifact = localArtifact(item), artifactId = artifact && (artifact.Id || artifact.id);
      if (artifactId) byId[artifactId] = item.resourceUri;
      var entry = { key: "catalog::" + item.resourceUri, itemType: "artifact", itemId: item.resourceUri,
        title: item.title, iconKind: icon(item), meta: item.availabilityIssue ? "Недоступен" : (item.linked ? "В чате" : ""),
        tooltip: item.title + " · " + item.documentTitle,
        expanded: !collapsed["catalog::" + item.resourceUri] };
      if (item.documentId === response.documentId) {
        entry.rowActions = [];
        if (item.linked) entry.rowActions.push({ id: "detach", title: "Убрать из чата",
          iconKind: "minus" });
        if (item.canDelete) entry.rowActions.push({ id: "delete-document", title: "Удалить из документа…",
          iconKind: "trash", danger: true });
      }
      if (item.kind === "html_workspace" && item.documentId === response.documentId && item.selected) {
        entry.children = htmlChildren; entry.groupKey = "catalog::" + item.resourceUri;
      }
      var parent = owner(item);
      if (item.kind === "html_workspace") {
        var htmlKey = "catalog-html::" + parent.groupKey;
        if (!htmlGroups[htmlKey]) {
          htmlGroups[htmlKey] = makeGroup(htmlKey, "HTML-проекты");
          htmlGroups[htmlKey].iconKind = "folder";
          parent.children.push(htmlGroups[htmlKey]);
        }
        htmlGroups[htmlKey].children.push(entry);
      } else parent.children.push(entry);
    });
    // Preserve chat-only generated/system artifacts and a new, unsaved workspace.
    if (el("Scope").value === "chat") nodes.forEach(function (group) {
      if (group.groupKey === "artifacts:html") {
        if (!response.items.some(function (item) { return item.kind === "html_workspace" && item.selected; })) {
          group.title = "Новый HTML-проект";
          (chatGroup || (chatGroup = makeGroup("catalog-owner::chat", "Этот чат"))).children.push(group);
        }
      } else {
        var children = (group.children || []).filter(function (child) { return !byId[child.itemId]; });
        if (children.length) {
          var target = chatGroup || (chatGroup = makeGroup("catalog-owner::chat", "Этот чат"));
          target.children.push.apply(target.children, children);
        }
      }
    });
    var roots = [chatGroup, documentGroup, otherGroup].filter(Boolean);
    var selection = options.selected || {}, selectedKey = !overviewRequested && selected ? "catalog::" + selected.resourceUri : null;
    if (!overviewRequested && el("Preview").hidden && selection.id) {
      selectedKey = byId[selection.id] ? "catalog::" + byId[selection.id] : null;
    }
    return { nodes: roots, selectedKey: selectedKey };
  }
  async function read(item, preview, id, nav, isCurrent) {
    var transfer;
    try {
      transfer = await send("exportArtifact", { chatId: id, documentId: item.documentId, resourceUri: item.resourceUri, preview: preview });
      var bytes = await window.RNAssistantResourceDownload.read(transfer.data, { maxBytes: 20 * 1024 * 1024,
        fetch: window.fetch.bind(window), isCurrent: isCurrent || function () { return current(id, nav); } });
      return { transfer: transfer, bytes: bytes };
    } finally {
      if (transfer && transfer.data) await send("closeArtifactTransfer", { chatId: id, leaseId: transfer.data.leaseId }).catch(function () {});
    }
  }
  async function download(item) {
    var id = state.activeChatId, nav = state.chatNavigationVersion;
    try {
      status("Подготовка файла…"); var result = await read(item, false, id, nav);
      if (!current(id, nav)) return;
      var url = URL.createObjectURL(new Blob([result.bytes], { type: result.transfer.contentType }));
      var a = document.createElement("a"); a.href = url; a.download = result.transfer.fileName; a.click();
      setTimeout(function () { URL.revokeObjectURL(url); }, 1000); status("Файл подготовлен для скачивания.");
    } catch (error) { if (current(id, nav)) status(error.detail || error.message); }
  }
  async function previewSnapshot(views) {
    var snapshot = { version: 2, resources: [], parts: [], generations: {} };
    for (var view of views) {
      var reference = view.Reference;
      var resource = { name: view.Name, descriptor: { reference: reference, title: view.Name }, view: view.View,
        path: view.Path, maxBatchItems: 32000, maxBatchBytes: 8 * 1024 * 1024, parts: [] };
      var total = view.Rows ? view.Rows.length : view.Text.length;
      var batchSize = view.Rows ? 500 : 32000;
      for (var offset = 0; offset < total || offset === 0;) {
        var end = Math.min(offset + batchSize, total);
        var batch = { resource: reference, view: view.View, offset: offset, nextOffset: end, done: end === total,
          coverage: { kind: view.Rows ? "record-range" : "character-range", start: offset, end: end, path: view.Path, fields: [] } };
        if (view.Rows) { batch.rows = view.Rows.slice(offset, end); batch.columns = view.Columns; }
        else batch.text = view.Text.slice(offset, end);
        var text = JSON.stringify(batch).replace(/</g, "\\u003c");
        var hash = Array.from(new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text))), function (n) { return n.toString(16).padStart(2, "0"); }).join("");
        var part = { id: "rn-export-part-" + snapshot.parts.length, offset: offset, nextOffset: end,
          done: end === total, byteLength: new TextEncoder().encode(text).length, sha256: hash };
        snapshot.parts.push({ id: part.id, text: text }); resource.parts.push(part);
        if (end === total) break;
        offset = end;
      }
      snapshot.resources.push(resource);
    }
    return snapshot;
  }
  async function preview(item, revision) {
    var id = state.activeChatId, nav = state.chatNavigationVersion, version;
    var previousPicker = revision && el("PreviewBody").querySelector('select[aria-label="Файл проекта"]');
    var previousSource = revision && el("PreviewBody").querySelector(".artifact-catalog-source");
    var previousPath = previousPicker && previousPicker.value;
    var showSource = !!revision && (previousSource ? !previousSource.hidden : state.htmlWorkspaceMode === "edit");
    clearPreview(); version = previewGeneration;
    $("htmlWorkspaceEditor").hidden = true; el("Preview").hidden = false;
    var title = item.title + (revision ? " · v" + revision.revision : "");
    el("PreviewTitle").textContent = title; status("Загрузка предпросмотра…");
    try {
      var result = await read(item, true, id, nav, function () { return current(id, nav) && version === previewGeneration; });
      if (!current(id, nav) || version !== previewGeneration) return;
      el("PreviewTitle").textContent = title; el("Preview").hidden = false;
      var mime = result.transfer.contentType || "";
      if (item.kind === "html_workspace") {
        var envelope = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(result.bytes));
        var project = envelope.Project;
        var files = project.Files.map(function (f) { return { id: f.Path.toLowerCase(), path: f.Path, kind: f.Kind, content: f.Content }; });
        var picker = document.createElement("select"); picker.setAttribute("aria-label", "Файл проекта");
        files.forEach(function (f) { picker.appendChild(new Option(f.path, f.id)); });
        var pre = node("pre", "", "artifact-catalog-source");
        function source() { pre.textContent = files.find(function (f) { return f.id === picker.value; }).content; }
        var selection = state.htmlWorkspaceSelection || {};
        var currentFile = revision && selection.type === "file" && ((state.htmlWorkspace || {}).files || [])
          .find(function (f) { return (f.id || f.Id) === selection.id; });
        var path = previousPath || currentFile && (currentFile.path || currentFile.Path);
        var selectedFile = path && files.find(function (f) { return f.path.toLowerCase() === path.toLowerCase(); });
        picker.addEventListener("change", source); picker.value = (selectedFile || files[0]).id; source();
        if (path && !selectedFile) el("PreviewBody").appendChild(node("p", "Файл «" + path + "» отсутствует в этой версии проекта."));

        if (project.ExternalDependencies.length) el("PreviewBody").appendChild(node("p", "Внешние зависимости: " + project.ExternalDependencies.join(", ")));
        // Source inspection is always available; use the established sandbox renderer for HTML.
        {
          var snapshot = await previewSnapshot(envelope.Views);
          await window.RNAssistantHtmlWorkspacePreview.ensureVendors(files);
          if (!current(id, nav) || version !== previewGeneration) return;
          var frame = document.createElement("iframe"); frame.title = item.title; frame.setAttribute("sandbox", "allow-scripts");
          frame.srcdoc = window.RNAssistantHtmlWorkspacePreview.build({ files: files, activeFileId: project.EntryPath.toLowerCase(), dataSources: project.Data.map(function (d) { return { name: d.Name }; }), resourceSnapshot: snapshot, hostBridge: false });
          el("PreviewBody").appendChild(frame);
        }
        var modes = node("div", "", "view-switch artifact-preview-modes");
        var viewButton = node("button", "Просмотр", "button compact active"), sourceButton = node("button", "Код", "button compact");
        var toolbar = node("div", "", "material-viewer-toolbar");
        var copy = node("button", "", "icon-btn material-copy-button");
        var copyStatus = node("span", "", "material-copy-status");
        viewButton.type = sourceButton.type = copy.type = "button";
        modes.setAttribute("role", "group"); modes.setAttribute("aria-label", "Режим материала");
        copy.setAttribute("aria-label", "Копировать выбранный файл");
        copy.title = "Копировать выбранный файл";
        copy.innerHTML = RNAssistantIcons.render("copy");
        copyStatus.setAttribute("role", "status");
        copy.addEventListener("click", function () {
          Promise.resolve(window.copyTextResult(pre.textContent)).then(function () { copyStatus.textContent = "Скопировано"; })
            .catch(function () { copyStatus.textContent = "Не удалось скопировать"; });
        });
        function mode(code) {
          frame.hidden = code; picker.hidden = !code; pre.hidden = !code;
          viewButton.classList.toggle("active", !code); sourceButton.classList.toggle("active", code);
          viewButton.setAttribute("aria-pressed", String(!code)); sourceButton.setAttribute("aria-pressed", String(code));
        }
        viewButton.addEventListener("click", function () { mode(false); }); sourceButton.addEventListener("click", function () { mode(true); });
        modes.appendChild(viewButton); modes.appendChild(sourceButton);
        toolbar.appendChild(modes); toolbar.appendChild(copy); toolbar.appendChild(copyStatus);
        el("PreviewBody").insertBefore(toolbar, el("PreviewBody").firstChild);
        el("PreviewBody").appendChild(picker); el("PreviewBody").appendChild(pre);
        mode(showSource);
      } else if (/^application\/pdf(?:;|$)/i.test(mime) || /^image\//i.test(mime)) {
        previewUrl = URL.createObjectURL(new Blob([result.bytes], { type: mime }));
        el("Preview").classList.add("is-media-preview");
        if (item.documentId === response.documentId &&
            (/^application\/pdf(?:;|$)/i.test(mime) || /^image\/(?:jpeg|png|gif|webp)(?:;|$)/i.test(mime))) {
          typedPreview = { chatId: id, navigation: nav, uri: item.resourceUri, url: previewUrl,
            title: item.title, mime: mime, kind: /^image\//i.test(mime) ? "image" : "pdf" };
          refreshViewer();
        } else showRawMedia(item.title, mime, previewUrl, "");
      } else if (/text|json|javascript|xml/.test(mime)) {
        var text = new TextDecoder("utf-8", { fatal: true }).decode(result.bytes);
        if (item.kind === "sequence") {
          try { renderSequence(text); }
          catch (error) {
            el("PreviewBody").appendChild(node("p", "Коллекция не открыта: " + error.message + " Исходные данные доступны ниже.", "artifact-viewer-fallback-note"));
            var rawSequence = node("div", "", "artifact-viewer-host product-viewer");
            el("PreviewBody").appendChild(rawSequence);
            window.RNAssistantViewerRegistry.mount("json", rawSequence, {
              text: text, pageText: text, fullText: text, complete: true,
              sourceComplete: true, completeness: "full", onCopy: window.copyTextResult
            });
          }
          status(""); return;
        }
        var kind = item.kind === "task_list" ? "task_list" :
          item.kind === "markdown" || item.kind === "plan_document" || /markdown/.test(mime) ? "markdown" : /json/.test(mime) ? "json" : "text";
        el("PreviewBody").dataset.productViewer = kind;
        window.RNAssistantViewerRegistry.mount(kind, el("PreviewBody"), {
          text: text, pageText: text, fullText: text, complete: true,
          sourceComplete: true, completeness: "full", onCopy: window.copyTextResult
        });
      } else el("PreviewBody").appendChild(node("p", "Файл: " + result.transfer.fileName + " · " + result.bytes.length + " байт. Используйте «Скачать» для открытия."));
      status("");
    } catch (error) { if (current(id, nav) && version === previewGeneration) status(error.detail || error.message); }
  }
  async function importFile(file) {
    if (!file || !canMutate()) return;
    if (file.size > 20 * 1024 * 1024) { status("Максимальный размер — 20 МиБ."); return; }
    var id = state.activeChatId, nav = state.chatNavigationVersion, lease;
    pending = true; actions();
    try {
      var catalogState = await send("listArtifactCatalog", { chatId: id, scope: "chat" });
      if (!current(id, nav)) return;
      status("Загрузка файла…");
      lease = await send("beginArtifactImport", { chatId: id, fileName: file.name, contentType: file.type || "application/octet-stream", byteLength: file.size });
      await window.RNAssistantResourceUpload.write(lease, file, { maxBytes: 20 * 1024 * 1024, isCurrent: function () { return current(id, nav); } });
      if (!current(id, nav) || state.htmlWorkspaceDirty) return;
      var edit = state.htmlWorkspaceEditVersion || 0;
      var result = await send("importArtifact", { chatId: id, expectedSessionRevision: catalogState.sessionRevision, uploadLeaseId: lease.leaseId, operationId: operationId() });
      if (!current(id, nav) || edit !== (state.htmlWorkspaceEditVersion || 0)) return;
      applyChatStateForChat(result, id); editor(); show("chat");
    } catch (error) { if (current(id, nav)) status(error.detail || error.message); }
    finally { pending = false; actions(); if (lease) await send("closeArtifactTransfer", { chatId: id, leaseId: lease.leaseId }).catch(function () {}); }
  }
  ["Scope", "Kind"].forEach(function (name) { el(name).addEventListener("change", function () { show(); }); });
  document.querySelectorAll("#artifactCatalogOverviewTable [data-artifact-sort]").forEach(function (button) {
    button.addEventListener("click", function () {
      var key = button.getAttribute("data-artifact-sort");
      overviewSort = { key: key, descending: key === overviewSort.key ? !overviewSort.descending : key === "updated" };
      load();
    });
  });
  $("artifactNavSearch").addEventListener("input", function () { clearTimeout(searchTimer); searchTimer = setTimeout(function () { show(); }, 200); });
  el("All").addEventListener("click", backToOverview);
  el("More").addEventListener("click", function () { load(next); });
  el("Import").addEventListener("click", function () { if (canMutate()) el("File").click(); });
  el("File").addEventListener("change", function () { var file = this.files[0]; this.value = ""; importFile(file); });
  window.RNAssistantArtifactCatalog = { show: show, editor: editor, sync: sync, changeLink: changeLink,
    refreshViewer: refreshViewer,
    tree: tree, select: select, canSelect: canMutate, treeAction: treeAction, remove: function (uri) {
      var item = response && response.items.find(function (item) { return item.resourceUri === uri; });
      if (!item || !item.canDelete) { status("Обновите дерево и выберите актуальную версию артефакта для удаления."); return false; }
      return remove(item, response);
    }, toggle: function (key, expanded) { collapsed[key] = !expanded; } };
}());
