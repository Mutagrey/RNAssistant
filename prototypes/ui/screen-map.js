/* UI inventory, grounded in web/index.html and the named feature owners.
   This describes proposed presentation, not new runtime capabilities. */
window.PrototypeMap = [
  { title: "Документы Office и чаты", icon: "chat", owner: "app-chat-*", nodes: [
    ["Документы и чаты", "Рабочая область / Документы Office → дерево", "chat/list", "tab-chat, renderChatDocumentNode", "Прежняя иерархия и действия, новое оформление; без второй колонки."],
    ["Новый чат, поиск", "Дерево / компактный поиск и «+»", "chat/new", "newChatButton, toggleChatSearchButton", "Поиск раскрывается вправо; плюс также доступен у документа."],
    ["Переписка и composer", "Чаты / переписка", "chat/main", "chatForm", "Группы действий, промежуточные сообщения, предупреждения у причины и кнопка вниз."],
    ["Меню + / вложения / выделение", "Поле ввода / добавить", "chat/attach", "composerAddMenu", "Файл, выделение Office, материал и сжатие контекста в одном меню."],
    ["Режим планирования", "Поле ввода / Меню «+»", "chat/plan", "chatPlanButton", "Переключатель в меню «+»; активный режим обозначен у поля ввода."],
    ["Модель, reasoning, связь", "Поле ввода / выбор модели", "chat/model", "chatModelPicker", "Меню рядом с composer: модель по умолчанию, возможности, лимиты; лампочка reasoning отдельно."],
    ["Ресурсы чата и ручной контекст", "Панель «Контекст»", "chat/context", "chatResourcesPopover, contextManager", "Принадлежность материалу и включение в запрос показаны раздельно."],
    ["Арсенал / навыки / инструменты", "Возможности агента", "chat/abilities", "agentArsenalPanel", "Только доступность; редактирование ведёт в Библиотеку."],
    ["Индикатор токенов / инспектор", "Контекст / использование", "chat/usage", "promptContextInspector", "Компактное кольцо в поле ввода → аннотация по клику → подробный инспектор."],
    ["Запрос / ответ модели", "Контекст / технические данные", "chat/request", "modelContextDialog", "История запросов и выгрузка доступны из одной панели."],
    ["Форк / редактирование / очередь сообщений", "Переписка / ветки и ожидающие", "chat/queue", "messageEditBar, app-chat-inbox.js", "Форк сохраняет историю до выбранного сообщения; черновик остаётся у исходного чата."],
    ["План / ход работы / tool results", "Ход работы", "chat/activity", "agentPlanDock, app-agent-activity.js", "Одна сводка с раскрытием шагов."],
    ["Изменения run / журнал", "Ход работы / изменения", "chat/changes", "app-run-changes.js, app-run-journal.js", "Результат чтения и эффект изменения не смешиваются."],
    ["Подтверждение / вопрос агента", "Подтверждение рядом с действием", "chat/approval", "agentApprovalDock", "Объект, объём и последствия до подтверждения."],
    ["Закрытый документ / ошибка", "Переписка / недоступность документа", "chat/offline", "documentNotice", "Доступность Office не скрыта за индикатором модели."]
  ]},
  { title: "Материалы", icon: "files", owner: "app-artifact-* / viewer registry", nodes: [
    ["Каталог артефактов", "Материалы / каталог", "materials/all", "tab-artifacts", "Чат, документ и все документы — явный фильтр."],
    ["Импорт / создание Plan, HTML, CSS, JS, JSON", "Каталог / добавить материал", "materials/create", "artifactCatalogImport, addPlanButton", "Одна кнопка создания, форматы внутри меню."],
    ["Markdown / Plan", "Материал / просмотр", "materials/report", "app-text-viewer.js", "Общий каркас просмотрщиков."],
    ["HTML workspace / редактор", "Материал / код", "materials/code", "app-html-workspace-editor.js", "Редактор, предпросмотр и действия рядом."],
    ["Встроенный HTML / ECharts", "Материал / интерактивный отчёт", "materials/html", "app-html-workspace-preview.js", "Контент отделён от chrome просмотрщика."],
    ["JSON / дерево / текст", "Материал / данные", "materials/json", "app-json-viewer.js", "Общий заголовок, типоспецифичное содержимое."],
    ["PDF / страницы / извлечённый текст", "Материал / документ", "materials/pdf", "app-resource-viewer.js", "Пагинация в панели; без действий исходника и сохранения."],
    ["Изображения / галерея", "Материал / изображение", "materials/image", "app-resource-viewer.js", "Один владелец навигации по изображениям."],
    ["Sequence / коллекция / thumbnails", "Материал / коллекция", "materials/sequence", "app-sequence-viewer.js", "Общий переключатель предыдущего/следующего."],
    ["Версии / undo / redo / recover", "Материал / история версий", "materials/versions", "app-artifact-versions.js", "Явная выбранная версия вместо неясного undo."],
    ["Скачать HTML / исходники", "Материал / экспорт", "materials/export", "app-html-resource-export.js", "Выгрузка относится к выбранному материалу и его формату."],
    ["Удаление / связь с чатом", "Материал / дополнительные действия", "materials/remove", "deleteHtmlWorkspaceButton", "Удаление отдельно от отключения контекста."]
  ]},
  { title: "Проекты и данные", icon: "folder", owner: "workspace + shared Data Templates UI", nodes: [
    ["Новое представление для обсуждения", "Рабочая область / Проекты → чаты", "chat/project", "UI proposal; project chat binding requires domain mapping", "Плоский список проектов; внутри только чаты, без вложенных папок."],
    ["Рабочая папка / выбор и создание проекта", "Проекты / подключение", "projects/open", "workspaceSettings", "Общий вход; папка проекта не меняет документ чата."],
    ["Выбор файлов / подборка / пагинация", "Проект / источники", "projects/sources", "dtViewSources, wsBrowseList", "Сохранение состава и запуск разделены."],
    ["Очередь / подготовка / пауза / отмена", "Проект / обработка", "projects/processing", "wsSourceActivity, wsSourcePrepare", "У каждого файла свой статус и ошибка."],
    ["Шаблоны / выбор и JSON / expr", "Проект / шаблоны", "projects/templates", "dtViewTemplates, template-editor.js", "Черновик и проверенный шаблон различимы."],
    ["Черновики / открыть / сохранить", "Шаблоны / снимки черновиков", "projects/drafts", "browser-project-files.js", "Нативные draft controls есть в текущем baseline; переносим их представление."],
    ["Параметры / справочники", "Проект / параметры", "projects/parameters", "dtViewParameters", "Редактирование в контексте выбранного шаблона."],
    ["Маппинг / scoped mappings", "Проект / соответствия", "projects/mapping", "dtViewMapping, mapping-editor.js", "Предметное название и видимая область применения."],
    ["Результат / preview / JSON / report", "Проект / результаты", "projects/results", "dtViewResults, processing-view.js", "Ошибочные файлы не теряются за общим успехом."],
    ["Форматы / модель данных / лимиты", "Проект / справка", "projects/format", "dtViewFormat", "Справка вынесена из основного пути обработки."],
    ["Самостоятельное приложение", "Data Templates / отдельная оболочка", "projects/standalone", "web/data-templates/index.html", "Те же экраны и компоненты без навигации ассистента."]
  ]},
  { title: "Библиотека", icon: "book", owner: "app-skills / app-tools / app-prompts", nodes: [
    ["Общий список инструкций", "Библиотека / навыки, инструменты, инструкции", "library/all", "instructionsList", "Единый поиск и типизированные группы."],
    ["Навык / поля / SKILL.md / preview", "Навык / редактор", "library/skill", "skillEditorPanel", "Общий каркас редактора."],
    ["References / добавление / удаление", "Навык / файлы", "library/references", "skillResourceSelect", "Файловые операции в одном месте."],
    ["Промпт / копирование / reset", "Инструкция / редактор", "library/prompt", "promptEditorPanel", "Сброс спрятан в дополнительные действия."],
    ["Инструмент / основное", "Инструмент / описание", "library/tool", "toolEditorPanel", "Предметные настройки до технических."],
    ["JSON Schema / форма / JSON", "Инструмент / параметры", "library/schema", "toolSchemaVisual", "Тот же компонент редактирования кода."],
    ["Реализация / VBA components", "Инструмент / код", "library/implementation", "toolVbaComponentToolbar", "Состав пакета внутри инструмента."],
    ["Dry run / запуск / результат", "Инструмент / проверка", "library/test", "toolRunOutput", "Проверка и исполнение явно различаются."],
    ["README / builtin docs", "Инструмент / документация", "library/docs", "toolReadmeEditor", "Один просмотрщик Markdown."],
    ["Install / uninstall / clone / delete", "Инструмент / управление", "library/manage", "toolActionsMenu", "Установка показывает целевой документ и подтверждение."]
  ]},
  { title: "VBA", icon: "code", owner: "app-vba-*", nodes: [
    ["Дерево / поиск / обновление", "VBA / модули документа", "vba/list", "vbaModuleList", "Live VBA не становится материалом чата."],
    ["Код / сохранение / обсуждение", "VBA / редактор", "vba/code", "vbaCodeInput", "Общий editor shell, отдельный domain owner."],
    ["Diff before / after", "VBA / изменения", "vba/diff", "previewVbaDiffButton", "Отдельный просмотр перед применением."],
    ["Запуск макроса", "VBA / запуск", "vba/run", "runVbaMacroButton", "Целевой документ и неподтверждённый эффект видимы."],
    ["Информация / backup / restore", "VBA / история и восстановление", "vba/history", "restoreVbaButton", "Восстановление требует отдельного действия."]
  ]},
  { title: "Настройки", icon: "settings", owner: "app-settings / model / diagnostics", nodes: [
    ["Provider / API key / endpoint", "Настройки / подключение", "settings/connection", "data-settings-page=connection", "Чёткая проверка подключения; ключ не раскрывается."],
    ["Context / attachment budgets / calibration", "Настройки / контекст и вложения", "settings/context", "contextWindowOverrideTokensInput", "Бюджет истории, сжатие и обработка вложений сохраняются."],
    ["Каталог / capabilities / attachments / request", "Настройки / модель", "settings/model", "data-settings-page=model", "Редкие параметры в раскрываемой группе."],
    ["Instructions / protocol / execution / compatibility", "Настройки / агент", "settings/agent", "data-settings-page=agent", "Сложные параметры отделены от обычного использования."],
    ["Размер / шрифт / тема / акцент", "Настройки / интерфейс", "settings/interface", "data-settings-page=interface", "Одна система токенов для всех экранов."],
    ["Защита экрана / истории", "Настройки / защита", "settings/protection", "data-settings-page=protection", "Ограничения и область действия описаны рядом."],
    ["Логи / фильтры / связь с моделью", "Помощь и обслуживание", "settings/diagnostics", "data-settings-page=service", "Отдельно от рабочего хода выполнения."],
    ["Trajectory / payload / before-after / export", "Ошибка запуска → ход работы в чате", "chat/main", "trajectoryTitle", "Полный payload и журнал не переносятся; краткие детали действия остаются в чате."],
    ["CAS audit / GC / clear runtime", "Обслуживание / хранилище", "settings/diagnostics", "casHealthTitle", "Опасные операции в отдельной зоне с подтверждением."]
  ]}
];

