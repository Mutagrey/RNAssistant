(function () {
  var promptLibrary = null, promptSources = {}, promptReading = null, promptPending = 0, promptWriting = null;
  var maximumSourceBytes = 400000, maximumMutationBytes = 8 * 1024 * 1024;
  var promptDefinitions = [
    { key: "systemPrompt", label: "Агент · общие", group: "Основные", field: "SystemPrompt", description: "Роль, runtime context, формат ответа и условие завершения Agent-потока." },
    { key: "agentToolsPrompt", label: "Агент · tools", group: "Основные", field: "AgentToolsPrompt", description: "Общие правила выбора и выполнения tools; конкретные аргументы остаются в схемах tools." },
    { key: "agentSkillsPrompt", label: "Агент · skills", group: "Основные", field: "AgentSkillsPrompt", description: "Правила выбора, обязательного чтения и повторной загрузки skills." },
    { key: "chatSystemPrompt", label: "Чат", group: "Основные", field: "ChatSystemPrompt", description: "Прямой ответ и доступ только к read-only resource tools." },
    { key: "planSystemPrompt", label: "Plan mode", group: "Основные", field: "PlanSystemPrompt", description: "Read-only discovery, typed questions и Markdown-план без выполнения." },
    { key: "contextCompactionPrompt", label: "Сжатие контекста", group: "Служебные", field: "ContextCompactionPrompt", description: "Правила создания checkpoint активной истории." },
    { key: "chatTitlePrompt", label: "Название чата", group: "Служебные", field: "ChatTitlePrompt", description: "Короткий запрос для генерации названия." },
    { key: "attachmentAnalysisPrompt", label: "Анализ вложений", group: "Служебные", field: "AttachmentAnalysisPrompt", description: "Инструкции вспомогательной модели для изображений и аудио." }
  ];

  function sameResource(left, right) {
    return !!left && !!right && left.uri === right.uri && left.revision === right.revision;
  }

  function promptMetadata(key) {
    return promptLibrary && promptLibrary.items.filter(function (item) { return item.key === key; })[0];
  }

  function validatePromptLibrary(library) {
    if (!library || library.type !== "rnassistant.promptLibrary" || library.contractVersion !== 1 ||
        !library.publication || library.publication.uri !== "rna://catalog/prompts" ||
        typeof library.publication.revision !== "string" || !library.publication.revision ||
        !Array.isArray(library.items) || library.items.length !== promptDefinitions.length ||
        promptDefinitions.some(function (def) {
          var matches = library.items.filter(function (item) { return item && item.key === def.key; });
          return matches.length !== 1 || !sameResource(matches[0].resource,
            { uri: "rna://catalog/prompts/" + def.key, revision: library.publication.revision }) ||
            Object.keys(matches[0]).some(function (key) { return key !== "key" && key !== "resource"; });
        })) throw new Error("Некорректный metadata-only contract библиотеки промптов.");
    return library;
  }

  function promptDirty(key) {
    return !!promptSources[key] && state.promptDrafts[key] !== promptSources[key].baseline;
  }

  function trimPromptSources(key) {
    Object.keys(promptSources).forEach(function (item) {
      if (item !== key && !promptDirty(item)) { delete promptSources[item]; delete state.promptDrafts[item]; }
    });
  }

  function closePromptDownload(operation) {
    if (!operation || operation.closed || !operation.data || !/^[a-f0-9]{64}$/.test(operation.data.leaseId)) return Promise.resolve();
    operation.closed = true;
    return send("resourceDataClose", { chatId: operation.chatId, workspaceId: "prompt-editor", leaseId: operation.data.leaseId }).catch(function () {});
  }

  function cancelPromptSourceRead() {
    var operation = promptReading;
    if (!operation) return;
    promptReading = null; operation.abort.abort();
    if (operation.requestId) cancelBridgeRequest(operation.requestId).catch(function () {});
    closePromptDownload(operation);
  }

  async function ensurePromptSource(def) {
    if (!def || promptSources[def.key] || state.bridgeUnavailable || !state.activeChatId) return;
    var metadata = promptMetadata(def.key);
    if (!metadata) return;
    if (promptReading && promptReading.key === def.key && promptReading.chatId === state.activeChatId &&
        sameResource(promptReading.resource, metadata.resource)) return promptReading.promise;
    cancelPromptSourceRead();
    if (promptPending >= 2) { $("promptMeta").textContent = "Предыдущее чтение ещё закрывается. Откройте промпт повторно."; return; }
    var operation = { key: def.key, chatId: state.activeChatId, resource: metadata.resource, abort: new AbortController() };
    function current() {
      return promptReading === operation && !operation.abort.signal.aborted && !state.bridgeUnavailable &&
        state.activeChatId === operation.chatId && state.selectedInstructionKind === "prompt" && selectedPromptDefinition() === def &&
        promptMetadata(def.key) && sameResource(promptMetadata(def.key).resource, operation.resource);
    }
    function active() { if (!current()) throw new Error("RESOURCE_DOWNLOAD_CANCELLED"); }
    promptReading = operation; promptPending++;
    $("promptMeta").textContent = "Загружаю опубликованный промпт…";
    operation.promise = (async function () {
      try {
        var request = send("readPromptSource", { chatId: operation.chatId, resource: operation.resource });
        operation.requestId = request.requestId;
        var response = await request; operation.requestId = null; operation.data = response && response.data;
        active();
        if (!response || response.type !== "rnassistant.promptSource" || response.contractVersion !== 1 ||
            response.chatId !== operation.chatId || !sameResource(response.resource, operation.resource) ||
            !Number.isInteger(response.totalCharacters) || response.totalCharacters < 0 || response.totalCharacters > 100000 ||
            Object.prototype.hasOwnProperty.call(response, "text") || !response.data || !response.data.payload ||
            response.data.payload.contentType !== "text/markdown; charset=utf-8") throw new Error("Некорректный contract текста промпта.");
        var bytes = await window.RNAssistantResourceDownload.read(response.data,
          { maxBytes: maximumSourceBytes, fetch: window.fetch.bind(window), signal: operation.abort.signal, isCurrent: current });
        var text = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(bytes);
        if (text.length !== response.totalCharacters) throw new Error("Неполный текст промпта.");
        await closePromptDownload(operation); active();
        promptSources[def.key] = { resource: response.resource, baseline: text, chatId: operation.chatId };
        state.promptDrafts[def.key] = text;
        promptReading = null;
        renderPromptEditor();
      } catch (error) {
        if (current()) { $("promptMeta").textContent = error.detail || error.message; log(error.message, "error"); }
      } finally {
        await closePromptDownload(operation); promptPending--;
        if (promptReading === operation) promptReading = null;
      }
    })();
    return operation.promise;
  }

  function selectedPromptDefinition() {
    return promptDefinitions[state.selectedPromptIndex] || null;
  }

  function promptText(def) {
    return def ? (state.promptDrafts[def.key] || "") : "";
  }

  function updatePromptSaveButton() {
    var button = $("savePromptButton");
    if (!button) return;
    var dirty = Object.keys(promptSources).some(promptDirty);
    button.hidden = !dirty;
    button.disabled = !dirty || !!state.bridgeUnavailable || !!promptWriting;
  }

  function promptEditorValue() {
    return typeof getCodeEditorValue === "function"
      ? getCodeEditorValue("promptEditInput")
      : ($("promptEditInput") ? $("promptEditInput").value : "");
  }

  function setPromptEditorValue(value) {
    if (typeof setCodeEditorValue === "function") {
      setCodeEditorValue("promptEditInput", value || "");
    } else if ($("promptEditInput")) {
      $("promptEditInput").value = value || "";
    }
  }

  function promptMatchesSearch(def, query) {
    var text = [
      def.label,
      def.group,
      def.field,
      def.description,
      state.promptDrafts[def.key] || ""
    ].join(" ").toLowerCase();
    return text.indexOf(query) >= 0;
  }

  function syncSelectedInstruction() {
    if (state.selectedInstructionKind === "skill") {
      if (typeof syncSelectedSkillFromEditor === "function") syncSelectedSkillFromEditor();
    } else if (state.selectedInstructionKind === "tool") {
      if (typeof syncSelectedToolFromEditor === "function") syncSelectedToolFromEditor();
    } else {
      syncSelectedPromptFromEditor();
    }
  }

  function instructionRows() {
    var query = (($("skillSearchInput") && $("skillSearchInput").value) || "").trim().toLowerCase();
    var rows = [];
    promptDefinitions.forEach(function (def, index) {
      if (!query || promptMatchesSearch(def, query)) rows.push({ kind: "prompt", index: index, value: def });
    });
    (state.skills || []).forEach(function (skill, index) {
      if (!query || (typeof skillMatchesSearch === "function" && skillMatchesSearch(skill, query))) rows.push({ kind: "skill", index: index, value: skill });
    });
    (state.tools || []).forEach(function (tool, index) {
      if (!query || (typeof toolMatchesSearch === "function" && toolMatchesSearch(tool, query))) rows.push({ kind: "tool", index: index, value: tool });
    });
    return rows;
  }

  function instructionRowKey(row) {
    return row.kind + ":" + row.index;
  }

  function selectedInstructionKey() {
    var index = state.selectedInstructionKind === "skill"
      ? state.selectedSkillIndex
      : (state.selectedInstructionKind === "tool" ? state.selectedToolIndex : state.selectedPromptIndex);
    return state.selectedInstructionKind + ":" + index;
  }

  function selectInstructionRow(row) {
    closeLibraryEditorMenus();
    state.selectedInstructionKind = row.kind;
    if (row.kind === "skill") state.selectedSkillIndex = row.index;
    else if (row.kind === "tool") {
      state.selectedToolIndex = row.index;
      state.selectedToolComponentIndex = 0;
    } else state.selectedPromptIndex = row.index;
  }

  function hostName(row) {
    return String((row.value && row.value.Host) || "Common");
  }

  function orderedHosts(rows) {
    var preferred = ["Common", "Excel", "Word", "PowerPoint", "Outlook"];
    var found = {};
    rows.forEach(function (row) { found[hostName(row)] = true; });
    return Object.keys(found).sort(function (left, right) {
      var leftIndex = preferred.indexOf(left);
      var rightIndex = preferred.indexOf(right);
      if (leftIndex < 0) leftIndex = preferred.length;
      if (rightIndex < 0) rightIndex = preferred.length;
      return leftIndex === rightIndex ? left.localeCompare(right) : leftIndex - rightIndex;
    });
  }

  function resourceGroup(key, title, count, nested) {
    var group = createResourceGroup({ key: key, title: title, count: count });
    if (nested) group.className += " resource-tree-subgroup";
    return group;
  }

  function appendInstructionItem(parent, row, activeKey) {
    var value = row.value || {};
    var meta = "Промпт";
    if (row.kind === "skill") meta = value.BuiltIn ? "Встроенный" : "Пользовательский";
    if (row.kind === "tool") meta = value.BuiltIn ? "Встроенный" : (value.Executor || "vba");
    parent.appendChild(createResourceListItem({
      title: row.kind === "prompt" ? value.label : (value.Id || value.Name || (row.kind === "tool" ? "Инструмент" : "Навык")),
      meta: meta,
      description: row.kind === "prompt" ? value.description : (value.Description || (row.kind === "tool" ? "Office-инструмент" : "Инструкция навыка")),
      enabled: row.kind === "prompt" ? null : value.Enabled !== false,
      active: instructionRowKey(row) === activeKey,
      compact: true,
      onClick: function () {
        syncSelectedInstruction();
        selectInstructionRow(row);
        renderInstructions();
      }
    }));
  }

  function appendPromptGroups(parent, rows, activeKey) {
    var prompts = resourceGroup("library:instructions:prompts", "Промпты", rows.length, true);
    parent.appendChild(prompts);
    ["Основные", "Служебные"].forEach(function (name) {
      var grouped = rows.filter(function (row) { return row.value.group === name; });
      if (!grouped.length) return;
      var group = resourceGroup("library:prompts:" + name, name, grouped.length, true);
      prompts.treeChildren.appendChild(group);
      grouped.forEach(function (row) { appendInstructionItem(group.treeChildren, row, activeKey); });
    });
  }

  function appendHostedGroups(parent, key, title, rows, activeKey) {
    var root = resourceGroup("library:" + key, title, rows.length, true);
    parent.appendChild(root);
    appendHostGroups(root.treeChildren, key, rows, activeKey);
  }

  function appendHostGroups(parent, key, rows, activeKey) {
    orderedHosts(rows).forEach(function (host) {
      var hosted = rows.filter(function (row) { return hostName(row) === host; });
      var group = resourceGroup("library:" + key + ":" + host, host, hosted.length, true);
      parent.appendChild(group);
      hosted.forEach(function (row) { appendInstructionItem(group.treeChildren, row, activeKey); });
    });
  }

  function renderInstructions() {
    if (typeof isPanelActive === "function" && !isPanelActive("instructions")) return;
    var list = $("instructionsList");
    if (!list) return;
    var rows = instructionRows();
    list.innerHTML = "";
    if (!rows.length) {
      list.appendChild(createResourceEmptyState("В библиотеке ничего не найдено."));
      renderInstructionEditor();
      return;
    }
    var key = selectedInstructionKey();
    if (!rows.some(function (row) { return instructionRowKey(row) === key; })) {
      selectInstructionRow(rows[0]);
      key = instructionRowKey(rows[0]);
    }
    var prompts = rows.filter(function (row) { return row.kind === "prompt"; });
    var skills = rows.filter(function (row) { return row.kind === "skill"; });
    var tools = rows.filter(function (row) { return row.kind === "tool"; });
    if (prompts.length || skills.length) {
      var instructions = resourceGroup("library:instructions", "Инструкции", prompts.length + skills.length, false);
      list.appendChild(instructions);
      if (prompts.length) appendPromptGroups(instructions.treeChildren, prompts, key);
      if (skills.length) appendHostedGroups(instructions.treeChildren, "skills", "Навыки", skills, key);
    }
    if (tools.length) {
      var toolRoot = resourceGroup("library:tools", "Инструменты", tools.length, false);
      list.appendChild(toolRoot);
      appendHostGroups(toolRoot.treeChildren, "tools", tools, key);
    }
    renderInstructionEditor();
  }

  function renderInstructionEditor() {
    var promptPanel = $("promptEditorPanel");
    var skillPanel = $("skillEditorPanel");
    var empty = $("instructionEditorEmpty");
    var promptSelected = state.selectedInstructionKind === "prompt" && !!selectedPromptDefinition();
    var skillSelected = state.selectedInstructionKind === "skill" && !!state.skills[state.selectedSkillIndex];
    var toolSelected = state.selectedInstructionKind === "tool" && !!state.tools[state.selectedToolIndex];
    var instructionPanel = $("instructionEditorPanel");
    var toolPanel = $("toolEditorPanel");
    if (instructionPanel) instructionPanel.classList.toggle("hidden", toolSelected);
    if (toolPanel) toolPanel.classList.toggle("hidden", !toolSelected);
    if (empty) empty.style.display = promptSelected || skillSelected ? "none" : "grid";
    if (promptPanel) promptPanel.classList.toggle("hidden", !promptSelected);
    if (skillPanel) skillPanel.classList.toggle("hidden", !skillSelected);
    if (!skillSelected) {
      if (typeof cancelSkillSourceRead === "function") cancelSkillSourceRead();
      if (typeof trimSkillSourceCache === "function") trimSkillSourceCache(null, "");
    }
    if (!toolSelected) {
      if (typeof cancelToolSourceRead === "function") cancelToolSourceRead();
      if (typeof cancelToolDocumentationRead === "function") cancelToolDocumentationRead();
      if (typeof trimToolSourceCache === "function") trimToolSourceCache(null);
    }
    if (!promptSelected) { cancelPromptSourceRead(); trimPromptSources(null); }
    if (promptSelected) renderPromptEditor();
    if (skillSelected && typeof renderSkillEditor === "function") renderSkillEditor();
    if (toolSelected && typeof renderToolEditor === "function") renderToolEditor();
  }

  function closeLibraryEditorMenus(except) {
    Array.prototype.slice.call(document.querySelectorAll(".library-editor-menu[open]")).forEach(function (menu) {
      if (menu !== except) menu.removeAttribute("open");
    });
  }

  function bindLibraryEditorMenus() {
    Array.prototype.slice.call(document.querySelectorAll(".library-editor-menu")).forEach(function (menu) {
      menu.addEventListener("toggle", function () {
        if (menu.open) closeLibraryEditorMenus(menu);
      });
      menu.addEventListener("click", function (event) {
        if (event.target.closest(".library-editor-menu-body button")) menu.removeAttribute("open");
      });
    });
    document.addEventListener("click", function (event) {
      if (!event.target.closest(".library-editor-menu")) closeLibraryEditorMenus();
    });
    document.addEventListener("keydown", function (event) {
      if (event.key === "Escape") closeLibraryEditorMenus();
    });
  }

  function mountUnifiedLibrary() {
    var layout = $("instructionsLayout");
    var toolPanel = $("toolEditorPanel");
    if (layout && toolPanel && toolPanel.parentNode !== layout) layout.appendChild(toolPanel);
  }

  function renderPromptPreview(def) {
    var preview = $("promptPreview");
    if (!preview) {
      return;
    }

    var value = promptText(def);
    var source = def && promptSources[def.key];
    preview.innerHTML = markdown(!source ? "_Текст промпта ещё не загружен._" : value ||
      (source.baseline === null ? "_После сохранения будет использован встроенный дефолт._" : "_Промпт пуст._"));
    if (typeof enhanceMarkdown === "function") {
      enhanceMarkdown(preview);
    }
  }

  function renderPromptSettings(library) {
    promptLibrary = null;
    try { promptLibrary = validatePromptLibrary(library); }
    catch (error) { log(error.message, "error"); }
    cancelPromptSourceRead();
    Object.keys(promptSources).forEach(function (key) {
      var metadata = promptMetadata(key);
      if (!promptDirty(key) && (!metadata || !sameResource(metadata.resource, promptSources[key].resource) ||
          promptSources[key].chatId !== state.activeChatId)) {
        delete promptSources[key]; delete state.promptDrafts[key];
      }
    });
    updatePromptSaveButton();
    renderPromptList();
  }

  function renderPromptList() {
    renderInstructions();
  }

  function renderPromptEditor() {
    var def = selectedPromptDefinition();
    var disabled = !def;
    var panel = $("promptEditorPanel");
    if (panel) panel.classList.toggle("hidden", disabled);
    if (disabled) {
      return;
    }

    $("promptTitle").textContent = def.label;
    trimPromptSources(def.key);
    if (promptSources[def.key] && !promptDirty(def.key) && promptSources[def.key].chatId !== state.activeChatId) {
      delete promptSources[def.key]; delete state.promptDrafts[def.key];
    }
    var loaded = !!promptSources[def.key], metadata = promptMetadata(def.key);
    if (promptReading && (promptReading.key !== def.key || promptReading.chatId !== state.activeChatId)) cancelPromptSourceRead();
    var stale = loaded && (!metadata || !sameResource(metadata.resource, promptSources[def.key].resource));
    $("promptMeta").textContent = stale ? "Черновик устарел. Скопируйте правки и перезагрузите настройки перед сохранением." :
      def.group + " · Markdown · " + def.field + ".md";
    setPromptEditorValue(promptText(def));
    $("promptEditInput").readOnly = !loaded || !!state.bridgeUnavailable;
    if (typeof setCodeEditorReadOnly === "function") setCodeEditorReadOnly("promptEditInput", !loaded || !!state.bridgeUnavailable);
    $("copyPromptButton").disabled = !loaded;
    $("addPromptToChatButton").disabled = !loaded || !!state.bridgeUnavailable;
    $("resetCurrentPromptButton").disabled = !metadata || !!state.bridgeUnavailable;
    $("resetAllPromptsButton").disabled = !promptLibrary || !!state.bridgeUnavailable;
    renderPromptPreview(def);
    applyPromptMode();
    updatePromptSaveButton();
    if (!loaded) return ensurePromptSource(def);
  }

  function syncSelectedPromptFromEditor() {
    var def = selectedPromptDefinition();
    var input = $("promptEditInput");
    if (state.selectedInstructionKind !== "prompt" || !def || !input || !promptSources[def.key]) {
      return;
    }
    state.promptDrafts[def.key] = promptEditorValue();
  }

  function setPromptMode(mode) {
    syncSelectedPromptFromEditor();
    state.promptEditorMode = mode === "preview" ? "preview" : "edit";
    renderPromptPreview(selectedPromptDefinition());
    applyPromptMode();
  }

  function applyPromptMode() {
    var mode = state.promptEditorMode === "preview" ? "preview" : "edit";
    Array.prototype.slice.call(document.querySelectorAll(".prompt-mode-button")).forEach(function (button) {
      button.classList.toggle("active", button.getAttribute("data-prompt-mode") === mode);
    });
    $("promptPreview").classList.toggle("hidden", mode !== "preview");
    if (typeof setCodeEditorVisible === "function") {
      setCodeEditorVisible("promptEditInput", mode === "edit");
    } else {
      $("promptEditInput").classList.toggle("hidden", mode !== "edit");
    }
  }

  function selectedPromptContext() {
    syncSelectedPromptFromEditor();
    var def = selectedPromptDefinition();
    if (!def || !promptSources[def.key]) {
      return "";
    }

    return [
      "# RNAssistant prompt template",
      "key: " + def.key,
      "field: " + def.field,
      "group: " + def.group,
      "",
      "## Description",
      def.description,
      "",
      "## Current prompt",
      "```markdown",
      promptText(def),
      "```"
    ].join("\n");
  }

  async function addSelectedPromptToChat() {
    var def = selectedPromptDefinition();
    var context = selectedPromptContext();
    if (!def || !context) {
      return;
    }

    await addTextContext(
      "SuppliedData",
      "agent_prompt",
      "Prompt: " + def.label,
      "prompt:" + def.key,
      context,
      { type: "agent_prompt", key: def.key, field: def.field });
    switchTab("chat");
    setChatInputText("Улучши RNAssistant prompt template из добавленного контекста. Если правка нужна, предложи обновленный Markdown и после подтверждения используй common.prompts_save для сохранения.", true);
  }

  function cancelPromptSettingsWrite() {
    var operation = promptWriting;
    if (!operation) return;
    operation.abort.abort();
    if (operation.requestId) cancelBridgeRequest(operation.requestId).catch(function () {});
  }

  async function saveSettingsWithPromptChanges(settings, apiKey, historySecret, review) {
    if (promptWriting || !promptLibrary || !state.activeChatId || state.bridgeUnavailable)
      throw new Error("Настройки ещё не загружены или сохранение уже выполняется.");
    syncSelectedPromptFromEditor();
    var changes = Object.keys(promptSources).filter(promptDirty).map(function (key) {
      var value = state.promptDrafts[key];
      if (typeof value !== "string" || value.length > 100000) throw new Error("Промпт превышает лимит 100000 символов.");
      if (new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(new TextEncoder().encode(value)) !== value)
        throw new Error("Некорректный Unicode в промпте.");
      if (promptSources[key].resource.revision !== promptLibrary.publication.revision)
        throw new Error("Черновик промпта устарел. Скопируйте правки и перезагрузите настройки.");
      return { resource: promptSources[key].resource, value: value };
    });
    var operation = { chatId: state.activeChatId, abort: new AbortController(), publication: promptLibrary.publication };
    function current() { return promptWriting === operation && !operation.abort.signal.aborted &&
      !state.bridgeUnavailable && state.activeChatId === operation.chatId; }
    function active() { if (!current()) throw new Error("Сохранение отменено; после отправки результат мог измениться. Перезагрузите настройки перед повтором."); }
    async function closeUpload() {
      if (!operation.uploadClosed && operation.lease && /^[a-f0-9]{64}$/.test(operation.lease.leaseId)) {
        operation.uploadClosed = true;
        await send("cancelPromptMutationUpload", { chatId: operation.chatId, leaseId: operation.lease.leaseId }).catch(function () {});
      }
    }
    promptWriting = operation; updatePromptSaveButton();
    try {
      var hash = null;
      if (changes.length) {
        var bytes = new TextEncoder().encode(JSON.stringify({ type: "rnassistant.promptMutation", contractVersion: 1, changes: changes }));
        if (bytes.length > maximumMutationBytes) throw new Error("RESOURCE_BATCH_TOO_LARGE");
        hash = Array.from(new Uint8Array(await crypto.subtle.digest("SHA-256", bytes)))
          .map(function (part) { return part.toString(16).padStart(2, "0"); }).join("");
        active();
        var opening = send("beginPromptMutationUpload", { chatId: operation.chatId, byteLength: bytes.length });
        operation.requestId = opening.requestId;
        operation.lease = await opening; operation.requestId = null; active();
        await window.RNAssistantResourceUpload.write(operation.lease, new Blob([bytes]),
          { maxBytes: maximumMutationBytes, signal: operation.abort.signal, isCurrent: current });
      }
      active();
      var saving = send("saveSettings", { chatId: operation.chatId, settings: settings, apiKey: apiKey || null,
        historySecret: historySecret || null, reviewAgentPrompts: review === true, expectedPromptPublication: operation.publication,
        uploadLeaseId: operation.lease ? operation.lease.leaseId : null, sha256: hash });
      operation.requestId = saving.requestId;
      var response = await saving; operation.requestId = null; active();
      validatePromptLibrary(response && response.prompts);
      if (!response.settings || promptDefinitions.some(function (def) {
        return Object.prototype.hasOwnProperty.call(response.settings, def.field) || Object.prototype.hasOwnProperty.call(response.settings, def.key);
      })) throw new Error("Настройки должны возвращать только controls и metadata промптов.");
      await closeUpload(); active();
      syncSelectedPromptFromEditor();
      changes.forEach(function (change) {
        var key = change.resource.uri.split("/").pop();
        if (promptSources[key] && sameResource(promptSources[key].resource, change.resource) && state.promptDrafts[key] === change.value) {
          delete promptSources[key]; delete state.promptDrafts[key];
        }
      });
      return response;
    } finally {
      if (!operation.uploadClosed) await closeUpload();
      if (promptWriting === operation) promptWriting = null;
      updatePromptSaveButton();
    }
  }

  function resetPrompt(def) {
    var metadata = def && promptMetadata(def.key);
    if (!metadata) return;
    // Explicit reset intent; omitted/unloaded fields never become empty writes.
    promptSources[def.key] = { resource: metadata.resource, baseline: null, chatId: state.activeChatId };
    state.promptDrafts[def.key] = "";
  }

  function bindPromptSettingsActions() {
    mountUnifiedLibrary();
    bindLibraryEditorMenus();
    $("skillSearchInput").addEventListener("input", function () {
      syncSelectedInstruction();
      renderInstructions();
    });
    $("promptEditInput").addEventListener("input", function () {
      syncSelectedPromptFromEditor();
      settingsDirty = true;
      updateSettingsSaveButton();
      updatePromptSaveButton();
    });
    Array.prototype.slice.call(document.querySelectorAll(".prompt-mode-button")).forEach(function (button) {
      button.addEventListener("click", function () {
        setPromptMode(button.getAttribute("data-prompt-mode"));
      });
    });
    $("copyPromptButton").addEventListener("click", function () {
      copyText(promptText(selectedPromptDefinition()));
      log("Промпт скопирован.");
    });
    $("addPromptToChatButton").addEventListener("click", function () {
      addSelectedPromptToChat().catch(function (error) {
        log(error.detail || error.message, "error");
      });
    });
    $("resetCurrentPromptButton").addEventListener("click", function () {
      cancelPromptSourceRead();
      var def = selectedPromptDefinition();
      resetPrompt(def);
      setPromptEditorValue("");
      settingsDirty = true;
      renderPromptEditor();
      updateSettingsSaveButton();
      updatePromptSaveButton();
      log("Промпт будет сброшен после сохранения.");
    });
    $("resetAllPromptsButton").addEventListener("click", function () {
      cancelPromptSourceRead();
      promptDefinitions.forEach(resetPrompt);
      setPromptEditorValue("");
      settingsDirty = true;
      renderPromptList();
      updateSettingsSaveButton();
      updatePromptSaveButton();
      log("Все промпты будут сброшены после сохранения.");
    });
    $("reloadPromptSettingsButton").addEventListener("click", async function () {
      if (!window.confirm("Перезагрузить настройки и промпты? Несохранённые правки будут отброшены.")) return;
      if (promptWriting) { log("Дождитесь завершения сохранения.", "error"); return; }
      try {
        syncSelectedPromptFromEditor();
        var chat = state.activeChatId, drafts = JSON.stringify(state.promptDrafts), controls = JSON.stringify(readSettings());
        var response = await send("getSettings", {});
        validatePromptLibrary(response.prompts);
        if (!response.settings) throw new Error("Настройки не получены.");
        syncSelectedPromptFromEditor();
        if (chat !== state.activeChatId || promptWriting || state.bridgeUnavailable || drafts !== JSON.stringify(state.promptDrafts) || controls !== JSON.stringify(readSettings()))
          throw new Error("Правки или активный чат изменились во время загрузки. Перезагрузка отменена.");
        cancelPromptSourceRead(); promptSources = {}; state.promptDrafts = {};
        state.settings = response.settings; state.prompts = response.prompts;
        renderSettings();
      } catch (error) { log(error.message, "error"); }
    });
    $("savePromptButton").addEventListener("click", async function () {
      setControlBusy("savePromptButton", true);
      try { await persistSettingsFromForm(); log("Промпт сохранён."); }
      catch (error) { log(error.message, "error"); }
      finally { setControlBusy("savePromptButton", false); renderInstructions(); }
    });
  }

  function markPromptEditorDirty() {
    syncSelectedPromptFromEditor();
    settingsDirty = true;
    updateSettingsSaveButton();
    updatePromptSaveButton();
  }

  window.renderPromptSettings = renderPromptSettings;
  window.renderPromptList = renderPromptList;
  window.renderPromptEditor = renderPromptEditor;
  window.syncSelectedPromptFromEditor = syncSelectedPromptFromEditor;
  window.saveSettingsWithPromptChanges = saveSettingsWithPromptChanges;
  window.cancelPromptSourceRead = cancelPromptSourceRead;
  window.cancelPromptSettingsWrite = cancelPromptSettingsWrite;
  window.releasePromptEditorContext = function () { cancelPromptSourceRead(); cancelPromptSettingsWrite(); trimPromptSources(null); };
  if (typeof window.addEventListener === "function") window.addEventListener("pagehide", window.releasePromptEditorContext);
  window.bindPromptSettingsActions = bindPromptSettingsActions;
  window.markPromptEditorDirty = markPromptEditorDirty;
  window.renderInstructions = renderInstructions;
  window.renderInstructionEditor = renderInstructionEditor;
  window.syncSelectedLibraryItem = syncSelectedInstruction;
}());
