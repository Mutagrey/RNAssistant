(function () {
  "use strict";
  var scope = "office", catalog = null, active = null, busy = false, serial = 0, poll = null, pendingSend = null, projectExpanded = true;
  function el(id) { return document.getElementById("projectChat" + id); }
  function node(tag, className, text) {
    var result = document.createElement(tag);
    result.className = className || "";
    if (text !== undefined) result.textContent = text;
    return result;
  }
  function project() { return catalog && catalog.project; }
  function projectId() { return project() && project().projectId; }
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
    var root = el("Tree"), selected = currentId(), query = "";
    var chats = catalog && catalog.chats || [];
    if (!project()) {
      window.RNAssistantNavigationTree.render(root, [], { label: "Проекты и чаты", emptyText: "Проект данных не выбран. Откройте его в «Данные и шаблоны»." });
      return;
    }
    var items = chats.filter(function (chat) { return !query || chat.Title.toLocaleLowerCase().includes(query); }).map(function (chat) {
      var runStatus = chat.Id === selected && runState() ? field(runState(), "status", "Status") : chat.RunStatus;
      return { key: "project-chat:" + chat.Id, kind: "chat", title: chat.Title, selected: chat.Id === selected,
        status: runStatus === "running" ? "running" : runStatus === "awaitingconfirmation" ? "waiting" : "",
        actions: [
          { title: "Переименовать чат «" + chat.Title + "»", iconHtml: window.RNAssistantNavigationTree.icon("edit"), onClick: function () { rename(chat); } },
          { title: "Удалить чат «" + chat.Title + "»", iconHtml: window.RNAssistantNavigationTree.icon("trash"), danger: true,
            disabled: runStatus === "running" || runStatus === "awaitingconfirmation", onClick: function () { remove(chat); } }
        ], sessionId: chat.Id };
    });
    window.RNAssistantNavigationTree.render(root, [{ key: "project:" + projectId(), kind: "parent", title: project().name,
      iconHtml: window.RNAssistantNavigationTree.icon("folder"), expanded: projectExpanded, children: items,
      actions: [{ title: "Новый чат в проекте", iconHtml: window.RNAssistantNavigationTree.icon("plus"), onClick: create }] }],
    { label: "Проекты и чаты", onSelect: function (item) { if (item.sessionId) open(item.sessionId); },
      onToggle: function (item, expanded) { if (item.key === "project:" + projectId()) projectExpanded = expanded; } });
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
      runChat(function (pid, sid) { return send("submitProjectChat", { projectId: pid, sessionId: sid,
        input: { sessionId: sid, requestId: crypto.randomUUID(), answers: answers } }); });
    };
  }
  function render() {
    el("Context").textContent = project() ? "Проекты / " + project().name : "Проект не выбран";
    el("Title").textContent = active && active.state ? field(active.state, "title", "Title") : "Чат проекта";
    el("New").disabled = busy || !project();
    el("Send").disabled = busy || !active || !el("Input").value.trim() || !!(active && active.pendingId);
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
      empty.append(symbol, node("h2", "", !project() ? "Проект не выбран" : active ? "С чего начнём?" : "Выберите чат проекта"),
        node("p", "", !project() ? "Откройте проект в разделе «Данные и шаблоны», затем вернитесь к проектным чатам." :
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
    catalog = result; if (previousProject !== projectId()) { active = null; pendingSend = null; projectExpanded = true; }
    notice("");
    render();
    var wanted = previousProject === projectId() && (catalog.chats || []).some(function (item) { return item.Id === previousChat; })
      ? previousChat : (catalog.chats || [])[0] && catalog.chats[0].Id;
    if (wanted) await open(wanted);
  }
  async function open(sessionId) {
    if (!project() || !sessionId) return;
    var version = ++serial, pid = projectId();
    notice("Загружаю чат…");
    try {
      var result = await send("getProjectChat", { projectId: pid, sessionId: sessionId });
      if (version === serial && projectId() === pid) { active = result; notice(statusText()); render(); }
    } catch (error) { if (version === serial) notice(error.detail || error.message, true); }
  }
  async function create() {
    if (busy || !project()) return;
    busy = true; render(); var pid = projectId();
    try {
      var result = await send("createProjectChat", { projectId: pid, title: "Новый чат" });
      if (projectId() === pid) { active = result; catalog = await send("getProjectChats", {}); notice(statusText()); render(); }
    } catch (error) { notice(error.detail || error.message, true); }
    finally { busy = false; render(); }
  }
  async function rename(chat) {
    var title = window.prompt("Название чата", chat.Title);
    if (title === null || !title.trim() || title.trim() === chat.Title) return;
    try { catalog = await send("renameProjectChat", { projectId: projectId(), sessionId: chat.Id, title: title.trim() });
      if (currentId() === chat.Id) { active.state.Title = title.trim(); active.state.title = title.trim(); } notice(""); render(); }
    catch (error) { notice(error.detail || error.message, true); }
  }
  async function remove(chat) {
    if (!window.confirm("Удалить чат «" + chat.Title + "»? История этого чата будет удалена; файлы проекта останутся.")) return;
    try { catalog = await send("deleteProjectChat", { projectId: projectId(), sessionId: chat.Id });
      if (currentId() === chat.Id) active = null; notice(""); render(); if (!active && catalog.chats.length) open(catalog.chats[0].Id); }
    catch (error) { notice(error.detail || error.message, true); }
  }
  async function readCurrent(pid, sid) {
    try { var result = await send("getProjectChat", { projectId: pid, sessionId: sid });
      if (projectId() === pid && currentId() === sid) { active = result; notice(statusText()); render(); } }
    catch (error) { if (projectId() === pid && currentId() === sid) notice(error.detail || error.message, true); }
  }
  async function runChat(action) {
    if (busy || !active || !project()) return;
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
    el("OpenData").addEventListener("click", function () { window.RNAssistantPlugins.select("data-templates"); });
    el("Input").addEventListener("input", function () { el("Send").disabled = busy || !active || !el("Input").value.trim(); });
    el("Input").addEventListener("keydown", function (event) {
      if (event.key === "Enter" && !event.shiftKey && !event.isComposing) { event.preventDefault(); el("Form").requestSubmit(); }
    });
    el("Form").addEventListener("submit", function (event) {
      event.preventDefault(); var text = el("Input").value.trim(); if (!text || !active) return;
      var pid = projectId(), sid = currentId();
      if (!pendingSend || pendingSend.projectId !== pid || pendingSend.sessionId !== sid || pendingSend.text !== text)
        pendingSend = { projectId: pid, sessionId: sid, text: text, requestId: crypto.randomUUID() };
      var request = pendingSend;
      runChat(function () { return send("submitProjectChat", { projectId: pid, sessionId: sid,
        input: { sessionId: sid, requestId: request.requestId, message: text, mode: "agent" } }).then(function (result) {
        if (pendingSend === request) { pendingSend = null; el("Input").value = ""; } return result;
      }); });
    });
    el("Stop").addEventListener("click", async function () {
      var state = runState(), pid = projectId(), sid = currentId();
      if (!state || !pid || !sid) return;
      el("Stop").disabled = true;
      try {
        var result = await send("cancelProjectChat", { projectId: pid, sessionId: sid,
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
      if (pendingId) runChat(function (pid, sid) { return send("resolveProjectChat", { projectId: pid, sessionId: sid, pendingId: pendingId, approve: entry[1] }); });
    }); });
    render();
  }
  document.addEventListener("DOMContentLoaded", bind);
  window.RNAssistantProjectChats = { show: function () { if (scope === "projects") refresh(); }, scope: function () { return scope; }, setScope: setScope };
}());
