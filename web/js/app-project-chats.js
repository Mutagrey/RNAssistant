(function () {
  "use strict";
  var scope = "office", catalog = null, catalogRevision = 0, catalogBusy = false,
    creatingSessionId = null, serial = 0;
  var selectedWorkspaceId = null, expanded = {}, query = "", connectionMode = "connect", connectionRequest = null;
  var treeRenderKey = null;
  var noticeText = "", noticeError = false;
  function currentPanel() {
    var panel = window.RNAssistantConversationPanel && RNAssistantConversationPanel.selected();
    return panel && panel.target.source === "workspace" ? panel : null;
  }
  function currentSnapshot() { var panel = currentPanel(); return panel ? panel.snapshot() : null; }
  function acceptSnapshot(snapshot, options) {
    var target = { source: "workspace", workspaceId: snapshot.project.workspaceId, sessionId: snapshot.sessionId };
    RNAssistantConversationPanel.open(target);
    return RNAssistantConversationPanel.find(target).accept(snapshot, options);
  }
  function el(id) {
    return window.RNAssistantWorkspaceChat.node(id) || document.getElementById("projectChat" + id);
  }
  async function readProjectChat(target, component) {
    var source = window.RNAssistantWorkspaceChat.snapshotSource(target);
    target = source.target;
    var drafts = window.RNAssistantChatDrafts, pending = drafts.pendingWorkspaceInput(target);
    if (!pending) {
      // Progress may already have acknowledged/cleared the draft while the
      // admission reply itself was lost. The transport still owns its identity.
      Object.keys(state.pending).some(function (id) {
        var waiting = state.pending[id], payload = waiting.payload || {};
        if (waiting.type !== "submitProjectChat" || payload.workspaceId !== target.workspaceId ||
            payload.sessionId !== target.sessionId || !payload.input) return false;
        pending = { input: payload.input, draftVersion: null }; return true;
      });
    }
    var query = { workspaceId: target.workspaceId, sessionId: target.sessionId };
    if (pending) query.receiptRequestId = pending.input.requestId;
    var snapshot = await source.read(function () { return send("getProjectChat", query); });
    var groupVersion = RNAssistantChatGroups.version(); RNAssistantChatGroups.accept(snapshot.groupScopes);
    if (groupVersion !== RNAssistantChatGroups.version()) renderTree();
    if (pending &&
        window.RNAssistantConversationInput.accepted("workspace", target, pending.input.requestId,
          snapshot.inputReceipt, pending.input.SteerRunId, pending.input.Enqueue)) {
      if (pending.input.EditMessageId) drafts.acknowledgeWorkspaceInput(target, pending.input);
      else if (component) {
        var before = readWorkspaceDraft(target);
        acceptWorkspaceInput(target, pending.input, pending.input.message, pending.draftVersion);
        component.acceptDraft(before, readWorkspaceDraft(target));
      } else acceptWorkspaceInput(target, pending.input, pending.input.message, pending.draftVersion);
      // The original admission reply may never arrive. The durable receipt is
      // enough to settle that exact submit, including its transport idle timer.
      Object.keys(state.pending).forEach(function (id) {
        var waiting = state.pending[id], payload = waiting.payload || {};
        if (waiting.type === "submitProjectChat" && payload.workspaceId === target.workspaceId &&
            payload.sessionId === target.sessionId && payload.input &&
            payload.input.requestId === pending.input.requestId) takeBridgePending(id).resolve(snapshot);
      });
    }
    return snapshot;
  }
  function node(tag, className, text) {
    var result = document.createElement(tag);
    result.className = className || "";
    if (text !== undefined) result.textContent = text;
    return result;
  }
  function projects() { return catalog && catalog.projects || []; }
  function replaceCatalog(value) { catalog = value; RNAssistantChatGroups.accept(value.groupScopes); ++catalogRevision; }
  async function refreshCatalog(isCurrent) {
    for (var attempt = 0; attempt < 3; attempt++) {
      var revision = catalogRevision, result = await send("getProjectChats", {});
      if (isCurrent && !isCurrent()) return false;
      if (revision !== catalogRevision) continue;
      replaceCatalog(result);
      return true;
    }
    throw new Error("Список проектов изменился во время обновления. Повторите обновление.");
  }
  function project() { return projects().find(function (item) { return item.workspaceId === selectedWorkspaceId; }); }
  function projectId() { return selectedWorkspaceId; }
  function currentId() { return field(currentSnapshot(), "sessionId", "SessionId"); }
  function chatRequestKey(workspaceId, sessionId) { return JSON.stringify([workspaceId, sessionId]); }
  function chatBusy() { return !!currentSnapshot() && window.RNAssistantConversationInput.isPending("workspace",
    { workspaceId: projectId(), sessionId: currentId() }); }
  function field(value, lower, upper) { return value && (value[lower] !== undefined ? value[lower] : value[upper]); }
  function runState() { return field(currentSnapshot(), "state", "State"); }
  function runStatus(state) { return window.RNAssistantWorkspaceChat.runStatus(state || runState()); }
  function notice(text, error) {
    noticeText = text || ""; noticeError = !!error;
    var panel = currentPanel(); if (panel && error) panel.notice(text);
  }
  function statusText() {
    var active = currentSnapshot(), state = active && active.state;
    if (!state) return "";
    var status = runStatus(state);
    var runNotice = window.RNAssistantWorkspaceChat.runNotice(active);
    if (runNotice) return runNotice.title + ". " + runNotice.text;
    if (active.pendingId) return "Нужно подтвердить действие агента.";
    if (state.userInput) return "Нужен ваш ответ.";
    if (field(state, "interruptedEffectPossible", "InterruptedEffectPossible")) return "Возможное изменение требует проверки. Не повторяйте действие автоматически.";
    if (status === "running")
      return window.RNAssistantConversationState.status(
        !field(state, "modelProgressUnavailable"), true, false).label + "…";
    if (status === "interrupted") return "Выполнение прервано. Проверьте ход работы перед новым запросом.";
    return "";
  }
  function chatState(entry, chat) {
    var panel = window.RNAssistantConversationPanel && RNAssistantConversationPanel.find({ source: "workspace", workspaceId: entry.workspaceId, sessionId: chat.Id });
    return panel && panel.snapshot().state;
  }
  function chatRunStatus(entry, chat) {
    var current = chatState(entry, chat);
    return current ? runStatus(current) : window.RNAssistantConversationState.lifecycle(chat.RunStatus);
  }
  function runBlocksDeletion(status) { return status === "running" || status === "awaiting_confirmation"; }
  function historyBlocked(entry) {
    return (entry.chats || []).some(function (chat) { return runBlocksDeletion(chatRunStatus(entry, chat)); }) ||
      entry.workspaceId === projectId() && !!currentId() && runBlocksDeletion(runStatus());
  }
  function renderTree() {
    var root = el("Tree"), selected = currentId(), needle = query.trim().toLocaleLowerCase("ru");
    var pluginSource = window.RNAssistantPlugins && window.RNAssistantPlugins.sourceWorkspaceId();
    var selectedRun = runState();
    var key = JSON.stringify([catalogRevision, RNAssistantChatGroups.version(), selectedWorkspaceId, selected, needle, pluginSource,
      runStatus(selectedRun), field(selectedRun, "title", "Title"),
      field(selectedRun, "modelProgressUnavailable", "ModelProgressUnavailable"),
      projects().map(function (entry) { return (entry.chats || []).map(function (chat) { var data = chatState(entry, chat);
        return [chat.Id, chatRunStatus(entry, chat), field(data, "title", "Title")]; }); })]);
    if (key === treeRenderKey && root.firstChild) return;
    treeRenderKey = key;
    var nodes = projects().map(function (entry) {
      var groupScope = { host: "Workspace", documentKey: entry.workspaceId, workspaceId: entry.workspaceId,
        sessionId: entry.chats && entry.chats.length ? entry.chats[0].Id : null, disabled: !entry.available };
      var ownMatch = entry.name.toLocaleLowerCase("ru").includes(needle);
      var items = (entry.chats || []).filter(function (chat) {
        return !needle || ownMatch || chat.Title.toLocaleLowerCase("ru").includes(needle) ||
          RNAssistantChatGroups.matches(groupScope, chat.Id, needle);
      }).map(function (chat) {
        var runStatus = chatRunStatus(entry, chat);
        var current = chatState(entry, chat);
        var presence = window.RNAssistantConversationState.status(
          runStatus === "running" && !!current && !field(current, "modelProgressUnavailable"),
          runStatus === "running",
          runStatus === "awaitingconfirmation");
        return { key: "project-chat:" + entry.workspaceId + ":" + chat.Id, kind: "chat",
          title: entry.workspaceId === projectId() && chat.Id === selected && runState()
            ? field(runState(), "title", "Title") || chat.Title : chat.Title,
          selected: entry.workspaceId === projectId() && chat.Id === selected,
          status: presence.status, statusLabel: presence.label,
          actions: (entry.pluginId ? [{ title: "Открыть чат в плагине", iconHtml: window.RNAssistantNavigationTree.icon("code"),
            disabled: !entry.available, onClick: function () { window.RNAssistantPlugins.select(entry.pluginId, chat.Id); } }] : []).concat([
            { title: "Диагностика чата", iconHtml: window.RNAssistantNavigationTree.icon("chart"), disabled: !entry.available,
              onClick: function () { window.openRunJournal({ owner: entry.pluginId ? "plugin" : "project", workspaceId: entry.workspaceId,
                sessionId: chat.Id, pluginId: entry.pluginId, label: chat.Title, document: entry.name,
                runId: field(current, "runId", "RunId") || field(field(chat, "runViewState", "RunViewState"), "runId", "RunId") }); } },
            { title: "Переименовать чат «" + chat.Title + "»", iconHtml: window.RNAssistantNavigationTree.icon("edit"),
              disabled: !entry.available, onClick: function () { rename(entry, chat); } },
            { title: "Удалить чат «" + chat.Title + "»", iconHtml: window.RNAssistantNavigationTree.icon("trash"), danger: true,
              disabled: !entry.available || runBlocksDeletion(runStatus),
              onClick: function () { remove(entry, chat); } }
          ]), workspaceId: entry.workspaceId, sessionId: chat.Id };
      });
      items = RNAssistantChatGroups.decorate(items, entry.chats || [], groupScope, needle);
      if (needle && !ownMatch && !items.length) return null;
      return { key: "project:" + entry.workspaceId, kind: "parent", selectable: true,
        title: entry.name, selected: entry.workspaceId === projectId() && !currentSnapshot(),
        badge: entry.available ? "" : "нет папки",
        tooltip: entry.rootPath + (entry.available ? "" : " · папка недоступна"),
        iconHtml: window.RNAssistantNavigationTree.icon("folder"), expanded: needle ? true : expanded[entry.workspaceId] !== false,
        workspaceId: entry.workspaceId, children: items,
        actions: [
          RNAssistantChatGroups.createAction(groupScope),
          { title: "Новый чат в «" + entry.name + "»", iconHtml: window.RNAssistantNavigationTree.icon("plus"),
            disabled: !entry.available, onClick: function () {
              if (pluginSource === entry.workspaceId) create();
              else { selectProject(entry.workspaceId, false); create(); }
            } },
          { title: "Открыть «" + entry.name + "»", iconHtml: window.RNAssistantNavigationTree.icon("external"),
            onClick: function () { selectProject(entry.workspaceId); } },
          { title: "Удалить историю «" + entry.name + "»", iconHtml: window.RNAssistantNavigationTree.icon("trash"), danger: true,
            disabled: historyBlocked(entry), onClick: function () { removeHistory(entry); } }
        ] };
    }).filter(Boolean);
    window.RNAssistantNavigationTree.render(root, nodes, {
      label: "Проекты и чаты", emptyText: projects().length ? "Ничего не найдено." : "Подключите папку проекта, чтобы видеть её чаты.",
      onSelect: function (item) {
        RNAssistantChatGroups.select(item);
        var entry = projects().find(function (project) { return project.workspaceId === item.workspaceId; });
        if (entry && entry.pluginId && window.RNAssistantPlugins)
          window.RNAssistantPlugins.select(entry.pluginId, item.sessionId);
        else window.RNAssistantProjectChats.open(item.workspaceId, item.sessionId);
      },
      onToggle: function (item, isExpanded) { if (item.chatGroupBranch) RNAssistantChatGroups.toggle(item, isExpanded);
        else expanded[item.workspaceId] = isExpanded; }
    });
  }
  function updateControls() {
    var panel = currentPanel(); if (panel) panel.controls();
    el("New").disabled = catalogBusy || !project() || !project().available;
    el("ConnectionCancel").textContent = connectionRequest ? "Скрыть" : "Отмена";
    el("ConnectionCancel").title = connectionRequest ? "Операция продолжится; проект появится в дереве." : "";
    ["Connect", "Create", "ConnectionSubmit"].forEach(function (name) { el(name).disabled = catalogBusy; });
  }
  function render(options) {
    renderTree(); updateControls();
    var panel = currentPanel(); if (panel) panel.render(options);
  }
  async function refresh(target) {
    var version = ++serial;
    try { if (!await refreshCatalog(function () { return version === serial; })) return; }
    catch (error) { if (version === serial) notice(error.detail || error.message, true); return; }
    if (version !== serial) return;
    var previousProject = projectId(), previousChat = currentId();
    var result = catalog;
    if (target && target.workspaceId) selectedWorkspaceId = target.workspaceId;
    else if (result.selectedWorkspaceId) selectedWorkspaceId = result.selectedWorkspaceId;
    else if (!projects().some(function (item) { return item.workspaceId === selectedWorkspaceId; }))
      selectedWorkspaceId = (projects().find(function (item) { return item.available; }) || projects()[0] || {}).workspaceId || null;
    notice("");
    render();
    var chats = project() && project().chats || [];
    var remembered = window.RNAssistantWorkspaceChat.selectedSession(projectId());
    var wanted = target && target.sessionId || remembered || (previousProject === projectId() ? previousChat : null);
    if (wanted && !chats.some(function (item) { return item.Id === wanted; })) {
      window.RNAssistantWorkspaceChat.rememberSession(projectId(), null);
      if (target && target.sessionId) { render(); notice("Чат отсутствует в этом проекте.", true); return; }
      wanted = null;
    }
    wanted = wanted || chats[0] && chats[0].Id;
    if (wanted && project().available) await open(projectId(), wanted);
    else if (!wanted) render();
  }
  function selectProject(workspaceId, openFirst) {
    if (!workspaceId || !projects().some(function (item) { return item.workspaceId === workspaceId; })) return;
    if (selectedWorkspaceId !== workspaceId) { ++serial; selectedWorkspaceId = workspaceId; }
    render();
    var entry = project();
    if (openFirst !== false && entry.available && entry.chats && entry.chats.length) open(workspaceId, entry.chats[0].Id);
  }
  async function open(workspaceId, sessionId) {
    selectedWorkspaceId = workspaceId;
    if (!project() || !project().available) return;
    window.RNAssistantWorkspaceChat.rememberSession(workspaceId, sessionId);
    RNAssistantConversationPanel.open({ source: "workspace", workspaceId: workspaceId, sessionId: sessionId });
    renderTree();
  }
  async function create() {
    if (catalogBusy || !project() || !project().available) return;
    var version = ++serial, pid = projectId(); catalogBusy = true; creatingSessionId = null; render();
    try {
      var result = await send("createProjectChat", { workspaceId: pid, title: "Новый чат" });
      window.RNAssistantWorkspaceChat.snapshotSource({ workspaceId: pid, sessionId: result && result.sessionId }).accept(result);
      var selected = version === serial && projectId() === pid;
      if (selected) {
        creatingSessionId = result.sessionId;
        acceptSnapshot(result); window.RNAssistantWorkspaceChat.rememberSession(pid, result.sessionId);
        notice(statusText()); render();
      }
      try { await refreshCatalog(); render(); }
      catch (error) { if (selected) notice("Чат создан, но список не обновлён: " + (error.detail || error.message), true); }
      return selected && version === serial && currentId() === result.sessionId ? result : null;
    } catch (error) { if (version === serial && projectId() === pid) notice(error.detail || error.message, true); }
    finally { creatingSessionId = null; catalogBusy = false; render(); }
  }
  async function rename(entry, chat) {
    var title = window.prompt("Название чата", chat.Title);
    if (title === null || !title.trim() || title.trim() === chat.Title) return;
    var version = serial, panel = currentPanel(), owner = panel && panel.projection();
    try { var result = await send("renameProjectChat", { workspaceId: entry.workspaceId,
        sessionId: chat.Id, title: title.trim() });
      var returnedProject = (result.projects || []).find(function (item) { return item.workspaceId === entry.workspaceId; });
      var returnedChat = returnedProject && (returnedProject.chats || []).find(function (item) { return item.Id === chat.Id; });
      var currentProject = projects().find(function (item) { return item.workspaceId === entry.workspaceId; });
      var currentChat = currentProject && (currentProject.chats || []).find(function (item) { return item.Id === chat.Id; });
      if (returnedChat && currentChat) { currentChat.Title = returnedChat.Title; ++catalogRevision; }
      if (returnedChat && owner && owner.isCurrent() && owner.snapshot() &&
          owner.target.workspaceId === entry.workspaceId && owner.target.sessionId === chat.Id) {
        var data = owner.snapshot().state;
        data.Title = data.title = returnedChat.Title;
      }
      if (version === serial) notice("");
      render(); }
    catch (error) { if (version === serial) notice(error.detail || error.message, true); }
  }
  async function remove(entry, chat) {
    if (!window.confirm("Удалить чат «" + chat.Title + "»? История этого чата будет удалена; файлы проекта останутся.")) return;
    try { await window.RNAssistantChatDrafts.flushWorkspace({ workspaceId: entry.workspaceId, sessionId: chat.Id });
      await send("deleteProjectChat", { workspaceId: entry.workspaceId, sessionId: chat.Id });
      var currentProject = projects().find(function (item) { return item.workspaceId === entry.workspaceId; });
      if (currentProject) {
        currentProject.chats = (currentProject.chats || []).filter(function (item) { return item.Id !== chat.Id; });
        ++catalogRevision;
      }
      RNAssistantConversationPanel.closeDeleted({ source: "workspace", workspaceId: entry.workspaceId, sessionId: chat.Id });
      window.RNAssistantChatDrafts.forgetWorkspace({ workspaceId: entry.workspaceId, sessionId: chat.Id });
      if (window.RNAssistantWorkspaceChat.selectedSession(entry.workspaceId) === chat.Id)
        window.RNAssistantWorkspaceChat.rememberSession(entry.workspaceId, null);
      var removedSelected = projectId() === entry.workspaceId && currentId() === chat.Id;
      if (removedSelected) notice("");
      render();
      if (!removedSelected) return;
      var selected = project();
      if (window.RNAssistantWorkspaceChat.owner() === "plugin" && window.RNAssistantPlugins &&
          window.RNAssistantPlugins.sourceWorkspaceId() === entry.workspaceId)
        await window.RNAssistantPlugins.select(entry.pluginId,
          selected && selected.chats && selected.chats[0] && selected.chats[0].Id);
      else if (selected && selected.available && selected.chats.length)
        open(selected.workspaceId, selected.chats[0].Id); }
    catch (error) { if (projectId() === entry.workspaceId && currentId() === chat.Id)
      notice(error.detail || error.message, true); }
  }
  async function removeHistory(entry) {
    if (!window.confirm("Удалить историю проекта «" + entry.name + "» и убрать его из дерева? Папка и файлы останутся.")) return;
    try { await window.RNAssistantChatDrafts.flushAll();
      await send("deleteProjectHistory", { workspaceId: entry.workspaceId });
      if (catalog) {
        catalog.projects = projects().filter(function (item) { return item.workspaceId !== entry.workspaceId; });
        ++catalogRevision;
      }
      (entry.chats || []).forEach(function (chat) {
        RNAssistantConversationPanel.closeDeleted({ source: "workspace", workspaceId: entry.workspaceId, sessionId: chat.Id });
      window.RNAssistantChatDrafts.forgetWorkspace({ workspaceId: entry.workspaceId, sessionId: chat.Id });
      });
      window.RNAssistantWorkspaceChat.rememberSession(entry.workspaceId, null);
      var removedSelected = projectId() === entry.workspaceId;
      var pluginSelected = removedSelected && window.RNAssistantWorkspaceChat.owner() === "plugin" &&
        window.RNAssistantPlugins && window.RNAssistantPlugins.sourceWorkspaceId() === entry.workspaceId;
      if (removedSelected) {
        ++serial;
        selectedWorkspaceId = (projects().find(function (item) { return item.available; }) || projects()[0] || {}).workspaceId || null;
        notice("");
      }
      render();
      if (pluginSelected) await window.RNAssistantPlugins.select(entry.pluginId);
      else if (removedSelected && project() && project().available && project().chats && project().chats.length && !currentSnapshot())
        open(projectId(), project().chats[0].Id); }
    catch (error) { var selected = projectId() === entry.workspaceId;
      await refresh(selected ? null : { workspaceId: projectId(), sessionId: currentId() });
      if (selected) notice(error.detail || error.message, true); }
  }
  function setScope(next, target) {
    RNAssistantChatGroups.dismissDialog();
    scope = next; ++serial;
    if (next === "projects") window.RNAssistantWorkspaceChat.mount("project");
    document.getElementById("chatLayout").hidden = next === "projects";
    el("Layout").hidden = next !== "projects";
    if (next === "projects") render();
    else window.RNAssistantWorkspaceChat.mount("office");
    document.querySelectorAll(".chat-scope-switcher").forEach(function (picker) { picker.open = false; });
    var loaded;
    if (next === "projects") {
      var known = target && projects().some(function (entry) {
        return entry.workspaceId === target.workspaceId && entry.available &&
          (entry.chats || []).some(function (chat) { return chat.Id === target.sessionId; });
      });
      if (known) {
        // Explicit navigation can read the addressed chat immediately. Refresh
        // the tree independently; a slow catalog must not gate the detail read.
        loaded = open(target.workspaceId, target.sessionId);
        var version = serial;
        refreshCatalog(function () { return version === serial; })
          .then(function (refreshed) { if (refreshed && version === serial) render(); })
          .catch(function () { /* The addressed chat read reports its own error. */ });
      } else loaded = refresh(target);
    }
    if (window.RNAssistantShellLayout) window.RNAssistantShellLayout.refresh();
    window.RNAssistantDockWorkspace.refreshPresentation();
    return loaded;
  }
  function bind() {
    window.RNAssistantWorkspaceChat.mount("office");
    document.querySelectorAll("[data-workspace-scope]").forEach(function (button) {
      button.addEventListener("click", function () { setScope(button.dataset.workspaceScope); });
    });
    document.querySelector("[data-project-scope-icon]").innerHTML = window.RNAssistantNavigationTree.icon("folder");
    el("Refresh").addEventListener("click", refresh);
    el("New").addEventListener("click", function () {
      create();
    });
    el("Search").addEventListener("input", function () { query = this.value; renderTree(); });
    [["Connect", "connect"], ["Create", "create"]].forEach(function (entry) {
      el(entry[0]).addEventListener("click", function () {
        el("WorkspaceMenu").open = false;
        connectionMode = entry[1]; el("Connection").hidden = false;
        el("ConnectionTitle").textContent = entry[1] === "create" ? "Создать workspace" : "Подключить папку";
        el("ConnectionSubmit").textContent = entry[1] === "create" ? "Создать" : "Подключить";
        el("ConnectionPath").focus();
      });
    });
    el("ConnectionCancel").addEventListener("click", function () {
      if (connectionRequest) connectionRequest.cancelled = true;
      el("Connection").hidden = true;
    });
    el("Connection").addEventListener("submit", async function (event) {
      event.preventDefault(); if (catalogBusy) return;
      var path = el("ConnectionPath").value.trim(); if (!path) return;
      var version = ++serial, mode = connectionMode, request = { cancelled: false };
      connectionRequest = request;
      catalogBusy = true; render();
      try {
        var result = await send(mode === "create" ? "createProjectWorkspace" : "connectProjectWorkspace", { rootPath: path });
        var connected = (result.projects || []).find(function (item) { return item.workspaceId === result.selectedWorkspaceId; });
        if (!catalog || !connected) replaceCatalog(result);
        else {
          var existing = projects().find(function (item) { return item.workspaceId === connected.workspaceId; });
          if (existing) {
            var chats = existing.chats;
            Object.assign(existing, connected);
            if (chats) existing.chats = chats;
          } else catalog.projects.push(connected);
          ++catalogRevision;
        }
        if (version === serial && !request.cancelled && !el("Connection").hidden) {
          selectedWorkspaceId = result.selectedWorkspaceId; notice("");
        }
        el("Connection").hidden = true;
        if (el("ConnectionPath").value.trim() === path) el("ConnectionPath").value = "";
        render();
      } catch (error) { if (version === serial && !request.cancelled && !el("Connection").hidden)
        notice(error.detail || error.message, true); }
      finally { if (connectionRequest === request) connectionRequest = null;
        catalogBusy = false; render(); }
    });
    render();
  }
  document.addEventListener("DOMContentLoaded", bind);
  window.RNAssistantProjectChats = { show: function (options) {
    if (options && options.preserveChatOwner) {
      render(); window.RNAssistantDockWorkspace.refreshPresentation(); return;
    }
    window.RNAssistantWorkspaceChat.mount(scope === "projects" ? "project" : "office");
    if (scope === "projects") { render(); refresh(); }
    window.RNAssistantDockWorkspace.refreshPresentation();
  }, scope: function () { return scope; }, workspaceId: projectId, setScope: setScope,
    refresh: refresh, refreshTree: renderTree, refreshControls: updateControls, render: render, create: create,
    snapshot: currentSnapshot, read: readProjectChat,
    focus: function (target) { selectedWorkspaceId = target.workspaceId; renderTree(); },
    targets: function () { return projects().filter(function (entry) { return entry.available; }).flatMap(function (entry) {
      return (entry.chats || []).map(function (chat) { return { title: entry.name + " · " + (chat.Title || "Чат"),
        target: { view: "conversation", source: "workspace", workspaceId: entry.workspaceId, sessionId: chat.Id } }; });
    }); },
    isBusy: function () { return catalogBusy || chatBusy(); },
    open: function (workspaceId, sessionId) { switchTab("chat"); return setScope("projects", { workspaceId: workspaceId, sessionId: sessionId }); } };
}());
