(function () {
  "use strict";
  var scope = "office", catalog = null, catalogRevision = 0, active = null, catalogBusy = false,
    creatingSessionId = null, serial = 0, stopTitleWatch = null;
  var pendingChatRequests = new Set(), pendingCancels = new Set();
  var composer = null, uploadingFiles = false, uploadTarget = null;
  var runObserver = window.RNAssistantWorkspaceChat.createRunObserver(
    function (target) { return send("getProjectChat", target); },
    function (snapshot) { active = window.RNAssistantWorkspaceChat.latestSnapshot(active, snapshot); notice(statusText()); render(); },
    function (error) { notice(error.detail || error.message, true); });
  var selectedWorkspaceId = null, expanded = {}, query = "", connectionMode = "connect", connectionRequest = null;
  var noticeText = "", noticeError = false, fileNotices = new Map();
  function el(id) {
    return window.RNAssistantWorkspaceChat.node(id) || document.getElementById("projectChat" + id);
  }
  function ownsView() { return window.RNAssistantWorkspaceChat.owner() === "project" ||
    window.RNAssistantWorkspaceChat.owner() === "plugin"; }
  function node(tag, className, text) {
    var result = document.createElement(tag);
    result.className = className || "";
    if (text !== undefined) result.textContent = text;
    return result;
  }
  function projects() { return catalog && catalog.projects || []; }
  function replaceCatalog(value) { catalog = value; ++catalogRevision; }
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
  function currentId() { return active && active.sessionId; }
  function chatRequestKey(workspaceId, sessionId) { return JSON.stringify([workspaceId, sessionId]); }
  function chatBusy() { return !!active && pendingChatRequests.has(chatRequestKey(projectId(), currentId())); }
  function cancelBusy() { return !!active && pendingCancels.has(chatRequestKey(projectId(), currentId())); }
  function selectedUploadBusy() { return !!active && uploadingFiles && !!uploadTarget &&
    uploadTarget.workspaceId === projectId() && uploadTarget.sessionId === currentId(); }
  function field(value, lower, upper) { return value && (value[lower] !== undefined ? value[lower] : value[upper]); }
  function runState() { return active && active.state; }
  function notice(text, error) {
    noticeText = text || ""; noticeError = !!error;
    if (!ownsView()) return;
    el("Notice").textContent = noticeText;
    el("Notice").dataset.error = noticeError ? "true" : "false";
  }
  function fileStatus(target, message, error) {
    fileNotices.set(chatRequestKey(target.workspaceId, target.sessionId),
      { target: target, message: message || "", error: !!error });
    if (projectId() !== target.workspaceId || currentId() !== target.sessionId) return;
    if (!ownsView()) return;
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
      if (result.modelReadable) composer.append(target, "Файл проекта: " + result.relativePath);
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
    if (!target || !files.length) return;
    if (uploadingFiles) { fileStatus(target, "Дождитесь завершения текущей загрузки файла.", true); return; }
    if (files.length > 10) {
      fileStatus(target, "За один раз можно добавить не более 10 файлов.", true);
      return;
    }
    uploadingFiles = true; uploadTarget = { workspaceId: target.workspaceId, sessionId: target.sessionId }; render();
    try {
      for (var index = 0; index < files.length; index++) {
        fileStatus(target, "Подготовка файла " + (index + 1) + " из " + files.length + ": " + files[index].name + "…");
        if (!await uploadProjectFile(target, files[index])) break;
      }
    } finally { uploadingFiles = false; uploadTarget = null; render(); }
  }
  function statusText() {
    var state = active && active.state;
    if (!state) return "";
    var runNotice = window.RNAssistantWorkspaceChat.runNotice(active);
    if (runNotice) return runNotice.title + ". " + runNotice.text;
    if (active.pendingId) return "Нужно подтвердить действие агента.";
    if (state.userInput) return "Нужен ваш ответ.";
    if (field(state, "interruptedEffectPossible", "InterruptedEffectPossible")) return "Возможное изменение требует проверки. Не повторяйте действие автоматически.";
    if (field(state, "status", "Status") === "running") return "Агент работает…";
    if (field(state, "status", "Status") === "interrupted") return "Выполнение прервано. Проверьте ход работы перед новым запросом.";
    return "";
  }
  function chatRunStatus(entry, chat) {
    return entry.workspaceId === projectId() && chat.Id === currentId() && runState()
      ? field(runState(), "status", "Status") : chat.RunStatus;
  }
  function runBlocksDeletion(status) { return status === "running" || status === "awaitingconfirmation"; }
  function historyBlocked(entry) {
    return (entry.chats || []).some(function (chat) { return runBlocksDeletion(chatRunStatus(entry, chat)); }) ||
      entry.workspaceId === projectId() && !!currentId() && runBlocksDeletion(field(runState(), "status", "Status"));
  }
  function renderTree() {
    var root = el("Tree"), selected = currentId(), needle = query.trim().toLocaleLowerCase("ru");
    var pluginSource = window.RNAssistantPlugins && window.RNAssistantPlugins.sourceWorkspaceId();
    var nodes = projects().map(function (entry) {
      var ownMatch = entry.name.toLocaleLowerCase("ru").includes(needle);
      var items = (entry.chats || []).filter(function (chat) {
        return !needle || ownMatch || chat.Title.toLocaleLowerCase("ru").includes(needle);
      }).map(function (chat) {
        var runStatus = chatRunStatus(entry, chat);
        return { key: "project-chat:" + entry.workspaceId + ":" + chat.Id, kind: "chat",
          title: entry.workspaceId === projectId() && chat.Id === selected && runState()
            ? field(runState(), "title", "Title") || chat.Title : chat.Title,
          selected: entry.workspaceId === projectId() && chat.Id === selected,
          status: runStatus === "running" ? "running" : runStatus === "awaitingconfirmation" ? "waiting" : "",
          actions: (entry.pluginId ? [{ title: "Открыть чат в плагине", iconHtml: window.RNAssistantNavigationTree.icon("code"),
            disabled: !entry.available, onClick: function () { window.RNAssistantPlugins.select(entry.pluginId, chat.Id); } }] : []).concat([
            { title: "Переименовать чат «" + chat.Title + "»", iconHtml: window.RNAssistantNavigationTree.icon("edit"),
              disabled: !entry.available, onClick: function () { rename(entry, chat); } },
            { title: "Удалить чат «" + chat.Title + "»", iconHtml: window.RNAssistantNavigationTree.icon("trash"), danger: true,
              disabled: !entry.available || runBlocksDeletion(runStatus),
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
        var entry = projects().find(function (project) { return project.workspaceId === item.workspaceId; });
        if (entry && entry.pluginId && window.RNAssistantPlugins)
          window.RNAssistantPlugins.select(entry.pluginId, item.sessionId);
        else window.RNAssistantProjectChats.open(item.workspaceId, item.sessionId);
      },
      onToggle: function (item, isExpanded) { expanded[item.workspaceId] = isExpanded; }
    });
  }
  function updateControls() {
    var pluginBusy = !!(window.RNAssistantPlugins && window.RNAssistantPlugins.isChatBusy());
    var selectedBusy = chatBusy() || selectedUploadBusy();
    el("ConnectionCancel").textContent = connectionRequest ? "Скрыть" : "Отмена";
    el("ConnectionCancel").title = connectionRequest ? "Операция продолжится; проект появится в дереве." : "";
    el("New").disabled = catalogBusy || !project() || !project().available || pluginBusy;
    el("Connect").disabled = catalogBusy;
    el("Create").disabled = catalogBusy;
    el("ConnectionSubmit").disabled = catalogBusy;
    if (!ownsView()) return;
    if (composer) composer.lock(window.RNAssistantWorkspaceChat.readOnly(active) || selectedBusy || pluginBusy ||
      !active || !!active.pendingId || field(runState(), "status", "Status") === "running");
    var target = active && active.sessionId ? { workspaceId: projectId(), sessionId: currentId() } : null;
    var draft = target && window.RNAssistantChatDrafts.readWorkspace(target);
    var hasAttachments = !!(draft && draft.attachments && draft.attachments.length);
    el("Send").disabled = window.RNAssistantWorkspaceChat.readOnly(active) || selectedBusy || pluginBusy ||
      !project() || !project().available || !active || (!el("Input").value.trim() && !hasAttachments) ||
      !!(draft && draft.pendingVisualAnnotation) || !!active.pendingId || field(runState(), "status", "Status") === "running";
  }
  function render() {
    runObserver.select(ownsView() && project() && project().available ? active : null, chatBusy());
    updateControls();
    renderTree();
    if (!ownsView()) return;
    if (composer) {
      composer.select({ workspaceId: projectId(), sessionId: currentId() }, active && active.state);
    }

    el("Context").textContent = project() ? "Проекты / " + project().name : "Проект не выбран";
    el("Title").textContent = active && active.state ? field(active.state, "title", "Title") : "Чат проекта";
    updateControls();
    var fileNotice = active && fileNotices.get(chatRequestKey(projectId(), currentId()));
    el("FileStatus").textContent = fileNotice ? fileNotice.message : "";
    el("FileStatus").classList.toggle("is-error", !!(fileNotice && fileNotice.error));
    el("FileStatus").hidden = !fileNotice || !fileNotice.message;
    window.RNAssistantWorkspaceChat.renderWorkspaceControls(active, function (answers, requestId) {
      return runChat(function (pid, sid) { return send("submitProjectChat", { workspaceId: pid, sessionId: sid,
        input: { sessionId: sid, requestId: requestId, answers: answers } }); }, true);
    });
    el("Stop").disabled = cancelBusy();
    var messages = el("Messages");
    var emptyOptions = null;
    if (!active || (!(active.messages || []).length && field(runState(), "status", "Status") !== "running")) {
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
      var pluginChat = window.RNAssistantWorkspaceChat.owner() === "plugin";
      emptyOptions = {
        title: !project() ? "Проект не выбран" : !project().available ? "Папка проекта недоступна" : active ? "С чего начнём?" :
          pluginChat ? "Чатов пока нет" : "Выберите чат проекта",
        text: !project() ? "Подключите папку проекта или выберите проект в дереве." : !project().available ?
          "Верните папку на прежний путь или подключите её заново." : pluginChat && !active ?
          "Нажмите «+», чтобы создать чат исходников." : "Обсудите файлы проекта, подготовку данных или составьте план.",
        buttons: suggestions
      };
    }
    window.RNAssistantWorkspaceChat.renderMessages(messages, active || {
      project: { workspaceId: projectId() }, sessionId: null, messages: [], state: {}
    }, emptyOptions);
    window.RNAssistantChatInspector.render();
    if (!chatBusy() && window.RNAssistantWorkspaceChat.runNotice(active)) notice(statusText(), true);
    else if (!chatBusy() && !noticeError && statusText()) notice(statusText());
    else notice(noticeText, noticeError);
    if (window.RNAssistantWorkspaceChat.owner() === "plugin" && window.RNAssistantPlugins)
      window.RNAssistantPlugins.onChatSnapshot(active);
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
    if (previousProject !== projectId()) { active = null; }
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
    if (selectedWorkspaceId !== workspaceId) { ++serial; selectedWorkspaceId = workspaceId; active = null; }
    render();
    var entry = project();
    if (openFirst !== false && entry.available && entry.chats && entry.chats.length) open(workspaceId, entry.chats[0].Id);
  }
  async function open(workspaceId, sessionId) {
    if (!workspaceId || !sessionId) return;
    if (stopTitleWatch) { stopTitleWatch(); stopTitleWatch = null; }
    if (projectId() !== workspaceId) { ++serial; selectedWorkspaceId = workspaceId; active = null; render(); }
    if (!project() || !project().available) return;
    var version = ++serial, pid = projectId();
    if (currentId() !== sessionId) { active = null; render(); }
    notice("Загружаю чат…");
    try {
      var result = await send("getProjectChat", { workspaceId: pid, sessionId: sessionId });
      if (version === serial && projectId() === pid) {
        active = window.RNAssistantWorkspaceChat.latestSnapshot(active, result); window.RNAssistantWorkspaceChat.rememberSession(pid, result.sessionId); notice(statusText()); render();
      }
    } catch (error) { if (version === serial) notice(error.detail || error.message, true); }
  }
  async function create() {
    if (catalogBusy || !project() || !project().available) return;
    var version = ++serial, pid = projectId(); catalogBusy = true; creatingSessionId = null; render();
    try {
      var result = await send("createProjectChat", { workspaceId: pid, title: "Новый чат" });
      var selected = version === serial && projectId() === pid;
      if (selected) {
        creatingSessionId = result.sessionId;
        active = window.RNAssistantWorkspaceChat.latestSnapshot(active, result); window.RNAssistantWorkspaceChat.rememberSession(pid, result.sessionId);
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
    var version = serial;
    try { var result = await send("renameProjectChat", { workspaceId: entry.workspaceId,
        sessionId: chat.Id, title: title.trim() });
      var returnedProject = (result.projects || []).find(function (item) { return item.workspaceId === entry.workspaceId; });
      var returnedChat = returnedProject && (returnedProject.chats || []).find(function (item) { return item.Id === chat.Id; });
      var currentProject = projects().find(function (item) { return item.workspaceId === entry.workspaceId; });
      var currentChat = currentProject && (currentProject.chats || []).find(function (item) { return item.Id === chat.Id; });
      if (returnedChat && currentChat) { currentChat.Title = returnedChat.Title; ++catalogRevision; }
      if (returnedChat && projectId() === entry.workspaceId && currentId() === chat.Id && active.state) {
        active.state.Title = returnedChat.Title; active.state.title = returnedChat.Title;
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
      window.RNAssistantChatDrafts.forgetWorkspace({ workspaceId: entry.workspaceId, sessionId: chat.Id });
      fileNotices.delete(chatRequestKey(entry.workspaceId, chat.Id));
      if (window.RNAssistantWorkspaceChat.selectedSession(entry.workspaceId) === chat.Id)
        window.RNAssistantWorkspaceChat.rememberSession(entry.workspaceId, null);
      var removedSelected = projectId() === entry.workspaceId && currentId() === chat.Id;
      if (removedSelected) { active = null; notice(""); }
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
        window.RNAssistantChatDrafts.forgetWorkspace({ workspaceId: entry.workspaceId, sessionId: chat.Id });
      });
      fileNotices.forEach(function (fileNotice, key) {
        if (fileNotice.target.workspaceId === entry.workspaceId) fileNotices.delete(key);
      });
      window.RNAssistantWorkspaceChat.rememberSession(entry.workspaceId, null);
      var removedSelected = projectId() === entry.workspaceId;
      var pluginSelected = removedSelected && window.RNAssistantWorkspaceChat.owner() === "plugin" &&
        window.RNAssistantPlugins && window.RNAssistantPlugins.sourceWorkspaceId() === entry.workspaceId;
      if (removedSelected) {
        ++serial; active = null;
        selectedWorkspaceId = (projects().find(function (item) { return item.available; }) || projects()[0] || {}).workspaceId || null;
        notice("");
      }
      render();
      if (pluginSelected) await window.RNAssistantPlugins.select(entry.pluginId);
      else if (removedSelected && project() && project().available && project().chats && project().chats.length && !active)
        open(projectId(), project().chats[0].Id); }
    catch (error) { var selected = projectId() === entry.workspaceId;
      await refresh(selected ? null : { workspaceId: projectId(), sessionId: currentId() });
      if (selected) notice(error.detail || error.message, true); }
  }
  async function runChat(action, reportFailure) {
    if (!active || !project() || !project().available) return false;
    var pid = projectId(), sid = currentId(), requestKey = chatRequestKey(pid, sid);
    if (pendingChatRequests.has(requestKey)) return false;
    if (catalogBusy && creatingSessionId !== sid) ++serial;
    pendingChatRequests.add(requestKey); render();
    try { var result = await action(pid, sid); if (projectId() === pid && currentId() === sid) {
      active = window.RNAssistantWorkspaceChat.latestSnapshot(active, result); notice(statusText()); render();
      try { if (await refreshCatalog(function () { return projectId() === pid && currentId() === sid; })) render();
      } catch (_) { /* The chat result remains available. */ }
      if (stopTitleWatch) stopTitleWatch();
      stopTitleWatch = window.RNAssistantWorkspaceChat.watchTitle(result,
        function () { return send("getProjectChat", { workspaceId: pid, sessionId: sid }); },
        async function (latest) { if (projectId() !== pid || currentId() !== sid) return;
          active = window.RNAssistantWorkspaceChat.latestSnapshot(active, latest); render();
          try { if (await refreshCatalog(function () { return projectId() === pid && currentId() === sid; })) render(); }
          catch (_) { /* The refreshed title is still shown in the open chat. */ }
        });
    } return true; }
    catch (error) { if (projectId() === pid && currentId() === sid) notice(error.detail || error.message, true);
      if (reportFailure) throw error; return false; }
    finally { pendingChatRequests.delete(requestKey); render(); }
  }
  function setScope(next, target) {
    scope = next; ++serial;
    if (next === "projects") window.RNAssistantWorkspaceChat.mount("project", el("Layout"));
    document.getElementById("chatLayout").hidden = next === "projects";
    el("Layout").hidden = next !== "projects";
    if (next === "projects") {
      render();
    } else if (window.RNAssistantWorkspaceChat.mount("office")) {
      resumeOfficeTranscript();
    }
    document.querySelectorAll(".chat-scope-switcher").forEach(function (picker) { picker.open = false; });
    var loaded;
    if (next === "projects") loaded = refresh(target);
    if (window.RNAssistantShellLayout) window.RNAssistantShellLayout.refresh();
    if (window.RNAssistantPlugins) window.RNAssistantPlugins.preview.refreshLayout();
    return loaded;
  }
  function bind() {
    window.RNAssistantWorkspaceChat.mount("office");
    composer = window.RNAssistantWorkspaceChat.bindComposer(el("Form"), {
      fileTarget: function () { return { workspaceId: projectId(), sessionId: currentId() }; },
      canAttachFiles: function () { return !uploadingFiles; },
      onFiles: addProjectFiles
    });
    var dropZone = el("Layout").querySelector(".project-chat-main");
    dropZone.addEventListener("dragover", function (event) {
      if (window.RNAssistantWorkspaceChat.owner() !== "project") return;
      if (event.dataTransfer && Array.from(event.dataTransfer.types || []).includes("Files")) {
        event.preventDefault(); event.dataTransfer.dropEffect = active && !uploadingFiles ? "copy" : "none";
      }
    });
    dropZone.addEventListener("drop", function (event) {
      if (window.RNAssistantWorkspaceChat.owner() !== "project") return;
      var files = Array.from(event.dataTransfer && event.dataTransfer.files || []);
      if (files.length) { event.preventDefault(); addProjectFiles(files,
        { workspaceId: projectId(), sessionId: currentId() }); }
    });
    el("Input").addEventListener("paste", function (event) {
      if (window.RNAssistantWorkspaceChat.owner() !== "project") return;
      var files = Array.from(event.clipboardData && event.clipboardData.files || []);
      if (files.length) { event.preventDefault(); addProjectFiles(files,
        { workspaceId: projectId(), sessionId: currentId() }); }
    });
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
          selectedWorkspaceId = result.selectedWorkspaceId; active = null; notice("");
        }
        el("Connection").hidden = true;
        if (el("ConnectionPath").value.trim() === path) el("ConnectionPath").value = "";
        render();
      } catch (error) { if (version === serial && !request.cancelled && !el("Connection").hidden)
        notice(error.detail || error.message, true); }
      finally { if (connectionRequest === request) connectionRequest = null;
        catalogBusy = false; render(); }
    });
    var chatActions = {
      snapshot: function () { return active; },
      refresh: render,
      suspend: function () { runObserver.select(null, false); },
      loadPrevious: async function () {
        if (!active || !active.messageStartIndex) return;
        var pid = projectId(), sid = currentId(), beforeIndex = active.messageStartIndex;
        try {
          var page = await send("getPreviousProjectChatMessages", {
            workspaceId: pid, sessionId: sid, beforeIndex: beforeIndex });
          if (projectId() === pid && currentId() === sid && active.messageStartIndex === beforeIndex) {
            window.RNAssistantWorkspaceChat.prependMessages(active, page); render();
          }
        } catch (error) { if (projectId() === pid && currentId() === sid) notice(error.detail || error.message, true); }
      },
      canMessageAction: function (name, message) {
        return (name === "fork" || name === "delete" || name === "edit") && !!active && !!message && !!message.Id &&
          (active.messages || []).some(function (item) { return item.Id === message.Id; }) &&
          !window.RNAssistantWorkspaceChat.readOnly(active) && !chatBusy() &&
          !active.pendingId && !window.RNAssistantWorkspaceChat.currentMessageEdit() &&
          !runBlocksDeletion(String(field(runState(), "status", "Status") || "").toLowerCase()) &&
          (name !== "edit" || String(field(message, "role", "Role") || "").toLowerCase() === "user");
      },
      messageAction: async function (action) {
        if (!chatActions.canMessageAction(action.name, action.message)) return;
        if (action.name === "edit") { window.RNAssistantWorkspaceChat.startMessageEdit(action.message); render(); return; }
        var pid = projectId(), sid = currentId(), version = serial;
        try {
          var result = await send(action.name === "fork" ? "forkProjectChat" : "deleteProjectChatMessage", { workspaceId: pid, sessionId: sid,
            messageId: action.message.Id });
          var selected = version === serial && projectId() === pid && currentId() === sid;
          if (selected) {
            if (action.name === "fork") ++serial;
            active = result;
            window.RNAssistantWorkspaceChat.rememberSession(pid, result.sessionId);
            notice(action.name === "fork" ? "Ветка чата создана." : "Сообщение удалено."); render();
          }
          if (action.name === "fork") {
            try { await refreshCatalog(); render(); }
            catch (error) { if (selected) notice("Ветка создана, но список не обновлён: " +
              (error.detail || error.message), true); }
          }
        } catch (error) { if (version === serial && projectId() === pid && currentId() === sid)
          notice(error.detail || error.message, true); }
      },
      input: function () {
        updateControls();
      },
      submit: function () {
        var text = el("Input").value.trim(); if (el("Send").disabled || !active || selectedUploadBusy()) return;
        var pid = projectId(), sid = currentId();
        var target = { workspaceId: pid, sessionId: sid }, edit = window.RNAssistantWorkspaceChat.currentMessageEdit();
        var draftVersion = edit ? null : composer.draftVersion();
        var request = composer.prepareInput(target, text, edit);
        var acknowledged = false;
        function acceptReceipt(receipt) {
          if (acknowledged) return true;
          if (!receipt || receipt.SessionId !== sid || receipt.RequestId !== request.requestId ||
              !receipt.InputId || !receipt.RunId || !receipt.AcceptedCursor ||
              !Number.isSafeInteger(receipt.AcceptedSequence) || receipt.AcceptedSequence < 1 ||
              receipt.DeliveryRecorded !== true) return false;
          if (edit) composer.acceptedEdit(target, request);
          else composer.accepted(target, text, draftVersion, request);
          acknowledged = true;
          return true;
        }
        runChat(async function () { await window.RNAssistantChatDrafts.flushWorkspace(target);
          return send("submitProjectChat", { workspaceId: pid, sessionId: sid,
          input: request }, function (progress) {
            if (progress.workspaceId === pid && progress.sessionId === sid) acceptReceipt(progress.inputReceipt);
          }).then(function (result) {
          if (!acceptReceipt(result && result.inputReceipt))
            throw new Error("Доставка сообщения не подтверждена. Черновик сохранён.");
          return result;
        }); });
      },
      escape: function (event) {
        if (window.RNAssistantWorkspaceChat.currentMessageEdit()) {
          event.preventDefault(); window.RNAssistantWorkspaceChat.cancelMessageEdit(true); render();
        }
      },
      stop: async function () {
        var state = runState(), pid = projectId(), sid = currentId();
        if (!state || !pid || !sid) return;
        var requestKey = chatRequestKey(pid, sid);
        if (pendingCancels.has(requestKey)) return;
        pendingCancels.add(requestKey); render();
        try {
          var result = await send("cancelProjectChat", { workspaceId: pid, sessionId: sid,
            runId: field(state, "runId", "RunId"), cursor: field(state, "cursor", "Cursor") });
          if (projectId() === pid && currentId() === sid) {
            active = window.RNAssistantWorkspaceChat.latestSnapshot(active, result); render();
            notice(result.cancelStatus === "Requested" && field(result.state, "status", "Status") === "running"
              ? "Отмена запрошена…" : statusText());
          }
        } catch (error) { if (projectId() === pid && currentId() === sid) notice(error.detail || error.message, true); }
        finally { pendingCancels.delete(requestKey); render(); }
      },
      resolve: function (approve) {
        var pendingId = active && active.pendingId;
        if (pendingId) runChat(function (pid, sid) { return send("resolveProjectChat", { workspaceId: pid, sessionId: sid, pendingId: pendingId, approve: approve }); });
      },
      progress: runObserver.acceptProgress
    };
    window.RNAssistantWorkspaceChat.registerActions("project", chatActions);
    window.RNAssistantWorkspaceChat.registerActions("plugin", chatActions);
    render();
  }
  document.addEventListener("DOMContentLoaded", bind);
  window.RNAssistantProjectChats = { show: function () {
    if (scope === "projects") { window.RNAssistantWorkspaceChat.mount("project", el("Layout")); render(); refresh(); }
    else if (window.RNAssistantWorkspaceChat.mount("office")) resumeOfficeTranscript();
    if (window.RNAssistantPlugins) window.RNAssistantPlugins.preview.refreshLayout();
  }, scope: function () { return scope; }, workspaceId: projectId, setScope: setScope,
    refresh: refresh, refreshTree: renderTree, refreshControls: updateControls, render: render, create: create,
    snapshot: function () { return active; },
    isBusy: function () { return catalogBusy || chatBusy(); },
    open: function (workspaceId, sessionId) { switchTab("chat"); return setScope("projects", { workspaceId: workspaceId, sessionId: sessionId }); } };
}());
