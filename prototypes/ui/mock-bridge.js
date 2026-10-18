(function () {
  "use strict";

  var scenario = new URLSearchParams(location.search).get("scenario") || "ready";
  var contract = window.__RNAMockBridgeContract;
  var token = "local-preview-session";
  var listeners = [];
  var downloads = Object.create(null);
  var uploads = Object.create(null);
  var leaseSequence = 0;
  var revision = 1, skillRevision = 1;
  var chatSequence = 1;
  var activeChatId = scenario === "empty" ? "" : "preview-chat";
  var messages = scenario === "empty" ? [] : [
    { id: "preview-user", role: "user", content: "Покажи динамику продаж по месяцам.", createdUtc: "2026-10-18T09:00:00Z" },
    { id: "preview-answer", role: "assistant", content: "В демонстрационном документе продажи растут с июля по сентябрь. Проверьте данные перед выводом.", createdUtc: "2026-10-18T09:00:05Z" }
  ];
  var chats = activeChatId ? [{ Id: activeChatId, Title: "Анализ продаж", Host: "Excel",
    DocumentKey: "preview-document", DocumentTitle: "Продажи · сентябрь.xlsx", IsCurrentDocument: true,
    LastActivityUtc: "2026-10-18T09:00:05Z", MessageCount: messages.length, Revision: revision }] : [];
  var documentItems = scenario === "empty" ? [] : [{ documentKey: "preview-document",
    title: "Продажи · сентябрь.xlsx", host: "Excel", isActive: true }];
  var initialSkillText = "# Анализ продаж\n\nСравнивай период с предыдущим.\n\n1. Проверь колонки и период.\n2. Раздели факты и предположения.\n3. Покажи исключения и пропуски.\n";
  var promptText = "# Агент\n\nОтвечай по данным текущего документа и явно указывай ограничения результата.\n";
  var skills = [], skillTexts = Object.create(null), skillReferences = Object.create(null);
  var messagesByChat = activeChatId ? { [activeChatId]: messages } : {};
  var inboxRevision = 1, inputSequence = 0, inputReceipts = Object.create(null);
  var tool = null;
  var toolSourceText = "";
  var settings = {};
  var workspaceRevision = 2;
  var sourcePaths = ["Москва.xlsx", "Казань.xlsx"];
  var workspaceFiles = [
    { name: "Москва.xlsx", path: "Москва.xlsx", isDirectory: false, byteLength: 4194304 },
    { name: "Казань.xlsx", path: "Казань.xlsx", isDirectory: false, byteLength: 3670016 },
    { name: "Самара.xlsx", path: "Самара.xlsx", isDirectory: false, byteLength: 3145728 }
  ];
  var templateHead = "template-sales-1730000000000-" + "a".repeat(32) + ".json";
  var dictionaryText = JSON.stringify({ version: 1, revision: 1,
    parameters: [{ id: "revenue", name: "Выручка", unitId: "rub" }, { id: "orders", name: "Заказы", unitId: "pcs" }],
    units: [{ id: "rub", name: "Рубль" }, { id: "pcs", name: "Штука" }],
    parameterMappings: [{ scope: "sales", source: "Выручка", targetId: "revenue" }], unitMappings: [] }, null, 2);
  var templateText = JSON.stringify({ kind: "data-templates-project-template", template: { id: "sales" } }, null, 2);
  var artifactUri = "rna://chat/preview-chat/artifact/sales-report/revision/1";
  var artifactText = "# Итоги сентября\n\nМосква: 7 420 000 ₽. Казань: 5 380 000 ₽.\n\nДанные демонстрационные.\n";
  var nativeFetch = window.fetch.bind(window);

  function clone(value) { return JSON.parse(JSON.stringify(value)); }
  function bytes(value) { return new TextEncoder().encode(value); }
  async function hash(value) {
    var data = value instanceof Uint8Array ? value : bytes(value);
    var digest = new Uint8Array(await crypto.subtle.digest("SHA-256", data));
    return Array.from(digest, function (part) { return part.toString(16).padStart(2, "0"); }).join("");
  }
  function leaseId() { return (++leaseSequence).toString(16).padStart(64, "0"); }
  function skillLibrary() {
    return { type: "rnassistant.skillLibrary", contractVersion: 1, skills: clone(skills) };
  }
  function toolLibrary() { return { type: "rnassistant.toolLibrary", contractVersion: 1, tools: tool ? [clone(tool)] : [] }; }
  function promptLibrary() {
    var publication = { uri: "rna://catalog/prompts", revision: "preview-prompts-1" };
    return { type: "rnassistant.promptLibrary", contractVersion: 1, publication: publication,
      items: ["systemPrompt", "agentToolsPrompt", "agentSkillsPrompt", "chatSystemPrompt", "planSystemPrompt",
        "contextCompactionPrompt", "chatTitlePrompt", "attachmentAnalysisPrompt"].map(function (key) {
        return { key: key, resource: { uri: "rna://catalog/prompts/" + key, revision: publication.revision } };
      }) };
  }
  function workspaceState() {
    return { type: "rnassistant.workspaceState", contractVersion: 1, revision: workspaceRevision,
      sourceQueueId: null,
      workspace: scenario === "empty" ? null : { bindingId: "preview-binding", workspaceId: "preview-workspace",
        rootPath: "C:\\Projects\\Reports", readOnly: false },
      project: scenario === "empty" ? null : { name: "Управленческая отчётность", manifestPath: "project.json",
        formatVersion: 2, sources: sourcePaths.map(function (path, index) {
          return { sourceId: "src_" + (index + 1).toString(16).padStart(32, "0"), path: path };
        }) } };
  }
  function requireProject(payload) {
    if (!payload || payload.bindingId !== "preview-binding" || payload.manifestPath !== "project.json" ||
        payload.projectRevision !== workspaceRevision)
      fail("Проект примера изменился.", "preview_project_changed");
  }
  function chatState() {
    return { sessionRevision: revision, activeChatId: activeChatId, activeChatMode: "agent",
      activeChatModel: "preview-model", activeChatReasoning: false, chats: clone(chats), documents: clone(documentItems),
      messages: clone(messages), messageStartIndex: 0, messageTotalCount: messages.length,
      skills: skillLibrary(), tools: toolLibrary(), context: {}, contextUsage: {},
      artifacts: [], artifactLibrary: { sessionRevision: revision, heads: [] }, inbox: chatInbox() };
  }
  function chatInbox(receipt) {
    return { chatId: activeChatId, epoch: "preview-inbox", revision: inboxRevision,
      running: false, paused: false, items: [], receipts: [], acceptedInput: receipt || null };
  }
  function emit(response) { listeners.forEach(function (listener) { listener({ data: response }); }); }
  function fail(message, code) {
    var error = new Error(message);
    error.code = code || "preview_unsupported";
    throw error;
  }
  function requireChat(payload) {
    if (!payload || payload.chatId !== activeChatId || !activeChatId) fail("Чат примера изменился.", "preview_chat_changed");
  }
  function mutationResult(mutation, previousRevision, nextRevision) {
    return { type: "rnassistant.skillMutationResult", contractVersion: 1, status: "ok",
      message: "Сохранено в памяти примера.", dispatch: "may_have_dispatched", effect: "verified_change",
      operation: mutation.kind, id: mutation.id || mutation.baseId,
      previousRevision: previousRevision || "", revision: nextRevision || "" };
  }
  async function uploadedBody(payload) {
    requireChat(payload);
    var upload = uploads[payload.uploadLeaseId];
    if (!upload || upload.bytes.length !== upload.byteLength || await hash(upload.bytes) !== payload.sha256)
      fail("Загрузка изменений навыка неполна.", "preview_upload_invalid");
    return JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(upload.bytes));
  }
  var ready = (async function () {
    if (scenario === "empty") return;
    skills.push({ id: "excel.sales_analysis", host: "Excel", name: "excel.sales_analysis",
      description: "Сравнение периодов и анализ продаж по регионам", version: "1.0.0",
      enabled: true, builtIn: false, revision: "preview-skill-1", references: [],
      body: { sha256: await hash(initialSkillText), byteLength: bytes(initialSkillText).length, characters: initialSkillText.length } });
    skillTexts[skills[0].id] = initialSkillText;
    var code = "Option Explicit\n\nPublic Sub FormatSalesReport()\n    ThisWorkbook.Worksheets(\"Продажи\").Range(\"A1:F1\").Font.Bold = True\nEnd Sub\n";
    toolSourceText = JSON.stringify({ argumentSchemaJson: '{"type":"object","properties":{},"additionalProperties":false}',
      code: code, readme: "# Форматирование отчёта\n\nПрименяет оформление к заголовку таблицы.\n",
      components: [{ name: "FormatSales", type: "StdModule", fileName: "FormatSales.bas", code: code,
        codeSha256: await hash(code) }] });
    tool = { id: "excel.format_sales", host: "Excel", name: "excel.format_sales",
      description: "Применяет оформление к заголовку таблицы в связанном документе.",
      revision: "preview-tool-1", source: { sha256: await hash(toolSourceText), byteLength: bytes(toolSourceText).length },
      executor: "vba", requiresConfirmation: true, mutatesDocument: true, mutatesLocalState: false,
      canSourceHtmlData: false, agentCanRun: false, enabled: true, builtIn: false,
      riskLevel: 1, useWhen: "", doNotUseWhen: "", capabilityStatus: "available", limitations: "",
      packageVersion: "1.0.0", entryPoint: "FormatSales.FormatSalesReport", argumentOrder: [],
      scope: "global", installationStatus: "not_installed" };
  })();

  function download(text, contentType) {
    var id = leaseId(), data = bytes(text);
    downloads[id] = { bytes: data, contentType: contentType };
    return { leaseId: id, url: "https://rnassistant.local-resource/v1/download/" + id,
      maxChunkBytes: 65536, payload: { sha256: null, byteLength: data.length, contentType: contentType } };
  }

  async function saveSkills(payload) {
    var body = await uploadedBody(payload);
    if (body.type !== "rnassistant.skillLibraryMutationRequest" || body.contractVersion !== 1 ||
        !Array.isArray(body.mutations) || body.mutations.length > 256)
      fail("Некорректный пакет изменений навыка.", "preview_mutation_invalid");
    var stagedSkills = clone(skills), stagedTexts = Object.assign(Object.create(null), skillTexts);
    var stagedReferences = clone(skillReferences);
    var nextLibraryRevision = skillRevision, results = [];
    for (var i = 0; i < body.mutations.length; i++) {
      var mutation = body.mutations[i];
      var index = stagedSkills.findIndex(function (item) { return item.id.toLowerCase() === String(mutation.baseId || "").toLowerCase(); });
      var base = index < 0 ? null : stagedSkills[index];
      var previous = base && base.revision || "";
      if (mutation.kind === "delete") {
        if (!base || mutation.expectedRevision !== base.revision)
          fail("Навык изменился перед удалением.", "preview_revision_changed");
        stagedSkills.splice(index, 1); delete stagedTexts[base.id]; delete stagedReferences[base.id];
        results.push(mutationResult(mutation, previous, ""));
      } else if (mutation.kind === "upsert") {
        if (mutation.baseId && (!base || mutation.expectedRevision !== base.revision))
          fail("Навык изменился перед сохранением.", "preview_revision_changed");
        if (!mutation.baseId && mutation.expectedRevision) fail("Ревизия нового навыка неверна.", "preview_revision_changed");
        if (typeof mutation.id !== "string" || !mutation.id.trim() ||
            stagedSkills.some(function (item, position) { return position !== index && item.id.toLowerCase() === mutation.id.toLowerCase(); }))
          fail("Идентификатор навыка занят или пуст.", "preview_mutation_invalid");
        if (!mutation.preserveBody && typeof mutation.bodyMarkdown !== "string")
          fail("Текст навыка отсутствует.", "preview_mutation_invalid");
        if (mutation.preserveBody && !base) fail("Текст нового навыка отсутствует.", "preview_mutation_invalid");
        var nextText = mutation.preserveBody ? stagedTexts[base.id] : mutation.bodyMarkdown;
        var nextRevision = "preview-skill-" + (++nextLibraryRevision);
        var saved = { id: mutation.id, host: mutation.host, name: mutation.name, description: mutation.description,
          version: mutation.version, enabled: !!mutation.enabled, builtIn: false, revision: nextRevision,
          package: mutation.package || null, references: base ? base.references : [],
          body: { sha256: await hash(nextText), byteLength: bytes(nextText).length,
            characters: nextText.length } };
        if (base) {
          stagedSkills[index] = saved; delete stagedTexts[base.id];
          stagedReferences[saved.id] = stagedReferences[base.id] || {};
          if (saved.id !== base.id) delete stagedReferences[base.id];
        } else { stagedSkills.push(saved); stagedReferences[saved.id] = {}; }
        stagedTexts[saved.id] = nextText;
        results.push(mutationResult(mutation, previous, nextRevision));
      } else fail("Операция навыка не поддержана в примере.");
    }
    skills = stagedSkills; skillTexts = stagedTexts; skillReferences = stagedReferences; skillRevision = nextLibraryRevision;
    return { type: "rnassistant.skillLibraryMutationResult", contractVersion: 1,
      results: results, library: skillLibrary() };
  }

  async function saveSkillReference(payload) {
    var body = await uploadedBody(payload);
    if (body.type !== "rnassistant.skillReferenceRequest" || body.contractVersion !== 1 ||
        typeof body.content !== "string" || !/^references\/[^\\/:*?"<>|]+\.md$/i.test(body.path || ""))
      fail("Некорректный reference навыка.", "preview_mutation_invalid");
    var selected = skills.find(function (item) { return item.id === body.skillId; });
    if (!selected || selected.revision !== body.expectedPackageRevision)
      fail("Навык изменился перед сохранением reference.", "preview_revision_changed");
    var previous = selected.revision, existing = selected.references.find(function (item) { return item.path === body.path; });
    var reference = { path: body.path, revision: await hash(body.content), byteLength: bytes(body.content).length };
    if (existing) selected.references[selected.references.indexOf(existing)] = reference;
    else selected.references.push(reference);
    (skillReferences[selected.id] || (skillReferences[selected.id] = {}))[body.path] = body.content;
    selected.revision = "preview-skill-" + (++skillRevision);
    return { type: "rnassistant.skillReferenceResult", contractVersion: 1,
      result: mutationResult({ kind: existing ? "update_reference" : "create_reference", id: selected.id }, previous, selected.revision),
      skill: clone(selected), path: body.path, deleted: false, reference: clone(reference) };
  }

  function deleteSkillReference(payload) {
    requireChat(payload);
    if (payload.type !== "rnassistant.skillReferenceRequest" || payload.contractVersion !== 1)
      fail("Некорректный запрос удаления reference.", "preview_mutation_invalid");
    var selected = skills.find(function (item) { return item.id === payload.skillId; });
    if (!selected || selected.revision !== payload.expectedPackageRevision)
      fail("Навык изменился перед удалением reference.", "preview_revision_changed");
    var reference = selected.references.find(function (item) { return item.path === payload.path; });
    if (!reference) fail("Reference отсутствует.", "preview_revision_changed");
    var previous = selected.revision;
    selected.references.splice(selected.references.indexOf(reference), 1);
    delete skillReferences[selected.id][payload.path];
    selected.revision = "preview-skill-" + (++skillRevision);
    return { type: "rnassistant.skillReferenceResult", contractVersion: 1,
      result: mutationResult({ kind: "delete_reference", id: selected.id }, previous, selected.revision),
      skill: clone(selected), path: payload.path, deleted: true, reference: null };
  }

  async function dispatch(type, payload) {
    if (type === "init") return { bridgeToken: token, bridgeContract: contract,
      init: Object.assign({ appVersion: "preview", host: "Excel", title: "Продажи · сентябрь.xlsx",
        settings: clone(settings), prompts: promptLibrary(), hasApiKey: true, hasHistorySecret: false }, chatState()) };
    if (type === "reportClientTiming" || type === "focusState") return {};
    if (type === "listChats" || type === "getChatState") {
      if (type === "getChatState") requireChat(payload);
      return chatState();
    }
    if (type === "getChatInbox") {
      requireChat(payload);
      var inbox = chatInbox();
      inbox.receipts = (payload.receiptOperationIds || []).map(function (id) { return inputReceipts[id]; })
        .filter(function (receipt) { return receipt && receipt.chatId === activeChatId; });
      return inbox;
    }
    if (type === "submitChatInput") {
      requireChat(payload);
      if (Array.isArray(payload.resourceDraftIds) && payload.resourceDraftIds.length || payload.answers)
        fail("В примере доступна отправка только текста.");
      if (typeof payload.text !== "string" || !payload.text.trim() || !payload.operationId)
        fail("Сообщение примера пусто или не имеет ID.", "preview_input_invalid");
      var receipt = inputReceipts[payload.operationId];
      if (receipt && receipt.chatId !== activeChatId) fail("ID сообщения принадлежит другому чату.", "preview_input_invalid");
      if (!receipt) {
        var number = ++inputSequence, now = new Date().toISOString();
        receipt = { chatId: activeChatId, Id: "preview-input-" + number, OperationId: payload.operationId,
          Text: payload.text, Status: "Applied", Delivery: payload.delivery || "Steer",
          Revision: 1, Attachments: [] };
        inputReceipts[payload.operationId] = receipt;
        messages.push({ Id: receipt.Id, Role: "user", Content: payload.text, CreatedUtc: now });
        messages.push({ Id: "preview-reply-" + number, Role: "assistant",
          Content: "Это ответ демонстрационного сценария. Модель и Office не запускались.", CreatedUtc: now });
        revision++; inboxRevision++;
        var active = chats.find(function (chat) { return chat.Id === activeChatId; });
        active.MessageCount = messages.length; active.Revision = revision; active.LastActivityUtc = now;
      }
      return chatInbox(receipt);
    }
    if (type === "getSkills") return skillLibrary();
    if (type === "getTools") return toolLibrary();
    if (type === "readPromptSource") {
      requireChat(payload);
      if (!payload.resource || !/^rna:\/\/catalog\/prompts\/[A-Za-z]+$/.test(payload.resource.uri) ||
          payload.resource.revision !== "preview-prompts-1")
        fail("Снимок инструкции изменился.", "preview_revision_changed");
      var promptData = download(promptText, "text/markdown; charset=utf-8");
      promptData.payload.sha256 = await hash(promptText);
      return { type: "rnassistant.promptSource", contractVersion: 1, chatId: activeChatId,
        resource: clone(payload.resource), totalCharacters: promptText.length, data: promptData };
    }
    if (type === "readToolSource") {
      requireChat(payload);
      if (!tool || payload.toolId !== tool.id || payload.expectedRevision !== tool.revision)
        fail("Снимок инструмента изменился.", "preview_revision_changed");
      var toolData = download(toolSourceText, "application/json; charset=utf-8");
      toolData.payload.sha256 = tool.source.sha256;
      return { type: "rnassistant.toolSourceRead", contractVersion: 1, chatId: activeChatId,
        toolId: tool.id, revision: tool.revision,
        sources: [{ uri: "rna://catalog/tools/" + encodeURIComponent(tool.id) + "/source", revision: tool.revision }],
        data: toolData };
    }
    if (type === "getWorkspaceState") return workspaceState();
    if (type === "browseWorkspaceFiles") {
      if (payload.bindingId !== "preview-binding" || payload.directory !== "")
        fail("Папка примера недоступна.", "preview_workspace_changed");
      var found = workspaceFiles.filter(function (item) {
        return !payload.query || item.name.toLowerCase().includes(String(payload.query).toLowerCase());
      });
      return { type: "rnassistant.workspaceFiles", contractVersion: 1, bindingId: "preview-binding",
        directory: "", offset: payload.offset || 0, matched: found.length, scanLimitReached: false,
        items: found.slice(payload.offset || 0, (payload.offset || 0) + 50) };
    }
    if (type === "saveTabularProjectSources") {
      requireProject(payload);
      if (!Array.isArray(payload.paths) || payload.paths.some(function (path) {
        return !workspaceFiles.some(function (item) { return item.path === path; });
      })) fail("Выбор файлов примера неверен.", "preview_source_invalid");
      sourcePaths = payload.paths.slice(); workspaceRevision++;
      return workspaceState();
    }
    if (type === "listTabularProjectDrafts") {
      requireProject(payload);
      return { state: workspaceState(), items: [] };
    }
    if (type === "listTabularProjectTemplates") {
      requireProject(payload);
      return { state: workspaceState(), offset: payload.offset, total: 1,
        items: payload.offset === 0 ? [{ id: "sales", headFile: templateHead, savedAt: 1730000000000,
          archived: false, versions: 1 }] : [] };
    }
    if (type === "openTabularProjectTemplate") {
      requireProject(payload);
      if (payload.id !== "sales" || payload.headFile !== templateHead) fail("Версия шаблона отсутствует.");
      return { state: workspaceState(), id: "sales", headFile: templateHead,
        template: { file: templateHead, format: "project", sha256: await hash(templateText), input: templateText } };
    }
    if (type === "readTabularProjectDictionary") {
      requireProject(payload);
      return { state: workspaceState(), dictionary: { file: "dictionary-1730000000000-" + "b".repeat(32) + ".json",
        format: "dictionary", sha256: await hash(dictionaryText), input: dictionaryText } };
    }
    if (type === "listArtifactCatalog") {
      requireChat(payload);
      var linked = activeChatId === "preview-chat";
      var artifact = { documentId: "preview-document", documentTitle: "Продажи · сентябрь.xlsx",
        resourceUri: artifactUri, title: "Итоги сентября.md", kind: "markdown",
        updatedUtc: "2026-10-18T09:00:00Z", linked: linked, selected: linked, canTransfer: true, canDelete: false };
      var matches = scenario !== "empty" && (!payload.query || artifact.title.toLowerCase().includes(payload.query.toLowerCase())) &&
        (!payload.kind || payload.kind === "all" || payload.kind === artifact.kind) &&
        (payload.scope !== "chat" || linked) && (!payload.documentId || payload.documentId === "preview-document") && !payload.cursor;
      return { chatId: activeChatId, sessionRevision: revision, documentId: "preview-document",
        documents: [{ id: "preview-document", title: "Продажи · сентябрь.xlsx" }],
        items: matches ? [artifact] : [], nextCursor: null };
    }
    if (type === "exportArtifact") {
      requireChat(payload);
      if (payload.documentId !== "preview-document" || payload.resourceUri !== artifactUri)
        fail("Материал примера изменился.", "preview_revision_changed");
      var artifactData = download(artifactText, "text/markdown; charset=utf-8");
      artifactData.payload.sha256 = await hash(artifactText);
      return { kind: "markdown", contentType: "text/markdown; charset=utf-8",
        fileName: "Итоги сентября.md", data: artifactData };
    }
    if (type === "getVbaProject") return { result: { Success: true, Message: "VBA-проект примера загружен.",
      DataJson: JSON.stringify({ modules: [{ name: "FormatSales", type: "StdModule",
        code: "Option Explicit\n\nPublic Sub FormatSalesReport()\n    ThisWorkbook.Worksheets(\"Продажи\").Range(\"A1:F1\").Font.Bold = True\nEnd Sub\n",
        lineCount: 5 }, { name: "ThisWorkbook", type: "Document", code: "Option Explicit\n", lineCount: 1 }] }) }, backups: [] };
    if (type === "selectChat") {
      if (!chats.some(function (chat) { return chat.Id === payload.chatId; })) fail("Чат примера отсутствует.", "preview_chat_changed");
      activeChatId = payload.chatId; messages = messagesByChat[activeChatId];
      return chatState();
    }
    if (type === "createChat" || type === "createDocumentChat") {
      activeChatId = "preview-chat-" + (++chatSequence); messages = [];
      messagesByChat[activeChatId] = messages;
      chats.unshift({ Id: activeChatId, Title: payload.title || "Новый чат", Host: "Excel",
        DocumentKey: "preview-document", DocumentTitle: "Продажи · сентябрь.xlsx", IsCurrentDocument: true,
        LastActivityUtc: new Date().toISOString(), MessageCount: 0, Revision: revision + 1 });
      revision++;
      return chatState();
    }
    if (type === "readSkillSource") {
      requireChat(payload);
      if (scenario === "error") fail("Демонстрационная ошибка чтения навыка.", "preview_read_failed");
      var selectedSkill = skills.find(function (item) { return item.id === payload.skillId; });
      if (!selectedSkill || payload.expectedPackageRevision !== selectedSkill.revision)
        fail("Снимок навыка изменился.", "preview_revision_changed");
      var selectedReference = payload.path ? selectedSkill.references.find(function (item) { return item.path === payload.path; }) : null;
      if (payload.path && !selectedReference) fail("Reference навыка изменился.", "preview_revision_changed");
      var selectedText = payload.path ? skillReferences[selectedSkill.id][payload.path] : skillTexts[selectedSkill.id];
      if (typeof selectedText !== "string") fail("Источник навыка отсутствует.", "preview_revision_changed");
      var data = download(selectedText, "text/markdown; charset=utf-8");
      data.payload.sha256 = selectedReference ? selectedReference.revision : selectedSkill.body.sha256;
      return { type: "rnassistant.skillSourceRead", contractVersion: 1,
        chatId: activeChatId, skillId: selectedSkill.id, packageRevision: selectedSkill.revision, path: payload.path,
        resource: { uri: "rna://catalog/skills/" + encodeURIComponent(selectedSkill.id) +
          (selectedReference ? "/reference/" + encodeURIComponent(payload.path.split("/").pop()) : "/body"),
          revision: selectedSkill.revision },
        reference: selectedReference ? clone(selectedReference) : null, totalCharacters: selectedText.length, data: data };
    }
    if (type === "resourceDataClose" || type === "closeArtifactTransfer" || type === "cancelSkillMutationUpload") {
      delete downloads[payload.leaseId]; delete uploads[payload.leaseId];
      return { closed: true };
    }
    if (type === "beginSkillMutationUpload") {
      requireChat(payload);
      var id = leaseId(); uploads[id] = { byteLength: payload.byteLength, bytes: new Uint8Array(0) };
      return { leaseId: id, byteLength: payload.byteLength,
        url: "https://rnassistant.local-resource/v1/upload/" + id, maxChunkBytes: 65536 };
    }
    if (type === "saveSkills") return saveSkills(payload);
    if (type === "saveSkillReference") return saveSkillReference(payload);
    if (type === "deleteSkillReference") return deleteSkillReference(payload);
    if (type === "saveSettings") {
      requireChat(payload);
      if (payload.uploadLeaseId) fail("Сохранение изменённой инструкции пока не смоделировано.");
      settings = clone(payload.settings || {});
      return { appVersion: "preview", settings: clone(settings), prompts: promptLibrary(),
        hasApiKey: true, hasHistorySecret: false };
    }
    fail("Действие «" + type + "» ещё не смоделировано в рабочем примере.");
  }

  window.fetch = function (input, options) {
    var url = typeof input === "string" ? input : input && input.url || "";
    if (!url.startsWith("https://rnassistant.local-resource/v1/")) return nativeFetch(input, options);
    var address = new URL(url), id = address.pathname.split("/").pop();
    var offset = Number(address.searchParams.get("offset")), count = Number(address.searchParams.get("count"));
    if (!Number.isSafeInteger(offset) || offset < 0 || !Number.isSafeInteger(count) || count < 1)
      return Promise.resolve(new Response("", { status: 400 }));
    if (address.pathname.includes("/download/")) {
      var item = downloads[id];
      if (!item || offset + count > item.bytes.length) return Promise.resolve(new Response("", { status: 404 }));
      return Promise.resolve(new Response(item.bytes.slice(offset, offset + count),
        { headers: { "Content-Type": item.contentType } }));
    }
    if (address.pathname.includes("/upload/") && options && options.method === "POST") {
      var upload = uploads[id], chunk = new Uint8Array(options.body);
      if (!upload || offset !== upload.bytes.length || count !== chunk.length || offset + count > upload.byteLength)
        return Promise.resolve(new Response("", { status: 409 }));
      var next = new Uint8Array(offset + count); next.set(upload.bytes); next.set(chunk, offset);
      upload.bytes = next;
      return Promise.resolve(new Response(JSON.stringify({ leaseId: id, nextOffset: next.length }),
        { headers: { "Content-Type": "application/json" } }));
    }
    return Promise.resolve(new Response("", { status: 404 }));
  };

  window.chrome = window.chrome || {};
  window.chrome.webview = {
    addEventListener: function (name, listener) { if (name === "message") listeners.push(listener); },
    postMessage: function (request) {
      if (request.type === "focusState") return;
      Promise.resolve().then(async function () {
        await ready;
        if (request.contractVersion !== 2 || request.type !== "init" && request.bridgeToken !== token)
          fail("Контракт просмотра изменился.", "preview_contract_mismatch");
        return dispatch(request.type, request.payload || {});
      }).then(function (payload) {
        emit({ id: request.id, ok: true, payload: payload });
      }, function (error) {
        emit({ id: request.id, ok: false, error: error.message,
          errorDetail: error.message, errorCode: error.code || "preview_failed" });
      });
    }
  };
}());
