(function () {
  "use strict";
  // Browser composition owns no RNAssistant services, credentials or workspace grants.
  const container = document.getElementById("dataTemplates");
  let processing;
  const loader = DataTemplatesLoader.create({ onChange: () => { if (processing) processing.sourcesChanged(); } });
  DataTemplatesShell.mount(container, "core/index.html");
  container.dataset.dtProcessing = "true";
  container.querySelector(".dt-header .dt-phase").textContent = "Самостоятельное приложение";
  loader.bind(container);
  processing = DataTemplatesProcessing.create(container, loader);
  container.querySelector("#dtIntakeGuide .dt-guide-note").textContent = "Далее откройте «Шаблоны», выберите JSON-правила и запустите очередь XLSX.";
  container.querySelector("#dtSelectionNote").textContent = "Подбор файлов не запускает обработку. На вкладке «Шаблоны» можно запустить очередь; список хранится в этой сессии.";
  window.addEventListener("pagehide", event => {
    if (!event.persisted) { processing.dispose(); loader.dispose(); }
  });
}());
