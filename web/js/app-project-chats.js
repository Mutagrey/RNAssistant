(function () {
  "use strict";
  var scope = "office", catalog = null, active = null, busy = false, serial = 0, poll = null, pendingSend = null;
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
          actions: [
            { title: "Переименовать чат «" + chat.Title + "»", iconHtml: window.RNAssistantNavigationTree.icon("edit"),
              disabled: !entry.available, onClick: function () { rename(entry, chat); } },
            { title: "Удалить чат «" + chat.Title + "»", iconHtml: window.RNAssistantNavigationTree.icon("trash"), danger: true,
              disabled: !entry.available || runStatus === "running" || runStatus === "awaitingconfirmation",
              onClick: function () { remove(entry, chat); } }
          ], workspaceId: entry.workspaceId, sessionId: chat.Id };
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
    var form = el("Questions"), set = input && (input.questionSet || input.QuestionSet);
    if (!set) { form.hidden = true; form.replaceChildren(); delete form.dataset.questionSet; return; }
    if (form.dataset.questionSet === set.questionSetId) return;
    form.hidden = false; form.replaceChildren(); form.dataset.questionSet = set.questionSetId;
    var fields = (set.questions || []).map(function (question) {
      var group = node("fieldset"), legend = node("legend", "", question.prompt), choices = [];
      group.appendChild(legend);
      (question.options || []).forEach(function (option) {
        var label = node("label"), choice = node("input");
        choice.type = question.selection === "multiple" ? "checkbox" : "radio";
        choice.name = question.id; choice.value = option.id;
        label.append(choice, document.createTextNode(option.label)); group.appendChild(label); choices.push(choice);
      });
      var free = node("textarea"); free.rows = 2; free.placeholder = "Ваш ответ"; free.hidden = !question.allowFreeText;
      group.appendChild(free); form.appendChild(group); return { question: question, choices: choices, free: free };
    });
    var submit = node("button", "", "Ответить"); submit.type = "submit"; form.appendChild(submit);
    form.onsubmit = function (event) {
      event.preventDefault();
      var answers = { runId: input.runId || input.RunId, questionSetId: set.questionSetId, answers: fields.map(function (field) {
        return { questionId: field.question.id, optionIds: field.choices.filter(function (choice) { return choice.checked; }).map(function (choice) { return choice.value; }), freeText: field.free.value };
      }) };
      runChat(function (pid, sid) { return send("submitProjectChat", { workspaceId: pid, sessionId: sid,
        input: { sessionId: sid, requestId: crypto.randomUUID(), answers: answers } }); });
    };
  }
  function render() {
    el("Context").textContent = project() ? "Проекты / " + project().name : "Проект не выбран";
    el("Title").textContent = active && active.state ? field(active.state, "title", "Title") : "Чат проекта";
    el("New").disabled = busy || !project() || !project().available;
    el("Connect").disabled = busy;
    el("Create").disabled = busy;
    el("ConnectionSubmit").disabled = busy;
    el("Send").disabled = busy || !project() || !project().available || !active || !el("Input").value.trim() || !!(active && active.pendingId);
    el("Stop").hidden = !(active && active.state && field(active.state, "status", "Status") === "running");
    el("Control").hidden = !(active && active.pendingId);
    el("Pending").textContent = active && active.pendingId ? (active.pendingTool || "Действие") + "\n" + (active.pendingArguments || "") : "";
    renderQuestions(active && active.state && field(active.state, "userInput", "UserInput"));
    var messages = el("Messages");
    messages.replaceChildren();
    if (!active || !(active.messages || []).length) {
      var empty = node("div", "project-chat-empty");
      var symbol = node("span", "project-chat-empty-symbol");
      symbol.innerHTML = window.RNAssistantNavigationTree.icon("spark");
      empty.append(symbol, node("h2", "", !project() ? "Проект не выбран" : !project().available ? "Папка проекта недоступна" : active ? "С чего начнём?" : "Выберите чат проекта"),
        node("p", "", !project() ? "Подключите папку проекта или выберите проект в дереве." : !project().available ?
          "Верните папку на прежний путь или подключите её заново." :
          "Обсудите файлы проекта, подготовку данных или составьте план. Нужные файлы добавляйте в контекст явно."));
      if (project() && active) {
        var suggest = node("button", "accent-soft project-chat-suggest");
        suggest.type = "button";
        suggest.innerHTML = window.RNAssistantNavigationTree.icon("chart") + "<span>Сравнить периоды</span>";
        suggest.addEventListener("click", function () {
          el("Input").value = "Сравни сентябрь с августом и выдели главное.";
          el("Input").dispatchEvent(new Event("input", { bubbles: true }));
          el("Input").focus();
        });
        empty.appendChild(suggest);
      }
      messages.appendChild(empty);
    } else (active.messages || []).forEach(function (message) {
      var article = node("article", "message " + (message.role === "user" ? "user" : "assistant"));
      var body = node("div", "markdown");
      body.innerHTML = window.markdown(message.text || "", { preserveLineBreaks: message.role === "user" });
      article.appendChild(body); messages.appendChild(article);
    });
    if (!busy && statusText()) notice(statusText());
    renderTree();
  }
  async function refresh() {
    var version = ++serial, result;
    try { result = await send("getProjectChats", {}); }
    catch (error) { if (version === serial) notice(error.detail || error.message, true); return; }
    if (version !== serial) return;
    var previousProject = projectId(), previousChat = currentId();
    catalog = result;
    if (result.selectedWorkspaceId) selectedWorkspaceId = result.selectedWorkspaceId;
    else if (!projects().some(function (item) { return item.workspaceId === selectedWorkspaceId; }))
      selectedWorkspaceId = (projects().find(function (item) { return item.available; }) || projects()[0] || {}).workspaceId || null;
    if (previousProject !== projectId()) { active = null; pendingSend = null; }
    notice("");
    render();
    var chats = project() && project().chats || [];
    var wanted = previousProject === projectId() && chats.some(function (item) { return item.Id === previousChat; })
      ? previousChat : chats[0] && chats[0].Id;
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
    if (projectId() !== workspaceId) { ++serial; selectedWorkspaceId = workspaceId; active = null; pendingSend = null; render(); }
    if (!project() || !project().available) return;
    var version = ++serial, pid = projectId();
    notice("Загружаю чат…");
    try {
      var result = await send("getProjectChat", { workspaceId: pid, sessionId: sessionId });
      if (version === serial && projectId() === pid) { active = result; notice(statusText()); render(); }
    } catch (error) { if (version === serial) notice(error.detail || error.message, true); }
  }
  async function create() {
    if (busy || !project() || !project().available) return;
    ++serial; busy = true; render(); var pid = projectId();
    try {
      var result = await send("createProjectChat", { workspaceId: pid, title: "Новый чат" });
      if (projectId() === pid) { active = result; catalog = await send("getProjectChats", {}); notice(statusText()); render(); }
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
      if (projectId() === entry.workspaceId && currentId() === chat.Id) active = null;
      notice(""); render(); var selected = project();
      if (!active && selected && selected.available && selected.chats.length) open(selected.workspaceId, selected.chats[0].Id); }
    catch (error) { notice(error.detail || error.message, true); }
  }
  async function removeHistory(entry) {
    if (!window.confirm("Удалить историю проекта «" + entry.name + "» и убрать его из дерева? Папка и файлы останутся.")) return;
    try { catalog = await send("deleteProjectHistory", { workspaceId: entry.workspaceId });
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
    try { var result = await action(pid, sid); if (projectId() === pid && currentId() === sid) { active = result; notice(statusText()); render(); } }
    catch (error) { if (projectId() === pid && currentId() === sid) notice(error.detail || error.message, true); }
    finally { window.clearInterval(poll); poll = null; busy = false; render(); }
  }
  function setScope(next) {
    scope = next; ++serial;
    document.getElementById("chatLayout").hidden = next === "projects";
    el("Layout").hidden = next !== "projects";
    document.querySelectorAll(".chat-scope-switcher").forEach(function (picker) { picker.open = false; });
    if (next === "projects") refresh();
    else if (poll) { window.clearInterval(poll); poll = null; }
    if (window.RNAssistantShellLayout) window.RNAssistantShellLayout.refresh();
  }
  function bind() {
    document.querySelectorAll("[data-workspace-scope]").forEach(function (button) {
      button.addEventListener("click", function () { setScope(button.dataset.workspaceScope); });
    });
    document.querySelector("[data-project-scope-icon]").innerHTML = window.RNAssistantNavigationTree.icon("folder");
    el("Refresh").addEventListener("click", refresh);
    el("New").addEventListener("click", create);
    el("Search").addEventListener("input", function () { query = this.value; renderTree(); });
    [["Connect", "connect"], ["Create", "create"]].forEach(function (entry) {
      el(entry[0]).addEventListener("click", function () {
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
    el("OpenData").addEventListener("click", function () { window.RNAssistantPlugins.select("data-templates"); });
    el("Input").addEventListener("input", function () {
      el("Send").disabled = busy || !project() || !project().available || !active || !el("Input").value.trim() || !!active.pendingId;
    });
    el("Input").addEventListener("keydown", function (event) {
      if (event.key === "Enter" && !event.shiftKey && !event.isComposing) { event.preventDefault(); el("Form").requestSubmit(); }
    });
    el("Form").addEventListener("submit", function (event) {
      event.preventDefault(); var text = el("Input").value.trim(); if (!text || !active) return;
      var pid = projectId(), sid = currentId();
      if (!pendingSend || pendingSend.workspaceId !== pid || pendingSend.sessionId !== sid || pendingSend.text !== text)
        pendingSend = { workspaceId: pid, sessionId: sid, text: text, requestId: crypto.randomUUID() };
      var request = pendingSend;
      runChat(function () { return send("submitProjectChat", { workspaceId: pid, sessionId: sid,
        input: { sessionId: sid, requestId: request.requestId, message: text, mode: "agent" } }).then(function (result) {
        if (pendingSend === request) { pendingSend = null; el("Input").value = ""; } return result;
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
  window.RNAssistantProjectChats = { show: function () { if (scope === "projects") refresh(); }, scope: function () { return scope; }, setScope: setScope };
}());
