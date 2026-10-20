(function () {
  "use strict";

  var titles = { context: "Контекст", abilities: "Возможности агента", usage: "Использование контекста", activity: "Ход работы" };
  var icons = { context: "paperclip", abilities: "spark", usage: "chart", activity: "clock" };
  var view = "context";
  var open = false;
  var width = 292;
  var widthKey = "rnassistant.ui.inspector.width";
  var openKey = "rnassistant.ui.inspector.open";
  var abilitiesKey = "", abilities = null, abilitiesError = "", abilitiesGeneration = 0;
  var inspectorTransitionTimer = null;
  var panelToggle = null;

  try {
    width = Number(window.localStorage.getItem(widthKey)) || width;
    open = window.localStorage.getItem(openKey) === "1";
  } catch (error) { /* Presentation preferences are optional. */ }

  function node(tag, className, value) {
    var element = document.createElement(tag);
    if (className) element.className = className;
    if (value != null) element.textContent = value;
    return element;
  }

  function icon(name) {
    var host = node("span");
    host.innerHTML = window.RNAssistantNavigationTree.icon(name);
    return host.firstElementChild;
  }

  function info(body, title, subtitle, iconName) {
    var row = node("div", "chat-inspector-info");
    row.appendChild(icon(iconName));
    var copy = node("span");
    copy.appendChild(node("strong", "", title));
    if (subtitle) copy.appendChild(node("small", "", subtitle));
    row.appendChild(copy);
    body.appendChild(row);
  }

  function heading(body, title) { body.appendChild(node("h3", "", title)); }
  function paragraph(body, value) { body.appendChild(node("p", "", value)); }
  function action(body, title, iconName, callback) {
    var button = node("button", "chat-inspector-action");
    button.type = "button";
    button.appendChild(icon(iconName));
    button.appendChild(node("span", "", title));
    button.addEventListener("click", callback);
    body.appendChild(button);
  }

  function activeChat() {
    return typeof activeChatSummary === "function" ? activeChatSummary() : null;
  }

  function currentAbilitiesKey() {
    var id = state.activeChatId || "";
    var summary = activeChat();
    return id + ":" + (state.chatProjectionRevisions && state.chatProjectionRevisions[id] ||
      summary && (summary.Revision || summary.revision) || 0) + ":" +
      (summary && (summary.IsCurrentDocument || summary.isCurrentDocument)) + ":" +
      (summary && (summary.Mode || summary.mode));
  }

  async function loadAbilities(key) {
    var generation = ++abilitiesGeneration;
    var chatId = state.activeChatId;
    try {
      var response = await send("getAgentArsenal", { chatId: chatId, includeDetails: true });
      if (generation !== abilitiesGeneration || key !== currentAbilitiesKey() || chatId !== state.activeChatId) return;
      if (!response || response.type !== "rnassistant.agentArsenal" || response.contractVersion !== 1 ||
          response.chatId !== chatId || !response.details || !Array.isArray(response.details.Selected) ||
          !Array.isArray(response.details.Tools)) throw new Error("Некорректный состав возможностей чата.");
      abilities = response;
      abilitiesError = "";
    } catch (error) {
      if (generation !== abilitiesGeneration || key !== currentAbilitiesKey() || chatId !== state.activeChatId) return;
      abilities = null;
      abilitiesError = error.detail || error.message || "Не удалось загрузить возможности чата.";
    }
    if (open && view === "abilities") render();
  }

  function renderContext(body) {
    paragraph(body, "Выбранный контекст этого чата. Вложенные файлы включаются в следующий запрос по действующим правилам.");
    heading(body, "Документ чата");
    var chat = activeChat();
    info(body, chat ? chatDocumentTitle(chat) || "Документ не указан" : "Чат не выбран",
      chat ? "Связанный документ Office" : "Выберите чат слева", "file");
    heading(body, "Добавлено вручную");
    var notes = typeof contextNotes === "function" ? contextNotes() : [];
    if (!notes.length) paragraph(body, "Пока ничего не добавлено.");
    notes.forEach(function (note) { info(body, noteTitle(note), noteHost(note) + " · " + noteKind(note), "paperclip"); });
    action(body, "Управлять контекстом", "plus", function () {
      var button = document.getElementById("openContextTabButton");
      if (button) button.click();
    });
    action(body, "Все материалы", "files", function () { switchTab("artifacts"); });
  }

  function renderAbilities(body) {
    paragraph(body, "Возможности выбранного чата. Состав и доступность проверяются перед выполнением.");
    if (!state.activeChatId || state.bridgeUnavailable) {
      paragraph(body, "Выберите доступный чат.");
      return;
    }
    var key = currentAbilitiesKey();
    if (key !== abilitiesKey) {
      abilitiesKey = key;
      abilities = null;
      abilitiesError = "";
      loadAbilities(key);
    }
    if (abilitiesError) paragraph(body, abilitiesError);
    else if (!abilities) paragraph(body, "Загрузка возможностей…");
    else {
      if (abilities.restriction) paragraph(body, abilities.restriction);
      paragraph(body, abilities.historical ? "Состав последнего завершённого запуска" :
        abilities.pending ? "Навыки подключаются" : abilities.prepared ? "Состав следующего запроса" : "Состав текущего запуска");
      heading(body, "Навыки · " + abilities.details.Selected.length);
      if (!abilities.details.Selected.length) paragraph(body, "Подключённых навыков нет.");
      abilities.details.Selected.forEach(function (skill) {
        info(body, skill.Name || skill.Id, [skill.Status, skill.Description || skill.Reason].filter(Boolean).join(" · "), "skill");
      });
      heading(body, "Инструменты · " + abilities.details.Tools.length);
      if (!abilities.details.Tools.length) paragraph(body, "Инструментов нет.");
      abilities.details.Tools.forEach(function (tool) {
        info(body, tool.Name || tool.Id, [tool.Description, tool.RequiresConfirmation ? "С разрешения" : ""].filter(Boolean).join(" · "), "tool");
      });
    }
    action(body, "Открыть Библиотеку", "book", function () { switchTab("instructions"); });
  }

  function renderUsage(body) {
    paragraph(body, "Расход последнего ответа модели и доступное контекстное окно.");
    heading(body, "Текущий чат");
    var summary = document.getElementById("contextMeterSummary");
    info(body, summary && summary.textContent || "Данных usage ещё нет", "По данным провайдера", "chart");
    action(body, "Посмотреть подробности", "braces", function () {
      var meter = document.getElementById("contextMeter");
      if (meter) meter.click();
    });
  }

  function renderActivity(body) {
    var run = state.activeRunViewState || {};
    var lifecycle = run.lifecycle || "idle";
    var labels = { idle: "Нет выполнения", running: "Выполняется", completed: "Завершён",
      failed: "Ошибка", cancelled: "Остановлен", awaiting_user: "Нужен ваш ответ", awaiting_confirmation: "Нужно подтверждение" };
    heading(body, "Последний запуск");
    info(body, labels[lifecycle] || "Состояние недоступно", run.currentAction || "", "clock");
    if (lifecycle === "idle") { paragraph(body, "Отправьте сообщение, чтобы увидеть ход работы."); return; }
    if (typeof collectVisibleAgentTimelineItems === "function") {
      var items = (state.messages || []).map(function (message, index) {
        return { message: message, index: index, activity: messageActivity(message) };
      }).filter(function (item) { return item.activity && messageRunId(item.message) === run.runId; });
      (state.liveAgentRun || []).forEach(function (activity) {
        if (activityValue(activity, "RunId", "runId", "") === run.runId) items.push({ activity: activity });
      });
      var timeline = collectVisibleAgentTimelineItems(items);
      var steps = groupAgentRunUpdates(groupAgentRunSteps(timeline));
      var list = node("div", "chat-inspector-steps");
      steps.forEach(function (step, index) {
        var actions = [];
        function collect(activity) {
          if (["tool", "control", "diagnostic", "notice"].indexOf(activityKind(activity)) >= 0) actions.push(activity);
          activityChildren(activity).forEach(collect);
        }
        step.items.forEach(function (item) { collect(item.activity); });
        var terminal = ["failed", "cancelled", "completed"].indexOf(lifecycle) >= 0;
        var states = actions.map(function (activity) {
          var value = activityPresentationState(activity);
          return terminal && ["running", "waiting"].indexOf(value) >= 0 ? "cancelled" : value;
        });
        var status = states.indexOf("unknown") >= 0 ? "unknown" : states.indexOf("failed") >= 0 ? "failed"
          : states.some(function (value) { return ["warning", "partial", "waiting"].indexOf(value) >= 0; }) ? "warning"
          : states.indexOf("running") >= 0 ? "running" : states.indexOf("cancelled") >= 0 ? "cancelled" : states.length && states.every(function (value) { return value === "completed"; }) ? "completed" : "idle";
        var row = node("div", "chat-inspector-step is-" + status);
        row.appendChild(node("span", "chat-inspector-step-number", String(index + 1)));
        var copy = node("div");
        copy.appendChild(node("strong", "", step.message || (actions.length ? activityPrimaryText(actions[0]) : "Ход работы")));
        var last = actions[actions.length - 1];
        if (last) copy.appendChild(node("small", "", activityDisplayResult(last) || agentStatusLabel(status)));
        row.appendChild(copy); list.appendChild(row);
      });
      if (steps.length) body.appendChild(list);
    }
    if (run.executionHealth === "unknown" || run.executionHealth === "errors") {
      var note = node("div", "chat-inspector-run-notice", run.executionHealth === "unknown"
        ? "Есть неподтверждённые результаты. Проверьте фактическое состояние." : "В ходе работы были неудачные попытки.");
      note.setAttribute("role", "status"); body.appendChild(note);
    }
    heading(body, "Подробности запуска");
    if (run.runId) {
      var chatId = state.activeChatId, runId = run.runId;
      action(body, "История и диагностика", "clock", function () {
        if (state.activeChatId === chatId) window.openRunJournal({ chatId: chatId, runId: runId });
      });
    }
  }

  function render() {
    var panel = document.getElementById("chatInspector");
    var body = document.getElementById("chatInspectorBody");
    if (!panel || !body || !open) return;
    document.getElementById("chatInspectorViewTitle").textContent = titles[view];
    var summary = document.querySelector("#chatInspectorSwitcher summary");
    var currentIcon = summary.querySelector("svg");
    if (currentIcon && currentIcon !== summary.lastElementChild) currentIcon.replaceWith(icon(icons[view]));
    document.querySelectorAll("[data-chat-inspector-view]").forEach(function (button) {
      button.setAttribute("aria-checked", String(button.dataset.chatInspectorView === view));
    });
    var previousView = body.dataset.view;
    body.replaceChildren();
    ({ context: renderContext, abilities: renderAbilities, usage: renderUsage, activity: renderActivity })[view](body);
    body.dataset.view = view;
    if (previousView && previousView !== view && !window.matchMedia("(prefers-reduced-motion: reduce)").matches) {
      body.animate([{ opacity: 0 }, { opacity: 1 }], { duration: 140, easing: "cubic-bezier(.2, .8, .2, 1)" });
    }
  }

  function sync() {
    var layout = document.getElementById("chatLayout");
    var panel = document.getElementById("chatInspector");
    var divider = document.getElementById("chatInspectorSplitter");
    var toggle = document.getElementById("toggleChatInspectorButton");
    if (!layout || !panel || !divider || !toggle) return;
    layout.classList.toggle("with-inspector", open);
    layout.style.setProperty("--inspector-width", width + "px");
    panel.hidden = false;
    panel.inert = !open;
    panel.setAttribute("aria-hidden", String(!open));
    divider.hidden = window.innerWidth <= 1100;
    divider.inert = !open;
    if (panelToggle) panelToggle.sync();
    if (open) render();
  }

  function setOpen(value) {
    if (open === !!value) return;
    var layout = document.getElementById("chatLayout");
    clearTimeout(inspectorTransitionTimer);
    if (layout) {
      layout.classList.add("inspector-transition");
      inspectorTransitionTimer = setTimeout(function () { layout.classList.remove("inspector-transition"); }, 280);
    }
    open = !!value;
    try { window.localStorage.setItem(openKey, open ? "1" : "0"); } catch (error) { /* Optional. */ }
    sync();
  }

  function setWidth(value, persist) {
    var layout = document.getElementById("chatLayout");
    var available = layout ? layout.clientWidth : window.innerWidth;
    width = Math.max(220, Math.min(480, Math.min(value, available - 330)));
    if (layout) layout.style.setProperty("--inspector-width", width + "px");
    if (persist) try { window.localStorage.setItem(widthKey, String(width)); } catch (error) { /* Optional. */ }
  }

  function bindResize() {
    var divider = document.getElementById("chatInspectorSplitter");
    var drag = null;
    if (!divider) return;
    divider.tabIndex = 0;
    divider.addEventListener("pointerdown", function (event) {
      if (event.button !== 0 || !open || window.innerWidth <= 1100) return;
      event.preventDefault();
      divider.setPointerCapture(event.pointerId);
      drag = { id: event.pointerId, x: event.clientX, width: width };
      document.body.classList.add("resizing-pane");
    });
    divider.addEventListener("pointermove", function (event) {
      if (drag && event.pointerId === drag.id) setWidth(drag.width + drag.x - event.clientX, false);
    });
    function finish() {
      if (!drag) return;
      drag = null;
      document.body.classList.remove("resizing-pane");
      setWidth(width, true);
    }
    divider.addEventListener("pointerup", finish);
    divider.addEventListener("pointercancel", finish);
    divider.addEventListener("lostpointercapture", finish);
    divider.addEventListener("dblclick", function () { setWidth(292, true); });
    divider.addEventListener("keydown", function (event) {
      var step = event.shiftKey ? 40 : 12;
      if (event.key === "ArrowLeft") setWidth(width + step, true);
      else if (event.key === "ArrowRight") setWidth(width - step, true);
      else if (event.key === "Home") setWidth(220, true);
      else if (event.key === "End") setWidth(480, true);
      else if (event.key === "Enter") setWidth(292, true);
      else return;
      event.preventDefault();
    });
  }

  function bind() {
    panelToggle = window.RNAssistantPanelToggle.bind(document.getElementById("toggleChatInspectorButton"), {
      controls: "chatInspector", isOpen: function () { return open; }, setOpen: setOpen
    });
    var summary = document.querySelector("#chatInspectorSwitcher summary");
    summary.insertBefore(icon(icons.context), summary.firstChild);
    document.querySelectorAll("[data-chat-inspector-view]").forEach(function (button) {
      button.insertBefore(icon(icons[button.dataset.chatInspectorView]), button.firstChild);
    });
    var panel = document.getElementById("chatInspector");
    var toggle = document.getElementById("toggleChatInspectorButton");
    document.getElementById("closeChatInspectorButton").addEventListener("click", function () {
      setOpen(false);
      toggle.focus({ preventScroll: true });
    });
    panel.addEventListener("keydown", function (event) {
      if (event.key !== "Escape" || !open) return;
      var switcher = document.getElementById("chatInspectorSwitcher");
      if (switcher.open) {
        switcher.open = false;
        switcher.querySelector("summary").focus();
      } else {
        setOpen(false);
        toggle.focus({ preventScroll: true });
      }
      event.preventDefault();
      event.stopPropagation();
    });
    document.getElementById("chatInspectorSwitcher").addEventListener("click", function (event) {
      var button = event.target.closest("[data-chat-inspector-view]");
      if (!button) return;
      view = button.dataset.chatInspectorView;
      document.getElementById("chatInspectorSwitcher").open = false;
      render();
    });
    bindResize();
    window.addEventListener("resize", function () {
      clearTimeout(inspectorTransitionTimer);
      document.getElementById("chatLayout").classList.remove("inspector-transition");
      sync();
    });
    sync();
  }

  window.RNAssistantChatInspector = { bind: bind, render: render, refreshLayout: sync };
}());
