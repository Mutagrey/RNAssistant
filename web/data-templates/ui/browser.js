(function () {
  "use strict";
  // Browser composition owns no RNAssistant services, credentials or workspace grants.
  const container = document.getElementById("dataTemplates");
  let processing, project, dictionaryController, library;
  const loader = DataTemplatesLoader.create({ onChange: () => { if (processing) processing.sourcesChanged(); } });
  DataTemplatesShell.mount(container, "core/index.html");
  container.dataset.dtProcessing = "true";
  if (location.protocol === "file:") {
    const notice = document.createElement("p");
    notice.className = "dt-file-protocol-notice dt-card";
    notice.setAttribute("role", "status");
    notice.textContent = "Страница открыта как file://. Браузер блокирует Worker проверки и обработки. Откройте приложение через локальную статическую HTTP-раздачу; команда приведена в README.";
    container.querySelector(".dt-header").insertAdjacentElement("afterend", notice);
  }
  loader.bind(container);
  processing = DataTemplatesProcessing.create(container, loader);
  project = DataTemplatesBrowserProjectView.create(container, processing);
  dictionaryController = DataTemplatesProjectDictionaryController.create(project, processing);
  processing.bindProjectDictionary(dictionaryController);
  library = DataTemplatesTemplateLibraryView.create(container, processing, project, dictionaryController);
  if (window.parent !== window) {
    document.documentElement.classList.add("dt-embedded");
    let requestedView = "sources", lastState = "", scheduled = false;
    function publishNavigation() {
      scheduled = false;
      const selected = container.querySelector('.dt-tabs [aria-selected="true"]');
      const templates = Array.from(container.querySelectorAll('#dtLibraryList button[data-action="open"]'))
        .map(button => ({ id: button.dataset.id })).filter(item => item.id);
      const activeTemplate = container.querySelector("#dtTemplateEditor").hidden ? "" : (library.active() || "");
      const templateHeading = container.querySelector("#dtViewTemplates .dt-section-heading");
      const title = activeTemplate || "Шаблоны преобразования";
      const subtitle = activeTemplate ? "Черновик шаблона · JSON" : "Повторяемые правила для файлов с похожей структурой.";
      if (templateHeading.querySelector("h3").textContent !== title) templateHeading.querySelector("h3").textContent = title;
      if (templateHeading.querySelector("p").textContent !== subtitle) templateHeading.querySelector("p").textContent = subtitle;
      const state = { type: "rna.dataTemplates.state", ready: !window.RNPreviewDataProject || document.documentElement.dataset.previewReady === "true",
        projectName: container.querySelector("#dtPageTitle").textContent,
        view: requestedView === "processing" || requestedView === "drafts" ? requestedView : selected.dataset.dtView,
        templateId: activeTemplate, templates: templates };
      const json = JSON.stringify(state);
      if (json !== lastState) { lastState = json; window.parent.postMessage(state, "*"); }
    }
    function scheduleNavigation() { if (!scheduled) { scheduled = true; requestAnimationFrame(publishNavigation); } }
    new MutationObserver(scheduleNavigation).observe(container, { subtree: true, childList: true,
      attributes: true, attributeFilter: ["aria-selected", "open", "hidden"] });
    function navigateFromParent(request) {
      const view = request.view;
      if (!["sources", "templates", "drafts", "parameters", "mapping", "processing", "results", "format"].includes(view)) return;
      requestedView = view;
      const tab = container.querySelector('[data-dt-view="' + (view === "drafts" ? "templates" : view) + '"]');
      if (tab) tab.click();
      requestedView = view;
      if (view === "drafts") { const card = container.querySelector("#dtProjectCard"); card.open = true; card.scrollIntoView({ block: "start" }); }
      if (view === "templates" && !request.templateId && !container.querySelector("#dtTemplateEditor").hidden)
        container.querySelector("#dtCloseTemplateEditor").click();
      if (view === "templates" && typeof request.templateId === "string") {
        const button = Array.from(container.querySelectorAll('#dtLibraryList button[data-action="open"]'))
          .find(item => item.dataset.id === request.templateId);
        if (button) {
          if (library.active() !== request.templateId) button.click();
          let attempts = 0;
          const showEditor = () => {
            const editorButton = container.querySelector("#dtOpenTemplateEditor");
            if (library.active() === request.templateId && editorButton && !editorButton.hidden) {
              if (container.querySelector("#dtTemplateEditor").hidden) editorButton.click();
              let editorAttempts = 0;
              const showJson = () => {
                if (container.querySelector("#dtEditorJsonCode .CodeMirror"))
                  container.querySelector("#dtEditorJsonTab").click();
                else if (++editorAttempts < 40) setTimeout(showJson, 50);
              };
              showJson();
              container.querySelector("#dtViewTemplates").scrollIntoView({ block: "start" });
            } else if (++attempts < 40) setTimeout(showEditor, 50);
          };
          showEditor();
        }
      }
      scheduleNavigation();
    }
    window.addEventListener("message", event => {
      if (event.source !== window.parent || !event.data || typeof event.data !== "object") return;
      if (event.data.type === "rna.dataTemplates.project") { container.querySelector("#dtProjectHeaderButton").click(); return; }
      if (event.data.type !== "rna.dataTemplates.navigate") return;
      navigateFromParent({ view: event.data.view, templateId: event.data.templateId || "" });
    });
    window.addEventListener("rna.preview.dataTemplatesReady", scheduleNavigation);
    container.querySelectorAll(".dt-tabs [data-dt-view]").forEach(tab => tab.addEventListener("click", () => { requestedView = tab.dataset.dtView; scheduleNavigation(); }));
    scheduleNavigation();
  }
  const formatNotes = container.querySelectorAll("#dtViewFormat .dt-format-details p");
  formatNotes[0].textContent = "Доступны подбор файлов и папок, проверка и редактирование JSON-шаблона, просмотр параметров и маппинга, последовательная очередь XLSX, прогресс, отмена и экспорт результата.";
  formatNotes[1].textContent = "Ядро работает с матрицей, нормализованными записями и JSON, но в основном приложении пока подключён только ограниченный XLSX-читатель. JSON, выражения и точные соответствия можно редактировать и сохранять черновиками. Завершённые извлечения можно отдельно сохранить в папке проекта. Публикация dataset, шаблонов и остальные форматы импорта ещё разрабатываются.";
  window.addEventListener("pagehide", event => {
    if (!event.persisted) { library.dispose(); dictionaryController.dispose(); project.dispose(); processing.dispose(); loader.dispose(); }
  });
}());
