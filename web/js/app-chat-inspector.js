(function () {
  "use strict";

  var titles = { context: "Контекст", abilities: "Возможности агента", usage: "Токены и прогноз", activity: "Ход работы" };
  var icons = { context: "paperclip", abilities: "spark", usage: "chart", activity: "clock" };
  var view = "activity";
  var open = false;
  var visible = false;
  var abilitiesKey = "", abilities = null, abilitiesError = "", abilitiesGeneration = 0;
  var panelToggle = null;
  var owner = "office", pinned = null, contextGeneration = 0;
  var usageKey = "", usageSnapshot = null, usageError = "", usageRequest = null;
  var presentationChat = "", viewState = {};
  var returnFocus = null, returnFocusKey = "";


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
    var status = { selected: "Подключён", pending: "Подключается", unavailable: "Недоступен", available: "Доступен" }[item.Status] || item.Status || "";
    var details = disclosure(parent, kind + ":" + item.Id, item.Name || item.Id,
      item.RequiresConfirmation ? "С разрешения" : kind === "skill" ? status : "");
    if (item.Status === "unavailable") details.classList.add("is-unavailable");
    var summary = details.querySelector("summary");
    summary.insertBefore(icon(kind === "skill" ? "skill" : "tool"), summary.firstChild);
    var description = String(item.Description || ""), reason = String(item.Reason || "");
    if (description || reason) summary.appendChild(node("span", "chat-inspector-description-preview", description || reason));
    var content = node("div", "chat-inspector-disclosure-body");
    if (description) paragraph(content, description);
    if (reason && reason !== description) paragraph(content, reason);
    content.appendChild(node("code", "", item.Id || ""));
    details.appendChild(content);
  }

  function renderCapabilityList(body, details) {
    heading(body, "Навыки · " + details.Selected.length);
    if (!details.Selected.length) paragraph(body, "Подключённых навыков нет.");
    details.Selected.forEach(function (skill) { capability(body, skill, "skill"); });
    heading(body, "Инструменты · " + details.Tools.length);
    if (!details.Tools.length) paragraph(body, "Инструментов нет.");
    var rows = details.Tools.map(function (tool) { return { value: tool }; });
    var tree = window.RNAssistantLibraryGroups.tree(rows, "tools", function (row) {
      return { kind: "leaf", title: row.value.Id, value: row.value };
    }, function () { return false; });
    function append(parent, nodes) {
      nodes.forEach(function (entry) {
        if (entry.kind === "leaf") capability(parent, entry.value, "tool");
        else {
          var section = disclosure(parent, entry.key, entry.title, "");
          section.classList.add("chat-inspector-tool-group"); append(section, entry.children);
        }
      });
    }
    append(body, tree);
  }

  function field(value, key) { return value && (value[key] !== undefined ? value[key] : value[key[0].toUpperCase() + key.slice(1)]); }
  function source() {
    var current = RNAssistantDiagnostics.context(pinned);
    owner = current.owner;
    return Object.assign({}, current, { key: RNAssistantDiagnostics.key(current) + (pinned && pinned.runId ? ':' + pinned.runId + ':' + (pinned.stepId || '') : ':live') + (current.available ? '' : ':unavailable') });
  }
  function target() {
    var current = source();
    return { owner: current.owner, chatId: current.chatId, sessionId: current.sessionId, workspaceId: current.workspaceId,
      documentId: current.documentId, label: current.label, document: current.document };
  }
  function toggleNode() { return document.getElementById("toggleChatInspectorButton"); }
  function mount() { if (visible) render(); }
  function draftIds(current) { return (current.draft.attachments || []).map(attachmentId).filter(Boolean); }

  function renderWorkspaceContext(body, current) {
    if (!current.sessionId) { paragraph(body, "Выберите чат, чтобы увидеть его контекст."); return; }
    heading(body, current.owner === "plugin" ? "Исходники плагина" : "Папка проекта");
    info(body, field(current.project, "name") || "Проект", field(current.project, "rootPath"), "folder");
    paragraph(body, "Файлы из этой папки агент читает по необходимости.");
    if (field(current.project, "available") === false) paragraph(body, "Папка сейчас недоступна.");
    var office = field(current.snapshot, "office");
    if (office) {
      heading(body, "Документ запуска");
      info(body, field(office, "documentTitle") || "Документ Office", field(office, "host"), "file");
    }
    heading(body, "Чат");
    info(body, field(current.snapshot, "title") || "Без названия", field(current.snapshot, "model"), "chat");
    if (window.RNAssistantWorkspaceChat.readOnly(current.chat)) paragraph(body, "Чат доступен только для чтения.");
  }

  function currentAbilitiesKey() {
    var current = source();
    return JSON.stringify([current.key, current.available, field(current.snapshot, 'sessionRevision'), field(current.snapshot, 'activeChatMode')]);
  }

  async function loadAbilities(key) {
    var generation = ++abilitiesGeneration;
    var current = source(), chatId = current.chatId;
    try {
      var response = await send("getAgentArsenal", { chatId: chatId, includeDetails: true });
      if (generation !== abilitiesGeneration || key !== currentAbilitiesKey() || !source().available || chatId !== source().chatId) return;
      if (!response || response.type !== "rnassistant.agentArsenal" || response.contractVersion !== 1 ||
          response.chatId !== chatId || !response.details || !Array.isArray(response.details.Selected) ||
          !Array.isArray(response.details.Tools)) throw new Error("Некорректный состав возможностей чата.");
      abilities = response;
      abilitiesError = "";
    } catch (error) {
      if (generation !== abilitiesGeneration || key !== currentAbilitiesKey() || !source().available || chatId !== source().chatId) return;
      abilities = null;
      abilitiesError = error.detail || error.message || "Не удалось загрузить возможности чата.";
    }
    if (open && visible && view === "abilities") render();
  }

  function renderContext(body) {
    if (owner !== "office") { renderWorkspaceContext(body, source()); return; }
    paragraph(body, "Выбранный контекст этого чата. Вложенные файлы включаются в следующий запрос по действующим правилам.");
    heading(body, "Документ чата");
    var current = source();
    info(body, current.document, "Связанный документ Office", "file");
    heading(body, "Добавлено вручную");
    var notes = field(current.snapshot.context, "notes") || [];
    if (!notes.length) paragraph(body, "Пока ничего не добавлено.");
    notes.forEach(function (note) { info(body, noteTitle(note), noteHost(note) + " · " + noteKind(note), "paperclip"); });
    action(body, "Управлять контекстом", "plus", function () {
      var component = source().component; if (component) component.focus();
      var button = component && component.node("openContextTabButton");
      if (button) button.click();
    });
    action(body, "Все материалы", "files", function () { switchTab("artifacts"); });
  }

  function renderAbilities(body) {
    if (owner !== "office") { renderWorkspaceAbilities(body); return; }
    if (!source().available || state.bridgeUnavailable) {
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
      renderCapabilityList(body, abilities.details);
    }
    action(body, "Открыть Библиотеку", "book", function () { switchTab("instructions"); });
  }

  function renderWorkspaceAbilities(body) {
    var current = source(), receipt = field(current.snapshot, "contextReceipt"), details = receipt && receipt.Arsenal;
    if (!current.sessionId) { paragraph(body, "Выберите чат, чтобы увидеть его возможности."); return; }
    if (!details) {
      info(body, "Состав ещё не сохранён", "Навыки и инструменты появятся после подготовки запроса модели.", "spark");
      return;
    }
    function validItems(items) {
      return Array.isArray(items) && items.every(function (item) { return item && typeof item.Id === "string" && item.Id.length > 0; });
    }
    if (typeof details.RunId !== "string" || !details.RunId || !validItems(details.Selected) || !validItems(details.Tools)) {
      paragraph(body, "Сохранённый состав возможностей недоступен.");
      return;
    }
    var runId = current.run.runId, previous = runId && details.RunId !== runId;
    var active = ["running", "awaiting_user", "awaiting_confirmation"].indexOf(current.run.lifecycle) >= 0;
    info(body, previous ? "Состав предыдущего запуска" : active ? "Текущий запуск" : "Сохранённый состав запроса",
      previous ? "Для текущего запуска состав ещё не сохранён." : "Навыки и инструменты подготовленного запроса", "spark");
    paragraph(body, "Доступность проверяется заново перед выполнением.");
    renderCapabilityList(body, details);
    action(body, "Открыть Библиотеку", "book", function () { switchTab("instructions"); });
  }

  function usageInputKey() {
    var current = source();
    return JSON.stringify([currentAbilitiesKey(), current.draft.text || '', draftIds(current), current.snapshot.contextUsage, current.snapshot.activeChatModel]);
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
    var current = source(), operation = { chatId: current.chatId, generation: contextGeneration, requestId: null };
    usageRequest = operation;
    try {
      var request = send("inspectPromptContext", { chatId: operation.chatId,
        text: current.draft.text || "",
        resourceDraftIds: draftIds(current), includeRaw: false });
      operation.requestId = request.requestId;
      var response = await request;
      if (usageRequest !== operation || operation.generation !== contextGeneration || !source().available || key !== usageInputKey()) return;
      if (!response || response.chatId !== operation.chatId || !Array.isArray(response.sections))
        throw new Error("Состав контекста недоступен.");
      usageSnapshot = response;
    } catch (error) {
      if (usageRequest === operation && key === usageInputKey()) usageError = error.detail || error.message || "Не удалось получить состав.";
    } finally {
      if (response && response.rawData) send("resourceDataClose", { chatId: operation.chatId, workspaceId: "context-inspector", leaseId: response.rawData.leaseId }).catch(function () {});
      if (usageRequest === operation) { usageRequest = null; if (open && visible && view === "usage") render(); }
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
          var tokens = typeof item.tokens === "number" ? "≈ " + formatNumber(item.tokens) + " ток." : "размер не оценён";
          var row = disclosure(parent, "context-item:" + section.id + ":" + item.id, item.title || "Элемент", included ? tokens : "не включён");
          var content = node("div", "chat-inspector-disclosure-body"); row.appendChild(content);
          if (item.subtitle) paragraph(content, item.subtitle);
          if (item.reason) paragraph(content, item.reason);
          if (item.sizeBytes) paragraph(content, "Размер ресурса: " + formatStorageBytes(item.sizeBytes));
          if (item.preview) {
            paragraph(content, "Предварительный текст с ограничением размера; полный запрос доступен в предпросмотре.");
            content.appendChild(node("pre", "chat-inspector-estimate-preview", item.preview));
          }

        });
        if (section.count > (section.items || []).length) paragraph(parent, "Показано " + (section.items || []).length + " из " + section.count + " элементов.");
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

  function renderWorkspaceUsage(body) {
    var current = source(), usage = field(current.snapshot, "contextUsage"), receipt = field(current.snapshot, "contextReceipt");
    function tokens(value) { return typeof value === "number" && isFinite(value) && value >= 0 ? value : null; }
    var prompt = tokens(field(usage, "promptTokens")), capacity = tokens(field(usage, "contextWindowTokens"));
    var runId = field(usage, "runId");
    heading(body, "Последний принятый ответ · API");
    info(body, prompt === null ? "API не сообщил число входных токенов" : formatNumber(prompt) +
      (capacity > 0 ? " / " + formatNumber(capacity) : "") + " токенов",
      !usage ? "Сохранённого ответа пока нет" : runId && runId !== current.run.runId ? "Предыдущий запуск" : "По сохранённым данным провайдера", "chart");
    if (prompt !== null && capacity > 0) {
      var chart = node("div", "chat-inspector-composition-chart"), used = Math.min(prompt, capacity);
      var percent = Math.round(used * 100 / capacity);
      chart.setAttribute("role", "img"); chart.setAttribute("aria-label", "Занято " + percent + "% контекстного окна");
      [used, capacity - used].forEach(function (amount, index) {
        if (!amount) return;
        var segment = node("span", "chat-inspector-composition-color " + (index ? "is-available" : "color-0"));
        segment.style.flexGrow = String(amount); chart.appendChild(segment);
      });
      body.appendChild(chart); paragraph(body, "Свободно " + (100 - percent) + "% · окно этого запуска");
    }
    if (usage) {
      var output = disclosure(body, "workspace-usage", "Ответ и общий расход", "Токены API");
      [["Ответ", "completionTokens"], ["Всего", "totalTokens"]].forEach(function (item) {
        var amount = tokens(field(usage, item[1])); paragraph(output, item[0] + ": " + (amount === null ? "нет данных" : formatNumber(amount)));
      });
    }
    heading(body, "Подготовленный контекст");
    var arsenal = field(receipt, "arsenal"), receiptRun = field(arsenal, "runId");
    if (!receiptRun) paragraph(body, "Состав ещё не сохранён.");
    else {
      var estimate = tokens(field(receipt, "estimatedTokens"));
      paragraph(body, (estimate === null ? "Размер не записан" : "≈ " + formatNumber(estimate) + " токенов") +
        " · локальная оценка подготовленного запроса" + (receiptRun !== current.run.runId ? " · предыдущий запуск" : ""));
      var parts = field(receipt, "messages");
      if (Array.isArray(parts) && parts.length) {
        var details = disclosure(body, "workspace-receipt", "Сведения о составе", "Записей: " + parts.length);
        paragraph(details, "Не включено: " + parts.filter(function (part) { return field(part, "presentation") === "Excluded"; }).length);
        paragraph(details, field(receipt, "compactionApplied") ? "История сжата перед подготовкой запроса." : "Сжатие истории в этом снимке не отмечено.");
      }
      action(body, "Сообщения и ресурсы запроса", "braces", function () {
        if (source().key === current.key) window.RNAssistantModelContext.open(Object.assign(target(), { runId: receiptRun, stepId: field(arsenal, "stepId") }));
      });
    }
    paragraph(body, "Оценка следующего запроса и доли токенов по источникам пока недоступны. Счётчики API и сохранённая оценка относятся к разным этапам.");
    if (runId) action(body, "Запросы запуска", "braces", function () {
      if (source().key === current.key) window.RNAssistantModelContext.open(Object.assign(target(), { runId: runId }));
    });
  }

  function renderUsage(body) {
    if (owner !== "office") { renderWorkspaceUsage(body); return; }
    heading(body, "Последний запрос · API usage");
    var current = source(), request = current.snapshot.liveRun && current.snapshot.liveRun.requestContext;
    var usage = modelUsageFromContext(request ? { lastPromptTokens: request.promptTokens, lastCompletionTokens: request.completionTokens, lastTotalTokens: request.totalTokens } : current.snapshot.contextUsage),
      capacity = Number(field(request || current.snapshot.contextUsage, "contextWindowTokens"));
    var known = usage && typeof usage.prompt === "number" && isFinite(usage.prompt) && usage.prompt >= 0;
    info(body, known ? formatNumber(usage.prompt) + (capacity > 0 ? " / " + formatNumber(capacity) : "") + " токенов" : "API ещё не сообщил число входных токенов",
      known && capacity > 0 ? "Свободно " + Math.max(0, Math.round(100 - usage.prompt * 100 / capacity)) + "% · вход по данным API" : "По данным провайдера", "chart");
    var estimate = disclosure(body, "estimate", "Оценка следующего запроса", "ещё не отправлен");
    var estimateBody = node("div", "chat-inspector-disclosure-body"); estimate.appendChild(estimateBody);
    function showEstimate() {
      if (!estimate.open || estimateBody.childElementCount) return;
      var key = usageInputKey();
      if (!source().available || state.bridgeUnavailable || source().readOnly) {
        estimateBody.textContent = source().readOnly ? "Архивный чат. Доступны сохранённые запросы." : "Выберите доступный чат."; return;
      }
      if (key !== usageKey) loadUsage(key);
      estimateBody.replaceChildren();
      if (usageError) paragraph(estimateBody, usageError);
      else if (!usageSnapshot) paragraph(estimateBody, "Собираю состав следующего запроса…");
      else {
        var budget = usageSnapshot;
        function amount(value) { return typeof value === "number" && isFinite(value) ? formatNumber(value) : "неизвестно"; }
        paragraph(estimateBody, (budget.overBudget ? "Превышен бюджет входа. " : "") +
          "Вход ≈ " + amount(budget.admissionTokens) + " / " + amount(budget.inputLimitTokens) + " токенов; осталось ≈ " + amount(budget.remainingInputTokens) + ".");
        paragraph(estimateBody, "Окно: " + amount(budget.contextWindowTokens) + "; резерв ответа: " + amount(budget.reservedOutputTokens) + "; запас: " + amount(budget.safetyTokens) + ". Это локальная оценка, не usage API.");
        renderComposition(estimateBody, budget);
      }
      action(estimateBody, "Обновить оценку", "refresh", function () { loadUsage(usageInputKey()); render(); });
    }
    estimate.addEventListener("toggle", showEstimate);
    // Build the saved open section before the shared restoration pass restores its children.
    if (viewState.usage && viewState.usage.expanded.indexOf("estimate") >= 0) {
      estimate.open = true; showEstimate();
    }
    action(body, "Фактический запрос / ответ", "braces", function () { window.RNAssistantModelContext.open(target()); });
    action(body, "Предпросмотр следующего запроса", "braces", function () { window.RNAssistantModelContext.open(Object.assign(target(), { mode: "preview" })); });
  }

  function renderActivity(body) {
    var current = source(), run = current.run;
    if (pinned && run.runId !== pinned.runId) run = current.messages.filter(function (message) {
      return messageRunId(message) === pinned.runId;
    }).map(messageRunViewState).filter(function (snapshot) { return snapshot && snapshot.runId === pinned.runId; }).pop() || { runId: pinned.runId, lifecycle: 'unavailable' };
    var lifecycle = run.lifecycle || 'idle', labels = { idle: 'Нет выполнения', running: 'Выполняется', completed: 'Завершён', failed: 'Ошибка',
      cancelled: 'Остановлен', awaiting_user: 'Нужен ваш ответ', awaiting_confirmation: 'Нужно подтверждение' };
    info(body, labels[lifecycle] || 'Состояние недоступно в загруженной истории', run.currentAction || '', 'clock');
    if (run.executionHealth === 'unknown' || run.executionHealth === 'errors') paragraph(body, run.executionHealth === 'unknown'
      ? 'Есть неподтверждённые результаты. Проверьте фактическое состояние.' : 'В ходе работы были неудачные попытки.');
    if (!run.runId) { paragraph(body, 'Отправьте сообщение, чтобы увидеть ход работы.'); return; }
    var route = Object.assign(target(), { runId: run.runId, stepId: pinned && pinned.stepId || '' });
    action(body, 'Открыть журнал запуска', 'clock', function () { window.openRunJournal(route); });
    action(body, route.stepId ? 'Запрос выбранного шага' : 'Запросы запуска', 'braces', function () { RNAssistantModelContext.open(route); });
    if (pinned && pinned.stepId) paragraph(body, 'Шаг: ' + pinned.stepId + '. Сохранённый запрос открывается отдельно.');
    // Only bounded navigation from already loaded facts. Details stay with the existing journal.
    if (!pinned && typeof collectVisibleAgentTimelineItems === 'function') {
      var items = current.messages.map(function (message, index) { return { message: message, index: index, activity: messageActivity(message) }; })
        .filter(function (item) { return item.activity && messageRunId(item.message) === run.runId; });
      current.live.forEach(function (activity) { if (activityValue(activity, 'RunId', 'runId', '') === run.runId) items.push({ activity: activity }); });
      var steps = groupAgentRunSteps(collectVisibleAgentTimelineItems(items));
      steps.slice(-20).forEach(function (step) {
        var marker = step.marker && step.marker.activity, stepId = marker && activityStepId(marker);
        if (stepId) action(body, String(step.message || 'Шаг').slice(0, 180), 'right', function () { window.openRunJournal(Object.assign({}, route, { stepId: stepId })); });
      });
      if (steps.length > 20) paragraph(body, 'Показаны последние 20 шагов. Остальные доступны в журнале.');
    }
  }

  function render() {
    var panel = document.getElementById("chatInspector");
    var body = document.getElementById("chatInspectorBody");
    if (!panel || !body || !open || !visible) return;
    var current = source();
    if (presentationChat !== current.key) {
      presentationChat = current.key; contextGeneration++; abilitiesGeneration++; abilitiesKey = ""; abilities = null; viewState = {}; cancelUsage(); usageKey = ""; usageSnapshot = null;
    }
    document.getElementById("chatInspectorViewTitle").textContent = titles[view];
    var scope = document.getElementById('chatInspectorScope');
    scope.textContent = [current.document, current.label, pinned && ('Запуск ' + pinned.runId), pinned && pinned.stepId && ('Шаг ' + pinned.stepId)].filter(Boolean).join(' · ') || 'Чат не выбран';
    document.getElementById('chatInspectorScopeMode').textContent = pinned ? 'Закреплённый запуск · сохранённые данные' : 'Выбранный чат · текущее состояние';
    document.getElementById('chatInspectorFollow').hidden = !pinned;
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
    if (previousView && body.dataset.chatId === current.key) viewState[previousView] = {
      expanded: Array.from(body.querySelectorAll("details[open][data-inspector-key]")).map(function (item) { return item.dataset.inspectorKey; }), scroll: body.scrollTop
    };
    body.replaceChildren();
    if (!current.available && !pinned) paragraph(body, current.reason || 'Выбранный чат недоступен.');
    else if (pinned && view !== 'activity') {
      paragraph(body, 'Состав, возможности и расход выбранного запуска доступны в его сохранённом запросе.');
      action(body, 'Открыть сохранённый запрос', 'braces', function () { RNAssistantModelContext.open(Object.assign(target(), { runId: pinned.runId, stepId: pinned.stepId || '' })); });
    } else ({ context: renderContext, abilities: renderAbilities, usage: renderUsage, activity: renderActivity })[view](body);
    body.dataset.view = view;
    body.dataset.chatId = current.key;
    var saved = viewState[view];
    if (saved) {
      body.querySelectorAll("details[data-inspector-key]").forEach(function (item) { item.open = saved.expanded.indexOf(item.dataset.inspectorKey) >= 0; });
      body.scrollTop = saved.scroll;
    }
    if (focusKey && previousView === view) {
      var focusTarget = Array.from(body.querySelectorAll("[data-inspector-key]")).find(function (item) { return item.dataset.inspectorKey === focusKey; });
      if (focusTarget) focusTarget.querySelector("summary").focus({ preventScroll: true });
    }
    if (previousView && previousView !== view && !window.matchMedia("(prefers-reduced-motion: reduce)").matches) {
      body.animate([{ opacity: 0 }, { opacity: 1 }], { duration: 140, easing: "cubic-bezier(.2, .8, .2, 1)" });
    }
  }

  function sync() {
    var panel = document.getElementById("chatInspector");
    if (!panel) return;
    panel.inert = !visible;
    panel.setAttribute("aria-hidden", String(!visible));
    var meter = $("contextMeter"); if (meter) meter.setAttribute("aria-expanded", String(visible && view === "usage"));
    if (panelToggle) panelToggle.sync();
    if (visible) render();
    syncSwitcher();
  }

  function setOpen(value) {
    if (!window.RNAssistantDockWorkspace) return;
    if (value) window.RNAssistantDockWorkspace.show("inspector");
    else window.RNAssistantDockWorkspace.close("inspector");
  }
  function onDockState(present, active) {
    if (panelToggle) panelToggle.sync();
    if (open === present && visible === active) return;
    open = present; visible = active;
    if (!visible) { cancelUsage(); abilitiesGeneration++; abilitiesKey = ""; }
    sync();
  }

  function syncSwitcher() {
    document.querySelectorAll("[data-chat-inspector-view]").forEach(function (button) {
      button.setAttribute("aria-checked", String(button.dataset.chatInspectorView === view));
    });
  }

  function openActivity(options, trigger) {
    if (!options || !options.runId || !options.owner || !(options.chatId || options.sessionId) || options.owner !== 'office' && !options.workspaceId) return false;
    var exact = RNAssistantDiagnostics.context(options);
    pinned = Object.freeze(Object.assign({}, options, { chatId: options.chatId || options.sessionId, sessionId: options.sessionId || options.chatId,
      label: options.label || exact.label, document: options.document || exact.document, documentId: options.documentId || exact.documentId }));
    returnFocus = trigger || document.activeElement; returnFocusKey = source().key;
    view = 'activity'; cancelUsage();
    if (visible) render(); else setOpen(true);
    return true;
  }

  function restoreFocus() {
    // Media queries can change before the queued resize event updates navigation.
    window.RNAssistantShellLayout.refresh();
    var focusTarget = returnFocus && returnFocus.isConnected && returnFocusKey === source().key
      ? returnFocus : toggleNode();
    if (focusTarget.closest("#conversationView") && window.RNAssistantDockWorkspace) window.RNAssistantDockWorkspace.show("conversation");
    if (!focusTarget.getClientRects().length || focusTarget.closest("[inert]")) focusTarget = toggleNode();
    focusTarget.focus({ preventScroll: true });
  }

  function panelControls() { return "chatInspector"; }
  function panelOpen() { return visible; }
  function togglePanel() { window.RNAssistantDockWorkspace.toggleTools(); }

  function bind() {
    panelToggle = window.RNAssistantPanelToggle.bind(document.getElementById("toggleChatInspectorButton"), {
      controls: panelControls, isOpen: panelOpen,
      label: function (expanded) { return window.RNAssistantDockWorkspace.inspectorToggleLabel(); },
      setOpen: togglePanel
    });
    document.querySelectorAll("[data-chat-inspector-view]").forEach(function (button) {
      // Menu icons already belong to index.html and app-icons; do not append a second SVG.
      button.setAttribute("role", "menuitemradio");
    });
    document.getElementById('chatInspectorFollow').addEventListener('click', function () { pinned = null; render(); });
    var panel = document.getElementById("chatInspector");
    panel.addEventListener("keydown", function (event) {
      if (event.key !== "Escape" || !open) return;
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
      if (button.hidden) return;
      view = button.dataset.chatInspectorView;
      if (view !== "usage") cancelUsage();
      document.getElementById("chatInspectorSwitcher").open = false;
      if (!visible) setOpen(true);
      else render();
      document.querySelector("#chatInspectorSwitcher summary").focus({ preventScroll: true });
    });
    window.addEventListener("pagehide", cancelUsage);
    sync();
  }

  function libraryCapabilityStatus(id, kind) {
    var details = null; if (pinned || !source().available) return null;
    if (owner === "office" && abilities && !abilities.historical && abilitiesKey === currentAbilitiesKey()) details = abilities.details;
    else if (owner !== "office") {
      var current = source(), receipt = field(current.snapshot, "contextReceipt");
      if (receipt && receipt.Arsenal && receipt.Arsenal.RunId === current.run.runId) details = receipt.Arsenal;
    }
    var items = details && (kind === "skill" ? details.Selected : details.Tools);
    var item = items && items.find(function (entry) { return entry.Id === id; });
    return item ? item.Status === "unavailable" ? "Недоступен" : "Подключён" : null;
  }
  window.RNAssistantChatInspector = { libraryCapabilityStatus: libraryCapabilityStatus, bind: bind, render: render, refreshLayout: sync, setOpen: setOpen, openActivity: openActivity,
    mount: mount, target: target, isOpen: function () { return visible; }, onDockState: onDockState, panelControls: panelControls, panelOpen: panelOpen, togglePanel: togglePanel,
    syncToggle: function () { if (panelToggle) panelToggle.sync(); },
    syncSwitcher: syncSwitcher,
    openUsage: function (target, scope, trigger) {
      if (scope && !scope.isCurrent()) return false;
      pinned = target ? Object.freeze({ owner: target.source === "office" ? "office" : "project",
        workspaceId: target.workspaceId, chatId: target.sessionId, sessionId: target.sessionId }) : null;
      returnFocus = trigger || document.activeElement; returnFocusKey = source().key;
      view = "usage"; if (visible) sync(); else setOpen(true);
      document.querySelector("#chatInspectorSwitcher summary").focus(); return true;
    } };
}());
