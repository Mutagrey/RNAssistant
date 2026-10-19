(function () {
  "use strict";
  var P = window.Prototype, E = P.escape, I = P.icon, B = P.button, G = P.go;
  var initial = { title: P.fixtures.chat.title, document: P.fixtures.document, subtitle: "Сравнение периодов · 12:42", draft: "", messages: P.sampleMessages(), plan: false, status: "ready", empty: false, usage: { input: 24600, limit: 128000 } };
  var S = P.state = {
    route: "chat/main", review: "app", theme: "light", scenario: "ready", standalone: false,
    chatId: "sales", chats: { sales: Object.assign({}, initial), summary: { title: P.fixtures.officeExamples[0].chats[1].title, subtitle: "3 региона · 11:18", draft: "", messages: [], plan: false, status: "ready", empty: true } },
    document: P.fixtures.document, attachments: ["Продажи · A1:F1249"], fields: {}, drafts: {}, dirty: {}, saved: {},
    vbaModule: "FormatSales", requestPage: 3, removedMaterials: [], processing: "running", sourceSelection: [true, true, true],
    mappings: [["Регион", "region"], ["Выручка", "revenue"], ["Месяц", "period"]],
    snapshots: [{ name: "Черновик · 17 октября, 12:30", time: "Сегодня, 12:30", body: P.fixtures.template }],
    templateValid: true, processingIndices: [0, 1, 2],
    references: ["examples.md"], parameters: [["region", "Регион", "Строка", "—"], ["revenue", "Выручка", "Число", "₽"], ["period", "Период", "Дата", "Месяц"]]
  };
  P.initSettings(S);
  P.initAppearance(S);
  P.initMaterials(S);
  P.initWorkspaceTree(S);
  P.initNavigation(S);
  P.edits.init();
  var routes = P.map.flatMap(function (g) { return g.nodes.map(function (n) { return n[2]; }); });
  var dialog = document.getElementById("dialog"), toastTimer, runTimers = {}, restoreFocus, confirmCallback, nextFocus;


  function chat() { return S.chats[S.chatId]; }
  function announce(text) { document.getElementById("announcer").textContent = text; }
  function toast(text) { var el = document.getElementById("toast"); el.textContent = text; el.hidden = false; clearTimeout(toastTimer); toastTimer = setTimeout(function () { el.hidden = true; }, 3800); }
  function navigation() { return P.renderNavigation(); }
  function refreshSidebar() {
    P.productTree.unmount();
    document.getElementById("section-rail").innerHTML = P.renderSectionRail();
    document.getElementById("global-nav").innerHTML = '<div class="navigation-content">' + navigation() + '</div>';
    document.querySelector("#global-nav .workspace-heading").insertAdjacentHTML("afterbegin", P.iconButton("Свернуть навигацию", "navigation", "panelLeft", "nav-collapse"));
    P.productTree.mount();
  }
  function bindChat(id) {
    S.chatId = id;
    var c = chat(); c.unread = false;
    S.workspaceScope = c.scope || "office";
    S.lastScopeChat[S.workspaceScope] = id;
    S.attachments = c.attachments || (c.attachments = []);
    if (c.scope !== "projects") S.document = c.document || P.fixtures.document;
    S.scenario = c.offline ? "offline" : "ready";
    if (c.parent) S.collapsedParents[c.parent] = false;
  }
  function selectRemainingChat() {
    var next = Object.keys(S.chats).find(function (id) { return S.chats[id].scope === S.workspaceScope; });
    if (!next) {
      next = "unbound-" + Date.now();
      S.chats[next] = { title: "Новый чат", scope: S.workspaceScope, parent: null, draft: "", messages: [], attachments: [], plan: false, status: "ready", empty: true };
    }
    bindChat(next); navigate("chat/main");
  }
  function renameTreeChat(id) {
    S.renameTarget = id;
    showDialog("Название чата", P.field("Название", S.chats[id].title, "renameChat"), B("Отмена", "close-dialog") + B("Переименовать", "apply-chat-name", "check", "primary"));
  }
  function deleteTreeChat(id) {
    if (S.chats[id].status === "running") { toast("Сначала остановите выполнение."); return; }
    confirm("Удалить чат «" + S.chats[id].title + "»?", "Удалится демонстрационная история этого разговора. Документ и другие чаты останутся.", "Удалить чат", function () { delete S.chats[id]; if (S.chatId === id) selectRemainingChat(); else render(false); }, true);
  }
  function render(focusMain) {
    var surface = document.getElementById(S.review === "app" ? "main" : "review-content"), motionBefore = P.motion.capture(surface);
    var previousInput = document.getElementById("chat-input");
    var composerFocus = previousInput && previousInput === document.activeElement ? { start: previousInput.selectionStart, end: previousInput.selectionEnd } : null;
    P.chatScroll.capture();
    P.productComponents.unmount();
    P.applyAppearance();
    document.getElementById("theme-toggle").innerHTML = I(S.effectiveTheme === "light" ? "moon" : "sun");
    document.getElementById("theme-toggle").setAttribute("aria-label", S.effectiveTheme === "light" ? "Включить тёмную тему" : "Включить светлую тему");
    document.querySelectorAll("[data-review]").forEach(function (button) { button.setAttribute("aria-pressed", String(button.dataset.review === S.review)); });
    document.getElementById("scenario").value = S.scenario;
    P.refreshDemoControls();
    document.getElementById("app-shell").hidden = S.review !== "app";
    document.getElementById("review-content").hidden = S.review === "app";
    if (S.review === "app") {
      S.sectionRoutes[P.activeSection()] = S.route;
      refreshSidebar();
      document.getElementById("global-nav").hidden = S.standalone && S.route.startsWith("projects/");
      document.getElementById("section-rail").hidden = S.standalone && S.route.startsWith("projects/");
      document.getElementById("main").innerHTML = P.renderScreen();
      P.edits.capture(document.getElementById("main"));
      P.layout.refresh();
      P.chatScroll.restore();
    } else document.getElementById("review-content").innerHTML = ({ map: P.renderMap, system: P.renderSystem, handoff: P.renderHandoff }[S.review])();
    document.querySelectorAll("[data-field]").forEach(function (input) {
      var value = S.fields[input.dataset.field];
      if (value != null) { if (input.type === "checkbox") input.checked = value; else input.value = value; }
    });
    if (S.review === "app") { P.edits.sync(); P.productComponents.mount(); P.resizeComposer(); }
    P.motion.render(surface, motionBefore, S.review + "/" + (S.route.startsWith("chat/") ? "chat/" + S.chatId : S.route));
    if (focusMain) document.getElementById(S.review === "app" ? "main" : "review-content").focus({ preventScroll: true });
    if (!focusMain && !nextFocus && composerFocus) { var input = document.getElementById("chat-input"); if (input) { input.focus({ preventScroll: true }); input.setSelectionRange(composerFocus.start, composerFocus.end); } }
    if (nextFocus) {
      var target = document.querySelector(nextFocus), panelBack = document.querySelector('.inspector-compact-back'); nextFocus = null;
      if (target && target.dataset.action === "toggle-inspector" && panelBack && getComputedStyle(panelBack).display !== "none") target = panelBack;
      if (target) target.focus({ preventScroll: true });
    }
    if (S.openNavigationAfterNavigate) { S.openNavigationAfterNavigate = false; P.layout.setNavigation(true, true); }
  }
  P.refreshSidebar = refreshSidebar;
  P.isChatVisible = chatIsVisible;
  P.openChat = function (id) { bindChat(id); navigate("chat/main"); };
  P.refreshDemoControls = function () { document.getElementById("chat-demo-controls").innerHTML = P.demo.renderControls(); };
  P.refreshDemo = function (id) {
    P.refreshDemoControls(); refreshSidebar();
    if (chatIsVisible(id)) {
      var transcript = document.querySelector(".transcript");
      if (!transcript || !document.querySelector('.conversation-scroll[data-chat="' + id + '"]')) { render(false); return; }
      var focused = document.activeElement.closest("[data-disclosure]");
      var focusKey = transcript.contains(document.activeElement) && focused ? focused.dataset.disclosure : null;
      var surface = document.getElementById("main"), motionBefore = P.motion.capture(surface);
      P.chatScroll.capture(); transcript.innerHTML = P.renderChatMessages(); P.diffUI.mount(transcript); P.chatScroll.restore(true);
      P.motion.render(surface, motionBefore, surface.dataset.motionScene);
      if (focusKey) { var restored = Array.from(transcript.querySelectorAll("[data-disclosure]")).find(function (el) { return el.dataset.disclosure === focusKey; }); if (restored) (restored.closest('.run-history:not([open])') || restored).querySelector("summary").focus({ preventScroll: true }); }
      var send = document.querySelector(".composer .send-btn");
      if (send) send.outerHTML = chat().status === "running" ? P.iconButton("Остановить выполнение", "stop-run", "stop", "send-btn") : '<button type="submit" class="icon-btn send-btn" aria-label="Отправить сообщение">' + I("arrow") + '</button>';
    }
  };
  P.refresh = function () { render(false); };
  function applyRoute(route) {
    if (!routes.includes(route) && !P.materials().some(function (item) { return route === "materials/" + item.id; })) { route = "chat/main"; toast("Этот экран не найден. Открыт чат."); }
    if (route.startsWith("materials/") && S.removedMaterials.includes(route.split("/")[1])) route = "materials/all";
    var material = P.materials().find(function (item) { return route === "materials/" + item.id; });
    if (material) { if (S.materialId !== material.id) S.materialFile = "index.html"; S.materialId = material.id; }
    S.route = route; S.review = "app";
    if (route.startsWith("chat/")) {
      chat().unread = false;
      var inspector = route.split("/")[1];
      if (P.inspectorTitles[inspector]) chat().inspector = inspector;
    }
    if (route.startsWith("projects/")) S.workspaceScope = "projects";
    if (route === "chat/project" || route === "chat/list") {
      var scope = route === "chat/project" ? "projects" : "office";
      var candidate = S.lastScopeChat[scope];
      if (!S.chats[candidate]) candidate = Object.keys(S.chats).find(function (id) { return S.chats[id].scope === scope; });
      if (candidate) bindChat(candidate);
    }
    if (!route.startsWith("projects/")) S.standalone = false;
    if (route === "projects/standalone") S.standalone = true;
    if (route === "chat/new") { createChat(false); }
    if (route === "chat/plan") { chat().plan = true; chat().status = "ready"; S.scenario = "ready"; }
    if (route === "chat/approval") { chat().plan = false; chat().status = "approval"; chat().runDocument = S.document; S.scenario = "approval"; }
    else if (route === "chat/offline") S.scenario = "offline";
    else if (S.scenario === "offline" && route.startsWith("chat/") && !chat().offline) S.scenario = "ready";
    render(true);
    if (route === "chat/list" && document.getElementById("stage").clientWidth <= 820) P.layout.setNavigation(true, true);
    announce("Открыто: " + (P.map.flatMap(function (g) { return g.nodes; }).find(function (n) { return n[2] === route; }) || ["", route])[1]);
  }
  function navigate(route) {
    closeDialog();
    P.layout.closeCompactNavigation();
    if (location.hash === "#/" + route) applyRoute(route);
    else location.hash = "/" + route;
  }
  function closeDialog() { P.productTree.unmount(dialog); if (dialog.open) dialog.close(); }
  function showDialog(title, content, footer) {
    if (!dialog.open) restoreFocus = document.activeElement;
    P.productTree.unmount(dialog);
    delete dialog.dataset.view;
    document.getElementById("dialog-body").innerHTML = '<div class="dialog-head"><h2 id="dialog-title">' + E(title) + '</h2>' + P.iconButton("Закрыть диалог", "close-dialog", "close") + '</div><div class="dialog-content">' + content + '</div>' + (footer ? '<div class="dialog-footer">' + footer + '</div>' : "");
    if (!dialog.open) dialog.showModal();
    P.productTree.mount(dialog);
  }
  function confirm(title, text, label, callback, danger) {
    confirmCallback = callback;
    showDialog(title, '<p>' + E(text) + '</p>', B("Отмена", "close-dialog") + B(label, "confirm", danger ? "trash" : "check", danger ? "danger" : "primary"));
  }
  dialog.addEventListener("close", function () { P.productTree.unmount(dialog); confirmCallback = null; if (restoreFocus && restoreFocus.isConnected) restoreFocus.focus({ preventScroll: true }); });
  dialog.addEventListener("click", function (event) { if (event.target === dialog) { var r = dialog.getBoundingClientRect(); if (event.clientX < r.left || event.clientX > r.right || event.clientY < r.top || event.clientY > r.bottom) closeDialog(); } });

  function download(name, body, mime) {
    var url = URL.createObjectURL(new Blob([body], { type: mime || "text/plain;charset=utf-8" })), a = document.createElement("a");
    a.href = url; a.download = name; document.body.appendChild(a); a.click(); a.remove(); setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
    toast("Скачана демонстрационная копия: " + name);
  }
  function editorKey() {
    if (S.route.startsWith("vba/")) return "vba:" + S.vbaModule;
    if (S.route.startsWith("materials/")) return P.materialKey();
    if (S.route.startsWith("projects/")) return "template";
    if (S.route === "library/schema") return "tool-schema";
    if (S.route === "library/implementation") return "tool-code";
    if (S.route === "library/prompt") return "prompt";
    return ["library/tool", "library/manage", "library/test", "library/docs"].includes(S.route) ? "tool" : "skill";
  }
  function saveEditor() {
    var key = editorKey();
    if (!P.edits.pending(key)) return;
    if (S.route.startsWith("materials/")) {
      if (!P.materialEditable(P.selectedMaterial())) return;
      if (P.selectedMaterial().type === "JSON") { try { JSON.parse(P.materialSource()); } catch (_) { toast("JSON содержит ошибку. Исправьте исходник перед сохранением."); return; } }
    }
    if (S.route.startsWith("materials/")) {
      var item = P.selectedMaterial(), source = P.materialSource(), previous = P.materialVersions()[P.materialVersions().length - 1];
      if (!previous || previous.body !== source) P.materialVersions().push({ body: source, note: "Сохранено в этой вкладке" });
    }
    P.edits.commit(key);
    render(false); toast("Изменения сохранены в демонстрационной копии этой вкладки.");
  }
  function createChat(go, parentId) {
    var parent = S.parents.find(function (p) { return p.id === (parentId || chat().parent) && p.scope === S.workspaceScope; }) || S.parents.find(function (p) { return p.scope === S.workspaceScope; });
    if (!parent) { showDialog("Сначала выберите рабочую область", '<p>Добавьте документ Office или откройте папку проекта. Затем можно создать связанный чат.</p>', S.workspaceScope === "office" ? B("Создать документ Excel", "launch-office:Excel", "plus") : G("Открыть проект", "projects/open", "folder")); return; }
    var id = "new-" + Date.now();
    S.chats[id] = { title: "Новый чат", order: ++S.chatSequence, parent: parent.id, scope: parent.scope, document: parent.scope === "office" ? parent.name : null, offline: parent.scope === "office" && !parent.open, subtitle: "Без сообщений", draft: "", messages: [], attachments: [], plan: false, status: "ready", empty: true };
    bindChat(id);
    if (go !== false) navigate(chat().offline ? "chat/offline" : "chat/main");
  }
  function chatIsVisible(id) { return S.chatId === id && S.route.startsWith("chat/") && S.review === "app" && !document.hidden; }
  function completeChat(id, text) {
    var current = S.chats[id];
    current.messages.push({ role: "assistant", text: text });
    current.status = "complete"; current.unread = !chatIsVisible(id);
  }
  function forkChat(index) {
    var source = chat(), origin = S.chatId, id = "fork-" + Date.now();
    S.chats[id] = { title: source.title + " · ветка", order: ++S.chatSequence, parent: source.parent, scope: source.scope, document: source.document, offline: source.offline,
      messages: JSON.parse(JSON.stringify(source.messages.slice(0, index + 1))),
      attachments: (source.attachments || []).slice(), model: source.model, reasoning: source.reasoning, plan: source.plan,
      draft: "", status: "ready", forkOf: origin };
    bindChat(id);
  }
  function showCopy(text) { showDialog("Текст сообщения", '<label class="field">Скопируйте текст<textarea readonly>' + E(text) + '</textarea></label>'); }
  function addAttachment(name) { if (!S.attachments.includes(name)) S.attachments.push(name); closeDialog(); render(false); toast("Добавлено в контекст: " + name); }
  function updateResult(id, html, fallback) { var target = document.getElementById(id); if (target) target.innerHTML = html; else toast(fallback || "Демонстрационная проверка завершена."); }
  function setTheme(theme) { P.setAppearance("theme", theme); render(false); }
  function fileText() { if (S.route.startsWith("materials/")) return P.materialSource(); var key = editorKey(); if (S.drafts[key] != null) return S.drafts[key]; if (S.saved[key] != null) return S.saved[key]; return key.startsWith("vba:") || key === "tool-code" ? P.fixtures.vba : key === "template" ? P.fixtures.template : key === "skill" ? P.fixtures.skill : P.fixtures.report; }

  var actions = {
    "start-chat-demo": function () { P.demo.start(); },
    "toggle-chat-demo": function () { P.demo.toggle(); },
    "next-chat-demo": function () { P.demo.next(); },
    "scroll-chat-bottom": function () { P.chatScroll.bottom(); },
    "navigation": function () { P.layout.setNavigation(!document.getElementById("app-shell").classList.contains("navigation-open"), true); },
    "close-dialog": closeDialog,
    "confirm": function () { var callback = confirmCallback; closeDialog(); confirmCallback = null; if (callback) callback(); },
    "open-project-dialog": function () { navigate("projects/open"); },
    "close-inspector": function () { nextFocus = '[data-action="toggle-inspector"]'; navigate("chat/main"); },
    "toggle-inspector": function () { nextFocus = '[data-action="toggle-inspector"]'; navigate(P.inspectorTitles[S.route.split("/")[1]] ? "chat/main" : "chat/" + (chat().inspector || "context")); },
    "new-chat": function () { createChat(true); },
    "new-scoped-chat": function () { createChat(true); },
    "toggle-tree-search": function () {
      S.treeSearchOpen = !S.treeSearchOpen;
      if (!S.treeSearchOpen) S.treeSearch = "";
      refreshSidebar();
      var root = document.getElementById("global-nav");
      root.querySelector(S.treeSearchOpen ? "[data-tree-search]" : "[data-action='toggle-tree-search']").focus();
    },
    "toggle-nav-search": function () {
      var section = P.activeSection(); S.navSearchOpen[section] = !S.navSearchOpen[section];
      if (!S.navSearchOpen[section]) S.navSearch[section] = "";
      refreshSidebar();
      var root = document.getElementById("global-nav");
      root.querySelector(S.navSearchOpen[section] ? "[data-nav-search]" : "[data-action='toggle-nav-search']").focus();
    },
    "rename-chat": function () { renameTreeChat(S.chatId); },
    "apply-chat-name": function () { var input = dialog.querySelector("input"); if (!input.value.trim()) { input.focus(); return; } S.chats[S.renameTarget || S.chatId].title = input.value.trim(); closeDialog(); render(false); },
    "delete-chat": function () { deleteTreeChat(S.chatId); },
    "document": function () { showDialog("Документ чата", '<p>Документ задаёт контекст Office. Проект с файлами выбирается отдельно.</p>' + '<button class="list-row selected" data-action="select-sales-document">' + I("file") + '<span class="row-copy"><strong>Продажи · сентябрь.xlsx</strong><small>Excel · открыт</small></span>' + P.badge("Подключён", "success") + '</button><button class="list-row" data-action="other-document">' + I("file") + '<span class="row-copy"><strong>Бюджет · IV квартал.xlsx</strong><small>Закрыт · история доступна</small></span></button>'); },
    "select-sales-document": function () { S.document = P.fixtures.document; S.scenario = "ready"; if (S.chats.sales) bindChat("sales"); else createChat(false); closeDialog(); render(false); toast("Выбран демонстрационный документ продаж."); },
    "other-document": function () { S.document = "Бюджет · IV квартал.xlsx"; S.scenario = "offline"; if (!S.chats.budget) S.chats.budget = { title: "План бюджета", document: S.document, draft: "", messages: [], plan: false, status: "ready", empty: true, offline: true }; bindChat("budget"); navigate("chat/offline"); },
    "attach-dialog": function () { showDialog("Добавить в контекст", '<div class="form-stack">' + B("Файл с компьютера", "pick-file", "paperclip") + B("Выделение из документа", "attach-selection", "selection") + B("Итоги сентября.md", "attach-material", "file") + '</div>'); },
    "pick-file": function () { closeDialog(); document.getElementById("demo-file").click(); },
    "attach-selection": function () { addAttachment("Выделение · Продажи!A1:F12"); },
    "attach-material": function () { var item = P.selectedMaterial(); if (item.owner === "Все документы") { actions["copy-material"](); item = P.selectedMaterial(); } item.owner = "Этот чат"; addAttachment(item.name); },
    "detach-material": function () { var item = P.selectedMaterial(); confirm("Убрать материал из чата?", "Материал останется в документе. Его можно будет добавить снова.", "Убрать из чата", function () { item.owner = "В документе"; S.attachments = chat().attachments = S.attachments.filter(function (name) { return name !== item.name; }); render(false); }); },
    "copy-material": function () { var original = P.selectedMaterial(), copy = JSON.parse(JSON.stringify(original)); copy.id = "copy-" + Date.now(); copy.name = "Копия · " + original.name; copy.owner = "Этот чат"; copy.source = P.materialSource(); copy.versions = [{ body: copy.source, note: "Независимая копия" }]; if (copy.files) Object.keys(copy.files).forEach(function (path) { copy.files[path] = P.materialFileSource(original, path); copy.fileVersions[path] = [{ body: copy.files[path], note: "Независимая копия" }]; }); S.materials.push(copy); S.materialId = copy.id; navigate("materials/" + copy.id); toast("Создана независимая копия в этом чате."); },
    "material-info": function () { var item = P.selectedMaterial(); showDialog("Сведения о материале", P.table(["Свойство", "Значение"], [["Название", E(item.name)], ["Тип", E(item.type)], ["Принадлежность", E(item.owner)], ["Документ", E(item.owner === "Все документы" ? "Другой документ" : S.document)], ["Версии", String(P.materialVersions().length)], ["Содержимое", "Демонстрационные данные"]]), B("Закрыть", "close-dialog")); },
    "toggle-plan": function () { chat().plan = !chat().plan; nextFocus = '#chat-input'; render(false); },
    "toggle-reasoning": function () { chat().reasoning = !chat().reasoning; nextFocus = '[data-action="toggle-reasoning"]'; render(false); },
    "suggest": function () { chat().draft = "Сравни сентябрь с августом и выдели главное."; render(false); document.getElementById("chat-input").focus(); },
    "cancel-edit": function () { chat().draft = chat().previousDraft || ""; chat().editing = false; render(false); },
    "show-activity": function () { navigate("chat/activity"); },
    "stop-run": function () { if (chat().demo) P.demo.stop(true); clearTimeout(runTimers[S.chatId]); chat().status = "stopped"; S.scenario = "ready"; render(false); announce("Выполнение остановлено"); },
    "approve": function () { completeChat(S.chatId, "Оформление заголовка применено в демонстрации. Реальный документ не изменялся."); S.scenario = "ready"; render(false); toast("Демосценарий: применение и проверка результата показаны как успешные."); },
    "reject": function () { chat().status = "stopped"; S.scenario = "ready"; render(false); toast("Действие отклонено."); },
    "answer-previous": function () { chat().status = "complete"; S.scenario = "ready"; chat().messages.push({ role: "user", text: "Сравнить с августом." }); completeChat(S.chatId, "Сравнение с августом завершено в демонстрации."); render(false); },
    "answer-year": function () { chat().status = "complete"; S.scenario = "ready"; chat().messages.push({ role: "user", text: "Сравнить с сентябрём прошлого года." }); completeChat(S.chatId, "Сравнение с прошлым годом завершено в демонстрации."); render(false); },
    "retry": function () { S.scenario = "ready"; render(false); toast("Демоданные загружены."); },
    "verify": function () { showDialog("Проверка результата", '<p>В демонстрации документ не изменяется. В рабочем интерфейсе здесь будет результат повторного чтения, без повторного исполнения действия.</p>' + P.notice("Статус остаётся неизвестным", "Прототип не может подтвердить внешний эффект.", "warning"), B("Понятно", "close-dialog")); },
    "compact": function () { confirm("Сжать ранний контекст?", "Полная история остаётся доступной. Меняется представление ранних сообщений для следующего запроса.", "Сжать демоконтекст", function () { toast("Демоконтекст сжат. История сохранена."); }); },
    "previous-request": function () { S.requestPage = S.requestPage > 1 ? S.requestPage - 1 : 3; document.getElementById("request-position").textContent = "Просмотр демозапроса № " + S.requestPage; },
    "download-request": function () { download("demo-model-request.json", JSON.stringify({ demo: true, request: S.requestPage, model: P.chatModel(), messages: 7 }, null, 2), "application/json"); },
    "create-material": function () { showDialog("Новый материал", '<div class="form-stack">' + P.field("Название", "Новый материал", "newMaterialName") + '<label class="field">Формат<select id="new-material-format"><option>Markdown / план</option><option>HTML</option><option>CSS</option><option>JavaScript</option><option>JSON</option></select></label></div>', B("Импортировать файл", "pick-file", "upload") + B("Создать", "apply-material", "plus", "primary")); },
    "apply-material": function () {
      var name = dialog.querySelector("input").value.trim(), format = document.getElementById("new-material-format").value;
      if (!name) { dialog.querySelector("input").focus(); return; }
      var id = "new-" + Date.now(), type = format === "Markdown / план" ? "Markdown" : format;
      var source = type === "JSON" ? JSON.stringify({ title: name }, null, 2) : type === "HTML" ? '<!doctype html>\n<h1>' + E(name) + '</h1>' : type === "Markdown" ? "# " + name + "\n\n" : "/* " + name.replace(/\*\//g, "") + " */\n";
      S.materials.push({ id: id, name: name, type: type, source: source, owner: "Этот чат", icon: type === "JSON" ? "braces" : "file", time: "Сейчас", page: 1, versions: [] });
      S.materialId = id; S.drafts[P.materialKey()] = source; S.dirty[P.materialKey()] = true;
      closeDialog(); navigate("materials/code"); toast("Создан черновик «" + name + "».");
    },
    "save-editor": function () { if (S.route.startsWith("vba/")) confirm("Сохранить код модуля?", "Демосценарий применения в «" + S.document + "». Реальное сохранение требует проверки результата.", "Сохранить демокопию", saveEditor); else saveEditor(); },
    "discard-editor": function () { confirm("Отменить изменения?", "Несохранённый текст выбранного демонстрационного объекта будет заменён последней сохранённой копией.", "Отменить изменения", function () { P.edits.discard(editorKey()); render(false); }); },
    "discuss": function () { var name = S.route.startsWith("vba/") ? S.vbaModule + ".bas" : S.route.startsWith("library/") ? "Инструкция из библиотеки" : P.selectedMaterial().name; if (!S.attachments.includes(name)) S.attachments.push(name); chat().draft = "Посмотри «" + name + "» и предложи улучшения."; navigate("chat/main"); },
    "delete-material": function () { confirm("Удалить демоматериал?", "Запись исчезнет из каталога прототипа. Внешние файлы не затрагиваются.", "Удалить", function () { S.removedMaterials.push(S.materialId); delete S.drafts[P.materialKey()]; delete S.dirty[P.materialKey()]; delete S.saved[P.materialKey()]; S.materialId = P.materials()[0] ? P.materials()[0].id : "report"; navigate("materials/all"); }, true); },
    "download-html-preview": function () { var item = P.selectedMaterial(); if (!item.files) return; var html = P.materialFileSource(item, "index.html"), css = P.materialFileSource(item, "styles.css"); download(item.name, '<style>' + css.replace(/<\/style/gi, "< /style") + '</style>' + html, "text/html"); },
    "download-material": function () { var item = P.selectedMaterial(); if (!P.materialEditable(item)) return; var ext = { Markdown: "md", "План": "md", "Текст": "txt", HTML: "html", JSON: "json", CSS: "css", JavaScript: "js" }[item.type]; var name = P.materialFile() || (item.name.endsWith("." + ext) ? item.name : item.name + "." + ext); download(name, P.materialSource(), { html: "text/html", json: "application/json", css: "text/css", js: "text/javascript" }[(P.materialFile() || name).split(".").pop()]); },
    "download": function () { var format = S.fields.exportFormat || "Markdown (.md)"; if (format.startsWith("HTML")) download("demo-report.html", '<!doctype html><html lang="ru"><meta charset="utf-8"><title>Демоотчёт</title><body><pre>' + E(fileText()) + '</pre></body></html>', "text/html"); else download(format.startsWith("Обычный") ? "demo-report.txt" : "demo-report.md", fileText()); },
    "page-prev": function () { P.selectedMaterial().page = Math.max(1, P.selectedMaterial().page - 1); render(false); },
    "page-next": function () { P.selectedMaterial().page = Math.min(3, P.selectedMaterial().page + 1); render(false); },
    "refresh-preview": function () { render(false); toast("Демонстрационный предпросмотр обновлён."); },
    "project-dialog": function () { showDialog("Текущий проект", P.info(P.fixtures.project, S.fields.projectRoot || "C:\\Projects\\Reports", "folder") + '<p>Выбор проекта не меняет документ чата и не подключает его файлы к модели.</p>', G("Другой проект", "projects/open", "folder") + B("Закрыть", "close-dialog")); },
    "open-project": function () { navigate("projects/sources"); toast("Открыт демонстрационный проект."); },
    "create-project": function () { P.fixtures.project = S.fields.projectName || "Новый проект"; navigate("projects/sources"); toast("Создан демопроект в памяти вкладки."); },
    "toggle-standalone": function () { S.standalone = !S.standalone; render(false); },
    "add-sources": function () { showDialog("Добавить источники", '<p>Прототип работает с подготовленными примерами. Выбранные локальные файлы не будут прочитаны или загружены.</p>' + P.info("Москва.xlsx · Казань.xlsx · Самара.xlsx", "2 успешных источника и 1 с ошибкой", "files"), B("Выбрать локальные файлы", "pick-sources", "upload") + B("Добавить примеры", "sample-sources", "plus", "primary")); },
    "pick-sources": function () { document.getElementById("demo-file").dataset.target = "sources"; closeDialog(); document.getElementById("demo-file").click(); },
    "sample-sources": function () { S.scenario = "ready"; S.sourceSelection = [true, true, true]; closeDialog(); render(false); toast("Демонстрационные источники добавлены."); },
    "clear-selection": function () { S.sourceSelection = [false, false, false]; render(false); },
    "save-selection": function () { S.saved.selection = S.sourceSelection.slice(); toast("Подборка сохранена в памяти прототипа."); },
    "process": function () { if (!S.sourceSelection.some(Boolean)) { toast("Выберите хотя бы один источник."); return; } if (S.dirty.template || !S.templateValid) { showDialog("Сначала проверьте шаблон", '<p>Есть непроверенные или несохранённые изменения шаблона. Проверьте JSON и сохраните снимок. Сохранение снимка не доказывает корректность.</p>', G("Открыть шаблон", "projects/templates", "edit", "primary")); return; } S.processingIndices = S.sourceSelection.map(function (v, i) { return v ? i : -1; }).filter(function (i) { return i >= 0; }); S.processing = "running"; navigate("projects/processing"); },
    "pause-processing": function () { S.processing = S.processing === "paused" ? "running" : "paused"; render(false); },
    "cancel-processing": function () { S.processing = "cancelled"; render(false); toast("Очередь демообработки отменена."); },
    "finish-processing": function () { S.processing = "done"; render(false); },
    "source-error": function () { showDialog("Самара.xlsx · ошибка", '<p>На листе «Продажи» не найдена колонка «Выручка». Проверьте заголовки файла или соответствия шаблона.</p>' + P.notice("Файл не обработан", "Результаты Москвы и Казани доступны отдельно.", "warning"), G("Соответствия", "projects/mapping", "edit", "primary") + G("К источникам", "projects/sources", "back")); },
    "validate-template": function () { try { JSON.parse(S.drafts.template == null ? P.fixtures.template : S.drafts.template); S.templateValid = true; updateResult("template-validation", P.notice("Синтаксис JSON корректен", "Демонстрационная проверка не валидирует семантику шаблона.", "success")); } catch (e) { S.templateValid = false; updateResult("template-validation", P.notice("Ошибка JSON", e.message, "danger")); } },
    "save-template": function () { var text = S.drafts.template == null ? P.fixtures.template : S.drafts.template; S.snapshots.unshift({ name: "Черновик · снимок " + (S.snapshots.length + 1), time: "Только что", body: text }); S.saved.template = text; P.edits.commit("template"); P.edits.sync(); toast("Новый снимок демочерновика сохранён. Синтаксическая корректность не требуется для сохранения черновика."); },
    "mapping-dialog": function () { S.editMapping = null; showDialog("Новое соответствие", '<div class="form-stack">' + P.field("Название в источнике", "", "mappingSource") + P.field("Параметр", "revenue", "mappingTarget") + '</div>', B("Отмена", "close-dialog") + B("Применить к демочерновику", "apply-mapping", "check", "primary")); },
    "apply-mapping": function () { var inputs = dialog.querySelectorAll("input"), from = inputs[0].value.trim(), to = inputs[1].value.trim(); if (!from || !to) { inputs[!from ? 0 : 1].focus(); return; } var pair = [from, to]; if (S.editMapping == null) S.mappings.push(pair); else S.mappings[S.editMapping] = pair; S.dirty.template = true; S.templateValid = false; closeDialog(); render(false); toast("Соответствие изменено в демочерновике. Перед обработкой проверьте шаблон."); },
    "download-results": function () { download("demo-results.json", JSON.stringify({ demo: true, rows: [{ region: "Москва", period: "2026-09", revenue: 7420000 }, { region: "Казань", period: "2026-09", revenue: 5380000 }] }, null, 2), "application/json"); },
    "download-job-report": function () { download("demo-processing-report.txt", "Демонстрация\nМосква.xlsx: 128 записей\nКазань.xlsx: 96 записей\nСамара.xlsx: ошибка — нет колонки Выручка\n"); },
    "new-library": function () { showDialog("Создать в библиотеке", '<div class="form-stack">' + G("Навык", "library/skill", "spark") + G("Инструмент", "library/tool", "code") + G("Инструкция", "library/prompt", "book") + '</div><p>В прототипе откроется пример выбранного редактора.</p>'); },
    "clone-library": function () { var key = editorKey(); S.drafts[key] = (S.drafts[key] || P.fixtures.skill) + "\n\nКопия для редактирования.\n"; S.dirty[key] = true; render(false); toast("Открыта демонстрационная копия."); },
    "delete-library": function () { confirm("Удалить демозапись?", "Удалится только локальный черновик прототипа. Пример из каталога останется доступен.", "Удалить черновик", function () { P.edits.discard(editorKey()); navigate("library/all"); }, true); },
    "add-reference": function () { showDialog("Файл навыка", P.field("Имя файла", "examples.md", "referenceName"), B("Добавить пример", "apply-reference", "plus", "primary")); },
    "apply-reference": function () { var name = dialog.querySelector("input").value.trim(); if (!name) return; if (!S.references.includes(name)) S.references.push(name); closeDialog(); render(false); toast("Файл добавлен в демонстрационный навык."); },
    "delete-reference": function () { confirm("Удалить файл?", "Удалится демонстрационный дополнительный файл. Основной SKILL.md сохраняется.", "Удалить", function () { S.references.shift(); render(false); }, true); },
    "package-components": function () { showDialog("Состав пакета", P.info("FormatSales.bas", "Стандартный модуль", "code") + P.info("SalesForm.frm", "Code-only UserForm · без FRX", "code"), B("Закрыть", "close-dialog")); },
    "dry-run": function () { updateResult("test-result", P.notice("Аргументы корректны", "Демопроверка формы. Исполнение не запускалось.", "success")); },
    "tool-run": function () { confirm("Запустить инструмент?", "Демо: изменить оформление A1:F1 в документе «" + S.document + "».", "Запустить демосценарий", function () { updateResult("test-result", P.notice("Демосценарий завершён", "В рабочем продукте успех требует read-back. Реальный документ здесь не изменялся.", "success")); }); },
    "install-tool": function () { confirm("Установить инструмент?", "Цель: " + S.document + ". В прототипе установка только моделируется.", "Установить демопакет", function () { toast("Демонстрационная установка завершена."); }); },
    "uninstall-tool": function () { confirm("Удалить установку?", "Изменится состав VBA связанного документа. Прототип не выполняет это действие.", "Показать удаление", function () { toast("Демосценарий удаления установки завершён."); }, true); },
    "refresh-vba": function () { if (S.dirty["vba:" + S.vbaModule]) confirm("Обновить проект?", "Черновик модуля останется в памяти прототипа; данные списка будут перечитаны в рабочем приложении.", "Обновить", function () { render(false); }); else { render(false); toast("Демонстрационный список модулей обновлён."); } },
    "vba-run": function () { confirm("Запустить макрос?", "Процедура FormatSalesReport в «" + S.document + "». Успешный вызов сам по себе не подтверждает эффект.", "Запустить демосценарий", function () { updateResult("test-result", P.notice("Результат не подтверждён", "Пример возможного внешнего эффекта без read-back. Не повторяйте автоматически.", "warning", B("Проверить состояние", "verify", "search"))); }); },
    "restore-vba": function () { confirm("Восстановить код модуля?", "В рабочем продукте сначала сохраняется резервная копия текущего состояния, затем проверяется восстановление.", "Восстановить демокод", function () { S.drafts["vba:" + S.vbaModule] = P.fixtures.vba; S.dirty["vba:" + S.vbaModule] = false; navigate("vba/code"); }); },
    "save-settings": function () { var error = P.validateSettings(); if (error) { toast(error); return; } S.settingsSaved = P.settingsPatch(); S.settingsBaseline = JSON.parse(JSON.stringify(S.settingsDraft)); toast("Настройки применены к демонстрационной копии."); render(false); },
    "test-connection": function () { updateResult("connection-result", P.notice("Подключение к хосту требуется", "Проверка API будет доступна в рабочем приложении. Сетевой запрос не выполнялся.", "")); },
    "refresh-models": function () { toast("Каталог демонстрационных моделей обновлён."); },
    "compatibility": function () { showDialog("Совместимость модели", P.notice("Демонстрационная проверка", "Protocol v7 · tools · structured response. Реальная квалификация не выполнялась.", ""), B("Закрыть", "close-dialog")); },
    "theme-light": function () { setTheme("light"); },
    "theme-dark": function () { setTheme("dark"); },
    "download-diagnostics": function () { download("demo-diagnostics.json", JSON.stringify({ demo: true, document: S.document, events: [{ time: "12:42:08", type: "material.created" }] }, null, 2), "application/json"); },
    "audit-storage": function () { updateResult("storage-result", P.notice("Демопроверка завершена", "Пример: 0 повреждённых объектов, 2 недостижимых. Реальное хранилище не проверялось.", "success")); },
    "collect-storage": function () { confirm("Очистить демохранилище?", "Рабочая очистка требует fail-closed проверки достижимости. Здесь только демонстрация подтверждения.", "Показать очистку", function () { updateResult("storage-result", P.notice("Демосценарий очистки завершён", "Пользовательские данные не затронуты.", "success")); }, true); },
    "reset-demo": function () { confirm("Сбросить демонстрационные данные?", "Черновики этой вкладки будут потеряны. Внешние файлы не меняются.", "Сбросить", function () { S.dirty = {}; window.location.reload(); }, true); },
    "component-save": function () { toast("Пример: изменения сохранены."); },
    "component-cancel": function () { toast("Пример: редактирование отменено."); },
    "component-more": function () { showDialog("Дополнительные действия", '<div class="form-stack">' + B("Создать копию", "component-save", "copy") + B("Скачать пример", "download", "download") + '</div>'); },
    "component-delete": function () { confirm("Удалить пример?", "Проверка общего компонента подтверждения.", "Удалить", function () { toast("Демонстрационный объект удалён."); }, true); },
    "component-verify": function () { actions.verify(); }
  };

  P.dispatchAction = function (name) { action(name); };
  P.activateTreeItem = function (data) {
    if (data.chat) { bindChat(data.chat); navigate(chat().offline ? "chat/offline" : "chat/main"); }
    else if (data.module) { S.vbaModule = data.module; navigate("vba/code"); }
    else if (data.materialFile) { S.materialId = data.materialId; S.materialFile = data.materialFile; navigate("materials/code"); }
    else if (data.route) navigate(data.route);
  };
  function action(name) {
    document.querySelectorAll("details.popover[open]").forEach(function (menu) { menu.open = false; });
    if (actions[name]) { actions[name](); return; }
    var parts = name.split(":"), kind = parts[0], value = parts.slice(1).join(":");
    if (kind === "material-file") { S.materialFile = decodeURIComponent(value); nextFocus = '.material-file-picker > summary'; render(false); return; }
    if (kind === "appearance") { P.setAppearance(parts[1], parts[2]); nextFocus = '[data-action="' + name + '"]'; render(false); return; }
    if (name === "appearance-example") { toast("Образец выбранного оформления."); return; }
    if (kind === "parent-new") { var parent = S.parents.find(function (p) { return p.id === value; }); S.workspaceScope = parent.scope; createChat(true, value); return; }
    if (kind === "rename-tree-chat") { renameTreeChat(value); return; }
    if (kind === "delete-tree-chat") { deleteTreeChat(value); return; }
    if (kind === "parent-open") {
      var parent = S.parents.find(function (p) { return p.id === value; });
      if (parent.scope === "projects") { P.fixtures.project = parent.name; S.fields.projectRoot = parent.path; navigate("projects/sources"); }
      else {
        var old = P.chatParent(); if (old && old.id !== parent.id) S.collapsedParents[old.id] = true;
        parent.open = true;
        Object.keys(S.chats).forEach(function (id) { if (S.chats[id].parent === parent.id) S.chats[id].offline = false; });
        var next = Object.keys(S.chats).find(function (id) { return S.chats[id].parent === parent.id; });
        if (next) { bindChat(next); navigate("chat/main"); } else { S.workspaceScope = "office"; createChat(true, parent.id); }
        toast("Демосценарий: документ открыт и активирован.");
      }
      return;
    }
    if (kind === "parent-delete") {
      var parent = S.parents.find(function (p) { return p.id === value; }), ids = [value];
      if (Object.keys(S.chats).some(function (id) { return ids.includes(S.chats[id].parent) && S.chats[id].status === "running"; })) { toast("Сначала остановите выполнение в чатах этой области."); return; }
      confirm("Удалить историю «" + parent.name + "»?", parent.scope === "office" ? "Документ и все его чаты исчезнут из истории прототипа. Сам Office-файл не удаляется." : "Папка и её чаты исчезнут из истории прототипа. Папка и файлы на диске не удаляются.", "Удалить историю", function () {
        var removeCurrent = ids.includes(chat().parent);
        Object.keys(S.chats).forEach(function (id) { if (ids.includes(S.chats[id].parent)) delete S.chats[id]; });
        S.parents = S.parents.filter(function (p) { return !ids.includes(p.id); });
        if (removeCurrent) selectRemainingChat(); else { closeDialog(); render(false); }
      }, true); return;
    }
    if (kind === "launch-office") {
      var parentId = "office-new-" + Date.now();
      S.parents.push({ id: parentId, scope: "office", name: "Новый документ " + value, host: value, open: true });
      S.workspaceScope = "office"; createChat(true, parentId); toast("Демонстрация: новый документ и чат " + value + ". Office не запускался."); return;
    }
    if (kind === "restore-material") {
      var item = P.selectedMaterial(), key = P.materialKey(), version = P.materialVersions()[Number(value)];
      if (!version) return;
      confirm("Восстановить версию " + (Number(value) + 1) + "?", "В «" + item.name + "» будет создана новая версия. Несохранённый черновик будет заменён.", "Восстановить", function () {
        S.saved[key] = version.body; S.drafts[key] = version.body; S.dirty[key] = false;
        P.materialVersions().push({ body: version.body, note: "Восстановлена версия " + (Number(value) + 1) });
        navigate("materials/versions"); toast("Создана новая версия материала.");
      }); return;
    }
    if (kind === "model") { chat().model = value === "default" ? "" : value; nextFocus = '.model-picker summary'; render(false); return; }
    if (kind === "fork-message") { forkChat(Number(value)); navigate("chat/main"); toast("Создана ветка до выбранного сообщения. Исходный чат сохранён."); return; }
    if (kind === "edit-message") { if (chat().status === "running") { toast("Сначала остановите выполнение."); return; } if (!chat().editing) chat().previousDraft = chat().draft; chat().editing = true; chat().editIndex = Number(value); chat().draft = chat().messages[Number(value)].text; nextFocus = '#chat-input'; render(false); return; }
    if (kind === "copy-message") { var message = chat().messages[Number(value)], text = message.text || (message.blocks || []).map(function (block) { return block.text || block.title || ""; }).filter(Boolean).join("\n\n"); if (navigator.clipboard) navigator.clipboard.writeText(text).then(function () { toast("Сообщение скопировано."); }, function () { showCopy(text); }); else showCopy(text); return; }
    if (kind === "remove-attachment") { S.attachments.splice(Number(value), 1); render(false); return; }
    if (kind === "inspect-source") { var source = P.fixtures.sources[Number(value)]; showDialog(source.name, P.info("Лист «Продажи»", source.size + " · источник проекта", "file") + '<p>Выбор файла проекта сам по себе не включает его в контекст модели.</p>', Number(value) === 2 ? B("Ошибка подготовки", "source-error", "warning") : B("Закрыть", "close-dialog")); return; }
    if (kind === "open-snapshot") { var snapshot = S.snapshots[Number(value)]; var open = function () { S.drafts.template = snapshot.body; S.dirty.template = false; S.templateValid = false; navigate("projects/templates"); }; if (S.dirty.template) confirm("Открыть снимок?", "Несохранённый демочерновик будет заменён выбранным снимком.", "Открыть снимок", open); else open(); return; }
    if (kind === "mapping") { S.editMapping = Number(value); var m = S.mappings[S.editMapping]; showDialog("Изменить соответствие", '<div class="form-stack">' + P.field("Название в источнике", m[0], "mappingSource") + P.field("Параметр", m[1], "mappingTarget") + '</div>', B("Отмена", "close-dialog") + B("Применить", "apply-mapping", "check", "primary")); return; }
    if (kind === "parameter") { S.editParameter = value; var parameter = S.parameters.find(function (p) { return p[0] === value; }); showDialog("Параметр «" + value + "»", '<div class="form-stack">' + P.field("Название", parameter[1], "parameterName") + '<label class="field">Тип<select><option>Строка</option><option>Число</option><option>Дата</option></select></label><p>Демонстрация редактирования определения в выбранном шаблоне.</p></div>', B("Отмена", "close-dialog") + B("Применить", "parameter-apply", "check", "primary")); dialog.querySelector("select").value = parameter[2]; return; }
    if (name === "parameter-apply") { var parameter = S.parameters.find(function (p) { return p[0] === S.editParameter; }); var nameValue = dialog.querySelector("input").value.trim(); if (!nameValue) return; parameter[1] = nameValue; parameter[2] = dialog.querySelector("select").value; S.dirty.template = true; S.templateValid = false; closeDialog(); render(false); toast("Параметр изменён в демонстрационной копии."); return; }
    toast("Действие не найдено: " + name);
  }
  function filters() {
    var search = Array.from(document.querySelectorAll("[data-search]")).find(function (el) { return !el.closest("[hidden]"); }), query = search ? search.value.toLocaleLowerCase("ru") : "";
    var selections = Array.from(document.querySelectorAll("[data-filter]")).map(function (v) { return v.value; }).filter(function (v) { return !v.startsWith("Все "); });
    var visible = 0;
    document.querySelectorAll("#main [data-filter-row], #review-content [data-filter-row]").forEach(function (row) { var text = row.textContent.toLocaleLowerCase("ru"); row.hidden = !text.includes(query) || !selections.every(function (v) { return text.includes(v.toLocaleLowerCase("ru")); }); if (!row.hidden) visible++; });
    var empty = document.getElementById("filter-empty"); if (empty) empty.hidden = visible > 0;
  }
  document.addEventListener("click", function (event) {
    var summary = event.target.closest("details > summary");
    if (summary) {
      var group = summary.parentElement;
      if (group.dataset.disclosure) { summary.focus({ preventScroll: true }); P.chatScroll.hold(); }
      var expanded = P.motion.toggle(group, event);
      if (group.dataset.disclosure) { chat().activityExpanded = chat().activityExpanded || {}; chat().activityExpanded[group.dataset.disclosure] = expanded; }
    }
    var el = event.target.closest("[data-go],[data-action],[data-review],button[data-chat],[data-module],[data-scope],[data-section]");
    if (!el || el.disabled) return;
    if (el.dataset.section) {
      var section = P.sections.find(function (item) { return item.id === el.dataset.section; });
      if (!section) return;
      S.openNavigationAfterNavigate = document.getElementById("app-shell").classList.contains("navigation-closed") || document.getElementById("stage").clientWidth <= 820;
      navigate(S.sectionRoutes[section.id] || section.route); return;
    }
    if (el.dataset.scope) {
      S.workspaceScope = el.dataset.scope; S.treeSearch = ""; S.treeSearchOpen = false;
      var candidate = S.lastScopeChat[S.workspaceScope];
      if (!S.chats[candidate]) candidate = Object.keys(S.chats).find(function (id) { return S.chats[id].scope === S.workspaceScope; });
      if (candidate) bindChat(candidate);
      S.route = candidate && chat().offline ? "chat/offline" : "chat/main"; S.review = "app";
      nextFocus = '.scope-switcher > summary';
      history.pushState(null, "", "#/" + S.route); render(false);
      return;
    }
    if (el.dataset.go) { if (el.closest('.inspector-switcher')) nextFocus = '.inspector-switcher > summary'; navigate(el.dataset.go); return; }
    if (el.dataset.review) { S.review = el.dataset.review; render(true); return; }
    if (el.dataset.chat) { bindChat(el.dataset.chat); navigate(chat().offline ? "chat/offline" : "chat/main"); return; }
    if (el.dataset.module) { S.vbaModule = el.dataset.module; navigate("vba/code"); return; }
    if (el.dataset.action) action(el.dataset.action);
  });
  document.addEventListener("input", function (event) {
    var el = event.target;
    if (el.matches("[data-chat-draft]")) { chat().draft = el.value; P.resizeComposer(); }
    if (el.dataset.draft) { S.drafts[el.dataset.draft] = el.value; S.dirty[el.dataset.draft] = true; if (el.dataset.draft === "template") S.templateValid = false; P.edits.sync(); }
    if (el.dataset.field && el.dataset.field !== "apiKey") { S.fields[el.dataset.field] = el.type === "checkbox" ? el.checked : el.value; if (el.closest("#main")) P.edits.sync(); }
    if (el.hasAttribute("data-tree-search")) { P.productTree.unmount(); S.treeSearch = el.value; document.querySelectorAll(".tree-scroll").forEach(function (node) { node.innerHTML = P.renderTreeContents(); }); P.productTree.mount(); }
    if (el.hasAttribute("data-nav-search")) { P.productTree.unmount(); S.navSearch[P.activeSection()] = el.value; document.querySelectorAll(".section-tree-scroll").forEach(function (node) { node.innerHTML = P.renderSectionTree(); }); P.productTree.mount(); }
    if (el.matches("[data-tree-search], [data-nav-search]")) {
      var searchButton = el.closest(".workspace-heading").querySelector(".sidebar-search-toggle"), query = el.value.trim();
      searchButton.classList.toggle("has-search-query", !!query);
      searchButton.title = query ? "Фильтр: " + query + " · изменить поиск" : searchButton.getAttribute("aria-label");
      searchButton.setAttribute("aria-description", query ? "Активен фильтр: " + query : "");
    }
    if (el.dataset.search) filters();
  });
  document.addEventListener("focusout", function (event) {
    var input = event.target;
    if (!input.matches("[data-tree-search], [data-nav-search]")) return;
    if (input.hasAttribute("data-tree-search")) S.treeSearchOpen = false;
    else S.navSearchOpen[input.dataset.navSearch] = false;
    // Hide only the field: rebuilding the tree here would swallow a result click.
    var heading = input.closest(".workspace-heading");
    input.hidden = true; heading.classList.remove("searching");
    heading.querySelector(".sidebar-search-toggle").setAttribute("aria-expanded", "false");
  });
  document.addEventListener("change", function (event) {
    var el = event.target;
    if (el.dataset.field && el.dataset.field !== "apiKey") { S.fields[el.dataset.field] = el.type === "checkbox" ? el.checked : el.value; if (el.closest("#main")) P.edits.sync(); }
    if (el.dataset.filter) filters();
    if (el.dataset.source != null) { S.sourceSelection[Number(el.dataset.source)] = el.checked; document.getElementById("selection-count").textContent = "Выбрано файлов: " + S.sourceSelection.filter(Boolean).length; }
    if (el.dataset.appearance) { P.setAppearance(el.dataset.appearance, el.value); nextFocus = '[data-appearance="' + el.dataset.appearance + '"]'; render(false); }
    if (el.hasAttribute("data-chat-model")) { chat().model = el.value; nextFocus = "[data-chat-model]"; render(false); }
    if (el.hasAttribute("data-chat-reasoning")) { chat().reasoning = el.checked; nextFocus = '[data-chat-reasoning]'; render(false); }
  });
  document.addEventListener("submit", function (event) {
    if (event.target.id !== "composer") return;
    event.preventDefault();
    var c = chat(), message = c.draft.trim();
    if (!message) { document.getElementById("chat-input").focus(); return; }
    if (c.status === "running") return;
    if (c.editing) {
      var index = c.editIndex;
      c.draft = c.previousDraft || ""; c.editing = false; c.previousDraft = "";
      forkChat(index - 1); c = chat();
    }
    if (c.demo) { P.demo.stop(); c.demo = false; }
    c.messages.push({ role: "user", text: message }); c.draft = ""; c.status = "running"; c.runDocument = P.chatContextName(); S.scenario = "ready";
    var id = S.chatId, runPlan = c.plan, runLimit = { "Модель B": 64000, "Модель C": 200000 }[c.model] || 128000; nextFocus = '#chat-input'; render(false); P.chatScroll.bottom();
    clearTimeout(runTimers[id]);
    runTimers[id] = setTimeout(function () {
      var current = S.chats[id]; if (!current) return;
      current.usage = { input: 24600, limit: runLimit };
      if (runPlan || current.scope === "projects") completeChat(id, runPlan ? "План подготовлен: проверить источники, сравнить периоды, собрать выводы. Действия в документе не выполнялись." : "Источники проверены, сравнение периодов подготовлено. Это демонстрационный результат.");
      else current.status = "approval";
      if (chatIsVisible(id)) { render(false); announce(current.status === "complete" ? "Ответ готов" : "Требуется подтверждение"); }
      else refreshSidebar();
    }, 2200);
  });
  document.addEventListener("keydown", function (event) {
    var picker = event.target.closest(".model-picker, .scope-switcher, .inspector-switcher");
    if (picker && ["ArrowDown", "ArrowUp", "Home", "End"].includes(event.key)) {
      event.preventDefault(); picker.open = true;
      var options = Array.from(picker.querySelectorAll('[role="menuitemradio"]')), current = options.indexOf(document.activeElement);
      var index = event.key === "Home" ? 0 : event.key === "End" ? options.length - 1 : (current + (event.key === "ArrowDown" ? 1 : -1) + options.length) % options.length;
      options[index].focus(); return;
    }
    if (event.key === "Escape" && event.target.hasAttribute("data-tree-search")) { event.preventDefault(); action("toggle-tree-search"); return; }
    if (event.key === "Escape" && event.target.hasAttribute("data-nav-search")) { event.preventDefault(); action("toggle-nav-search"); return; }
    if (event.key === "Escape" && !dialog.open) {
      var menu = document.querySelector("details.popover[open]");
      if (menu) { menu.open = false; menu.querySelector("summary").focus(); event.preventDefault(); return; }
      if (event.target.closest(".inspector")) { event.preventDefault(); action("close-inspector"); return; }
      if (event.target.closest("#global-nav")) { event.preventDefault(); P.layout.setNavigation(false, true); return; }
    }
    if (event.key === "Enter" && event.target.id === "chat-input" && !event.shiftKey && !event.isComposing && event.keyCode !== 229) {
      event.preventDefault(); if (!event.repeat) document.getElementById("composer").requestSubmit();
    }
  });
  document.addEventListener("pointerdown", function (event) {
    var searchButton = event.target.closest(".sidebar-search-toggle");
    if (searchButton && searchButton.closest(".workspace-heading").contains(document.activeElement) && document.activeElement.matches(".tree-search-input")) event.preventDefault();
    document.querySelectorAll("details.popover[open]").forEach(function (menu) { if (!menu.contains(event.target)) menu.open = false; });
  });
  document.getElementById("viewport").addEventListener("change", function (event) { document.getElementById("stage").style.setProperty("--preview-width", event.target.value === "auto" ? "100%" : event.target.value + "px"); });
  document.getElementById("theme-toggle").addEventListener("click", function () { setTheme(S.effectiveTheme === "light" ? "dark" : "light"); });
  document.getElementById("scenario").addEventListener("change", function (event) {
    P.demo.stop();
    S.scenario = event.target.value;
    if (["running", "approval", "needs-input"].includes(S.scenario)) { chat().status = S.scenario; chat().runDocument = S.document; if (S.scenario === "approval") chat().plan = false; if (!S.route.startsWith("chat/")) { S.route = "chat/main"; history.replaceState(null, "", "#/chat/main"); } }
    else { clearTimeout(runTimers[S.chatId]); chat().status = "ready"; }
    if (S.scenario === "dirty") {
      var key = editorKey();
      if (S.route.startsWith("chat/")) chat().draft = "Несохранённый черновик. Перейдите в другой раздел и вернитесь.";
      else if (S.route.startsWith("settings/")) S.settingsDraft.MaxTokens += 1;
      else if (S.saved[key] != null) S.drafts[key] = (S.drafts[key] == null ? S.saved[key] : S.drafts[key]) + "\n";
      else if (key === "tool") S.fields.toolName = (S.fields.toolName || "Форматирование таблицы") + " · черновик";
    }
    if (S.scenario === "loading" && !S.route.startsWith("chat/")) { S.route = "chat/main"; history.replaceState(null, "", "#/chat/main"); }
    S.review = "app"; render(false);
  });
  document.getElementById("demo-file").addEventListener("change", function (event) {
    var files = Array.from(event.target.files || []);
    if (event.target.dataset.target === "sources") { delete event.target.dataset.target; showDialog("Выбраны локальные файлы", '<p>' + E(files.map(function (f) { return f.name; }).join(", ")) + '</p><p>Это прототип выбора. Для демонстрации обработки используются встроенные примеры; содержимое ваших файлов не читается.</p>', B("Использовать примеры", "sample-sources", "check", "primary")); }
    else { files.forEach(function (file) { if (!S.attachments.includes(file.name)) S.attachments.push(file.name); }); render(false); toast("Добавлены названия файлов. Содержимое не читается и не отправляется."); }
    event.target.value = "";
  });
  document.addEventListener("visibilitychange", function () { if (chatIsVisible(S.chatId) && chat().unread) { chat().unread = false; refreshSidebar(); } });
  window.addEventListener("beforeunload", function (event) { if (P.settingsChanged() || Object.values(S.dirty).some(Boolean) || Object.values(S.chats).some(function (c) { return !!c.draft; })) { event.preventDefault(); event.returnValue = ""; } });
  window.addEventListener("hashchange", function () { applyRoute(location.hash.replace(/^#\//, "")); });
  P.navigate = navigate;
  P.render = render;
  bindChat("sales");
  applyRoute(location.hash.replace(/^#\//, "") || "chat/main");
}());
