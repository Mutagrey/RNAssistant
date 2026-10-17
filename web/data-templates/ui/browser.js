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
  const formatNotes = container.querySelectorAll("#dtViewFormat .dt-format-details p");
  formatNotes[0].textContent = "Доступны подбор файлов и папок, проверка и редактирование JSON-шаблона, просмотр параметров и маппинга, последовательная очередь XLSX, прогресс, отмена и экспорт результата.";
  formatNotes[1].textContent = "Ядро работает с матрицей, нормализованными записями и JSON, но в основном приложении пока подключён только ограниченный XLSX-читатель. JSON, выражения и точные соответствия можно редактировать в памяти и скачивать черновик; проектное сохранение и остальные форматы импорта ещё разрабатываются.";
  window.addEventListener("pagehide", event => {
    if (!event.persisted) { processing.dispose(); loader.dispose(); }
  });
}());
