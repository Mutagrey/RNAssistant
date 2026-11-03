(function () {
  "use strict";
  if (!document.getElementById("artifactCatalog")) return;
  var generation = 0, pending = false, response = null, next = null, chat = null;
  var selected = null, editorNavigation = false, collapsed = {}, loading = false, overviewRequested = true;
  var overviewLoading = false, overviewError = "", queryDirty = false, catalogNotice = "";
  var maximumCatalogItems = 500;
  var overviewSort = { key: "updated", descending: true };
  function el(id) { return document.getElementById("artifactCatalog" + id); }
  function node(tag, text, className) {
    var result = document.createElement(tag); result.textContent = text || "";
    if (className) result.className = className;
    return result;
  }
  var navigationPane;
  function navigation() {
    if (navigationPane) return navigationPane;
    navigationPane = RNAssistantNavigationPane.mount({
      root: $("htmlWorkspaceSidebar"), heading: $("materialNavigationHeading"), tree: $("htmlWorkspaceTree"),
      search: $("artifactNavSearch"), title: "Материалы", commands: $("artifactCreateCommands"),
      commandLabel: "Добавить материал", status: el("Status"), more: el("More")
    }, { searchDelay: 200, onQueryPending: function () { generation++; loading = false; queryDirty = true; },
      onQuery: function () { if (typeof isPanelActive !== "function" || isPanelActive("artifacts")) load(); }, onRetry: function () { load(); }, onMore: function () { if (!loading && next) load(next); } });
    return navigationPane;
  }
  function navigationState() {
    var message = el("Status").textContent;
    navigation().setState({ busy: loading, message: catalogNotice && message.indexOf(catalogNotice) < 0 ?
      [message, catalogNotice].filter(Boolean).join("\n") : message, retry: !!overviewError, more: !!next });
  }
  function status(text) { el("Status").textContent = text || ""; el("Message").textContent = text || ""; navigationState(); }
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
    return [item.documentTitle || "Документ", item.kind === "html_workspace" ? sourceChatTitle(item) : "", item.linked ? "В этом чате" : ""].filter(Boolean).join(" · ");
  }
  function sourceChatTitle(item) {
    if (item.sourceChatTitle) return item.sourceChatTitle;
    var source = (state.chats || []).find(function (chat) { return (chat.Id || chat.id) === item.sourceChatId; });
    return source && (source.Title || source.title) || (item.sourceChatId ? "Чат " + item.sourceChatId.slice(0, 8) : "Чат не указан");
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
    overview.hidden = !overviewRequested;
    if (overviewRequested) el("All").setAttribute("aria-current", "page");
    else el("All").removeAttribute("aria-current");
    if (!overviewRequested) return;
    $("htmlWorkspaceEditor").hidden = true;
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
  function show(scope) {
    if (!canMutate()) return false;
    if (scope) el("Scope").value = scope;
    if (scope) {
      selected = null; overviewRequested = true; $("htmlWorkspaceEditor").hidden = false; actions();
    }
    load();
    return true;
  }
  function backToOverview() {
    if (!canMutate()) return false;
    selected = null; overviewRequested = true;
    $("htmlWorkspaceEditor").hidden = false; actions(); render();
    return true;
  }
  function editorItem(artifact) {
    if (!response) return null;
    var selection = state.htmlWorkspaceSelection || {};
    var id = selection.type === "file" || selection.type === "data" ? state.activeHtmlArtifactId : selection.id;
    var uri = artifact && (artifact.ResourceUri || artifact.resourceUri);
    return response.items.find(function (item) {
      var local = localArtifact(item);
      return uri ? item.resourceUri === uri : local && (local.Id || local.id) === id;
    }) || null;
  }
  function editor(artifact, preserveOverview) {
    if (!preserveOverview) overviewRequested = false;
    $("htmlWorkspaceEditor").hidden = false;
    selected = editorItem(artifact); editorNavigation = true;
    actions(); renderOverview();
  }
  function sync() {
    if (chat !== state.activeChatId) {
      chat = state.activeChatId; generation++; response = null; selected = null; next = null;
      overviewRequested = true; overviewError = ""; overviewLoading = false; catalogNotice = "";
      $("htmlWorkspaceEditor").hidden = false; actions();
      if (typeof isPanelActive === "function" && isPanelActive("artifacts")) load();
    } else if (queryDirty && !loading && typeof isPanelActive === "function" && isPanelActive("artifacts")) {
      load();
    } else if (response && !loading && !pending && state.chatProjectionRevisions &&
        state.chatProjectionRevisions[chat] > response.sessionRevision &&
        typeof isPanelActive === "function" && isPanelActive("artifacts")) {
      load();
    }
  }
  async function load(cursor) {
    var id = state.activeChatId, nav = state.chatNavigationVersion, version = ++generation;
    if (!id) { response = null; next = null; loading = false; selected = null; overviewError = ""; overviewLoading = false; catalogNotice = "";
      $("htmlWorkspaceEditor").hidden = false; actions(); status("Откройте или создайте чат документа."); render(); return; }
    if (!cursor) next = null;
    queryDirty = false; chat = id; loading = true; overviewLoading = !cursor; overviewError = ""; catalogNotice = "";
    status("Загрузка…"); el("More").hidden = true; renderOverview();
    try {
      var result = await send("listArtifactCatalog", { chatId: id, scope: el("Scope").value, query: $("artifactNavSearch").value,
        kind: el("Kind").value, documentId: "", sortBy: overviewSort.key, sortDescending: overviewSort.descending,
        cursor: cursor || null });
      if (!current(id, nav) || version !== generation) return;
      if (result.chatId !== id) throw new Error("Получено дерево другого чата.");
      if (cursor && response) result.items = response.items.concat(result.items);
      var limited = result.items.length > maximumCatalogItems || result.items.length === maximumCatalogItems && !!result.nextCursor;
      result.items = result.items.slice(0, maximumCatalogItems);
      response = result; next = limited ? null : result.nextCursor;
      // A filter changes the tree, never the already opened exact resource.
      if (selected) selected = result.items.find(function (item) { return item.resourceUri === selected.resourceUri; }) || selected;
      el("More").hidden = !next;
      catalogNotice = limited ? "Показаны первые " + maximumCatalogItems + " материалов. Уточните поиск или фильтр." : "";
      status(result.items.length ? "" : "Артефакты не найдены.");
      if (!selected) editor(null, true);
      actions(); render();
    } catch (error) {
      if (current(id, nav) && version === generation) {
        overviewError = error.detail || error.message || "Не удалось загрузить материалы.";
        status(overviewError);
      }
    } finally {
      if (version === generation) { loading = false; overviewLoading = false; navigationState(); renderOverview(); }
    }
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
      resourceUri: item.resourceUri, confirmed: true }, button).then(function (removed) {
        if (removed && selected && selected.resourceUri === item.resourceUri) {
          selected = null; overviewRequested = true;
      $("htmlWorkspaceEditor").hidden = false; actions(); render();
        }
        return removed;
      });
  }
  function treeAction(actionId, uri, anchor) {
    var listed = response;
    var item = listed && listed.items.find(function (entry) { return entry.resourceUri === uri; });
    if (!item) return false;
    if (actionId === "menu" && anchor) return RNAssistantActionMenu.open(anchor, itemCommands(item, listed));
    if (item.documentId !== listed.documentId) return false;
    if (actionId === "detach" && item.linked) {
      return changeLink(listed.chatId, listed.sessionRevision, uri, true);
    }
    if (actionId === "delete-document" && item.canDelete) return remove(item, listed);
    return false;
  }
  function itemCommands(item, listed) {
    var id = listed.chatId, nav = state.chatNavigationVersion, artifact = localArtifact(item), items = [];
    function add(label, icon, run, disabled, danger) {
      items.push({ label: label, icon: icon, disabled: pending || !!disabled, danger: !!danger,
        onSelect: function (button) { if (current(id, nav) && response === listed) return run(button); } });
    }
    if (item.documentId === listed.documentId) add(item.linked ? "Убрать из чата" : "Подключить к чату", item.linked ? "minus" : "link",
      function (button) { return changeLink(id, listed.sessionRevision, item.resourceUri, item.linked, button); }, !item.linked && item.availabilityIssue);
    var requiresSelection = item.kind === "html_workspace" || item.kind === "plan_document";
    if (item.linked && item.documentId === listed.documentId && requiresSelection && !item.selected)
      add("Использовать в чате", "check", function (button) { return changeLink(id, listed.sessionRevision, item.resourceUri, false, button); }, !!item.availabilityIssue);
    if (artifact && (!requiresSelection || item.selected) && ["html_workspace", "plan_document", "markdown"].indexOf(item.kind) >= 0)
      add("Редактировать", "edit", function () {
        if (!canMutate()) return;
        state.htmlWorkspaceMode = "edit"; window.openArtifactResource(artifact, null, null, { purpose: "edit" }); render();
      });
    if (item.canTransfer) add("Независимая копия", "copy", function (button) { return copyItem(item, button, listed); });
    if (item.kind === "html_workspace" && item.canTransfer) add("Экспорт проекта", "download", function () { return download(item); });
    if (item.canDelete) add("Удалить из документа…", "trash", function (button) { return remove(item, listed, button); }, false, true);
    return items;
  }
  function actions() {
    var item = editorItem(), root = el("Actions"); root.replaceChildren(); root.hidden = !item || overviewRequested;
    if (!item || !response) return;
    var listed = response, id = state.activeChatId, nav = state.chatNavigationVersion;
    function action(parent, label, iconName, fn, disabled, className) {
      var b = node("button", label, className); b.type = "button"; b.disabled = pending || !!disabled;
      b.insertAdjacentHTML("afterbegin", RNAssistantIcons.render(iconName));
      b.addEventListener("click", function () { if (!b.disabled && current(id, nav)) fn(b); }); parent.appendChild(b);
    }
    var heading = node("div", "", "artifact-detail-heading");
    heading.appendChild(node("span", "Материалы / " + (item.linked ? "Этот чат" :
      item.documentId === listed.documentId ? "В документе" : item.documentTitle || "Все документы")));
    heading.appendChild(node("h2", item.title));
    root.appendChild(heading);
    var menu = node("details", "", "html-workspace-menu artifact-action-menu");
    var summary = node("summary", "⋯");
    summary.setAttribute("aria-label", "Действия с материалом"); menu.appendChild(summary);
    var options = node("div", "", "html-workspace-menu-items"); menu.appendChild(options);
    var artifact = localArtifact(item);
    var head = artifact && window.RNAssistantArtifactVisuals && window.RNAssistantArtifactVisuals.libraryHead(artifact);
    if (window.RNAssistantArtifactVersions) window.RNAssistantArtifactVersions.append(options, head, null, function (revision) {
      if (editorItem() !== item || response !== listed || !current(id, nav)) return false;
      return openItem(item, revision);
    });
    itemCommands(item, listed).forEach(function (entry) {
      action(options, entry.label, entry.icon, entry.onSelect, entry.disabled, entry.danger ? "danger-soft" : "");
    });
    if (options.children.length) root.appendChild(menu);
  }
  function localArtifact(item) {
    return (state.artifacts || []).find(function (a) { return (a.ResourceUri || a.resourceUri) === item.resourceUri; });
  }
  function select(uri) {
    navigation();
    var item = response && response.items.find(function (entry) { return entry.resourceUri === uri; });
    if (!item) return false;
    var opened = openItem(item);
    if (opened) { selected = item; editorNavigation = false; render(); }
    return opened;
  }
  function openItem(item, revision) {
    var artifact = localArtifact(item), head = artifact && RNAssistantArtifactVisuals.libraryHead(artifact);
    var target = RNAssistantMaterialView.target(item, { chatId: response.chatId, documentId: item.documentId,
      documentTitle: item.documentTitle, chatTitle: item.sourceChatId ? sourceChatTitle(item) : "", sessionRevision: response.sessionRevision, head: head });
    if (revision) Object.assign(target, { resourceUri: revision.resourceUri, revision: revision.revision, title: revision.title || item.title });
    return RNAssistantDockWorkspace.openTarget(target, { referencePanel: "materials" });
  }
  function tree(nodes, htmlChildren, options) {
    if (!response) return null;
    var byId = {}, documents = {}, chats = {};
    function makeGroup(key, title, iconKind) {
      return { key: key, groupKey: key, title: title, iconKind: iconKind || null, expanded: !collapsed[key], children: [] };
    }
    function documentGroup(item) {
      var id = item.documentId || response.documentId;
      if (!documents[id]) documents[id] = makeGroup("catalog-document::" + id, item.documentTitle ||
        ((response.documents || []).find(function (doc) { return doc.id === id; }) || {}).title || "Документ", "file");
      return documents[id];
    }
    function owner(item) {
      var parent = documentGroup(item);
      if (item.kind !== "html_workspace") return parent;
      var key = parent.key + "::chat::" + (item.sourceChatId || "unknown");
      if (!chats[key]) { chats[key] = makeGroup(key, sourceChatTitle(item), "chat"); parent.children.push(chats[key]); }
      return chats[key];
    }
    response.items.forEach(function (item) {
      var artifact = localArtifact(item), artifactId = artifact && (artifact.Id || artifact.id);
      if (artifactId) byId[artifactId] = item.resourceUri;
      var entry = { key: "catalog::" + item.resourceUri, itemType: "artifact", itemId: item.resourceUri,
        title: item.title, iconKind: icon(item), meta: item.availabilityIssue ? "Недоступен" : (item.linked ? "В чате" : ""),
        tooltip: item.title + " · " + item.documentTitle,
        expanded: !collapsed["catalog::" + item.resourceUri] };
      if (itemCommands(item, response).length) entry.rowActions = [{ id: "menu", title: "Действия с материалом", iconKind: "more", disabled: pending }];
      if (item.kind === "html_workspace" && item.documentId === response.documentId && item.selected) {
        entry.children = htmlChildren; entry.groupKey = "catalog::" + item.resourceUri;
      }
      owner(item).children.push(entry);
    });
    // Preserve chat-only generated/system artifacts and a new, unsaved workspace.
    if (el("Scope").value === "chat") nodes.forEach(function (group) {
      if (group.groupKey === "artifacts:html") {
        if (!response.items.some(function (item) { return item.kind === "html_workspace" && item.selected; })) {
          group.title = "Новый HTML-проект";
          owner({ documentId: response.documentId, kind: "html_workspace", sourceChatId: response.chatId }).children.push(group);
        }
      } else {
        var children = (group.children || []).filter(function (child) { return !byId[child.itemId]; });
        if (children.length) {
          var target = documentGroup({ documentId: response.documentId });
          target.children.push.apply(target.children, children);
        }
      }
    });
    var roots = Object.keys(documents).map(function (id) { return documents[id]; });
    var selection = options.selected || {}, selectedKey = selected ? "catalog::" + selected.resourceUri : null;
    if (editorNavigation && !overviewRequested && selection.id) {
      selectedKey = byId[selection.id] ? "catalog::" + byId[selection.id] : null;
    }
    return { nodes: roots, selectedKey: selectedKey };
  }
  function download(item) {
    var id = state.activeChatId, nav = state.chatNavigationVersion;
    return RNAssistantArtifactDownload.save({ chatId: id, documentId: item.documentId, resourceUri: item.resourceUri, title: item.title },
      { send: send, fetch: window.fetch.bind(window), isCurrent: function () { return current(id, nav); }, status: status });
  }
  async function importFile(file) {
    if (!file || !canMutate()) return;
    var id = state.activeChatId, nav = state.chatNavigationVersion, lease;
    pending = true; actions();
    try {
      var catalogState = await send("listArtifactCatalog", { chatId: id, scope: "chat" });
      if (!current(id, nav)) return;
      status("Загрузка файла…");
      lease = await send("beginArtifactImport", { chatId: id, fileName: file.name, contentType: file.type || "application/octet-stream", byteLength: file.size });
      await window.RNAssistantResourceUpload.write(lease, file, { maxBytes: Number.MAX_SAFE_INTEGER, isCurrent: function () { return current(id, nav); } });
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
  $("artifactOverviewCreate").addEventListener("click", function () { RNAssistantActionMenu.openButtons(this, $("artifactCreateCommands")); });
  el("All").addEventListener("click", backToOverview);
  el("Import").addEventListener("click", function () { if (canMutate()) el("File").click(); });
  el("File").addEventListener("change", function () { var file = this.files[0]; this.value = ""; importFile(file); });
  window.RNAssistantArtifactCatalog = { show: show, editor: editor, sync: sync, changeLink: changeLink,
    commands: function (target) {
      var item = response && target.chatId === state.activeChatId && response.chatId === target.chatId &&
        response.items.find(function (entry) { return entry.resourceUri === target.resourceUri; });
      return item ? itemCommands(item, response).filter(function (command) { return command.icon !== "download"; }) : [];
    },
    renderTree: function (root, nodes, options) { navigation().render(nodes, options); },
    tree: tree, select: select, treeAction: treeAction, remove: function (uri) {
      var item = response && response.items.find(function (item) { return item.resourceUri === uri; });
      if (!item || !item.canDelete) { status("Обновите дерево и выберите актуальную версию артефакта для удаления."); return false; }
      return remove(item, response);
    }, toggle: function (key, expanded) { collapsed[key] = !expanded; } };
}());
