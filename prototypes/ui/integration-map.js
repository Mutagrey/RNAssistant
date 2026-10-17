(function () {
  "use strict";
  var P = window.Prototype;
  P.renderIntegrationStatus = function () {
    return '<section class="component-section"><h2>Что уже связано с RNAssistant</h2>' + P.table(["Область", "Используется сейчас", "Остаётся подключить"], [
      ["Деревья всех разделов", "Собственный компактный ARIA tree; единое поведение всех разделов", "Предметные projections вместо демоданных; состояние выбранного объекта у feature owner"],
      ["Код и исходники", "CodeMirror 5.65.16; режимы VBA, JSON, Markdown; история отмены по файлу", "Feature controllers сохранения, проверка ревизии, read-back VBA"],
      ["Сравнение изменений", "RNAssistantTextDiff и RNAssistantRunChanges: VBA и плашки под ответом", "Retained before/after и availability из getRunChanges; демо не создаёт runtime evidence"],
      ["JSON, текст, Markdown", "RNAssistantViewerRegistry + существующие app-*-viewer.js", "Exact resource reads, страницы и лимиты текущего документа"],
      ["Изображения, PDF, коллекции", "Viewer.js, RNAssistantResourceViewers и SequenceViewer", "Blob leases и PDF-страницы от native resource service"],
      ["Планы и задачи", "Markdown и штатный task-list viewer", "Существующие projections плана и состояния задач"],
      ["HTML-проекты", "Файлы и данные в одном дереве; отдельные черновики и версии", "Существующий sandbox, bindings, revisions и .rna-html.zip exporter"],
      ["Настройки", "Поля AppSettings; проверка значений и преобразование типов", "Загрузка текущих значений и typed save; новые предпочтения оформления"],
      ["Чат, Библиотека, VBA", "Новый presentation и демосценарии", "Текущие тематические контроллеры; единый runtime lifecycle"],
      ["Данные и шаблоны", "Представления нового оформления", "Переоформить общий web/data-templates/ui, сохранить существующее ядро и adapters"]
    ]) + '<p>Все действия над Office, историей, моделью и файловой системой остаются демонстрационными. Подключение компонентов не является квалификацией Windows / Office.</p></section>';
  };
}());
