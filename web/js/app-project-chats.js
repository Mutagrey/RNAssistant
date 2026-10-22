(function () {
  "use strict";
  var scope = "office", catalog = null, active = null, busy = false, serial = 0, poll = null, pendingSend = null, stopTitleWatch = null;
  var composer = null, uploadingFiles = false;
  var selectedWorkspaceId = null, expanded = {}, query = "", connectionMode = "connect";
  function el(id) { return document.getElementById("projectChat" + id); }
  function node(tag, className, text) {
    var result = document.createElement(tag);
    result.className = className || "";
    if (text !== undefined) result.textContent = text;
    return result;
  }
  function projects() { return catalog && catalog.projects || []; }
  function project() { return projects().find(function (item) { return item.workspaceId === selectedWorkspaceId; }); }
  function projectId() { return selectedWorkspaceId; }
  function currentId() { return active && active.sessionId; }
  function field(value, lower, upper) { return value && (value[lower] !== undefined ? value[lower] : value[upper]); }
  function runState() { return active && active.state; }
  function notice(text, error) {
    el("Notice").textContent = text || "";
    el("Notice").dataset.error = error ? "true" : "false";
  }
  function fileStatus(target, message, error) {
    if (projectId() !== target.workspaceId || currentId() !== target.sessionId) return;
    var box = el("FileStatus");
    box.textContent = message || ""; box.hidden = !message;
    box.classList.toggle("is-error", !!error);
    box.dataset.session = target.sessionId;
  }
  function fileChunkBase64(bytes) {
    var binary = "";
    for (var offset = 0; offset < bytes.length; offset += 0x8000)
      binary += String.fromCharCode.apply(null, bytes.subarray(offset, offset + 0x8000));
    return btoa(binary);
  }
  async function uploadProjectFile(target, file) {
    var lease = null, completing = false;
    if (!file || file.size < 1)
      throw new Error("Выберите непустой файл.");
    try {
      lease = await send("beginProjectChatFileUpload", { workspaceId: target.workspaceId,
        sessionId: target.sessionId, fileName: file.name, byteLength: file.size });
      var limit = Math.min(256 * 1024, lease.maxChunkBytes);
      if (!(limit > 0)) throw new Error("Приложение не сообщило допустимый размер фрагмента.");
      for (var offset = 0; offset < file.size;) {
        var bytes = new Uint8Array(await file.slice(offset, offset + limit).arrayBuffer());
        var progress = await send("writeProjectChatFileChunk", { workspaceId: target.workspaceId,
          sessionId: target.sessionId, leaseId: lease.leaseId, offset: offset, base64: fileChunkBase64(bytes) });
        if (progress.nextOffset !== offset + bytes.length) throw new Error("Передача файла прервалась.");
        offset = progress.nextOffset;
        fileStatus(target, "Добавление «" + file.name + "»: " + Math.round(100 * offset / file.size) + "%");
      }
      completing = true;
      var result = await send("completeProjectChatFileUpload", { workspaceId: target.workspaceId,
        sessionId: target.sessionId, leaseId: lease.leaseId });
      if (result.modelReadable) composer.append(target.sessionId, "Файл проекта: " + result.relativePath);
      fileStatus(target, "Файл добавлен в проект: " + result.relativePath +
        (result.modelReadable ? "" : ". Его содержимое пока недоступно для чтения моделью в проектном чате."));
      return true;
    } catch (error) {
      if (lease && !completing) await send("cancelProjectChatFileUpload", { workspaceId: target.workspaceId,
        sessionId: target.sessionId, leaseId: lease.leaseId }).catch(function () {});
      var detail = error.detail || error.message || String(error);
      if (completing) detail = "Результат добавления файла не подтверждён. Проверьте " +
        (lease && lease.relativePath || "папку проекта") + " перед повторной загрузкой. " + detail;
      fileStatus(target, detail, true);
      return false;
    }
  }
  async function addProjectFiles(files, target) {
    if (uploadingFiles || !target || !files.length) return;
    if (files.length > 10) {
      fileStatus(target, "За один раз можно добавить не более 10 файлов.", true);
      return;
    }
    uploadingFiles = true; render();
    try {
      for (var index = 0; index < files.length; index++) {
        fileStatus(target, "Подготовка файла " + (index + 1) + " из " + files.length + ": " + files[index].name + "…");
        if (!await uploadProjectFile(target, files[index])) break;
      }
    } finally { uploadingFiles = false; render(); }
  }
  function statusText() {
    var state = active && active.state;
    if (!state) return "";
    if (active.pendingId) return "Нужно подтвердить действие агента.";
    if (state.userInput) return "Нужен ваш ответ.";
    if (field(state, "interruptedEffectPossible", "InterruptedEffectPossible")) return "Возможное изменение требует проверки. Не повторяйте действие автоматически.";
    if (field(state, "status", "Status") === "running") return "Агент работает…";
    if (field(state, "status", "Status") === "interrupted") return "Выполнение прервано. Проверьте ход работы перед новым запросом.";
    return "";
  }
  function renderTree() {
    var root = el("Tree"), selected = currentId(), needle = query.trim().toLocaleLowerCase("ru");
    var nodes = projects().map(function (entry) {
      var ownMatch = entry.name.toLocaleLowerCase("ru").includes(needle);
      var items = (entry.chats || []).filter(function (chat) {
        return !needle || ownMatch || chat.Title.toLocaleLowerCase("ru").includes(needle);
      }).map(function (chat) {
        var runStatus = entry.workspaceId === projectId() && chat.Id === selected && runState()
          ? field(runState(), "status", "Status") : chat.RunStatus;
        return { key: "project-chat:" + entry.workspaceId + ":" + chat.Id, kind: "chat", title: chat.Title,
          selected: entry.workspaceId === projectId() && chat.Id === selected,
          status: runStatus === "running" ? "running" : runStatus === "awaitingconfirmation" ? "waiting" : "",
          actions: (entry.pluginId ? [{ title: "Открыть чат в плагине", iconHtml: window.RNAssistantNavigationTree.icon("code"),
            disabled: !entry.available, onClick: function () { window.RNAssistantPlugins.select(entry.pluginId, chat.Id); } }] : []).concat([
            { title: "Переименовать чат «" + chat.Title + "»", iconHtml: window.RNAssistantNavigationTree.icon("edit"),
              disabled: !entry.available, onClick: function () { rename(entry, chat); } },
            { title: "Удалить чат «" + chat.Title + "»", iconHtml: window.RNAssistantNavigationTree.icon("trash"), danger: true,
              disabled: !entry.available || runStatus === "running" || runStatus === "awaitingconfirmation",
              onClick: function () { remove(entry, chat); } }
          ]), workspaceId: entry.workspaceId, sessionId: chat.Id };
      });
      if (needle && !ownMatch && !items.length) return null;
      return { key: "project:" + entry.workspaceId, kind: "parent", selectable: true,
        title: entry.name, selected: entry.workspaceId === projectId() && !active,
        badge: entry.available ? "" : "нет папки",
        tooltip: entry.rootPath + (entry.available ? "" : " · папка недоступна"),
        iconHtml: window.RNAssistantNavigationTree.icon("folder"), expanded: needle ? true : expanded[entry.workspaceId] !== false,
        workspaceId: entry.workspaceId, children: items,
        actions: [
          { title: "Новый чат в «" + entry.name + "»", iconHtml: window.RNAssistantNavigationTree.icon("plus"),
            disabled: !entry.available, onClick: function () { selectProject(entry.workspaceId, false); create(); } },
          { title: "Открыть «" + entry.name + "»", iconHtml: window.RNAssistantNavigationTree.icon("external"),
            onClick: function () { selectProject(entry.workspaceId); } },
          { title: "Удалить историю «" + entry.name + "»", iconHtml: window.RNAssistantNavigationTree.icon("trash"), danger: true,
            disabled: (entry.chats || []).some(function (chat) {
              return chat.RunStatus === "running" || chat.RunStatus === "awaitingconfirmation";
            }), onClick: function () { removeHistory(entry); } }
        ] };
    }).filter(Boolean);
    window.RNAssistantNavigationTree.render(root, nodes, {
      label: "Проекты и чаты", emptyText: projects().length ? "Ничего не найдено." : "Подключите папку проекта, чтобы видеть её чаты.",
      onSelect: function (item) { if (item.sessionId) open(item.workspaceId, item.sessionId); else selectProject(item.workspaceId); },
      onToggle: function (item, isExpanded) { expanded[item.workspaceId] = isExpanded; }
    });
  }
  function renderQuestions(input) {
    window.RNAssistantWorkspaceChat.renderQuestions(el("Questions"), input, function (answers) {
      return runChat(function (pid, sid) { return send("submitProjectChat", { workspaceId: pid, sessionId: sid,
        input: { sessionId: sid, requestId: crypto.randomUUID(), answers: answers } }); });
    });
  }
  function render() {
    if (composer) {
      composer.select(currentId(), active && active.state);
      composer.lock(busy || uploadingFiles || !active || !!active.pendingId || field(runState(), "status", "Status") === "running");
    }

    el("Context").textContent = project() ? "Проекты / " + project().name : "Проект не выбран";
    el("Title").textContent = active && active.state ? field(active.state, "title", "Title") : "Чат проекта";
    el("New").disabled = busy || !project() || !project().available;
    el("Connect").disabled = busy;
    el("Create").disabled = busy;
    el("ConnectionSubmit").disabled = busy;
    el("Send").disabled = busy || uploadingFiles || !project() || !project().available || !active || !el("Input").value.trim() || !!(active && active.pendingId) || field(runState(), "status", "Status") === "running";
    el("FileStatus").hidden = el("FileStatus").dataset.session !== currentId() || !el("FileStatus").textContent;
    el("Stop").hidden = !(active && active.state && field(active.state, "status", "Status") === "running");
    el("Control").hidden = !(active && active.pendingId);
    el("Pending").textContent = active && active.pendingId ? (active.pendingTool || "Действие") + "\n" + (active.pendingArguments || "") : "";
    renderQuestions(active && active.state && field(active.state, "userInput", "UserInput"));
    var messages = el("Messages");
    if (!active || !(active.messages || []).length) {
      messages.replaceChildren(); messages._chatSignature = null;
      var suggestions = [];
      if (project() && active) {
        [["Сравнить периоды", "Сравни сентябрь с августом и выдели главное."],
          ["Найти риски и слабые места", "Изучи проект и найди риски и слабые места."],
          ["Подготовить план работы", "Изучи проект и подготовь план дальнейшей работы."]].forEach(function (entry) {
          var suggest = node("button", "chat-empty-suggestion");
          suggest.type = "button"; suggest.textContent = entry[0];
          suggest.addEventListener("click", function () {
            el("Input").value = entry[1];
            el("Input").dispatchEvent(new Event("input", { bubbles: true }));
            el("Input").focus();
          });
          suggestions.push(suggest);
        });
      }
      messages.appendChild(renderChatEmptyState({
        title: !project() ? "Проект не выбран" : !project().available ? "Папка проекта недоступна" : active ? "С чего начнём?" : "Выберите чат проекта",
        text: !project() ? "Подключите папку проекта или выберите проект в дереве." : !project().available ?
          "Верните папку на прежний путь или подключите её заново." : "Обсудите файлы проекта, подготовку данных или составьте план.",
        buttons: suggestions
      }));
    } else window.RNAssistantWorkspaceChat.renderMessages(messages, active);
    if (!busy && statusText()) notice(statusText());
    renderTree();
  }
  async function refresh(target) {
    var version = ++serial, result;
    try { result = await send("getProjectChats", {}); }
    catch (error) { if (version === serial) notice(error.detail || error.message, true); return; }
    if (version !== serial) return;
    var previousProject = projectId(), previousChat = currentId();
    catalog = result;
    if (target && target.workspaceId) selectedWorkspaceId = target.workspaceId;
    else if (result.selectedWorkspaceId) selectedWorkspaceId = result.selectedWorkspaceId;
    else if (!projects().some(function (item) { return item.workspaceId === selectedWorkspaceId; }))
      selectedWorkspaceId = (projects().find(function (item) { return item.available; }) || projects()[0] || {}).workspaceId || null;
    if (previousProject !== projectId()) { active = null; pendingSend = null; }
    notice("");
    render();
    var chats = project() && project().chats || [];
    var remembered = window.RNAssistantWorkspaceChat.selectedSession(projectId());
    var wanted = target && target.sessionId || remembered || (previousProject === projectId() ? previousChat : null);
    if (wanted && !chats.some(function (item) { return item.Id === wanted; })) {
      window.RNAssistantWorkspaceChat.rememberSession(projectId(), null);
      if (target && target.sessionId) { active = null; render(); notice("Чат отсутствует в этом проекте.", true); return; }
      wanted = null;
    }
    wanted = wanted || chats[0] && chats[0].Id;
    if (wanted && project().available) await open(projectId(), wanted);
    else if (!wanted) { active = null; render(); }
  }
  function selectProject(workspaceId, openFirst) {
    if (!workspaceId || !projects().some(function (item) { return item.workspaceId === workspaceId; })) return;
    if (selectedWorkspaceId !== workspaceId) { ++serial; selectedWorkspaceId = workspaceId; active = null; pendingSend = null; }
    render();
    var entry = project();
    if (openFirst !== false && entry.available && entry.chats && entry.chats.length) open(workspaceId, entry.chats[0].Id);
  }
  async function open(workspaceId, sessionId) {
    if (!workspaceId || !sessionId) return;
    if (stopTitleWatch) { stopTitleWatch(); stopTitleWatch = null; }
    if (projectId() !== workspaceId) { ++serial; selectedWorkspaceId = workspaceId; active = null; pendingSend = null; render(); }
    if (!project() || !project().available) return;
    var version = ++serial, pid = projectId();
    if (currentId() !== sessionId) { active = null; render(); }
    notice("Загружаю чат…");
    try {
      var result = await send("getProjectChat", { workspaceId: pid, sessionId: sessionId });
      if (version === serial && projectId() === pid) {
        active = result; window.RNAssistantWorkspaceChat.rememberSession(pid, result.sessionId); notice(statusText()); render();
      }
    } catch (error) { if (version === serial) notice(error.detail || error.message, true); }
  }
  async function create() {
    if (busy || !project() || !project().available) return;
    ++serial; busy = true; render(); var pid = projectId();
    try {
      var result = await send("createProjectChat", { workspaceId: pid, title: "Новый чат" });
      if (projectId() === pid) {
        active = result; window.RNAssistantWorkspaceChat.rememberSession(pid, result.sessionId);
        catalog = await send("getProjectChats", {}); notice(statusText()); render();
      }
    } catch (error) { notice(error.detail || error.message, true); }
    finally { busy = false; render(); }
  }
  async function rename(entry, chat) {
    var title = window.prompt("Название чата", chat.Title);
    if (title === null || !title.trim() || title.trim() === chat.Title) return;
    try { catalog = await send("renameProjectChat", { workspaceId: entry.workspaceId, sessionId: chat.Id, title: title.trim() });
      if (projectId() === entry.workspaceId && currentId() === chat.Id) { active.state.Title = title.trim(); active.state.title = title.trim(); }
      notice(""); render(); }
    catch (error) { notice(error.detail || error.message, true); }
  }
  async function remove(entry, chat) {
    if (!window.confirm("Удалить чат «" + chat.Title + "»? История этого чата будет удалена; файлы проекта останутся.")) return;
    try { catalog = await send("deleteProjectChat", { workspaceId: entry.workspaceId, sessionId: chat.Id });
      if (window.RNAssistantWorkspaceChat.selectedSession(entry.workspaceId) === chat.Id)
        window.RNAssistantWorkspaceChat.rememberSession(entry.workspaceId, null);
      if (projectId() === entry.workspaceId && currentId() === chat.Id) active = null;
      notice(""); render(); var selected = project();
      if (!active && selected && selected.available && selected.chats.length) open(selected.workspaceId, selected.chats[0].Id); }
    catch (error) { notice(error.detail || error.message, true); }
  }
  async function removeHistory(entry) {
    if (!window.confirm("Удалить историю проекта «" + entry.name + "» и убрать его из дерева? Папка и файлы останутся.")) return;
    try { catalog = await send("deleteProjectHistory", { workspaceId: entry.workspaceId });
      window.RNAssistantWorkspaceChat.rememberSession(entry.workspaceId, null);
      if (projectId() === entry.workspaceId) {
        ++serial; active = null; pendingSend = null;
        selectedWorkspaceId = (projects().find(function (item) { return item.available; }) || projects()[0] || {}).workspaceId || null;
      }
      notice(""); render();
      if (project() && project().available && project().chats && project().chats.length && !active)
        open(projectId(), project().chats[0].Id); }
    catch (error) { await refresh(); notice(error.detail || error.message, true); }
  }
  async function readCurrent(pid, sid) {
    try { var result = await send("getProjectChat", { workspaceId: pid, sessionId: sid });
      if (projectId() === pid && currentId() === sid) { active = result; notice(statusText()); render(); } }
    catch (error) { if (projectId() === pid && currentId() === sid) notice(error.detail || error.message, true); }
  }
  async function runChat(action) {
    if (busy || !active || !project() || !project().available) return;
    busy = true; var pid = projectId(), sid = currentId(); render();
    poll = window.setInterval(function () { readCurrent(pid, sid); }, 1200);
    try { var result = await action(pid, sid); if (projectId() === pid && currentId() === sid) {
      active = result; notice(statusText()); render();
      try { var latestCatalog = await send("getProjectChats", {});
        if (projectId() === pid && currentId() === sid) { catalog = latestCatalog; render(); }
      } catch (_) { /* The chat result remains available. */ }
      if (stopTitleWatch) stopTitleWatch();
      stopTitleWatch = window.RNAssistantWorkspaceChat.watchTitle(result,
        function () { return send("getProjectChat", { workspaceId: pid, sessionId: sid }); },
        async function (latest) { if (projectId() !== pid || currentId() !== sid) return;
          active = latest; render();
          try { catalog = await send("getProjectChats", {}); if (projectId() === pid && currentId() === sid) render(); }
          catch (_) { /* The refreshed title is still shown in the open chat. */ }
        });
    } }
    catch (error) { if (projectId() === pid && currentId() === sid) notice(error.detail || error.message, true); }
    finally { window.clearInterval(poll); poll = null; busy = false; render(); }
  }
  function setScope(next, target) {
    scope = next; ++serial;
    document.getElementById("chatLayout").hidden = next === "projects";
    el("Layout").hidden = next !== "projects";
    document.querySelectorAll(".chat-scope-switcher").forEach(function (picker) { picker.open = false; });
    var loaded;
    if (next === "projects") loaded = refresh(target);
    else if (poll) { window.clearInterval(poll); poll = null; }
    if (window.RNAssistantShellLayout) window.RNAssistantShellLayout.refresh();
    return loaded;
  }
  function bind() {
    composer = window.RNAssistantWorkspaceChat.bindComposer(el("Form"), {
      fileTarget: function () { return { workspaceId: projectId(), sessionId: currentId() }; },
      onFiles: addProjectFiles
    });
    var dropZone = el("Layout").querySelector(".project-chat-main");
    dropZone.addEventListener("dragover", function (event) {
      if (event.dataTransfer && Array.from(event.dataTransfer.types || []).includes("Files")) {
        event.preventDefault(); event.dataTransfer.dropEffect = active && !uploadingFiles ? "copy" : "none";
      }
    });
    dropZone.addEventListener("drop", function (event) {
      var files = Array.from(event.dataTransfer && event.dataTransfer.files || []);
      if (files.length) { event.preventDefault(); addProjectFiles(files,
        { workspaceId: projectId(), sessionId: currentId() }); }
    });
    el("Input").addEventListener("paste", function (event) {
      var files = Array.from(event.clipboardData && event.clipboardData.files || []);
      if (files.length) { event.preventDefault(); addProjectFiles(files,
        { workspaceId: projectId(), sessionId: currentId() }); }
    });
    document.querySelectorAll("[data-workspace-scope]").forEach(function (button) {
      button.addEventListener("click", function () { setScope(button.dataset.workspaceScope); });
    });
    document.querySelector("[data-project-scope-icon]").innerHTML = window.RNAssistantNavigationTree.icon("folder");
    el("Refresh").addEventListener("click", refresh);
    el("New").addEventListener("click", create);
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
    el("ConnectionCancel").addEventListener("click", function () { el("Connection").hidden = true; });
    el("Connection").addEventListener("submit", async function (event) {
      event.preventDefault(); if (busy) return;
      var path = el("ConnectionPath").value.trim(); if (!path) return;
      busy = true; render();
      try {
        var result = await send(connectionMode === "create" ? "createProjectWorkspace" : "connectProjectWorkspace", { rootPath: path });
        catalog = result; selectedWorkspaceId = result.selectedWorkspaceId; active = null; pendingSend = null;
        el("Connection").hidden = true; el("ConnectionPath").value = ""; notice(""); render();
      } catch (error) { notice(error.detail || error.message, true); }
      finally { busy = false; render(); }
    });
    el("Input").addEventListener("input", function () {
      el("Send").disabled = busy || uploadingFiles || !project() || !project().available || !active || !el("Input").value.trim() || !!active.pendingId || field(runState(), "status", "Status") === "running";
    });
    el("Form").addEventListener("submit", function (event) {
      event.preventDefault(); var text = el("Input").value.trim(); if (!text || !active || uploadingFiles) return;
      var pid = projectId(), sid = currentId();
      var preferences = composer.options();
      if (!pendingSend || pendingSend.workspaceId !== pid || pendingSend.sessionId !== sid || pendingSend.text !== text || JSON.stringify(pendingSend.preferences) !== JSON.stringify(preferences))
        pendingSend = { workspaceId: pid, sessionId: sid, text: text, preferences: preferences, requestId: crypto.randomUUID() };
      var request = pendingSend;
      runChat(function () { return send("submitProjectChat", { workspaceId: pid, sessionId: sid,
        input: Object.assign({ sessionId: sid, requestId: request.requestId, message: text }, request.preferences) }).then(function (result) {
        composer.accepted(sid, text); if (pendingSend === request) pendingSend = null; return result;
      }); });
    });
    el("Stop").addEventListener("click", async function () {
      var state = runState(), pid = projectId(), sid = currentId();
      if (!state || !pid || !sid) return;
      el("Stop").disabled = true;
      try {
        var result = await send("cancelProjectChat", { workspaceId: pid, sessionId: sid,
          runId: field(state, "runId", "RunId"), cursor: field(state, "cursor", "Cursor") });
        if (projectId() === pid && currentId() === sid) {
          active = result; render();
          notice(result.cancelStatus === "Requested" && field(result.state, "status", "Status") === "running"
            ? "Отмена запрошена…" : statusText());
        }
      } catch (error) { notice(error.detail || error.message, true); }
      finally { el("Stop").disabled = false; }
    });
    [["Approve", true], ["Reject", false]].forEach(function (entry) { el(entry[0]).addEventListener("click", function () {
      var pendingId = active && active.pendingId;
      if (pendingId) runChat(function (pid, sid) { return send("resolveProjectChat", { workspaceId: pid, sessionId: sid, pendingId: pendingId, approve: entry[1] }); });
    }); });
    render();
  }
  document.addEventListener("DOMContentLoaded", bind);
  window.RNAssistantProjectChats = { show: function () { if (scope === "projects") refresh(); }, scope: function () { return scope; }, setScope: setScope,
    open: function (workspaceId, sessionId) { switchTab("chat"); return setScope("projects", { workspaceId: workspaceId, sessionId: sessionId }); } };
}());
