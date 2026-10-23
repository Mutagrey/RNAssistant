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
  var usageKey = "", usageSnapshot = null, usageError = "", usageRequest = null;
  var presentationChat = "", viewState = {};
  var activitySelection = null, activityFocusPending = false, returnFocus = null;
  var requestView = null, requestMode = "docked", requestRestoreMode = "docked", requestWidth = 640;
  var requestIsolation = [], requestScroll = {}, requestContent = null;

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

  function disclosure(parent, key, title, meta) {
    var details = node("details", "chat-inspector-disclosure");
    details.dataset.inspectorKey = key;
    var summary = node("summary");
    summary.appendChild(node("span", "chat-inspector-disclosure-title", title));
    if (meta) summary.appendChild(node("small", "chat-inspector-disclosure-meta", meta));
    details.appendChild(summary);
    parent.appendChild(details);
    return details;
  }

  function capability(parent, item, kind) {
    var details = disclosure(parent, kind + ":" + item.Id, item.Name || item.Id,
      item.RequiresConfirmation ? "С разрешения" : "");
    var summary = details.querySelector("summary");
    summary.insertBefore(icon(kind === "skill" ? "skill" : "tool"), summary.firstChild);
    var description = String(item.Description || item.Reason || "");
    if (description) summary.appendChild(node("span", "chat-inspector-description-preview", description));
    var content = node("div", "chat-inspector-disclosure-body");
    if (description) paragraph(content, description);
    content.appendChild(node("code", "", item.Id || ""));
    if (item.Status) paragraph(content, item.Status);
    details.appendChild(content);
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
      paragraph(body, abilities.historical ? "Последний запуск · доступность проверяется перед выполнением" :
        abilities.pending ? "Навыки подключаются" : abilities.prepared ? "Состав следующего запроса" : "Состав текущего запуска");
      heading(body, "Навыки · " + abilities.details.Selected.length);
      if (!abilities.details.Selected.length) paragraph(body, "Подключённых навыков нет.");
      abilities.details.Selected.forEach(function (skill) {
        capability(body, skill, "skill");
      });
      heading(body, "Инструменты · " + abilities.details.Tools.length);
      if (!abilities.details.Tools.length) paragraph(body, "Инструментов нет.");
      var groups = new Map();
      abilities.details.Tools.forEach(function (tool) {
        var group = window.RNAssistantLibraryGroups.category(tool.Id);
        if (!groups.has(group.key)) groups.set(group.key, { title: group.title, tools: [] });
        groups.get(group.key).tools.push(tool);
      });
      groups.forEach(function (group, id) {
        var details = disclosure(body, "tools:" + id, group.title, String(group.tools.length));
        details.classList.add("chat-inspector-tool-group");
        group.tools.forEach(function (tool) { capability(details, tool, "tool"); });
      });
    }
    action(body, "Открыть Библиотеку", "book", function () { switchTab("instructions"); });
  }

  function usageInputKey() {
    return JSON.stringify([currentAbilitiesKey(), typeof officeChatDraftText === "function" ? officeChatDraftText() : "",
      promptContextResourceDraftIds(), state.contextUsage, typeof activeChatModel === "function" ? activeChatModel() : ""]);
  }

  function cancelUsage() {
    var request = usageRequest;
    usageRequest = null;
    if (request) {
      usageKey = "";
      if (request.requestId) cancelBridgeRequest(request.requestId).catch(function () {});
    }
  }

  async function loadUsage(key) {
    cancelUsage();
    usageKey = key; usageSnapshot = null; usageError = "";
    var operation = { chatId: state.activeChatId, requestId: null };
    usageRequest = operation;
    try {
      var request = send("inspectPromptContext", { chatId: operation.chatId,
        text: typeof officeChatDraftText === "function" ? officeChatDraftText() : "",
        resourceDraftIds: promptContextResourceDraftIds(), includeRaw: false });
      operation.requestId = request.requestId;
      var response = await request;
      if (usageRequest !== operation || operation.chatId !== state.activeChatId || key !== usageInputKey()) return;
      if (!response || response.chatId !== operation.chatId || !Array.isArray(response.sections))
        throw new Error("Состав контекста недоступен.");
      usageSnapshot = response;
    } catch (error) {
      if (usageRequest === operation && key === usageInputKey()) usageError = error.detail || error.message || "Не удалось получить состав.";
    } finally {
      if (response && response.rawData) closePromptContextDownload({ chatId: operation.chatId, lease: response.rawData });
      if (usageRequest === operation) { usageRequest = null; if (open && view === "usage") render(); }
    }
  }

  function renderComposition(body, snapshot) {
    var groups = [
      { title: "Диалог и запрос", ids: ["history", "tool_history", "current_request"] },
      { title: "Инструкции", ids: ["instructions", "tool_instructions", "skill_instructions"] },
      { title: "Инструменты и навыки", ids: ["tools", "capabilities", "catalog_tools", "catalog_skills"] },
      { title: "Ресурсы", ids: ["document_context", "attachments", "artifacts"] },
      { title: "Формат и прочее", ids: [] }
    ];
    groups.forEach(function (group) { group.sections = []; group.tokens = 0; group.unknown = false; });
    var excluded = [], reserves = [];
    snapshot.sections.forEach(function (section) {
      if (section.included === false) { excluded.push(section); return; }
      if (["format_repair_reserve", "continuation_reserve"].indexOf(section.id) >= 0) { reserves.push(section); return; }
      var group = groups.find(function (item) { return item.ids.indexOf(section.id) >= 0; }) || groups[4];
      group.sections.push(section);
      if (typeof section.tokens === "number" && isFinite(section.tokens) && section.tokens >= 0) group.tokens += section.tokens;
      else group.unknown = true;
    });
    var total = groups.reduce(function (sum, group) { return sum + group.tokens; }, 0);
    var unknown = groups.some(function (group) { return group.unknown; });
    var chart = node("div", "chat-inspector-composition-chart");
    chart.setAttribute("role", "img");
    chart.setAttribute("aria-label", "Доли оценённых частей следующего запроса" + (unknown ? "; часть размеров неизвестна" : "") + ": " + groups.filter(function (group) { return group.tokens; }).map(function (group) {
      return group.title + " " + Math.round(group.tokens * 100 / total) + "%";
    }).join(", "));
    if (total) {
      groups.forEach(function (group, index) {
        if (!group.tokens) return;
        var segment = node("span", "chat-inspector-composition-color color-" + index);
        segment.style.flexGrow = String(group.tokens);
        chart.appendChild(segment);
      });
      body.appendChild(chart);
    }
    paragraph(body, "≈ " + formatNumber(total) + " токенов · оценка следующего запроса" + (unknown ? " · часть размеров неизвестна" : ""));
    function sectionDetails(parent, sections, included) {
      sections.forEach(function (section) {
        heading(parent, section.title || section.id);
        if (section.detail) paragraph(parent, section.detail);
        (section.items || []).forEach(function (item) {
          var row = renderPromptContextItem(item, included, included);
          if (row.tagName === "DETAILS") row.dataset.inspectorKey = "context-item:" + section.id + ":" + item.id;
          parent.appendChild(row);
        });
        if (!(section.items || []).length) paragraph(parent, "Подробности элементов не записаны.");
      });
    }
    groups.forEach(function (group, index) {
      if (!group.sections.length) return;
      var percent = total && group.tokens ? Math.round(group.tokens * 100 / total) : 0;
      var details = disclosure(body, "composition:" + index, group.title,
        group.unknown ? "неполная оценка" : "≈ " + formatNumber(group.tokens) + " · " + percent + "%");
      var dot = node("i", "chat-inspector-composition-dot chat-inspector-composition-color color-" + index);
      dot.setAttribute("aria-hidden", "true"); details.querySelector("summary").prepend(dot);
      var content = node("div", "chat-inspector-disclosure-body");
      sectionDetails(content, group.sections, true); details.appendChild(content);
    });
    if (reserves.length) sectionDetails(disclosure(body, "reserves", "Резервы бюджета", "вне диаграммы"), reserves, true);
    if (excluded.length) sectionDetails(disclosure(body, "excluded", "Не включено в запрос", String(excluded.length) + " разделов"), excluded, false);
  }

  function renderUsage(body) {
    heading(body, "Последний запрос · API usage");
    var usage = lastModelUsage(), capacity = Number(promptContextInspectorValue(state.contextUsage, "contextWindowTokens", "ContextWindowTokens", 0));
    var known = usage && typeof usage.prompt === "number" && isFinite(usage.prompt) && usage.prompt >= 0;
    info(body, known ? formatNumber(usage.prompt) + (capacity > 0 ? " / " + formatNumber(capacity) : "") + " токенов" : "API ещё не сообщил число входных токенов",
      known && capacity > 0 ? "Свободно " + Math.max(0, Math.round(100 - usage.prompt * 100 / capacity)) + "% · вход по данным API" : "По данным провайдера", "chart");
    heading(body, "Из чего состоит контекст");
    if (!state.activeChatId || state.bridgeUnavailable || state.chatReadOnly) {
      paragraph(body, state.chatReadOnly ? "Предварительный состав недоступен для архивного чата. Откройте сохранённый запрос." : "Выберите доступный чат.");
    } else {
      var key = usageInputKey();
      if (key !== usageKey) loadUsage(key);
      if (usageError) paragraph(body, usageError);
      else if (!usageSnapshot) paragraph(body, "Собираю состав следующего запроса…");
      else {
        renderComposition(body, usageSnapshot);
        paragraph(body, "Доли рассчитаны локально, а не получены от API. Нажмите на раздел, чтобы увидеть его содержимое.");
      }
      action(body, "Обновить состав", "refresh", function () { loadUsage(usageInputKey()); render(); });
    }
    action(body, "Фактический запрос / ответ", "braces", function () { window.RNAssistantModelContext.open(); });
    action(body, "Бюджет и подробная оценка", "chart", function () {
      var meter = document.getElementById("contextMeter");
      if (meter) meter.click();
    });
  }

  function renderActivity(body) {
    var run = state.activeRunViewState || {};
    if (activitySelection && run.runId !== activitySelection.runId) {
      run = (state.messages || []).filter(function (message) {
        return messageRunId(message) === activitySelection.runId;
      }).map(messageRunViewState).filter(function (snapshot) {
        return snapshot && snapshot.runId === activitySelection.runId;
      }).pop() || { runId: activitySelection.runId, lifecycle: "unavailable" };
    }
    var lifecycle = run.lifecycle || "idle";
    var labels = { idle: "Нет выполнения", running: "Выполняется", completed: "Завершён",
      failed: "Ошибка", cancelled: "Остановлен", awaiting_user: "Нужен ваш ответ", awaiting_confirmation: "Нужно подтверждение" };
    heading(body, activitySelection ? "Выбранный запуск" : "Последний запуск");
    if (activitySelection) {
      if (!activitySelection.stepId) body.lastElementChild.dataset.inspectorSelection = "true";
      action(body, "К последнему запуску", "left", function () {
        activitySelection = null; viewState.activity = null; render();
        body.querySelector("h3").tabIndex = -1; body.querySelector("h3").focus({ preventScroll: true });
      });
    }
    var narrative = String(run.currentAction || "");
    info(body, labels[lifecycle] || "Состояние недоступно", narrative.length <= 180 ? narrative : "", "clock");
    if (narrative.length > 180) paragraph(disclosure(body, "run-summary:" + run.runId, "Итог запуска", "Подробнее"), narrative);
    if (lifecycle === "idle") { paragraph(body, "Отправьте сообщение, чтобы увидеть ход работы."); return; }
    var selectedStepFound = false;
    if (typeof collectVisibleAgentTimelineItems === "function") {
      var items = (state.messages || []).map(function (message, index) {
        return { message: message, index: index, activity: messageActivity(message) };
      }).filter(function (item) { return item.activity && messageRunId(item.message) === run.runId; });
      (state.liveAgentRun || []).forEach(function (activity) {
        if (activityValue(activity, "RunId", "runId", "") === run.runId) items.push({ activity: activity });
      });
      var timeline = collectVisibleAgentTimelineItems(items);
      var steps = groupAgentRunSteps(timeline);
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
        var copy = node("div", "chat-inspector-step-copy");
        var details = disclosure(copy, "step:" + run.runId + ":" + step.id, step.message || (actions.length ? activityPrimaryText(actions[0]) : "Шаг без сообщения"),
          agentStatusLabel(status) + " · действий: " + actions.length);
        details.classList.add("chat-inspector-step-detail");
        var sourceChatId = state.activeChatId, sourceRunId = run.runId;
        var stepId = step.marker ? activityStepId(step.marker.activity) : actions.length ? activityStepId(actions[0]) : "";
        if (activitySelection && activitySelection.stepId && activitySelection.stepId === stepId) {
          selectedStepFound = true;
          row.classList.add("is-selected");
          details.dataset.inspectorSelection = "true";
          details.querySelector("summary").setAttribute("aria-current", "step");
        }
        actions.forEach(function (activity) {
          var content = node("div", "chat-inspector-step-call");
          content.appendChild(node("strong", "", activityToolId(activity) || activityPrimaryText(activity)));
          content.appendChild(node("small", "", agentStatusLabel(activityPresentationState(activity))));
          var result = activityDisplayResult(activity);
          if (result) paragraph(content, result);
          var callId = activityToolCallId(activity), callStep = activityStepId(activity);
          if (callId || callStep) action(content, "События действия", "clock", function () {
            if (state.activeChatId === sourceChatId) window.openRunJournal({ chatId: sourceChatId, runId: sourceRunId,
              stepId: callStep, toolCallId: callId });
          });
          details.appendChild(content);
        });
        if (stepId) action(details, "Запрос / ответ модели", "braces", function () {
          if (state.activeChatId === sourceChatId) window.RNAssistantModelContext.open({ chatId: sourceChatId, runId: sourceRunId, stepId: stepId });
        });
        row.appendChild(copy); list.appendChild(row);
      });
      if (steps.length) body.appendChild(list);
    }
    if (activitySelection && activitySelection.stepId && !selectedStepFound) {
      var missing = node("p", "chat-inspector-run-notice", "Шаг не найден в загруженной части истории. Его события можно открыть в журнале.");
      missing.dataset.inspectorSelection = "true";
      missing.setAttribute("role", "status"); body.appendChild(missing);
    }
    if (run.executionHealth === "unknown" || run.executionHealth === "errors") {
      var note = node("div", "chat-inspector-run-notice", run.executionHealth === "unknown"
        ? "Есть неподтверждённые результаты. Проверьте фактическое состояние." : "В ходе работы были неудачные попытки.");
      note.setAttribute("role", "status"); body.appendChild(note);
    }
    heading(body, "Подробности запуска");
    if (run.runId) {
      var chatId = state.activeChatId, runId = run.runId;
      var selectedStep = activitySelection && activitySelection.stepId || "";
      action(body, "История и диагностика", "clock", function () {
        if (state.activeChatId === chatId) window.openRunJournal({ chatId: chatId, runId: runId, stepId: selectedStep });
      });
    }
  }

  function render() {
    var panel = document.getElementById("chatInspector");
    var body = document.getElementById("chatInspectorBody");
    if (!panel || !body || !open) return;
    if (requestView) {
      if (requestView.chatId !== state.activeChatId) window.RNAssistantModelContext.close();
      return;
    }
    if (presentationChat !== state.activeChatId) {
      presentationChat = state.activeChatId; viewState = {}; cancelUsage(); usageKey = ""; usageSnapshot = null;
      if (activitySelection && activitySelection.chatId !== state.activeChatId) {
        activitySelection = null; activityFocusPending = false; returnFocus = null;
      }
    }
    document.getElementById("chatInspectorViewTitle").textContent = titles[view];
    var summary = document.querySelector("#chatInspectorSwitcher summary");
    var currentIcon = summary.querySelector("[data-inspector-icon]");
    var nextIcon = icon(icons[view]); nextIcon.dataset.inspectorIcon = "true";
    if (currentIcon) currentIcon.replaceWith(nextIcon);
    else summary.prepend(nextIcon);
    document.querySelectorAll("[data-chat-inspector-view]").forEach(function (button) {
      button.setAttribute("aria-checked", String(button.dataset.chatInspectorView === view));
    });
    var previousView = body.dataset.view;
    var focused = body.contains(document.activeElement) && document.activeElement.closest("[data-inspector-key]");
    var focusKey = focused && focused.dataset.inspectorKey;
    if (previousView && body.dataset.chatId === state.activeChatId) viewState[previousView] = {
      expanded: Array.from(body.querySelectorAll("details[open][data-inspector-key]")).map(function (item) { return item.dataset.inspectorKey; }), scroll: body.scrollTop
    };
    body.replaceChildren();
    ({ context: renderContext, abilities: renderAbilities, usage: renderUsage, activity: renderActivity })[view](body);
    body.dataset.view = view;
    body.dataset.chatId = state.activeChatId || "";
    var saved = viewState[view];
    if (saved) {
      body.querySelectorAll("details[data-inspector-key]").forEach(function (item) { item.open = saved.expanded.indexOf(item.dataset.inspectorKey) >= 0; });
      body.scrollTop = saved.scroll;
    }
    if (focusKey && previousView === view) {
      var focusTarget = Array.from(body.querySelectorAll("[data-inspector-key]")).find(function (item) { return item.dataset.inspectorKey === focusKey; });
      if (focusTarget) focusTarget.querySelector("summary").focus({ preventScroll: true });
    }
    if (activityFocusPending && view === "activity") {
      activityFocusPending = false;
      var selected = body.querySelector("[data-inspector-selection]");
      if (selected) {
        if (selected.tagName === "DETAILS") { selected.open = true; selected = selected.querySelector("summary"); }
        else selected.tabIndex = -1;
        selected.focus({ preventScroll: true });
        selected.scrollIntoView({ block: "nearest" });
      }
    }
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
    panel.classList.toggle("is-request", !!requestView);
    panel.classList.toggle("is-request-wide", !!requestView && requestMode === "wide");
    panel.classList.toggle("is-request-focus", !!requestView && requestMode === "focus");
    setWidth(requestView && requestMode === "wide" ? requestWidth : width, false, true);
    panel.hidden = false;
    panel.inert = !open;
    panel.setAttribute("aria-hidden", String(!open));
    divider.hidden = window.innerWidth <= 1100 || requestMode === "focus";
    divider.inert = !open;
    if (panelToggle) panelToggle.sync();
    if (open) render();
    isolateRequest(!!requestView && requestMode === "focus");
  }

  function setOpen(value) {
    if (!value && requestView) {
      window.RNAssistantModelContext.close();
      setOpen(false);
      document.getElementById("toggleChatInspectorButton").focus({ preventScroll: true });
      return;
    }
    if (open === !!value) return;
    var layout = document.getElementById("chatLayout");
    clearTimeout(inspectorTransitionTimer);
    if (layout) {
      layout.classList.add("inspector-transition");
      inspectorTransitionTimer = setTimeout(function () { layout.classList.remove("inspector-transition"); }, 280);
    }
    open = !!value;
    if (!open) cancelUsage();
    try { window.localStorage.setItem(openKey, open ? "1" : "0"); } catch (error) { /* Optional. */ }
    sync();
  }

  function widthBounds() {
    var layout = document.getElementById("chatLayout");
    var available = layout ? layout.clientWidth : window.innerWidth;
    if (requestView && requestMode === "wide") {
      var nav = layout && layout.querySelector(".chat-sidebar");
      var navWidth = nav && window.RNAssistantShellLayout.isNavigationOpen() ? nav.getBoundingClientRect().width : 0;
      return { min: 400, max: Math.max(400, Math.min(960, available - navWidth - 332)) };
    }
    return { min: 220, max: Math.max(220, Math.min(480, available - 330)) };
  }

  function setWidth(value, persist, layoutOnly) {
    var layout = document.getElementById("chatLayout"), bounds = widthBounds();
    var next = Math.max(bounds.min, Math.min(bounds.max, value));
    // Responsive clamping changes the visible size, not the user's chosen width.
    if (!layoutOnly) { if (requestView && requestMode === "wide") requestWidth = next; else width = next; }
    if (layout) layout.style.setProperty("--inspector-width", next + "px");
    window.RNAssistantSplitter.update(document.getElementById("chatInspectorSplitter"), next, bounds);
    if (persist && (!requestView || requestMode !== "wide")) try { window.localStorage.setItem(widthKey, String(width)); } catch (error) { /* Optional. */ }
  }

  function bindResize() {
    var divider = document.getElementById("chatInspectorSplitter");
    window.RNAssistantSplitter.bind(divider, {
      getValue: function () {
        var bounds = widthBounds(), value = requestView && requestMode === "wide" ? requestWidth : width;
        return Math.max(bounds.min, Math.min(bounds.max, value));
      },
      setValue: setWidth,
      bounds: widthBounds,
      defaultValue: function () { return requestView && requestMode === "wide" ? 640 : 292; },
      direction: -1,
      enabled: function () { return open && requestMode !== "focus" && window.innerWidth > 1100; }
    });
  }

  function isolateRequest(enabled) {
    if (!enabled) {
      var hadIsolation = requestIsolation.length > 0;
      requestIsolation.forEach(function (entry) { entry.node.inert = entry.inert; }); requestIsolation = [];
      if (hadIsolation) window.RNAssistantShellLayout.refresh();
      return;
    }
    if (!requestIsolation.length) {
      var branch = document.getElementById("chatInspector");
      while (branch && branch.parentElement) {
        Array.from(branch.parentElement.children).forEach(function (sibling) {
          if (sibling !== branch && !/^(SCRIPT|STYLE|LINK)$/.test(sibling.tagName))
            requestIsolation.push({ node: sibling, inert: sibling.inert });
        });
        branch = branch.parentElement;
        if (branch === document.body) break;
      }
    }
    requestIsolation.forEach(function (entry) { entry.node.inert = true; });
  }

  function requestLayout(mode) {
    if (!requestView || ["docked", "wide", "focus"].indexOf(mode) < 0) return;
    var body = document.getElementById("modelContextBody");
    if (requestContent !== body.firstElementChild) { requestContent = body.firstElementChild; requestScroll = {}; }
    requestScroll[requestMode] = body.scrollTop;
    var top = body.scrollTop;
    if (mode === "focus" && requestMode !== "focus") requestRestoreMode = requestMode;
    isolateRequest(false);
    requestMode = mode;
    document.getElementById("modelContextLayout").value = mode;
    var viewer = document.getElementById("modelContextDialog");
    viewer.setAttribute("role", mode === "focus" ? "dialog" : "region");
    if (mode === "focus") viewer.setAttribute("aria-modal", "true"); else viewer.removeAttribute("aria-modal");
    sync();
    body.scrollTop = requestScroll[mode] === undefined ? top : requestScroll[mode];
  }

  function showRequest(trigger) {
    var focused = trigger || document.activeElement, details = focused && focused.closest("[data-inspector-key]");
    requestView = { open: open, chatId: state.activeChatId, focused: focused,
      key: details && details.dataset.inspectorKey, label: focused && focused.textContent };
    requestMode = "docked"; requestScroll = {}; requestContent = null;
    cancelUsage();
    setOpen(true); requestLayout("docked");
  }

  function hideRequest() {
    if (!requestView) return;
    var previous = requestView;
    isolateRequest(false);
    requestView = null; requestMode = "docked";
    setOpen(previous.open); sync();
    window.RNAssistantShellLayout.refresh();
    var target = previous.focused;
    if ((!target || !target.isConnected || !target.getClientRects().length) && previous.chatId === state.activeChatId && previous.key) {
      var details = Array.from(document.querySelectorAll("#chatInspectorBody [data-inspector-key]")).find(function (item) {
        return item.dataset.inspectorKey === previous.key;
      });
      target = details && Array.from(details.querySelectorAll("button, summary")).find(function (item) { return item.textContent === previous.label; });
    }
    if (!target || !target.isConnected || !target.getClientRects().length) target = document.getElementById("toggleChatInspectorButton");
    target.focus({ preventScroll: true });
  }

  function openActivity(options, trigger) {
    if (!options || !options.runId || options.chatId !== state.activeChatId ||
        (window.RNAssistantWorkspaceChat && window.RNAssistantWorkspaceChat.owner() !== "office")) return false;
    if (requestView) window.RNAssistantModelContext.close();
    activitySelection = { chatId: options.chatId, runId: options.runId, stepId: options.stepId || "" };
    activityFocusPending = true;
    returnFocus = trigger || document.activeElement;
    view = "activity"; cancelUsage();
    if (open) render(); else setOpen(true);
    return true;
  }

  function restoreFocus() {
    // Media queries can change before the queued resize event updates navigation.
    window.RNAssistantShellLayout.refresh();
    var target = returnFocus && returnFocus.isConnected && activitySelection && activitySelection.chatId === state.activeChatId
      ? returnFocus : document.getElementById("toggleChatInspectorButton");
    if (!target.getClientRects().length) target = document.getElementById("toggleChatInspectorButton");
    target.focus({ preventScroll: true });
  }

  function bind() {
    panelToggle = window.RNAssistantPanelToggle.bind(document.getElementById("toggleChatInspectorButton"), {
      controls: "chatInspector", isOpen: function () { return open; }, setOpen: setOpen
    });
    document.querySelectorAll("[data-chat-inspector-view]").forEach(function (button) {
      // Menu icons already belong to index.html and app-icons; do not append a second SVG.
      button.setAttribute("role", "menuitemradio");
    });
    var panel = document.getElementById("chatInspector");
    document.getElementById("closeChatInspectorButton").addEventListener("click", function () {
      setOpen(false);
      restoreFocus();
    });
    panel.addEventListener("keydown", function (event) {
      if (event.key !== "Escape" || !open || requestView) return;
      var switcher = document.getElementById("chatInspectorSwitcher");
      if (switcher.open) {
        switcher.open = false;
        switcher.querySelector("summary").focus();
      } else {
        setOpen(false);
        restoreFocus();
      }
      event.preventDefault();
      event.stopPropagation();
    });
    document.getElementById("chatInspectorSwitcher").addEventListener("click", function (event) {
      var button = event.target.closest("[data-chat-inspector-view]");
      if (!button) return;
      view = button.dataset.chatInspectorView;
      if (view !== "usage") cancelUsage();
      document.getElementById("chatInspectorSwitcher").open = false;
      render();
      document.querySelector("#chatInspectorSwitcher summary").focus({ preventScroll: true });
    });
    bindResize();
    window.addEventListener("pagehide", cancelUsage);
    window.addEventListener("resize", function () {
      clearTimeout(inspectorTransitionTimer);
      document.getElementById("chatLayout").classList.remove("inspector-transition");
      sync();
    });
    sync();
  }

  window.RNAssistantChatInspector = { bind: bind, render: render, refreshLayout: sync, openActivity: openActivity,
    showRequest: showRequest, hideRequest: hideRequest, setRequestLayout: requestLayout,
    restoreRequestLayout: function () { requestLayout(requestRestoreMode); } };
}());
