(function () {
  "use strict";
  var selectedSessions = new Map();
  var activeOwner = "office", sharedComposer = null, actions = Object.create(null), actionsBound = false;
  var officeComposerAnchor = null;
  var viewNodes = { Messages: "messages", Form: "chatForm", Input: "chatInput", Send: "sendButton", Stop: "stopButton",
    Notice: "workspaceChatNotice", Control: "workspaceChatControl", Pending: "workspaceChatPending",
    Approve: "workspaceChatApprove", Reject: "workspaceChatReject", Questions: "workspaceChatQuestions" };
  function node(name) { return viewNodes[name] ? document.getElementById(viewNodes[name]) : null; }
  function registerActions(owner, handlers) {
    if (owner !== "office" && owner !== "project" && owner !== "plugin") throw new Error("Unknown chat owner: " + owner);
    actions[owner] = handlers;
  }
  function dispatch(name, value) {
    var handler = actions[activeOwner] && actions[activeOwner][name];
    if (handler) return handler(value);
  }
  function bindActions() {
    if (actionsBound) return;
    actionsBound = true;
    var form = node("Form"), input = node("Input");
    form.addEventListener("submit", function (event) { event.preventDefault(); dispatch("submit"); });
    input.addEventListener("input", function () { dispatch("input"); });
    input.addEventListener("keydown", function (event) {
      if (event.key === "Escape") dispatch("escape", event);
      if (!event.defaultPrevented && event.key === "Enter" && !event.isComposing &&
          !event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey) {
        event.preventDefault(); dispatch("submit");
      }
    });
    node("Stop").addEventListener("click", function () { dispatch("stop"); });
    document.getElementById("clearInputButton").addEventListener("click", function () {
      if (activeOwner === "office") dispatch("clear");
      else { input.value = ""; input.dispatchEvent(new Event("input", { bubbles: true })); input.focus(); }
    });
    node("Approve").addEventListener("click", function () { dispatch("resolve", true); });
    node("Reject").addEventListener("click", function () { dispatch("resolve", false); });
  }
  function mount(owner, container) {
    var view = document.getElementById("workspaceChatView");
    var list = document.getElementById("messages");
    var form = document.getElementById("chatForm"), input = document.getElementById("chatInput");
    if (!view || !list || !form || !input || (owner !== "office" && owner !== "project" && owner !== "plugin")) return false;
    if (owner !== "office" && !container) return false;
    var changed = activeOwner !== owner;
    if (activeOwner === "office" && owner !== "office") {
      if (!officeComposerAnchor) {
        officeComposerAnchor = document.createComment("Office composer");
        form.parentNode.insertBefore(officeComposerAnchor, form);
      }
      suspendOfficeTranscript();
      captureChatDraft(state.activeChatId, false);
      list._chatSignature = null;
      delete list.dataset.session;
    }
    if (changed) ["composerAddPicker", "chatModelPicker", "workspaceComposerPicker"].forEach(function (id) {
      var picker = document.getElementById(id);
      if (picker) picker.open = false;
    });
    if (changed && activeOwner !== "office" && sharedComposer) sharedComposer.select(null);
    var transcriptMount = document.getElementById(owner === "office" ? "officeChatTranscriptMount" : "workspaceChatTranscriptMount");
    if (list.parentNode !== transcriptMount) {
      if (owner === "office") transcriptMount.insertBefore(list, document.getElementById("chatScrollBottomButton"));
      else transcriptMount.appendChild(list);
      changed = true;
    }
    activeOwner = owner;
    form.dataset.owner = owner;
    form.classList.toggle("project-chat-composer", owner !== "office");
    if (owner === "office") {
      if (form.parentNode !== document.getElementById("chatDropZone"))
        document.getElementById("chatDropZone").insertBefore(form, officeComposerAnchor);
      document.getElementById("stopButton").hidden = false;
      input.setAttribute("aria-label", "Сообщение в чат Office");
      if (changed) {
        var editText = hasActiveMessageEdit() ? state.editingText : null;
        restoreChatDraft(state.activeChatId);
        if (editText !== null) setChatInputText(editText || "", false, false);
      }
      renderSendControls();
      list.setAttribute("aria-label", "Сообщения чата");
      return changed;
    }
    if (view.parentNode !== container) container.appendChild(view);
    if (form.parentNode !== view) view.appendChild(form);
    form.classList.remove("is-message-editing", "is-preparing-attachments", "is-dragging");
    document.getElementById("stopButton").classList.remove("hidden");
    document.getElementById("sendButton").title = "Отправить";
    document.getElementById("sendButton").setAttribute("aria-label", "Отправить");
    input.readOnly = false;
    if (changed && sharedComposer) sharedComposer.select(null);
    var plugin = owner === "plugin";
    view.dataset.owner = owner;
    view.classList.toggle("chat-main", !plugin);
    view.classList.toggle("plugin-workspace-chat-view", plugin);
    view.querySelector(".workspace-chat-head").classList.toggle("plugin-chat-head", plugin);
    ["projectChatTitleBlock", "projectChatContextNote"].forEach(function (id) {
      document.getElementById(id).hidden = plugin;
    });
    ["pluginChatTitle", "pluginDevelopmentActions", "pluginChatSource"].forEach(function (id) {
      document.getElementById(id).hidden = !plugin;
    });
    if (plugin) document.getElementById("projectChatFileStatus").hidden = true;
    input.placeholder = plugin ? "Опишите изменение плагина" : "Спросите о проекте…";
    input.setAttribute("aria-label", plugin ? "Сообщение в чат исходников" : "Сообщение в чат проекта");
    list.setAttribute("aria-label", plugin ? "Сообщения чата исходников" : "Сообщения чата проекта");
    if (sharedComposer) sharedComposer.refresh();
    return changed;
  }
  function rememberSession(workspaceId, sessionId) {
    if (!workspaceId) return;
    if (sessionId) selectedSessions.set(workspaceId, sessionId); else selectedSessions.delete(workspaceId);
  }
  function field(value, key) { return value && (value[key] !== undefined ? value[key] : value[key[0].toUpperCase() + key.slice(1)]); }
  function readOnly(snapshot) { return !!field(field(snapshot, "state"), "readOnly"); }
  function runNotice(snapshot) {
    if (readOnly(snapshot)) return { title: "Чат доступен только для чтения", text: "Старый протокол. Создайте новый чат для продолжения.", warning: false };
    var state = field(snapshot, "state");
    return window.RNAssistantRunViewState.runNotice(
      window.RNAssistantRunViewState.normalize(field(state, "runViewState")));
  }
  function latestSnapshot(current, incoming) {
    if (!current || field(current, "sessionId") !== field(incoming, "sessionId") ||
        field(field(current, "project"), "workspaceId") !== field(field(incoming, "project"), "workspaceId")) return incoming;
    var previousRevision = field(field(current, "state"), "revision");
    var nextRevision = field(field(incoming, "state"), "revision");
    if (Number.isSafeInteger(previousRevision) && (!Number.isSafeInteger(nextRevision) || nextRevision < previousRevision))
      return current;
    var currentStart = field(current, "messageStartIndex"), incomingStart = field(incoming, "messageStartIndex");
    var currentTotal = field(current, "messageTotalCount"), incomingTotal = field(incoming, "messageTotalCount");
    if (Number.isSafeInteger(currentStart) && Number.isSafeInteger(incomingStart) &&
        currentStart < incomingStart && Number.isSafeInteger(currentTotal) &&
        Number.isSafeInteger(incomingTotal) && incomingTotal >= currentTotal &&
        Array.isArray(current.messages) && current.messages.length >= incomingStart - currentStart) {
      incoming.messages = current.messages.slice(0, incomingStart - currentStart).concat(incoming.messages || []);
      incoming.messageStartIndex = currentStart;
    }
    return incoming;
  }
  function prependMessages(current, page) {
    var start = field(current, "messageStartIndex"), total = field(current, "messageTotalCount");
    var revision = field(field(current, "state"), "revision");
    if (!current || !page || field(page, "chatId") !== field(current, "sessionId") ||
        field(page, "sessionRevision") !== revision || field(page, "totalCount") !== total ||
        field(page, "startIndex") + (page.messages || []).length !== start)
      throw new Error("История изменилась. Обновите чат и повторите загрузку.");
    current.messages = page.messages.concat(current.messages || []);
    current.messageStartIndex = page.startIndex;
    return current;
  }
  function createRunObserver(read, changed, failed) {
    var target = null, timer = null;
    async function tick(current) {
      if (target !== current) return;
      timer = null;
      try {
        var snapshot = await read(current);
        if (target === current) changed(snapshot);
      } catch (error) { if (target === current) failed(error); }
      if (target === current) timer = setTimeout(function () { tick(current); }, 1200);
    }
    return { select: function (snapshot, pending) {
      var workspaceId = field(field(snapshot, "project"), "workspaceId"), sessionId = field(snapshot, "sessionId");
      var observe = workspaceId && sessionId && (pending || field(field(snapshot, "state"), "status") === "running");
      if (observe && target && target.workspaceId === workspaceId && target.sessionId === sessionId) return;
      if (timer) clearTimeout(timer);
      timer = null; target = null;
      if (!observe) return;
      var current = target = { workspaceId: workspaceId, sessionId: sessionId };
      timer = setTimeout(function () { tick(current); }, 1200);
    } };
  }
  function button(label, className, icon, action) {
    var node = document.createElement("button"); node.type = "button"; node.className = className;
    node.title = label; node.setAttribute("aria-label", label);
    node.innerHTML = window.RNAssistantIcons.render(icon); node.onclick = action; return node;
  }
  function renderMessages(list, chat) {
    var key = JSON.stringify([field(field(chat, "project"), "workspaceId") || "", chat.sessionId || chat.pluginId || ""]), changed = list.dataset.session !== key;
    var following = changed || list.scrollHeight - list.scrollTop - list.clientHeight < 64;
    var snapshot = field(chat, "state"), live = field(snapshot, "modelProgress");
    if (field(snapshot, "status") !== "running" || field(live, "runId") !== field(snapshot, "runId")) live = null;
    var start = field(chat, "messageStartIndex") || 0;
    var signature = JSON.stringify([chat.messages || [], live, start, field(chat, "messageTotalCount")]);
    if (!changed && list._chatSignature === signature) return;
    var expanded = new Set(Array.from(list.querySelectorAll("details[open][data-message-key]")).map(function (item) { return item.dataset.messageKey; }));
    var scroll = list.scrollTop, height = list.scrollHeight;
    var oldStart = changed ? null : Number(list.dataset.startIndex);
    if (changed) { list.replaceChildren(); list._workspaceEntries = Object.create(null); }
    list.dataset.session = key; list.dataset.startIndex = String(start); list._chatSignature = signature;
    var previous = list._workspaceEntries || Object.create(null), next = Object.create(null), ordered = [];
    if (start > 0) {
      var historyKey = "history:older", saved = previous[historyKey];
      var more = saved ? saved.nodes[0] : document.createElement("button");
      if (!saved) {
        more.type = "button"; more.className = "chat-history-more";
        more.textContent = "Показать предыдущие сообщения";
        more.onclick = function () {
          more.disabled = true;
          Promise.resolve(dispatch("loadPrevious")).finally(function () { more.disabled = false; });
        };
      }
      next[historyKey] = saved || { nodes: [more] }; ordered.push(more);
    }
    var visible = (chat.messages || []).filter(function (message) {
      return !message.ProtocolMessage && (!!(message.Content || "").trim() || !!message.Activity ||
        !!message.ReflectionDecision || !!message.ReasoningContent ||
        !!(message.Results && message.Results.length));
    });
    if (!visible.length && !start && !live) {
      var empty = previous.empty || { signature: "empty", nodes: [renderChatEmptyState({ title: "С чего начнём?",
        text: "Опишите изменение плагина или задайте вопрос об исходниках." })] };
      next.empty = empty; ordered.push.apply(ordered, empty.nodes);
    }
    visible.forEach(function (message, index) {
      var id = message.Id || "fixture:" + index, messageSignature = JSON.stringify(message);
      var cached = previous[id];
      if (cached && cached.signature === messageSignature) {
        next[id] = cached; ordered.push.apply(ordered, cached.nodes); return;
      }
      var article = renderMessageArticle(message, index, { workspace: true });
      article.querySelectorAll("details[data-message-key]").forEach(function (details) {
        details.open = !changed && expanded.has(details.dataset.messageKey);
      });
      var nodes = [article];
      next[id] = { signature: messageSignature, nodes: nodes }; ordered.push(article);
    });
    if (live) {
      var liveKey = "live:" + field(live, "runId"), liveEntry = previous[liveKey];
      var node = liveEntry ? liveEntry.nodes[0] : document.createElement("section");
      node.className = "agent-model-step agent-stream-step";
      var thoughts = field(live, "reasoning") || "", oldReasoning = node.querySelector(".reasoning-block");
      if (thoughts && typeof reasoningBlock === "function") {
        if (oldReasoning) updateReasoningBlock(oldReasoning, thoughts, null, true, !!field(live, "reasoningTruncated"));
        else node.insertBefore(reasoningBlock(thoughts, null, true, !!field(live, "reasoningTruncated")), node.firstChild);
      } else if (oldReasoning) node.removeChild(oldReasoning);
      var content = field(live, "message") || "", body = node.querySelector(".agent-stream-message");
      if (content !== node._content) {
        if (body && typeof clearMarkdownEnhancements === "function") clearMarkdownEnhancements(body);
        if (content) {
          if (!body) { body = document.createElement("div"); body.className = "agent-step-message markdown agent-stream-message"; node.appendChild(body); }
          body.innerHTML = markdown(content); enhanceMarkdown(body, { enableJsonViewer: true, sourceText: content, streaming: true });
        } else if (body) node.removeChild(body);
        node._content = content;
      }
      var status = node.querySelector(".agent-stream-status");
      if (!status) { status = document.createElement("div"); status.className = "agent-stream-status"; status.setAttribute("role", "status"); node.appendChild(status); }
      status.textContent = field(live, "retrying") ? "Повторная попытка…" : "Формируется…";
      next[liveKey] = liveEntry || { nodes: [node] }; ordered.push(node);
    }
    Object.keys(previous).forEach(function (id) {
      if (next[id] === previous[id]) return;
      previous[id].nodes.forEach(function (node) {
        if (typeof clearMarkdownEnhancements === "function") clearMarkdownEnhancements(node);
        if (node.parentNode === list) list.removeChild(node);
      });
    });
    var cursor = list.firstChild;
    ordered.forEach(function (node) {
      if (node === cursor) cursor = cursor.nextSibling;
      else list.insertBefore(node, cursor);
    });
    list._workspaceEntries = next;
    list.scrollTop = !changed && Number.isSafeInteger(oldStart) && start < oldStart
      ? scroll + list.scrollHeight - height : following ? list.scrollHeight : scroll;
  }
  function renderWorkspaceControls(chat, submitQuestions) {
    if (activeOwner === "office") return;
    var snapshot = field(chat, "state") || {}, pending = field(chat, "pendingId");
    node("Stop").hidden = field(snapshot, "status") !== "running";
    node("Control").hidden = !pending;
    node("Pending").textContent = pending ? (field(chat, "pendingTool") || "Действие") + "\n" +
      (field(chat, "pendingArguments") || "") : "";
    var input = field(snapshot, "userInput"), set = field(input, "questionSet"), container = node("Questions");
    if (!set) { container.hidden = true; container.replaceChildren(); return; }
    window.RNAssistantQuestions.render(container, set, {
      scope: "workspace:" + field(field(chat, "project"), "workspaceId") + ":" + field(chat, "sessionId"),
      runId: field(input, "runId"), submit: submitQuestions
    });
  }
  function bindComposer(form, options) {
    if (sharedComposer) { if (options && options.onFiles) sharedComposer.attachFiles(options); return sharedComposer; }
    var input = form.querySelector("textarea"), toolbar = form.querySelector(".composer-toolbar"), actions = form.querySelector(".composer-actions");
    var current = null, loaded = null, mode = "agent", reasoning = false, locked = false;
    var draftStore = window.RNAssistantChatDrafts;
    function sameTarget(left, right) {
      return !!left && !!right && left.workspaceId === right.workspaceId && left.sessionId === right.sessionId;
    }
    function save() {
      if (current) loaded = draftStore.saveWorkspace(current, { text: input.value, mode: mode, reasoning: reasoning });
    }
    var note = toolbar.querySelector(".plugin-chat-context-note, .project-chat-context-note");
    function showOfficeContext(snapshot) {
      if (!note) return;
      var office = field(snapshot, "office"), status = String(field(snapshot, "status") || "").toLowerCase();
      var activeRun = status === "running" || status === "awaitingconfirmation", pinned = office && activeRun;
      note.hidden = activeOwner !== "project";
      note.textContent = pinned ? "Office: " + field(office, "host") + " · " + (field(office, "documentTitle") || "документ") :
        activeRun ? "Office: документ не привязан" : "Office: текущий документ при отправке, если открыт";
      note.title = pinned ? "Документ закреплён за этим запуском, включая подтверждение действий" :
        activeRun ? "В этом запуске Office-инструменты недоступны" : "Новый запуск будет привязан к текущему документу Office, если он открыт";
    }
    var clear = document.getElementById("clearInputButton");
    var quick = document.createElement("div"); quick.className = "composer-quick-actions workspace-composer-only";
    var picker = document.createElement("details"); picker.className = "composer-picker composer-add-picker";
    picker.id = "workspaceComposerPicker";
    var trigger = document.createElement("summary"); trigger.className = "composer-picker-trigger composer-add-trigger";
    trigger.setAttribute("aria-label", "Действия чата"); trigger.title = "Действия чата"; trigger.innerHTML = window.RNAssistantIcons.render("plus");
    var menu = document.createElement("div"); menu.className = "composer-picker-menu composer-add-menu";
    var fileAction = null, fileInput = null, fileTarget = null, fileOptions = null;
    function attachFiles(nextOptions) {
      if (!nextOptions || !nextOptions.onFiles) return;
      fileOptions = nextOptions;
      if (fileAction) { update(); return; }
      fileAction = document.getElementById("attachFileButton").cloneNode(true); fileAction.removeAttribute("id");
      fileInput = document.createElement("input"); fileInput.type = "file"; fileInput.multiple = true; fileInput.hidden = true;
      fileAction.onclick = function () { if (locked || !current) return;
        fileTarget = fileOptions.fileTarget ? fileOptions.fileTarget() : current;
        picker.open = false; fileInput.click(); };
      fileInput.onchange = function () {
        var files = Array.from(fileInput.files || []), target = fileTarget;
        fileInput.value = "";
        if (files.length && target) fileOptions.onFiles(files, target);
      };
      menu.appendChild(fileAction);
      var divider = document.createElement("hr"); menu.appendChild(divider);
      quick.appendChild(fileInput);
      if (sharedComposer) update();
    }
    var plan = document.getElementById("chatPlanButton").cloneNode(true); plan.removeAttribute("id");
    plan.id = "workspacePlanButton";
    plan.onclick = function () { if (!locked) { mode = mode === "plan" ? "agent" : "plan"; save(); update(); picker.open = false; } };
    menu.appendChild(plan); picker.append(trigger, menu); quick.appendChild(picker); toolbar.insertBefore(quick, toolbar.firstChild);
    var model = document.createElement("button"); model.type = "button"; model.className = "composer-picker-trigger workspace-chat-model";
    model.id = "workspaceModelButton";
    model.title = "Модель из общих настроек"; model.classList.add("workspace-composer-only");
    model.onclick = function () { switchTab("settings"); };
    var label = document.createElement("span"); label.className = "composer-picker-trigger-label"; model.appendChild(label);
    var think = button("Включить reasoning", "composer-reasoning-button workspace-composer-only", "bulb", function () { if (!locked) { reasoning = !reasoning; save(); update(); } });
    think.id = "workspaceReasoningToggle";
    actions.prepend(model, think);
    function update() {
      if (activeOwner === "office") return;
      clear.hidden = !input.value; resizeComposerInput(input);
      plan.classList.toggle("is-selected", mode === "plan"); plan.setAttribute("aria-pressed", String(mode === "plan")); plan.disabled = locked;
      if (fileAction) { fileAction.hidden = activeOwner !== "project"; fileAction.disabled = locked || !current || activeOwner !== "project"; }
      think.setAttribute("aria-pressed", String(reasoning)); think.title = reasoning ? "Reasoning включён" : "Reasoning выключен";
      think.setAttribute("aria-label", reasoning ? "Выключить reasoning" : "Включить reasoning"); think.disabled = locked;
      label.textContent = field(state.settings, "model") || "Модель";
    }
    input.addEventListener("input", function () { if (activeOwner !== "office") { save(); update(); } });
    form.addEventListener("keydown", function (event) { if (event.key === "Escape" && picker.open) { picker.open = false; trigger.focus(); } });
    document.addEventListener("pointerdown", function (event) { if (!picker.contains(event.target)) picker.open = false; });
    window.addEventListener("resize", update);
    attachFiles(options);
    update();
    sharedComposer = {
      attachFiles: attachFiles,
      refresh: update,
      select: function (target, snapshot) {
        showOfficeContext(snapshot);
        var next = target && target.workspaceId && target.sessionId
          ? { workspaceId: target.workspaceId, sessionId: target.sessionId } : null;
        var same = sameTarget(current, next);
        current = next;
        var saved = draftStore.readWorkspace(next);
        if (same && saved === loaded) { update(); return; }
        loaded = saved;
        input.value = saved ? saved.text : ""; mode = saved ? saved.mode : field(snapshot, "mode") || "agent";
        reasoning = saved ? saved.reasoning : !!field(field(snapshot, "modelConfiguration"), "reasoningEnabled"); update();
      },
      options: function () { return { Mode: mode, ReasoningEnabled: reasoning }; },
      prepareInput: function (target, text) { return draftStore.prepareWorkspaceInput(target, text, this.options()); },
      draftVersion: function () {
        if (!current) return null;
        var saved = draftStore.readWorkspace(current);
        if (saved !== loaded || !saved || saved.text !== input.value || saved.mode !== mode || saved.reasoning !== reasoning) save();
        return loaded.version;
      },
      lock: function (value) { locked = value; update(); },
      append: function (target, line) {
        if (sameTarget(current, target)) {
          var latest = draftStore.readWorkspace(current);
          if (latest !== loaded) this.select(target);
          input.value = input.value.trim() ? input.value.replace(/\s*$/, "") + "\n" + line : line;
          input.dispatchEvent(new Event("input", { bubbles: true }));
          return;
        }
        var saved = draftStore.readWorkspace(target) || { text: "", mode: "agent", reasoning: false };
        draftStore.saveWorkspace(target, { text: saved.text.trim() ? saved.text.replace(/\s*$/, "") + "\n" + line : line,
          mode: saved.mode, reasoning: saved.reasoning });
      },
      accepted: function (target, text, version, request) {
        draftStore.acknowledgeWorkspaceInput(target, request);
        var previous = draftStore.readWorkspace(target);
        var cleared = draftStore.acceptWorkspace(target, text, version);
        if (cleared && sameTarget(current, target) && loaded === previous) {
          input.value = ""; loaded = cleared; update();
        }
      }
    };
    return sharedComposer;
  }
  function watchTitle(snapshot, read, complete) {
    if (!field(field(snapshot, "state"), "titleGenerationPending")) return function () {};
    var stopped = false, timer = null, attempts = 0;
    function stop() { stopped = true; if (timer) clearTimeout(timer); }
    async function tick() {
      if (stopped) return;
      try {
        var latest = await read();
        if (stopped) return;
        if (!field(field(latest, "state"), "titleGenerationPending")) { stop(); complete(latest); return; }
      } catch (_) { /* The chat can be reopened after a transient read failure. */ }
      if (++attempts >= 65) { stop(); return; }
      timer = setTimeout(tick, 2000);
    }
    timer = setTimeout(tick, 2000);
    return stop;
  }
  window.RNAssistantWorkspaceChat = { renderMessages: renderMessages, renderWorkspaceControls: renderWorkspaceControls, bindComposer: bindComposer,
    mount: mount, owner: function () { return activeOwner; }, node: node, messages: function () { return node("Messages"); },
    registerActions: registerActions, bindActions: bindActions,
    rememberSession: rememberSession, selectedSession: function (workspaceId) { return selectedSessions.get(workspaceId); },
    watchTitle: watchTitle, latestSnapshot: latestSnapshot, prependMessages: prependMessages,
    createRunObserver: createRunObserver, readOnly: readOnly, runNotice: runNotice };
}());
