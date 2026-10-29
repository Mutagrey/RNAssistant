(function () {
  "use strict";

  var scenario = new URLSearchParams(location.search).get("scenario") || "ready";
  var fixtures = window.PrototypeFixtures;
  if (!fixtures) throw new Error("Preview fixtures are required before the mock bridge.");
  var contract = window.__RNAMockBridgeContract;
  var token = "local-preview-session";
  var listeners = [];
  // Preview-only stand-in for the native protected draft store across page reloads.
  var draftStorageKey = "rnassistant-preview-chat-drafts";
  var previewDrafts = JSON.parse(sessionStorage.getItem(draftStorageKey) || "[]");
  function previewDraftKey(item) { return item.Owner === "office" ? "office:" + item.ChatId :
    "workspace:" + JSON.stringify([item.WorkspaceId, item.SessionId]); }
  function savePreviewDrafts() { sessionStorage.setItem(draftStorageKey, JSON.stringify(previewDrafts)); }
  var downloads = Object.create(null);
  var viewerBatches = Object.create(null);
  var uploads = Object.create(null);
  var leaseSequence = 0;
  var revision = 1, skillRevision = 1;
  var chatSequence = 1;
  var activeChatId = scenario === "empty" ? "" : "preview-chat";
  var messages = scenario === "empty" ? [] : [
    { Id: "preview-user", Role: "user", Content: fixtures.chat.user, CreatedUtc: "2026-10-18T09:00:00Z" },
    { Id: "preview-answer", Role: "assistant", Content: fixtures.chat.assistant,
      RunId: "preview-run-initial", RunViewState: runView("preview-run-initial", "2026-10-18T09:00:00Z"),
      ArtifactIds: ["report"], CreatedUtc: "2026-10-18T09:00:05Z" }
  ];
  if (scenario === "visuals") {
    messages = [
      { Id: "preview-visual-user", Role: "user", Content: fixtures.visualChat.user, CreatedUtc: "2026-10-25T09:00:00Z" },
      { Id: "preview-visual-answer", Role: "assistant", Content: fixtures.visualChat.assistant,
        RunId: "preview-run-visual", RunViewState: runView("preview-run-visual", "2026-10-25T09:00:00Z"), CreatedUtc: "2026-10-25T09:00:05Z" }
    ];
  }
  var chats = activeChatId ? fixtures.officeExamples.flatMap(function (documentExample) {
    return documentExample.chats.map(function (chatExample, index) {
      return { Id: chatExample.previewId, Title: chatExample.title, Host: documentExample.host,
        DocumentKey: documentExample.documentKey, DocumentTitle: documentExample.name,
        IsCurrentDocument: documentExample.documentKey === "preview-document",
        LastActivityUtc: new Date(Date.parse("2026-10-18T09:00:05Z") - index * 60000).toISOString(),
        MessageCount: chatExample.previewId === "preview-chat" ? messages.length : 0, Revision: revision };
    });
  }) : [];
  var documentItems = scenario === "empty" ? [] : fixtures.officeExamples.map(function (example) {
    return { documentKey: example.documentKey, title: example.name, host: example.host,
      isActive: example.documentKey === "preview-document", isOpen: example.open };
  });
  var initialSkillText = fixtures.skill;
  var promptText = "# Агент\n\nОтвечай по данным текущего документа и явно указывай ограничения результата.\n";
  var skills = [], skillTexts = Object.create(null), skillReferences = Object.create(null);
  var messagesByChat = activeChatId ? { "preview-chat": messages, "preview-summary": [],
    "preview-budget": [], "preview-notes": [] } : {};
  var previewRuns = Object.create(null);
  var dataProject = scenario === "empty" ? null : { projectId: "preview-data-project", workspaceId: "preview-data-workspace", name: fixtures.project };
  var projectSelected = !!dataProject;
  var projectChatSequence = 2;
  var projectChats = dataProject ? [{ Id: "preview-project-review", Title: "Подготовка отчёта", RunStatus: "completed" },
    { Id: "preview-project-clean", Title: "Проверка источников", RunStatus: "completed" }] : [];
  var workspaceProjects = dataProject ? [
    { workspaceId: dataProject.workspaceId, name: dataProject.name, rootPath: "C:\\Projects\\Reports", available: true, chats: projectChats },
    { workspaceId: "preview-plan-workspace", name: "План продаж", rootPath: "C:\\Projects\\SalesPlan", available: true,
      chats: [{ Id: "preview-plan-chat", Title: "План запуска", RunStatus: "completed" }] }
  ] : [];
  var hiddenWorkspaceProjects = [], workspaceSequence = 0;
  var projectMessages = { "preview-project-review": [], "preview-project-clean": [] };
  var projectRevisions = Object.create(null);
  if (dataProject) projectMessages["preview-plan-chat"] = [];
  var projectRuns = Object.create(null), projectRequests = Object.create(null);
  var projectFileUploads = Object.create(null);
  var pluginItem = { pluginId: "data-templates", sourceWorkspaceId: "preview-plugin-workspace", name: "Данные и шаблоны", icon: "dataTemplates",
    sourceRoot: "Локальный демонстрационный плагин", releaseId: "preview-release-1", enabled: true,
    previewSimulated: true };
  var extraPlugins = [], pluginCandidateOwner = null;
  var pluginSourceRevision = Object.create(null);
  var pluginPublishedSourceRevision = { "data-templates": 0 };
  var pluginPublishedVersions = { "data-templates": [{ releaseId: "preview-release-1", name: "Данные и шаблоны",
    version: "1.0.0", capturedUtc: "2026-10-18T09:00:00Z", sourceRevision: 0 }] };
  var savedPluginIcons = [], pluginIconSequence = 0;
  // Preview-only SVG sizing; production admission and persistence are exercised by the Harness.
  function previewIconSvg(svg) {
    var xml = new DOMParser().parseFromString(svg, "image/svg+xml"), root = xml.documentElement;
    if (xml.querySelector('parsererror') || root.localName !== 'svg') fail('Нужен полный SVG.', 'preview_icon_invalid');
    root.setAttribute('xmlns', 'http://www.w3.org/2000/svg');
    if (!root.hasAttribute('viewBox')) root.setAttribute('viewBox', '0 0 ' + (parseFloat(root.getAttribute('width')) || 24) + ' ' + (parseFloat(root.getAttribute('height')) || 24));
    root.setAttribute('width', '24'); root.setAttribute('height', '24'); root.setAttribute('preserveAspectRatio', 'xMidYMid meet');
    return new XMLSerializer().serializeToString(root);
  }
  var pluginCandidate = null, pluginInstanceSequence = 0;
  var inboxRevision = 1, inputSequence = 0, inputReceipts = Object.create(null);
  var tool = null;
  var toolSourceText = "";
  var settings = scenario === "empty" ? {} : { Model: "demo-balanced", MaxTokens: 3072,
    RequestTimeoutSeconds: 1800, StreamResponses: true };
  var workspaceRevision = 2;
  var sourcePaths = fixtures.sources.slice(0, 2).map(function (source) { return source.name; });
  var workspaceFiles = fixtures.sources.map(function (source) {
    return { name: source.name, path: source.name, isDirectory: false, byteLength: source.byteLength };
  });
  var templateHead = "template-sales-1730000000000-" + "a".repeat(32) + ".json";
  var dictionaryText = JSON.stringify({ version: 1, revision: 1,
    parameters: [{ id: "revenue", name: "Выручка", unitId: "rub" }, { id: "orders", name: "Заказы", unitId: "pcs" }],
    units: [{ id: "rub", name: "Рубль" }, { id: "pcs", name: "Штука" }],
    parameterMappings: [{ scope: "sales", source: "Выручка", targetId: "revenue" }], unitMappings: [] }, null, 2);
  var templateText = JSON.stringify({ kind: "data-templates-project-template", template: { id: "sales" } }, null, 2);
  var artifactExamples = [
    { id: "report", title: fixtures.materials[0].name, kind: "markdown",
      contentType: "text/markdown; charset=utf-8", body: fixtures.report, linked: true },
    { id: "html", title: fixtures.materials[1].name, kind: "html_workspace",
      contentType: "application/json; charset=utf-8", body: JSON.stringify({ Project: {
        Files: [{ Path: "index.html", Kind: "html", Content: fixtures.materialSources.html }],
        EntryPath: "index.html", Data: [], ExternalDependencies: [] }, Views: [] }), linked: true },
    { id: "json", title: fixtures.materials[2].name, kind: "file",
      contentType: "application/json; charset=utf-8", body: fixtures.materialSources.json, linked: true },
    { id: "pdf", title: fixtures.materials[3].name, kind: "file",
      contentType: "application/pdf", assetUrl: "/preview/assets/sales-methodology.pdf", linked: false },
    { id: "image", title: fixtures.materials[4].name, kind: "image",
      contentType: "image/png", assetUrl: "/preview/assets/sales-chart.png", linked: false },
    { id: "sequence", title: fixtures.materials[5].name, kind: "sequence",
      contentType: "application/json; charset=utf-8", body: fixtures.materialSources.sequence, linked: false },
    { id: "plan", title: "План отчёта.md", kind: "plan_document",
      contentType: "text/markdown; charset=utf-8", body: fixtures.materialSources.plan, linked: true },
    { id: "tasks", title: "Подготовка отчёта", kind: "task_list",
      contentType: "application/vnd.rnassistant.task-list+json; charset=utf-8", body: fixtures.materialSources.tasks, linked: true },
    { id: "text", title: "Заметки к отчёту.txt", kind: "file",
      contentType: "text/plain; charset=utf-8", body: fixtures.materialSources.text, linked: false }
  ];
  var removedArtifactUris = [];
  var pdfText = "Методика расчёта\n\nДемонстрационные данные: сентябрь 2026\n\nПоказатели\nВыручка — сумма подтверждённых заказов за месяц.\nСредний чек — выручка, делённая на число заказов.\nИзменение — отношение к предыдущему периоду.\n\nОграничения\nПо Самаре отсутствует поле «Выручка». Файл требует проверки.";
  function exampleDate(item) {
    var today = new Date(), date = new Date(today.getFullYear(), today.getMonth(), today.getDate(), 12, 0);
    var times = { report: 42, html: 40, json: 39, plan: 38, tasks: 38 };
    if (Object.prototype.hasOwnProperty.call(times, item.id)) date.setMinutes(times[item.id]);
    else if (item.id === "sequence") date.setDate(date.getDate() - 5);
    else date.setDate(date.getDate() - 1);
    return date.toISOString();
  }
  artifactExamples.forEach(function (item) {
    item.resourceUri = "rna://chat/preview-chat/artifact/" + item.id + "/revision/1";
  });
  var nativeFetch = window.fetch.bind(window);

  function clone(value) { return JSON.parse(JSON.stringify(value)); }
  function runView(runId, startedUtc) {
    return { RunId: runId, TurnId: runId, Narrative: "Сравнить продажи за сентябрь и август",
      Lifecycle: "completed", ExecutionHealth: "clean", GoalVerification: "not_assessed",
      SuccessfulReads: 2, VerifiedWrites: 0, NoChangeWrites: 0, UnverifiedWrites: 0,
      FailedCalls: 0, UnknownEffects: 0, PendingConfirmation: null, Reason: "model_done", CurrentAction: "",
      StartedUtc: startedUtc };
  }
  function bytes(value) { return value instanceof Uint8Array ? value : new TextEncoder().encode(value); }
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
      project: scenario === "empty" ? null : { name: fixtures.project, manifestPath: "project.json",
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
    var lastRunMessage = messages.slice().reverse().find(function (item) { return item.RunViewState || item.runViewState; });
    var report = artifactExamples.find(function (item) { return item.id === "report" && item.linked; });
    return { sessionRevision: revision, activeChatId: activeChatId, activeChatMode: "agent",
      activeChatModel: "preview-model", activeChatReasoning: false, chats: clone(chats), documents: clone(documentItems),
      messages: clone(messages), messageStartIndex: 0, messageTotalCount: messages.length,
      skills: skillLibrary(), tools: toolLibrary(), context: {},
      contextUsage: { lastPromptTokens: 6200, lastCompletionTokens: 240, lastTotalTokens: 6440, contextWindowTokens: 32000 },
      runViewState: previewRuns[activeChatId] ? null : lastRunMessage && (lastRunMessage.RunViewState || lastRunMessage.runViewState) || null,
      artifacts: activeChatId === "preview-chat" && report ? [{ id: "report", kind: "markdown",
        title: fixtures.materials[0].name, mimeType: "text/markdown", sourceMessageId: "preview-answer",
        runId: "preview-run-initial", revision: 1, inlineText: fixtures.report,
        inlineTruncated: false, metadataJson: "{}", relatedArtifactIds: [],
        createdUtc: "2026-10-18T09:00:05Z", resourceUri: report.resourceUri,
        documentScoped: false }] : [],
      artifactLibrary: { sessionRevision: revision, heads: [], removedResourceUris: clone(removedArtifactUris) }, inbox: chatInbox() };
  }
  function chatInbox(receipt) {
    var run = previewRuns[activeChatId];
    return { chatId: activeChatId, epoch: "preview-inbox", revision: inboxRevision,
      running: !!run, phase: run ? run.phase : "", paused: false,
      items: [], receipts: [], acceptedInput: receipt || null };
  }
  function projectCatalog(selectedWorkspaceId) {
    var projects = clone(workspaceProjects);
    projects.forEach(function (entry) {
      var plugin = [pluginItem].concat(extraPlugins).find(function (item) { return item.sourceWorkspaceId === entry.workspaceId && !item.hidden; });
      if (plugin) { entry.pluginId = plugin.pluginId; entry.name = plugin.name; } else delete entry.pluginId;
    });
    return { projects: projects, selectedWorkspaceId: selectedWorkspaceId || null };
  }
  function requireWorkspace(payload) {
    var entry = workspaceProjects.find(function (item) { return item.workspaceId === payload.workspaceId; });
    if (!entry || !entry.available) fail("Папка проекта недоступна.", "preview_project_changed");
    return entry;
  }
  function requireProjectChat(payload) {
    var chat = requireWorkspace(payload).chats.find(function (item) { return item.Id === payload.sessionId; });
    if (!chat) fail("Чат проекта отсутствует.", "preview_chat_changed");
    return chat;
  }
  function projectChatState(payload) {
    var entry = requireWorkspace(payload), chat = requireProjectChat(payload), run = projectRuns[chat.Id];
    return { project: clone(entry), sessionId: chat.Id,
      state: { id: chat.Id, workspaceId: entry.workspaceId, title: chat.Title,
        revision: projectRevisions[chat.Id] || 0,
        status: run ? "running" : chat.RunStatus, runId: run && run.runId || null,
        cursor: run && run.cursor || "preview-project-cursor", userInput: null,
        modelProgress: run && run.modelProgress || null },
      pendingId: null, pendingTool: null, pendingArguments: null,
      messages: clone(projectMessages[chat.Id] || []), messageStartIndex: 0,
      messageTotalCount: (projectMessages[chat.Id] || []).length };
  }
  function pluginChatState(pluginId) {
    var plugin = [pluginItem].concat(extraPlugins).find(function (item) { return item.pluginId === pluginId && !item.hidden; });
    if (!plugin) fail("Плагин примера отсутствует.");
    var entry = workspaceProjects.find(function (item) { return item.workspaceId === plugin.sourceWorkspaceId; });
    if (!entry) {
      entry = hiddenWorkspaceProjects.find(function (item) { return item.workspaceId === plugin.sourceWorkspaceId; }) ||
        { workspaceId: plugin.sourceWorkspaceId, rootPath: plugin.sourceRoot, available: true, chats: [] };
      hiddenWorkspaceProjects = hiddenWorkspaceProjects.filter(function (item) { return item !== entry; });
      workspaceProjects.push(entry);
    }
    entry.pluginId = pluginId; entry.name = plugin.name;
    if (!entry.chats.length) {
      var chat = { Id: "preview-project-" + (++projectChatSequence), Title: "Разработка: " + plugin.name, RunStatus: "" };
      entry.chats.unshift(chat); projectMessages[chat.Id] = [];
    }
    return projectChatState({ workspaceId: entry.workspaceId, sessionId: entry.chats[0].Id });
  }
  function emit(response) { listeners.forEach(function (listener) { listener({ data: response }); }); }
  function emitChatState(chatId) {
    emit({ type: "chatState", scope: activeChatId === chatId ? "full" : "catalog", payload: chatState() });
  }
  function simulateChatRun(chatId, number) {
    var runId = "preview-run-" + number;
    var answer = fixtures.chat.assistant + "\n\n_Это смоделированный ответ: модель и Office не запускались._";
    var split = Math.ceil(answer.length / 2);
    function progress(phase, message, contentDelta) {
      if (!previewRuns[chatId]) return;
      previewRuns[chatId].phase = phase;
      emit({ type: "progress", id: "inbox:" + chatId,
        payload: { chatId: chatId, runId: runId, phase: phase, message: message, contentDelta: contentDelta || "" } });
    }
    setTimeout(function () { emitChatState(chatId); progress("thinking", "Сверяю периоды и источники…"); }, 0);
    setTimeout(function () { progress("streaming", null, answer.slice(0, split)); }, 220);
    setTimeout(function () { progress("streaming", null, answer.slice(split)); }, 520);
    setTimeout(function () {
      if (!previewRuns[chatId]) return;
      var now = new Date().toISOString();
      messagesByChat[chatId].push({ Id: "preview-reply-" + number, Role: "assistant", Content: answer,
        RunId: runId, RunViewState: runView(runId, now), CreatedUtc: now });
      delete previewRuns[chatId];
      revision++; inboxRevision++;
      var chat = chats.find(function (item) { return item.Id === chatId; });
      if (chat) { chat.MessageCount = messagesByChat[chatId].length; chat.Revision = revision; chat.LastActivityUtc = now; }
      emitChatState(chatId);
    }, 850);
  }
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
    skills.push({ id: "excel.sales_analysis", host: "Excel", name: "Анализ продаж",
      description: "Сравнение периодов и анализ продаж по регионам", version: "1.0.0",
      enabled: true, builtIn: false, revision: "preview-skill-1", references: [],
      body: { sha256: await hash(initialSkillText), byteLength: bytes(initialSkillText).length, characters: initialSkillText.length } });
    skillTexts[skills[0].id] = initialSkillText;
    var code = fixtures.vba;
    toolSourceText = JSON.stringify({ argumentSchemaJson: '{"type":"object","properties":{},"additionalProperties":false}',
      code: code, readme: "# Форматирование отчёта\n\nПрименяет оформление к заголовку таблицы.\n",
      components: [{ name: "FormatSales", type: "StdModule", fileName: "FormatSales.bas", code: code,
        codeSha256: await hash(code) }] });
    tool = { id: "excel.format_sales", host: "Excel", name: "Форматирование таблицы",
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
  async function viewerBinary(resourceUri, assetUrl, contentType) {
    var asset = await nativeFetch(assetUrl);
    if (!asset.ok) fail("Файл демонстрационного материала недоступен.");
    var body = new Uint8Array(await asset.arrayBuffer()), id = leaseId();
    downloads[id] = { bytes: body, contentType: contentType };
    return { leaseId: id, url: "https://rnassistant.local-resource/v1/" + id,
      descriptor: { reference: { uri: resourceUri, revision: "1" } }, view: "binary",
      expiresUtc: new Date(Date.now() + 600000).toISOString(),
      maxBatchItems: 65536, maxBatchBytes: 65536,
      binary: { payload: { byteLength: body.length, sha256: await hash(body), contentType: contentType } } };
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

  async function dispatch(type, payload, requestId) {
    if (type === "resolveHtmlVendor") {
      if (payload.id !== "echarts" || payload.version !== "5.6.0")
        fail("В примере доступен только поставляемый ECharts 5.6.0.", "preview_vendor_unavailable");
      var vendorResponse = await fetch("/preview/web/js/vendor/echarts.min.js");
      if (!vendorResponse.ok) fail("Локальный ECharts недоступен.", "preview_vendor_unavailable");
      return { js: await vendorResponse.text(), css: "" };
    }
    if (type === "init") return { bridgeToken: token, bridgeContract: contract,
      init: Object.assign({ appVersion: "preview", host: "Excel", title: fixtures.document,
        settings: clone(settings), prompts: promptLibrary(), hasApiKey: true, hasHistorySecret: false }, chatState()) };
    if (type === "getChatDrafts") {
      if (scenario === "draft-read-error") fail("Черновики недоступны.", "preview_draft_read_failed");
      return { drafts: previewDrafts.map(function (draft) {
        return { draft: clone(draft), attachments: [],
          attachmentError: draft.Owner === "office" && (draft.ResourceDraftIds || []).length
            ? "Часть вложений черновика недоступна; проверьте их перед отправкой." : null }; }) };
    }
    if (type === "saveChatDraft") {
      if (scenario === "draft-save-error") fail("Черновик не сохранён.", "preview_draft_save_failed");
      var key = previewDraftKey(payload);
      previewDrafts = previewDrafts.filter(function (draft) { return previewDraftKey(draft) !== key; });
      if (payload.Text || (payload.ResourceDraftIds || []).length || payload.PendingInput || payload.PendingOfficeInput ||
          payload.PendingVisualAnnotation ||
          payload.Owner === "office" && payload.NextAnnotationNumber > 1 ||
          payload.Owner === "workspace" && (payload.Mode !== "agent" || payload.Reasoning)) previewDrafts.push(clone(payload));
      savePreviewDrafts(); return { saved: true };
    }
    if (type === "getModelCatalog") {
      if (scenario === "error") fail("Демонстрационная ошибка чтения каталога моделей.", "preview_read_failed");
      return { catalog: { default_model: "demo-balanced", models: scenario === "empty" ? [] : [
        { id: "demo-balanced", display_name: "Модель A", supports_images: true,
          supports_reasoning: true, supports_audio: false, max_context_tokens: 128000,
          max_output_tokens: 8192, is_default: true }
      ] } };
    }
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
        if (previewRuns[activeChatId]) fail("Дождитесь завершения ответа примера.", "preview_run_busy");
        var number = ++inputSequence, now = new Date().toISOString();
        receipt = { chatId: activeChatId, Id: "preview-input-" + number, OperationId: payload.operationId,
          Text: payload.text, Status: "Applied", Delivery: payload.delivery || "Steer",
          Revision: 1, Attachments: [] };
        inputReceipts[payload.operationId] = receipt;
        messages.push({ Id: receipt.Id, Role: "user", Content: payload.text, CreatedUtc: now });
        previewRuns[activeChatId] = { phase: "thinking" };
        revision++; inboxRevision++;
        var active = chats.find(function (chat) { return chat.Id === activeChatId; });
        active.MessageCount = messages.length; active.Revision = revision; active.LastActivityUtc = now;
        simulateChatRun(activeChatId, number);
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
    if (type === "getPlugins") return { defaultSourceParent: "C:\\Users\\Demo\\AppData\\Roaming\\RNAssistant\\plugin-workspaces",
      items: clone([pluginItem].concat(extraPlugins).filter(function (item) { return !item.hidden; })) };
    if (type === "getRemovedPlugins") return { items: clone([pluginItem].concat(extraPlugins).filter(function (item) { return !!item.hidden; })) };
    if (type === "getPluginIcons") return { items: clone(savedPluginIcons) };
    if (type === "savePluginIcon") {
      if (!payload.name || payload.name.length > 120) fail("Укажите название и SVG-код.", "preview_icon_invalid");
      var svg = previewIconSvg(payload.svg);
      var existing = savedPluginIcons.find(function (item) { return item.svg === svg; });
      if (existing) return clone(existing);
      var added = { id: "icon-" + (++pluginIconSequence), name: payload.name, svg: svg };
      savedPluginIcons.push(added); return clone(added);
    }
    if (type === "drawPluginIcon") {
      if (!payload.description || payload.description.length > 500) fail("Опишите иконку.", "preview_icon_invalid");
      var drawn = previewIconSvg('<svg viewBox="0 0 64 64"><defs><linearGradient id="ink"><stop stop-color="' + (/^#[a-f0-9]{6}$/i.test(payload.iconColor || '') ? payload.iconColor : '#368453') + '"/><stop offset="1" stop-color="#69b7e4"/></linearGradient></defs><rect x="4" y="4" width="56" height="56" rx="12" fill="url(#ink)"/><path d="M16 42l12-12 8 6 12-16" fill="none" stroke="white" stroke-width="5"/></svg>');
      var existingDrawn = savedPluginIcons.find(function (item) { return item.svg === drawn; });
      if (existingDrawn) return Object.assign(clone(existingDrawn), { previewSimulated: true });
      var generated = { id: "icon-" + (++pluginIconSequence), name: payload.name || "Новый плагин", svg: drawn };
      savedPluginIcons.push(generated); return Object.assign(clone(generated), { previewSimulated: true });
    }
    if (type === "deletePluginIcon") {
      var index = savedPluginIcons.findIndex(function (item) { return item.id === payload.id; });
      if (index < 0) fail("Базовые иконки нельзя удалять.", "preview_icon_protected");
      savedPluginIcons.splice(index, 1); return { items: clone(savedPluginIcons) };
    }
    if (type === "removePlugin" || type === "restorePlugin") {
      var visiblePlugin = [pluginItem].concat(extraPlugins).find(function (item) { return item.pluginId === payload.pluginId; });
      if (!visiblePlugin) fail("Плагин не найден.", "preview_plugin_missing");
      visiblePlugin.hidden = type === "removePlugin";
      if (visiblePlugin === pluginItem && visiblePlugin.hidden) projectSelected = false;
      if (visiblePlugin.hidden && pluginCandidateOwner === visiblePlugin.pluginId) {
        pluginCandidate = null; pluginCandidateOwner = null;
      }
      return type === "removePlugin" ? { pluginId: payload.pluginId, hidden: true } : clone(visiblePlugin);
    }
    if (type === "createPlugin" || type === "connectPlugin") {
      if (type === "connectPlugin" && !payload.rootPath || type === "createPlugin" && !payload.name)
        fail("Укажите название плагина или папку готового плагина.", "preview_input_invalid");
      var sourceRoot = payload.rootPath || "C:\\Users\\Demo\\AppData\\Roaming\\RNAssistant\\plugin-workspaces\\" +
        (payload.name || "Plugin").replace(/[<>:\"/\\|?*]/g, "-").slice(0, 48) + "-" + (extraPlugins.length + 1);
      var newPlugin = { pluginId: "preview-plugin-" + (extraPlugins.length + 1),
        name: type === "createPlugin" ? payload.name : "Подключённый плагин",
        icon: "puzzle", iconColor: payload.iconColor || null, iconDescription: payload.iconDescription || "", sourceRoot: sourceRoot,
        releaseId: null, enabled: false, previewSimulated: true };
      if (type === "createPlugin" && payload.iconId && payload.iconId !== "draw") {
        var selectedIcon = savedPluginIcons.find(function (item) { return item.id === payload.iconId; });
        if (payload.iconId.startsWith("icon-") && !selectedIcon) fail("Сохранённая иконка недоступна.", "preview_icon_missing");
        newPlugin.icon = selectedIcon ? "icon.svg" : payload.iconId;
        if (selectedIcon) newPlugin.iconSvg = selectedIcon.svg;
      }
      newPlugin.sourceWorkspaceId = "source-" + newPlugin.pluginId;
      extraPlugins.push(newPlugin);
      return clone(newPlugin);
    }
    if (type === "getProjectChats") return projectCatalog();
    if (type === "connectProjectWorkspace" || type === "createProjectWorkspace") {
      var rootPath = (payload.rootPath || "").trim();
      if (!/^(?:[a-z]:[\\/]|\/)/i.test(rootPath)) fail("Укажите полный путь к папке.", "preview_project_root_invalid");
      var connected = workspaceProjects.concat(hiddenWorkspaceProjects).find(function (item) {
        return item.rootPath.toLowerCase() === rootPath.toLowerCase();
      });
      if (connected && type === "createProjectWorkspace") fail("Папка уже существует.", "preview_project_root_exists");
      if (connected && hiddenWorkspaceProjects.includes(connected)) {
        hiddenWorkspaceProjects = hiddenWorkspaceProjects.filter(function (item) { return item !== connected; });
        workspaceProjects.push(connected);
      } else if (!connected) {
        connected = { workspaceId: "preview-workspace-" + (++workspaceSequence),
          name: rootPath.split(/[\\/]/).filter(Boolean).pop(), rootPath: rootPath,
          available: true, chats: [] };
        workspaceProjects.push(connected);
      }
      return projectCatalog(connected.workspaceId);
    }
    if (type === "getProjectChat") {
      var observed = projectChatState(payload), receiptInput = payload.receiptRequestId && projectRequests[payload.receiptRequestId];
      if (receiptInput && receiptInput.sessionId === payload.sessionId) observed.inputReceipt = clone(receiptInput.receipt);
      return observed;
    }
    if (type === "beginProjectChatFileUpload") {
      requireProjectChat(payload);
      if (!payload.fileName || payload.byteLength < 1 || payload.byteLength > 20 * 1024 * 1024)
        fail("Выберите непустой файл размером не более 20 МБ.", "preview_project_file_invalid");
      var fileLease = leaseId();
      var relativePath = "chat-files/" + fileLease + "-" + payload.fileName;
      projectFileUploads[fileLease] = { workspaceId: payload.workspaceId, sessionId: payload.sessionId,
        relativePath: relativePath, byteLength: payload.byteLength, chunks: [], received: 0 };
      return { leaseId: fileLease, maxChunkBytes: 256 * 1024, relativePath: relativePath };
    }
    if (type === "writeProjectChatFileChunk" || type === "completeProjectChatFileUpload" ||
        type === "cancelProjectChatFileUpload") {
      var projectUpload = projectFileUploads[payload.leaseId];
      if (!projectUpload || projectUpload.workspaceId !== payload.workspaceId || projectUpload.sessionId !== payload.sessionId)
        fail("Загрузка файла не найдена.", "preview_project_file_missing");
      if (type === "cancelProjectChatFileUpload") {
        delete projectFileUploads[payload.leaseId]; return { nextOffset: projectUpload.received };
      }
      if (type === "writeProjectChatFileChunk") {
        var data = atob(payload.base64 || "");
        if (payload.offset !== projectUpload.received || !data.length || data.length > 256 * 1024 ||
            projectUpload.received + data.length > projectUpload.byteLength)
          fail("Фрагменты файла идут не подряд.", "preview_project_file_offset");
        projectUpload.chunks.push(data); projectUpload.received += data.length;
        return { nextOffset: projectUpload.received };
      }
      if (projectUpload.received !== projectUpload.byteLength)
        fail("Файл передан не полностью.", "preview_project_file_incomplete");
      delete projectFileUploads[payload.leaseId];
      var projectTextReadable = projectUpload.received <= 1024 * 1024;
      if (projectTextReadable) try {
        var rawFile = Uint8Array.from(projectUpload.chunks.join(""), function (character) { return character.charCodeAt(0); });
        new TextDecoder("utf-8", { fatal: true }).decode(rawFile);
      } catch (_) { projectTextReadable = false; }
      return { relativePath: projectUpload.relativePath, byteLength: projectUpload.received,
        modelReadable: projectTextReadable };
    }
    if (type === "createProjectChat") {
      var targetWorkspace = requireWorkspace(payload);
      var created = { Id: "preview-project-" + (++projectChatSequence), Title: payload.title || "Новый чат", RunStatus: "" };
      targetWorkspace.chats.unshift(created); projectMessages[created.Id] = [];
      return projectChatState({ workspaceId: targetWorkspace.workspaceId, sessionId: created.Id });
    }
    if (type === "forkProjectChat") {
      var forkWorkspace = requireWorkspace(payload), forkSource = requireProjectChat(payload);
      if (projectRuns[payload.sessionId]) fail("Сначала остановите чат.", "preview_run_busy");
      var sourceMessages = projectMessages[forkSource.Id] || [];
      var forkIndex = sourceMessages.findIndex(function (message) { return message.Id === payload.messageId; });
      if (forkIndex < 0) fail("Сообщение не найдено.", "preview_message_missing");
      var forked = { Id: "preview-project-" + (++projectChatSequence),
        Title: forkSource.Title + " (ветка)", RunStatus: "" };
      forkWorkspace.chats.unshift(forked);
      projectMessages[forked.Id] = JSON.parse(JSON.stringify(sourceMessages.slice(0, forkIndex + 1)));
      projectRevisions[forked.Id] = 1;
      return projectChatState({ workspaceId: forkWorkspace.workspaceId, sessionId: forked.Id });
    }
    if (type === "deleteProjectChatMessage") {
      requireProjectChat(payload);
      if (projectRuns[payload.sessionId]) fail("Сначала остановите чат.", "preview_run_busy");
      var history = projectMessages[payload.sessionId] || [];
      var removedIndex = history.findIndex(function (message) { return message.Id === payload.messageId; });
      if (removedIndex < 0) fail("Сообщение не найдено.", "preview_message_missing");
      history.splice(removedIndex, 1);
      projectRevisions[payload.sessionId] = (projectRevisions[payload.sessionId] || 0) + 1;
      return projectChatState(payload);
    }
    if (type === "renameProjectChat") {
      var renamed = requireProjectChat(payload);
      if (!payload.title || !payload.title.trim()) fail("Название чата пусто.", "preview_chat_invalid");
      renamed.Title = payload.title.trim(); return projectCatalog();
    }
    if (type === "deleteProjectChat") {
      var chatWorkspace = requireWorkspace(payload); requireProjectChat(payload);
      if (projectRuns[payload.sessionId]) fail("Сначала остановите чат.", "preview_run_busy");
      chatWorkspace.chats = chatWorkspace.chats.filter(function (item) { return item.Id !== payload.sessionId; });
      delete projectMessages[payload.sessionId];
      previewDrafts = previewDrafts.filter(function (draft) { return !(draft.Owner === "workspace" &&
        draft.WorkspaceId === payload.workspaceId && draft.SessionId === payload.sessionId); });
      savePreviewDrafts(); return projectCatalog();
    }
    if (type === "deleteProjectHistory") {
      var historyWorkspace = requireWorkspace(payload);
      if (historyWorkspace.chats.some(function (item) { return !!projectRuns[item.Id]; }))
        fail("Сначала остановите чаты проекта.", "preview_run_busy");
      historyWorkspace.chats.forEach(function (item) { delete projectMessages[item.Id]; });
      historyWorkspace.chats = [];
      workspaceProjects = workspaceProjects.filter(function (item) { return item !== historyWorkspace; });
      hiddenWorkspaceProjects.push(historyWorkspace);
      previewDrafts = previewDrafts.filter(function (draft) { return draft.WorkspaceId !== payload.workspaceId; });
      savePreviewDrafts();
      return projectCatalog();
    }
    if (type === "submitProjectChat") {
      var submitting = requireProjectChat(payload), input = payload.input || {};
      if (input.sessionId !== submitting.Id || !input.requestId || typeof input.message !== "string" || !input.message.trim())
        fail("Сообщение проекта неверно.", "preview_input_invalid");
      var accepted = projectRequests[input.requestId];
      if (accepted && (accepted.sessionId !== submitting.Id || accepted.message !== input.message))
        fail("ID запроса уже занят другим сообщением.", "preview_input_invalid");
      if (!accepted) {
        if (projectRuns[submitting.Id]) fail("Дождитесь ответа примера.", "preview_run_busy");
        if (input.EditMessageId) {
          if (input.ResourceDraftIds && input.ResourceDraftIds.length)
            fail("Новые вложения при редактировании не поддерживаются.", "preview_input_invalid");
          if (input.ExpectedRevision !== (projectRevisions[submitting.Id] || 0))
            fail("Чат изменился. Откройте его заново.", "preview_chat_changed");
          var editIndex = projectMessages[submitting.Id].findIndex(function (item) {
            return item.Id === input.EditMessageId && item.Role === "user";
          });
          if (editIndex < 0) fail("Сообщение не найдено.", "preview_message_missing");
          projectMessages[submitting.Id].splice(editIndex);
        }
        accepted = projectRequests[input.requestId] = { sessionId: submitting.Id, message: input.message };
        projectMessages[submitting.Id].push({ Id: input.requestId, Role: "user", Content: input.message });
        projectRevisions[submitting.Id] = (projectRevisions[submitting.Id] || 0) + 1;
        var run = { runId: "preview-project-run-" + input.requestId, cursor: "preview-project-cursor-" + input.requestId };
        projectRuns[submitting.Id] = run; submitting.RunStatus = "running";
        accepted.receipt = { SessionId: submitting.Id, RequestId: input.requestId, InputId: input.requestId,
          RunId: run.runId, AcceptedSequence: projectRevisions[submitting.Id], AcceptedCursor: run.cursor,
          DeliveryRecorded: true };
        emit({ type: "progress", id: requestId, payload: { workspaceId: payload.workspaceId,
          sessionId: submitting.Id, runId: run.runId, phase: "input_receipt", inputReceipt: accepted.receipt } });
        ["Проверяю запрос…", "Готовлю ответ проекта…"].forEach(function (message, index) {
          setTimeout(function () {
            if (projectRuns[submitting.Id] !== run) return;
            run.modelProgress = { RunId: run.runId, StepId: "preview-step", AttemptId: "preview-attempt",
              Sequence: index + 1, Message: message, Reasoning: "" };
            emit({ type: "progress", id: requestId, payload: { workspaceId: payload.workspaceId,
              sessionId: submitting.Id, runId: run.runId, phase: "model_progress",
              modelProgress: run.modelProgress } });
          }, 180 + index * 420);
        });
        setTimeout(function () {
          if (projectRuns[submitting.Id] === run) {
            var sourcePlugin = requireWorkspace(payload).pluginId;
            projectMessages[submitting.Id].push({ Id: run.runId + "-answer", Role: "assistant", Content: sourcePlugin
              ? "Изменение исходников смоделировано в preview. Файлы плагина не менялись."
              : "Проектный пример: план и файлы проекта можно обсудить здесь. Модель в preview не запускалась." });
            if (sourcePlugin) pluginSourceRevision[sourcePlugin] = (pluginSourceRevision[sourcePlugin] || 0) + 1;
            delete projectRuns[submitting.Id]; submitting.RunStatus = "completed";
            projectRevisions[submitting.Id] = (projectRevisions[submitting.Id] || 0) + 1;
            emit({ type: "progress", id: requestId, payload: { workspaceId: payload.workspaceId,
              sessionId: submitting.Id, runId: run.runId, phase: "session_changed",
              cursor: "preview-project-terminal-" + projectRevisions[submitting.Id] } });
          }
        }, 1800);
      }
      return Object.assign(projectChatState(payload), { inputReceipt: accepted.receipt });
    }
    if (type === "cancelProjectChat") {
      var stopped = requireProjectChat(payload), currentRun = projectRuns[stopped.Id];
      if (!currentRun || currentRun.runId !== payload.runId || currentRun.cursor !== payload.cursor)
        fail("Состояние запуска изменилось.", "preview_run_changed");
      delete projectRuns[stopped.Id]; stopped.RunStatus = "interrupted";
      return Object.assign(projectChatState(payload), { cancelStatus: "Requested" });
    }
    if (type === "resolveProjectChat") {
      requireProjectChat(payload); fail("В этом примере нет ожидающего подтверждения.", "preview_run_changed");
    }
    if (type === "getPluginSourceState") {
      if (![pluginItem].concat(extraPlugins).some(function (item) { return item.pluginId === payload.pluginId && !item.hidden; })) fail("Плагин примера отсутствует.");
      return { pluginId: payload.pluginId, stamp: "preview-source-" + (pluginSourceRevision[payload.pluginId] || 0) };
    }
    if (type === "checkPlugin") {
      if (![pluginItem].concat(extraPlugins).some(function (item) { return item.pluginId === payload.pluginId && !item.hidden; })) fail("Плагин примера отсутствует.");
      pluginCandidate = "preview-candidate-" + (++pluginInstanceSequence);
      pluginCandidateOwner = payload.pluginId;
      var checkedPlugin = [pluginItem].concat(extraPlugins).find(function (item) { return item.pluginId === payload.pluginId; });
      var checkedIcon = extraPlugins.find(function (item) { return item.pluginId === payload.pluginId; });
      if (checkedIcon && checkedIcon.iconDescription && checkedIcon.icon === "puzzle") {
        var drawnSvg = previewIconSvg('<svg viewBox="0 0 24 24"><path d="M4 18V7h4v11M10 18v-7h4v7M16 18V4h4v14"/></svg>');
        if (!savedPluginIcons.some(function (item) { return item.svg === drawnSvg; }))
          savedPluginIcons.push({ id: "icon-" + (++pluginIconSequence), name: checkedIcon.name, svg: drawnSvg });
      }
      return { pluginId: payload.pluginId, candidateId: pluginCandidate,
        hasChanges: !checkedPlugin.enabled || !checkedPlugin.releaseId ||
          (pluginSourceRevision[payload.pluginId] || 0) !== (pluginPublishedSourceRevision[payload.pluginId] || 0),
        files: 1, bytes: 0,
        checks: ["preview-only"], previewSimulated: true };
    }
    if (type === "applyPlugin") {
      if (payload.pluginId !== pluginCandidateOwner || payload.candidateId !== pluginCandidate)
        fail("Снимок примера изменился.", "preview_candidate_changed");
      var applying = [pluginItem].concat(extraPlugins).find(function (item) { return item.pluginId === payload.pluginId; });
      applying.enabled = true; applying.releaseId = "preview-release-" + (++pluginInstanceSequence);
      pluginPublishedSourceRevision[payload.pluginId] = pluginSourceRevision[payload.pluginId] || 0;
      if (!pluginPublishedVersions[payload.pluginId]) pluginPublishedVersions[payload.pluginId] = [];
      pluginPublishedVersions[payload.pluginId].unshift({ releaseId: applying.releaseId, name: applying.name,
        version: "1.0.0", capturedUtc: new Date().toISOString(), sourceRevision: pluginPublishedSourceRevision[payload.pluginId] });
      if (applying !== pluginItem && applying.iconDescription) {
        applying.icon = "icon.svg";
        applying.iconSvg = previewIconSvg('<svg viewBox="0 0 24 24"><path d="M4 18V7h4v11M10 18v-7h4v7M16 18V4h4v14"/></svg>');
      }
      if (applying === pluginItem) projectSelected = false;
      pluginCandidate = null; pluginCandidateOwner = null; return clone(applying);
    }
    if (type === "getPluginVersions") {
      var historyPlugin = [pluginItem].concat(extraPlugins).find(function (item) { return item.pluginId === payload.pluginId && !item.hidden; });
      if (!historyPlugin) fail("Плагин примера отсутствует.");
      var versions = pluginPublishedVersions[payload.pluginId] || [], offset = payload.cursor ? Number(payload.cursor) : 0;
      if (!Number.isInteger(offset) || offset < 0 || offset >= versions.length && offset !== 0) fail("История изменилась.", "preview_history_changed");
      return { items: versions.slice(offset, offset + 12).map(function (item) { return {
        releaseId: item.releaseId, name: item.name, version: item.version, capturedUtc: item.capturedUtc,
        active: historyPlugin.enabled && historyPlugin.releaseId === item.releaseId }; }),
        nextCursor: versions.length > offset + 12 ? String(offset + 12) : null };
    }
    if (type === "restorePluginVersion") {
      var restoring = [pluginItem].concat(extraPlugins).find(function (item) { return item.pluginId === payload.pluginId && !item.hidden; });
      if (!restoring || restoring.releaseId !== payload.expectedActiveReleaseId) fail("Активная версия изменилась.", "preview_history_changed");
      var saved = (pluginPublishedVersions[payload.pluginId] || []).find(function (item) { return item.releaseId === payload.releaseId; });
      if (!saved) fail("Версия не была опубликована.", "preview_release_missing");
      restoring.releaseId = saved.releaseId; restoring.enabled = true;
      pluginPublishedSourceRevision[payload.pluginId] = saved.sourceRevision;
      pluginPublishedVersions[payload.pluginId] = [saved].concat(pluginPublishedVersions[payload.pluginId].filter(function (item) { return item !== saved; }));
      pluginCandidate = null; pluginCandidateOwner = null;
      return clone(restoring);
    }
    if (type === "disablePlugin") {
      var disabling = [pluginItem].concat(extraPlugins).find(function (item) { return item.pluginId === payload.pluginId; });
      if (!disabling) fail("Плагин примера отсутствует.");
      disabling.enabled = false; pluginCandidate = null; pluginCandidateOwner = null;
      if (disabling === pluginItem) projectSelected = false;
      return clone(disabling);
    }
    if (type === "openPlugin") {
      var opening = [pluginItem].concat(extraPlugins).find(function (item) { return item.pluginId === payload.pluginId; });
      var historical = payload.releaseId && (pluginPublishedVersions[payload.pluginId] || []).find(function (item) { return item.releaseId === payload.releaseId; });
      if (!opening || opening.hidden || payload.candidateId && (payload.candidateId !== pluginCandidate || payload.pluginId !== pluginCandidateOwner) ||
          payload.releaseId && !historical || !payload.candidateId && !payload.releaseId && !opening.enabled) fail("Плагин примера недоступен.", "preview_plugin_changed");
      if (opening === pluginItem && !payload.candidateId && !payload.releaseId) projectSelected = !!dataProject;
      return { instanceId: "preview-plugin-instance-" + (++pluginInstanceSequence), pluginId: opening.pluginId,
        releaseId: payload.candidateId || payload.releaseId || opening.releaseId,
        preview: !!payload.candidateId || !!payload.releaseId, previewSimulated: true,
        entryUrl: opening === pluginItem ? "/preview/web/data-templates/index.html?revision=" + (historical ? historical.sourceRevision : pluginSourceRevision[opening.pluginId] || 0) :
          "/preview/mock-plugin.html?name=" + encodeURIComponent(opening.name) + "&revision=" + (historical ? historical.sourceRevision : pluginSourceRevision[opening.pluginId] || 0) };
    }
    if (type === "closePlugin") return {};
    if (type === "updatePluginAppearance") {
      var edited = [pluginItem].concat(extraPlugins).find(function (item) { return item.pluginId === payload.pluginId; });
      if (!edited || !payload.name.trim()) fail("Укажите название плагина.");
      edited.name = payload.name.trim(); edited.iconColor = payload.iconColor || null;
      if (payload.iconId) {
        var saved = savedPluginIcons.find(function (item) { return item.id === payload.iconId; });
        edited.icon = payload.iconId; edited.iconSvg = saved ? saved.svg : null;
      }
      return clone(edited);
    }
    if (type === "getPluginChat") return pluginChatState(payload.pluginId);
    if (type === "getPluginVendors") return { items: [] };
    if (type === "listArtifactCatalog") {
      requireChat(payload);
      var items = scenario === "empty" || payload.cursor ? [] : artifactExamples.filter(function (item) {
        var linked = item.linked && activeChatId === "preview-chat";
        return (!payload.query || item.title.toLowerCase().includes(String(payload.query).toLowerCase())) &&
          (!payload.kind || payload.kind === "all" || payload.kind === item.kind) &&
          (payload.scope !== "chat" || linked) &&
          (!payload.documentId || payload.documentId === "preview-document");
      }).map(function (item) {
        var linked = item.linked && activeChatId === "preview-chat";
        var otherDocument = item.id === "sequence";
        return { documentId: otherDocument ? "preview-other-document" : "preview-document",
          documentTitle: otherDocument ? "Все документы" : fixtures.document,
          resourceUri: item.resourceUri, title: item.title, kind: item.kind,
          updatedUtc: exampleDate(item), linked: linked, selected: linked,
          canTransfer: true, canDelete: !otherDocument };
      });
      var sortBy = payload.sortBy || "updated", descending = payload.sortDescending == null ? sortBy === "updated" : payload.sortDescending;
      function sortValue(item) {
        if (sortBy === "updated") return Date.parse(item.updatedUtc);
        if (sortBy === "owner") return item.linked ? "Этот чат" : item.documentId === "preview-document" ? "В документе" : item.documentTitle;
        if (sortBy === "format") {
          var suffix = String(item.title || "").split(".").pop().toLowerCase();
          if (item.kind === "file" || item.kind === "attachment") return ({ json: "JSON", pdf: "PDF", txt: "Текст", md: "Markdown" })[suffix] || "Файл";
          return ({ html_workspace: "HTML", markdown: "Markdown", plan_document: "План", task_list: "Задачи",
            image: "Изображение", sequence: "Коллекция" })[item.kind] || item.kind || "Файл";
        }
        return item.title || "";
      }
      items.sort(function (left, right) {
        var a = sortValue(left), b = sortValue(right);
        var order = typeof a === "number" ? a - b : String(a).localeCompare(String(b), "ru", { sensitivity: "base" });
        return (descending ? -order : order) || String(left.resourceUri).localeCompare(String(right.resourceUri));
      });
      return { chatId: activeChatId, sessionRevision: revision, documentId: "preview-document",
        documents: [{ id: "preview-document", title: fixtures.document }], items: items, nextCursor: null };
    }
    if (type === "changeArtifactLink") {
      requireChat(payload);
      if (payload.expectedSessionRevision !== revision) fail("Список материалов изменился.", "preview_revision_changed");
      var linkedItem = artifactExamples.find(function (item) { return item.resourceUri === payload.resourceUri && item.id !== "sequence"; });
      if (!linkedItem) fail("Материал недоступен.", "preview_revision_changed");
      linkedItem.linked = !payload.detached;
      revision += 1;
      return chatState();
    }
    if (type === "deleteDocumentArtifact") {
      requireChat(payload);
      if (payload.expectedSessionRevision !== revision || payload.confirmed !== true)
        fail("Подтвердите удаление из актуального списка материалов.", "preview_revision_changed");
      var removeIndex = artifactExamples.findIndex(function (item) { return item.resourceUri === payload.resourceUri && item.id !== "sequence"; });
      if (removeIndex < 0) fail("Материал недоступен.", "preview_revision_changed");
      removedArtifactUris.push(artifactExamples[removeIndex].resourceUri);
      artifactExamples.splice(removeIndex, 1);
      revision += 1;
      return chatState();
    }
    if (type === "exportArtifact") {
      requireChat(payload);
      var example = artifactExamples.find(function (item) { return item.resourceUri === payload.resourceUri; });
      if (!example || payload.documentId !== (example.id === "sequence" ? "preview-other-document" : "preview-document"))
        fail("Материал примера изменился.", "preview_revision_changed");
      var body = example.body;
      if (example.assetUrl) {
        var asset = await nativeFetch(example.assetUrl);
        if (!asset.ok) fail("Файл демонстрационного материала недоступен.");
        body = new Uint8Array(await asset.arrayBuffer());
      }
      var artifactData = download(body, example.contentType);
      artifactData.payload.sha256 = await hash(body);
      return { kind: example.kind, contentType: example.contentType,
        fileName: example.title, data: artifactData };
    }
    if (type === "readArtifactImage") {
      requireChat(payload);
      var imageExample = artifactExamples.find(function (item) { return item.id === "image" && item.resourceUri === payload.resourceUri; });
      if (!imageExample) fail("Версия изображения примера недоступна.", "preview_revision_changed");
      var imageData = await viewerBinary(imageExample.resourceUri, imageExample.assetUrl, imageExample.contentType);
      return { resourceUri: imageExample.resourceUri, viewerKind: "image", title: imageExample.title,
        mimeType: imageExample.contentType, contentSha256: imageData.binary.payload.sha256,
        byteLength: imageData.binary.payload.byteLength, data: imageData };
    }
    if (type === "readArtifactPdfInfo" || type === "readArtifactPdfPage" || type === "readArtifactPdfThumbnail") {
      requireChat(payload);
      var pdfExample = artifactExamples.find(function (item) { return item.id === "pdf" && item.resourceUri === payload.resourceUri; });
      if (!pdfExample || type !== "readArtifactPdfInfo" && payload.pageIndex !== 0)
        fail("Версия PDF примера недоступна.", "preview_revision_changed");
      var pdfAsset = await nativeFetch(pdfExample.assetUrl);
      if (!pdfAsset.ok) fail("Файл PDF примера недоступен.");
      var pdfBytes = new Uint8Array(await pdfAsset.arrayBuffer()), pdfHash = await hash(pdfBytes);
      if (type === "readArtifactPdfInfo") return { resourceUri: pdfExample.resourceUri, viewerKind: "pdf",
        title: pdfExample.title, mimeType: "application/pdf", contentSha256: pdfHash,
        byteLength: pdfBytes.length, pageCount: 1, pageTextLengths: [pdfText.length],
        extractedTextSha256: await hash(pdfText), extractedCharacters: pdfText.length,
        textTruncated: false, extractionWarning: "" };
      var thumbnail = type === "readArtifactPdfThumbnail";
      var pdfImage = await viewerBinary(pdfExample.resourceUri,
        thumbnail ? "/preview/assets/sales-methodology-thumb.jpg" : "/preview/assets/sales-methodology-page.jpg", "image/jpeg");
      return { resourceUri: pdfExample.resourceUri, viewerKind: "pdf", contentSha256: pdfHash,
        pageIndex: 0, pageCount: 1, width: thumbnail ? 213 : 990, height: thumbnail ? 300 : 1400,
        imageMimeType: "image/jpeg", imageContentSha256: pdfImage.binary.payload.sha256,
        imageByteLength: pdfImage.binary.payload.byteLength, data: pdfImage };
    }
    if (type === "readArtifactViewerPage") {
      requireChat(payload);
      var viewed = artifactExamples.find(function (item) { return item.resourceUri === payload.resourceUri; });
      if (!viewed || viewed.id !== "report" && viewed.id !== "pdf" || payload.cursor)
        fail("Версия материала примера недоступна.", "preview_revision_changed");
      var viewerId = leaseId();
      var reference = { uri: viewed.resourceUri, revision: "1" };
      var viewerText = viewed.id === "pdf" ? pdfText : viewed.body;
      viewerBatches[viewerId] = { resource: reference, view: "text", text: viewerText,
        offset: 0, nextOffset: viewerText.length, done: true };
      return { resourceUri: viewed.resourceUri, viewerKind: viewed.id === "pdf" ? "pdf" : "markdown", title: viewed.title,
        mimeType: viewed.contentType, contentSha256: await hash(viewerText),
        data: { leaseId: viewerId, url: "https://rnassistant.local-resource/v1/" + viewerId,
          descriptor: { reference: reference }, view: "text",
          expiresUtc: new Date(Date.now() + 600000).toISOString(),
          maxBatchItems: 32000, maxBatchBytes: 8 * 1024 * 1024 },
        offset: 0, returnedCharacters: viewerText.length, totalCharacters: viewerText.length,
        nextCursor: null, complete: true, truncated: false, sourceComplete: true,
        fullReadAllowed: true, viewerLimitReached: false, maximumDocumentCharacters: 1000000 };
    }
    if (type === "getRunChanges") {
      requireChat(payload);
      if (!messages.some(function (item) { return item.RunId === payload.runId && item.RunViewState; }))
        fail("Запуск примера отсутствует.", "preview_run_changed");
      return { chatId: activeChatId, runId: payload.runId, complete: true, items: [] };
    }
    if (type === "inspectPromptContext" && !payload.includeRaw) {
      requireChat(payload);
      var contextParts = [
        ["history", "История чата", 2800, fixtures.chat.user],
        ["current_request", "Текущий запрос", 200, payload.text || "Черновик пуст"],
        ["instructions", "Инструкции", 900, "Демонстрационные правила помощника"],
        ["tools", "Инструменты", 1200, "Поиск ресурсов, чтение таблиц, форматирование диапазонов"],
        ["document_context", "Ресурсы", 700, "Демонстрационный фрагмент таблицы продаж"],
        ["response_format", "Формат ответа", 200, "message, action, tool_calls"]
      ];
      return { chatId: activeChatId, sessionRevision: revision, estimated: true, usedTokens: 6000,
        admissionTokens: 6500, inputLimitTokens: 28000, contextWindowTokens: 32000, reservedOutputTokens: 3500,
        safetyTokens: 500, remainingInputTokens: 21500, generatedUtc: new Date().toISOString(),
        lastPromptTokens: 6200, lastCompletionTokens: 240, lastTotalTokens: 6440, rawData: null,
        notice: "Демонстрационная оценка; модель и compiler в preview не запускаются.",
        sections: contextParts.map(function (part) { return { id: part[0], title: part[1], tokens: part[2], count: 1, included: true,
          items: [{ id: part[0] + "-item", title: part[1], tokens: part[2], preview: part[3], reason: "Демонстрационный состав" }] }; }) };
    }
    if (type === "getChatTrajectory" || type === "getChatEventPayload") {
      if (payload.workspaceId != null) requireProjectChat({ workspaceId: payload.workspaceId, sessionId: payload.chatId });
      else requireChat(payload);
      var journalText = JSON.stringify({ message: "Сохранённый запрос демонстрационного чата", value: 42 });
      if (type === "getChatEventPayload") {
        if (payload.eventId !== "preview-journal-event") fail("Событие не найдено.");
        var journalData = download(journalText, "text/plain; charset=utf-8");
        journalData.payload.sha256 = await hash(bytes(journalText));
        return { chatId: payload.chatId, workspaceId: payload.workspaceId, eventId: payload.eventId,
          sha256: journalData.payload.sha256, byteLength: journalData.payload.byteLength, contentType: "application/json",
          returnedCharacters: journalText.length, textTruncated: false, data: journalData };
      }
      var journalRow = { id: "preview-journal-row", kind: "model.request.prepared", title: "Запрос модели", status: "prepared",
        runId: payload.runId || "current-run", stepId: payload.stepId || "two", createdUtc: "2026-10-25T10:00:00Z",
        firstSequence: 1, lastSequence: 1, sourceEventIds: ["preview-journal-event"], sourceEventSeqs: [1],
        dataJson: journalText, dataTruncated: false, resourceRefs: [] };
      var journalEvent = { eventId: "preview-journal-event", sequence: 1, type: "llm.request", runId: journalRow.runId,
        stepId: journalRow.stepId, createdUtc: journalRow.createdUtc, dataJson: journalText, payloadByteLength: bytes(journalText).length };
      return { chatId: payload.chatId, workspaceId: payload.workspaceId, revision: revision, view: payload.view || "raw",
        events: payload.view === "raw" ? [journalEvent] : [], rows: payload.view === "raw" ? [] : [journalRow],
        totalEvents: 1, totalRows: 1, totalMatches: 1, hasMore: false, nextCursor: null };
    }
    if (type === "getModelContext" || type === "getModelContextPayload" || type === "inspectModelContextPreview") {
      var contextWorkspaceId = payload.workspaceId, contextChatId = payload.chatId;
      if (contextWorkspaceId != null) {
        requireProjectChat({ workspaceId: contextWorkspaceId, sessionId: contextChatId });
        if (type === "inspectModelContextPreview") fail("Предпросмотр запроса проекта недоступен.");
      } else requireChat(payload);
      var requestEvent = { eventId: "preview-request", alias: "Q1", sequence: 1, type: "llm.request",
        createdUtc: "2026-10-23T10:00:00Z", hasPayload: true,
        trace: { RunId: "preview-run", StepId: "preview-step", Model: "preview-model", Streaming: false,
          Purpose: "Демонстрационный запрос", ContextMessages: [] } };
      var responseEvent = { eventId: "preview-response", sequence: 2, type: "llm.response",
        createdUtc: "2026-10-23T10:00:01Z", hasPayload: true, trace: {} };
      if (payload.requestEventId && payload.requestEventId !== requestEvent.eventId || payload.alias && payload.alias !== "Q1")
        fail("Запрос примера не найден.", "preview_request_missing");
      if (type === "getModelContext") return { chatId: contextChatId, workspaceId: contextWorkspaceId, revision: revision,
        unchanged: !payload.requestEventId && payload.knownRevision === revision,
        events: payload.requestEventId || payload.alias ? [requestEvent, responseEvent] : [requestEvent],
        selectedAlias: payload.alias || null, resources: [], operations: [], evidence: [], nextBeforeSequence: null };
      if (type !== "inspectModelContextPreview" && [requestEvent.eventId, responseEvent.eventId].indexOf(payload.eventId) < 0)
        fail("Тело запроса примера не найдено.", "preview_request_missing");
      var exampleMessages = [{ role: "system", content: "Демонстрационный запрос. Реальная модель в preview не вызывается." },
        { role: "user", content: fixtures.chat.user },
        { role: "assistant", content: "Сравню показатели продаж за два месяца." },
        { role: "tool", content: "Показатель | Сентябрь | К августу\n--- | --- | ---\nВыручка | 12,8 млн ₽ | +18,4%\nЗаказы | 1248 | +13%\nСредний чек | 10 256 ₽ | +4,8%" },
        { role: "user", content: "Сначала покажи основные изменения, затем поясни возможные причины." }];
      if (type === "inspectModelContextPreview" && payload.text) exampleMessages.push({ role: "user", content: payload.text });
      var exampleText = JSON.stringify(type === "inspectModelContextPreview" || payload.eventId === requestEvent.eventId ? { model: "preview-model", messages: exampleMessages } :
        { Content: JSON.stringify({ action: "finish", reason: "Показатели двух месяцев сопоставлены.", results: [],
          message: "Выручка выросла на **18,4%**. Основной вклад дали количество заказов и средний чек." }) });
      var exampleData = download(exampleText, "application/json"); exampleData.payload.sha256 = await hash(exampleText);
      if (type === "inspectModelContextPreview") return { chatId: activeChatId, rawData: exampleData,
        generatedUtc: new Date().toISOString(), resourceContextReceipt: { Messages: [] } };
      return { chatId: contextChatId, workspaceId: contextWorkspaceId, eventId: payload.eventId, data: exampleData };
    }
    if (type === "getAgentArsenal") {
      requireChat(payload);
      var selectedSkills = skills.map(function (skill) {
        return { Id: skill.id, Name: skill.name || skill.id, Description: skill.description || "Анализ данных в связанной книге",
          Status: "selected", Tools: ["excel.read_table"], References: [], Revision: skill.revision };
      });
      var availableTools = [
        { Id: "core.find", Name: "Поиск ресурсов", Description: "Поиск материалов чата" },
        { Id: "excel.read_table", Name: "Чтение таблицы", Description: "Чтение данных Excel" },
        { Id: "excel.range.format", Name: "Форматирование диапазона", Description: "Изменение документа", RequiresConfirmation: true }
      ];
      return { type: "rnassistant.agentArsenal", contractVersion: 1, chatId: activeChatId,
        revision: revision, historical: true, prepared: false, pending: false, restriction: "",
        skillCount: selectedSkills.length, toolCount: availableTools.length,
        details: { Policy: "agent/confirm", Selected: selectedSkills, Tools: availableTools, Available: [] } };
    }
    if (type === "getVbaProject") return { result: { Success: true, Message: "VBA-проект примера загружен.",
      DataJson: JSON.stringify({ modules: [{ name: "FormatSales", type: "StdModule",
        code: fixtures.vba, lineCount: fixtures.vba.trimEnd().split("\n").length },
      { name: "ThisWorkbook", type: "Document", code: "Option Explicit\n", lineCount: 1 }] }) }, backups: [] };
    if (type === "selectChat") {
      if (!chats.some(function (chat) { return chat.Id === payload.chatId; })) fail("Чат примера отсутствует.", "preview_chat_changed");
      activeChatId = payload.chatId; messages = messagesByChat[activeChatId];
      return chatState();
    }
    if (type === "createChat" || type === "createDocumentChat") {
      activeChatId = "preview-chat-" + (++chatSequence); messages = [];
      messagesByChat[activeChatId] = messages;
      chats.unshift({ Id: activeChatId, Title: payload.title || "Новый чат", Host: "Excel",
        DocumentKey: "preview-document", DocumentTitle: fixtures.document, IsCurrentDocument: true,
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
      delete downloads[payload.leaseId]; delete viewerBatches[payload.leaseId]; delete uploads[payload.leaseId];
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
    var offset = Number(address.searchParams.get("offset"));
    var count = Number(address.searchParams.get(address.searchParams.has("count") ? "count" : "limit"));
    if (!Number.isSafeInteger(offset) || offset < 0 || !Number.isSafeInteger(count) || count < 1)
      return Promise.resolve(new Response("", { status: 400 }));
    if (viewerBatches[id] && address.pathname === "/v1/" + id) {
      if (offset !== 0 || count !== 32000) return Promise.resolve(new Response("", { status: 404 }));
      return Promise.resolve(new Response(JSON.stringify(viewerBatches[id]),
        { headers: { "Content-Type": "application/json" } }));
    }
    if (address.pathname.includes("/download/") || downloads[id] && address.pathname === "/v1/" + id) {
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
        return dispatch(request.type, request.payload || {}, request.id);
      }).then(function (payload) {
        emit({ id: request.id, ok: true, payload: payload });
      }, function (error) {
        emit({ id: request.id, ok: false, error: error.message,
          errorDetail: error.message, errorCode: error.code || "preview_failed" });
      });
    }
  };
}());
