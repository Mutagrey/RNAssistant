(function () {
  "use strict";
  var view = null, busy = false, sequence = 0, inspection = null, bound = false;
  function el(id) { return document.getElementById("workspace" + id); }
  function selected() { return view && view.workspace; }
  function invalidResponse() {
    var error = new Error("Не удалось проверить ответ приложения. Обновите состояние папки.");
    error.code = "workspace_response_invalid";
    return error;
  }
  function status(message, error) {
    el("Status").textContent = message || "";
    el("Status").dataset.error = error ? "true" : "false";
    el("Status").setAttribute("role", error ? "alert" : "status");
  }
  function render() {
    var workspace = selected(), project = view && view.project;
    el("Root").disabled = el("ReadOnly").disabled = el("Open").disabled = el("Refresh").disabled = busy;
    el("ProjectFields").disabled = busy || !workspace;
    el("ProjectCreate").disabled = busy || !workspace || workspace.readOnly;
    el("ProjectName").disabled = busy || !workspace || workspace.readOnly;
    el("Selection").textContent = workspace
      ? "Выбрана папка: " + workspace.rootPath + (workspace.readOnly ? " · Только чтение" : " · Чтение и запись")
      : "Рабочая папка не выбрана.";
    el("ProjectResult").hidden = !project;
    el("ProjectTitle").textContent = project ? project.name : "";
    el("ProjectLocation").textContent = project ? project.manifestPath : "";
    el("ContextTitle").textContent = project ? project.name : workspace ? "Проект не выбран" : "Подключить проект";
    el("ContextHint").textContent = workspace
      ? workspace.rootPath + (workspace.readOnly ? " · Только чтение" : "") : "Рабочая папка и параметры проекта";
    el("Reconcile").hidden = !inspection;
    el("Reconcile").disabled = busy || !workspace || !inspection || inspection.bindingId !== workspace.bindingId;
    if (window.RNAssistantDataTemplates) window.RNAssistantDataTemplates.setContext(view, busy);
  }
  function apply(response) {
    if (!response || response.type !== "rnassistant.workspaceState" || response.contractVersion !== 1 ||
        !Number.isSafeInteger(response.revision) || response.revision < 0 ||
        !Object.prototype.hasOwnProperty.call(response, "workspace") || !Object.prototype.hasOwnProperty.call(response, "project") ||
        (response.workspace && (typeof response.workspace.bindingId !== "string" || !response.workspace.bindingId ||
          typeof response.workspace.rootPath !== "string" || typeof response.workspace.readOnly !== "boolean")) ||
        (response.project && (!response.workspace || typeof response.project.name !== "string" ||
          typeof response.project.manifestPath !== "string"))) {
      throw invalidResponse();
    }
    var previous = selected();
    if (!previous || !response.workspace || previous.bindingId !== response.workspace.bindingId) inspection = null;
    view = response;
    if (response.workspace) {
      el("Root").value = response.workspace.rootPath;
      el("ReadOnly").checked = response.workspace.readOnly;
    }
  }
  var errors = {
    target_exists: "Файл проекта уже существует. Откройте его или выберите другой путь.",
    read_only_mount: "Папка открыта только для чтения.",
    workspace_binding_changed: "Выбор папки изменился. Нажмите «Обновить состояние» перед следующим действием.",
    effect_unknown: "Результат записи не подтверждён. Проверьте состояние файла перед следующим действием.",
    unresolved_previous_effect: "Осталась незавершённая запись. Сначала проверьте состояние файла.",
    bridge_response_timeout: "Ответ не получен. Действие могло завершиться; повторное создание не выполняется.",
    path_outside_mount: "Укажите безопасный относительный путь внутри выбранной папки.",
    project_invalid: "Описание проекта или его путь недопустимы. Проверьте project.json и имя проекта."
  };
  async function request(type, payload, message) {
    if (busy) return;
    busy = true;
    var ticket = ++sequence;
    if (view && type !== "getWorkspaceState" && type !== "openWorkspace") view.project = null;
    status(message, false); render();
    try {
      var response = await send(type, payload || {});
      if (ticket !== sequence) return;
      if (type === "reconcileWorkspaceFile" && (!response || typeof response.exists !== "boolean" ||
          ["UnknownAfterDispatch", "AlreadyPublished", "AbandonedBeforeDispatch"].indexOf(response.outcome) < 0)) throw invalidResponse();
      if ((type === "createTabularProject" || type === "openTabularProject") && (!response || !response.project)) throw invalidResponse();
      if (type === "openWorkspace" && (!response || !response.workspace)) throw invalidResponse();
      apply(type === "reconcileWorkspaceFile" ? response.state : response);
      if (type === "reconcileWorkspaceFile") {
        inspection = null;
        status(response.exists ? "Файл существует. Откройте проект, чтобы проверить его содержимое."
          : "Файл отсутствует. Создание можно запустить отдельным действием.", false);
      } else status(type === "createTabularProject" ? "Проект создан."
        : type === "openTabularProject" ? "Проект открыт." : type === "getWorkspaceState" ? "" : "Рабочая папка открыта.", false);
    } catch (error) {
      if (ticket !== sequence) return;
      if (error.code === "workspace_binding_changed") { view = null; inspection = null; }
      if (payload && payload.bindingId && ["effect_unknown", "unresolved_previous_effect", "bridge_response_timeout", "workspace_response_invalid", "bridge_transport_failed"].indexOf(error.code) >= 0)
        inspection = { bindingId: payload.bindingId, manifestPath: payload.manifestPath };
      status(errors[error.code] || error.message || "Не удалось выполнить действие.", true);
    } finally {
      if (ticket === sequence) { busy = false; render(); }
    }
  }
  function projectRequest(create) {
    var workspace = selected();
    if (!workspace || busy || create && workspace.readOnly) return;
    var payload = { bindingId: workspace.bindingId, manifestPath: el("ProjectPath").value.trim() };
    if (!payload.manifestPath) { status("Укажите путь к project.json внутри папки.", true); return; }
    if (create) {
      payload.name = el("ProjectName").value.trim();
      if (!payload.name) { status("Укажите имя нового проекта.", true); return; }
    }
    return request(create ? "createTabularProject" : "openTabularProject", payload,
      create ? "Создаём проект…" : "Открываем проект…");
  }
  function bind() {
    if (bound || !el("Open")) return;
    bound = true;
    el("Open").addEventListener("click", function () {
      var root = el("Root").value.trim();
      if (!root) { status("Укажите полный путь к рабочей папке.", true); return; }
      request("openWorkspace", { rootPath: root, readOnly: el("ReadOnly").checked }, "Открываем папку…");
    });
    el("Refresh").addEventListener("click", show);
    el("ProjectOpen").addEventListener("click", function () { projectRequest(false); });
    el("ProjectCreate").addEventListener("click", function () { projectRequest(true); });
    el("Reconcile").addEventListener("click", function () {
      if (inspection) request("reconcileWorkspaceFile", inspection, "Проверяем текущее состояние файла…");
    });
    render();
  }
  function show() { return request("getWorkspaceState", {}, "Читаем состояние папки…"); }
  window.RNAssistantWorkspace = { bind: bind, show: show };
}());