window.PrototypeFixtures = {
  document: "Продажи · сентябрь.xlsx",
  project: "Управленческая отчётность",
  materials: [
    { id: "report", name: "Итоги сентября.md", type: "Markdown", icon: "file", owner: "Этот чат", time: "12:42" },
    { id: "html", name: "Динамика продаж.html", type: "HTML", icon: "chart", owner: "Этот чат", time: "12:40" },
    { id: "json", name: "Продажи по регионам.json", type: "JSON", icon: "braces", owner: "Этот чат", time: "12:39" },
    { id: "pdf", name: "Методика расчёта.pdf", type: "PDF", icon: "file", owner: "В документе", time: "Вчера" },
    { id: "image", name: "Диаграмма.png", type: "Изображение", icon: "image", owner: "В документе", time: "Вчера" },
    { id: "sequence", name: "Обзор регионов", type: "Коллекция", icon: "layers", owner: "Все документы", time: "14 окт." }
  ],
  sources: [
    { name: "Москва.xlsx", size: "2,4 МБ", rows: 128, status: "Готов" },
    { name: "Казань.xlsx", size: "1,8 МБ", rows: 96, status: "Готов" },
    { name: "Самара.xlsx", size: "840 КБ", rows: 0, status: "Нужна проверка" }
  ],
  report: "# Итоги сентября\n\nВыручка выросла на 18,4% относительно августа.\n\n## Главное\n\n- Выручка: 12,8 млн ₽\n- Заказы: 1 248\n- Средний чек: 10 256 ₽\n\nРост обеспечили Москва и Казань. По Самаре нужно проверить исходные данные.\n",
  vba: "Option Explicit\n\nPublic Sub FormatSalesReport()\n    Dim report As Worksheet\n    Set report = ThisWorkbook.Worksheets(\"Продажи\")\n\n    With report.Range(\"A1:F1\")\n        .Font.Bold = True\n        .Interior.Color = RGB(232, 241, 233)\n    End With\n\n    report.Columns(\"A:F\").AutoFit\nEnd Sub\n",
  skill: "# Анализ продаж\n\nСравнивай период с предыдущим.\n\n1. Проверь наличие нужных колонок.\n2. Укажи период, валюту и источник.\n3. Раздели факты и предположения.\n4. Покажи исключения и пропуски.\n5. Сформируй краткий отчёт с таблицей.\n",
  template: '{\n  "name": "Ежемесячные продажи",\n  "sheet": "Продажи",\n  "headerRow": 1,\n  "fields": {\n    "region": "Регион",\n    "revenue": "Выручка",\n    "period": "Месяц"\n  }\n}'
};
