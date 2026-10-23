(function () {
  "use strict";
  var selectedSessions = new Map();
  var activeOwner = "office", sharedComposer = null;
  function mount(owner, container) {
    var view = document.getElementById("workspaceChatView");
    var list = document.getElementById("messages");
    var form = document.getElementById("chatForm"), input = document.getElementById("chatInput");
    if (!view || !list || !form || !input || (owner !== "office" && owner !== "project" && owner !== "plugin")) return false;
    if (owner !== "office" && !container) return false;
    var changed = activeOwner !== owner;
    if (activeOwner === "office" && owner !== "office") {
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
        document.getElementById("chatDropZone").insertBefore(form, document.getElementById("modelContextDialog"));
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
  function button(label, className, icon, action) {
    var node = document.createElement("button"); node.type = "button"; node.className = className;
    node.title = label; node.setAttribute("aria-label", label);
    node.innerHTML = window.RNAssistantIcons.render(icon); node.onclick = action; return node;
  }
  function renderMessages(list, chat) {
    var key = JSON.stringify([field(field(chat, "project"), "workspaceId") || "", chat.sessionId || chat.pluginId || ""]), changed = list.dataset.session !== key;
    var following = changed || list.scrollHeight - list.scrollTop - list.clientHeight < 64;
    var signature = JSON.stringify(chat.messages || []);
    if (!changed && list._chatSignature === signature) return;
    var expanded = new Set(Array.from(list.querySelectorAll("details[open][data-message-key]")).map(function (item) { return item.dataset.messageKey; }));
    var scroll = list.scrollTop;
    list.replaceChildren(); list.dataset.session = key; list._chatSignature = signature;
    if (!(chat.messages || []).length) list.appendChild(renderChatEmptyState({ title: "С чего начнём?",
      text: "Опишите изменение плагина или задайте вопрос об исходниках." }));
    (chat.messages || []).forEach(function (message, index) {
      var article = renderMessageArticle({ Role: message.role, Content: message.text || "" }, index, { workspace: true });
      if (message.actions && message.actions.length) {
        var details = document.createElement("details"); details.className = "agent-run-history agent-run-overview";
        details.dataset.messageKey = message.id || String(index); details.open = !changed && expanded.has(details.dataset.messageKey);
        var summary = document.createElement("summary"); summary.className = "agent-run-history-summary";
        var title = document.createElement("span"); title.className = "agent-run-history-title"; title.textContent = "Ход работы";
        var count = document.createElement("span"); count.className = "agent-work-count"; count.textContent = agentActionCountText(message.actions.length);
        var caret = document.createElement("span"); caret.className = "agent-run-history-caret";
        summary.append(title, count, caret); details.appendChild(summary);
        message.actions.forEach(function (action) {
          var row = document.createElement("div"); row.className = "agent-activity-tool-id"; row.textContent = action; details.appendChild(row);
        });
        article.insertBefore(details, article.querySelector(".message-footer"));
      }
      list.appendChild(article);
    });
    list.scrollTop = following ? list.scrollHeight : scroll;
  }
  function renderQuestions(form, input, submit) {
    var set = field(input, "questionSet");
    if (!set) { form.hidden = true; form.replaceChildren(); delete form.dataset.questionSet; delete form._questionFields; delete form._questionSend; return; }
    var key = field(input, "runId") + ":" + set.questionSetId;
    if (form.dataset.questionSet !== key) {
      form.dataset.questionSet = key; form.hidden = false; form.classList.add("plan-question-cards"); form.replaceChildren();
      form._questionFields = set.questions.map(function (question) {
        var group = document.createElement("fieldset"), legend = document.createElement("legend"); legend.textContent = question.prompt; group.appendChild(legend);
        var choices = (question.options || []).map(function (option) {
          var label = document.createElement("label"), choice = document.createElement("input");
          choice.type = question.selection === "multiple" ? "checkbox" : "radio"; choice.name = question.id; choice.value = option.id;
          var copy = document.createElement("span"); copy.textContent = option.label;
          label.append(choice, copy); group.appendChild(label); return choice;
        });
        var free = document.createElement("textarea"); free.rows = 2; free.placeholder = "Ваш ответ"; free.hidden = !question.allowFreeText;
        group.appendChild(free); form.appendChild(group); return { question: question, choices: choices, free: free };
      });
      form._questionSend = document.createElement("button"); form._questionSend.type = "submit";
      form._questionSend.className = "agent-action-button primary"; form._questionSend.textContent = "Ответить";
      form.appendChild(form._questionSend);
    }
    var fields = form._questionFields, send = form._questionSend;
    form.onsubmit = async function (event) {
      event.preventDefault(); if (send.disabled) return;
      var answers = fields.map(function (item) { return { questionId: item.question.id,
        optionIds: item.choices.filter(function (choice) { return choice.checked; }).map(function (choice) { return choice.value; }), freeText: item.free.value }; });
      if (answers.some(function (answer) { return !answer.optionIds.length && !answer.freeText.trim(); })) return;
      send.disabled = true;
      try { await submit({ runId: field(input, "runId"), questionSetId: set.questionSetId, answers: answers }); }
      finally { send.disabled = false; }
    };
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
    clear.addEventListener("click", function () {
      if (activeOwner === "office") return;
      input.value = ""; input.dispatchEvent(new Event("input", { bubbles: true })); input.focus();
    });
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
    input.addEventListener("keydown", function (event) {
      if (activeOwner === "office") return;
      if (event.key === "Enter" && !event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey && !event.isComposing) {
        event.preventDefault(); form.requestSubmit();
      }
    });
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
      accepted: function (target, text, version) {
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
  window.RNAssistantWorkspaceChat = { renderMessages: renderMessages, renderQuestions: renderQuestions, bindComposer: bindComposer,
    mount: mount, owner: function () { return activeOwner; }, messages: function () { return document.getElementById("messages"); },
    rememberSession: rememberSession, selectedSession: function (workspaceId) { return selectedSessions.get(workspaceId); },
    watchTitle: watchTitle };
}());
