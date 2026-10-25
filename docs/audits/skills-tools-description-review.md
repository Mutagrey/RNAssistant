# Skills и tools: сравнение и пересмотр описаний

Дата: 2026-10-26. Код: `15204908`; подготовленная версия: `16.1.0-alpha.1`.
Owner: Office tool/skill catalogs, Core skill contracts, Application workspace.
Это аудит встроенных возможностей RNAssistant, а не списка инструментов Codex.
16 уникальных built-in skills и 91 статический tool ID. Это объединение каталогов,
а не 91 одновременно доступный инструмент: host, settings, packages и bindings
определяют доступность. Пользовательские VBA/JS tools и SKILL.md имеют динамические
ID и требуют отдельного runtime inventory; их количество из Git неизвестно.
Предложения ниже не означают, что описания или manifests уже изменены.

Skill — инструкция и зависимости; tool — исполняемая capability с schema/policy/binding.
Совпадение тематики skill и tool не является дублированием исполняемого API.

## Выполненный следующий этап — 2026-10-26

Исходные таблицы ниже сохраняют выводы аудита до правки. Исправлены:
scoped VBA search guidance; краткое описание CodeOnly; manifests Word/PowerPoint/Outlook
с операциями, уже названными в их workflows; optional plugins.check/apply с явными
fallback; workspace description о verified after-state; условный browser baseline.
Проверки Harness: `skill packages: built-ins`, `skill packages: dependencies`,
`settings: built-in skill references resolve to catalogs` — 3 passed. Built-ins fixture
проверяет authoring tools в следующем request и optional plugin capabilities при наличии/отсутствии.
Это host-neutral evidence; Windows/Office и модельный A/B не выполнялись.
Runtime policies, v8 и публичные tool IDs не менялись. Общие resource dependencies
и остальные редакционные предложения пока сохранены. Реальное модельное сравнение
и файловый cutover остаются следующими этапами.

Продолжение: [реальный A/B pilot и найденный дефект live-harness](skills-tools-model-comparison.md).

## Все встроенные skills

| Skill ID | Контекст | Назначение | Решение / описание |
|---|---|---|---|
| common.task_tracking | Common | План и состояние задачи | Оставить; Required смягчить до условий полезности, не навязывать план каждой задаче. |
| common.text_search_replace | Office | Поиск/замена в содержимом Office | Уточнить host и поддержку regex; Outlook только поиск. Отделить от поиска исходников. |
| common.vba_code_editing | Excel/Word/PowerPoint | Изменение VBA и компонентов | Обновить инструкцию поиска: теперь есть scope=vba. Сократить повторные guards, сохранить специфические правила VBE. |
| common.vba_userform_authoring | Excel/Word/PowerPoint | CodeOnly UserForms | Оставить специализацией VBA; явно исключить Designer/FRX в кратком description. |
| common.tool_authoring | Excel/Word/PowerPoint | Авторство VBA tools | Уточнить пакет, вход/выход и отличие от обычного VBA-макроса. |
| common.skill_authoring | Common | Авторство инструкций и references | Оставить; отдельно назвать зависимости и то, что загрузка не исполняет scripts. |
| common.prompt_authoring | Common | Редактирование prompt | Оставить; обозначить editable prompts и границу с skills. |
| common.html_workspace_authoring | Office | HTML workspace и Office data | Уточнить destination; общие советы HTML вынести в общий текст с workspace.web_verify_repair, host-specific шаги сохранить. |
| common.chat_visualization | Common/Workspace | Визуализация в чате | Оставить; четко отличать inline-визуализацию от сохраняемого приложения/Office chart. |
| excel.analysis_reporting | Excel | Анализ, таблицы, диаграммы | Уточнить выбор inspect/summarize и chat chart/workbook chart; не требовать диаграмму для любого анализа. |
| word.document_editing | Word | Правка Word | Пересмотреть manifest: заявлены insert/format/review, явно добавлены только find/replace. |
| powerpoint.deck_building | PowerPoint | Структура и содержимое слайдов | Пересмотреть manifest: описание шире явно добавленных search/replace; проверить notes по реальным schema. |
| outlook.email_assistant | Outlook | Черновики, чтение, ответы | Описание draft/reply шире явно добавленного search_mail; обозначить draft и отсутствие обещания отправки. |
| workspace.files | Workspace | Обычные файлы проекта | Не превращать finish with current read evidence в обязательный повторный read после достаточного read-back; сохранить явные task acceptance. |
| workspace.web_verify_repair | Workspace | HTML/CSS/JS и browser checks | Запуск до исправления выбирать по задаче; не навязывать baseline browser run для очевидной статической правки. |
| workspace.plugin_authoring | Workspace | Plugin source, icon, preview | Уточнить optional plugins.check/apply и fallback: они упоминаются в body, но не объявлены в RequiredTools. |

Источники: [BuiltInSkillProvider](../../src/RNAssistant.Office/Services/BuiltInSkillProvider.cs),
[ExcelBuiltInSkillProvider](../../src/RNAssistant.Office/Services/ExcelBuiltInSkillProvider.cs),
[WorkspaceSkillProvider](../../src/RNAssistant.Core/Services/WorkspaceSkillProvider.cs),
[ChatVisualizationSkillProvider](../../src/RNAssistant.Core/Services/ChatVisualizationSkillProvider.cs),
[Word](../../src/RNAssistant.OfficeHosts/WordAdapter.cs),
[PowerPoint](../../src/RNAssistant.OfficeHosts/PowerPointAdapter.cs),
[Outlook](../../src/RNAssistant.OfficeHosts/OutlookAdapter.cs).
Common skills в Office фильтруются host; Workspace получает собственный набор.
Три VBA skills исключаются в Outlook.

## Сравнение семейств и критерии объединения

| Код | Семейство | Пересечение / решение | Что проверить или уточнить в описании |
|---|---|---|---|
| C | Capability discovery | search находит кандидатов; read раскрывает контракт. Оставить раздельно | Поиск не активирует skill; read не выполняет tool |
| R | Resource reads | find=объекты, search=совпадения, read=содержимое, outline=навигация. Общая semantic-target поверхность уже есть | Scope, representation, лимиты и полнота; search не даёт patch authority |
| D | Resource definitions | Схемы/маппинг/derive имеют разные эффекты. Не смешивать с чтением | Draft против publish; derive не изменяет исходный Office документ |
| F | Workspace files | Целевой кандидат единого файлового API | create/replace/patch различать по намерению и полноте source; delete/restore по журналу |
| A | Authored files | Реальное пересечение с F; объединять при workspace-first cutover | Сейчас document-owned download, а не путь workspace; copy сохраняет точные bytes |
| H | HTML source files | Реальное пересечение с F; единый frontend возможен через destination adapter | Preview/data ownership и атомарность; не обещать единую транзакцию нескольких файлов |
| HD | HTML data | bind/refresh/freeze — lifecycle live data; не обычный file patch | Различать snapshot, связь, обновление и фиксацию |
| HA | HTML assets | List/import/publish — разные этапы | Источник, destination и эффект публикации |
| V | VBA mutation | Общий edit frontend возможен, но простой перенос на files.patch неверен | Live COM, component identity/type, normalization, prepared/read-back и unknown |
| X | Выполнение кода | macro и JS различаются sandbox/host/effects. Оставить | Вход, среда исполнения, результат и возможные эффекты; execution не равен сохранению source |
| M | Markdown/Plan artifacts | Есть пересечение сохранения текста, но разные UI/lifecycle. Кандидат отдельного исследования | Managed artifact против обычного .md файла; save/restore/delete |
| S | Skill management | load активирует инструкции; upsert/delete меняют пакет. Не объединять с read | Зависимости, activation, reference files и отсутствие исполнения при load |
| T | Tool packages | CRUD определения capability, не запуск macro | Contract/version и изменение доступности |
| P | Prompt editing | Не заменять общей файловой записью без owner migration | Какие prompts editable и результат сохранения |
| U | Взаимодействие/план | questions и task_list не универсальный workflow engine | Просить только недостающее; task statuses не доказательство эффектов |
| E | Excel | Host-specific операции; search/replace не эквивалентны plain-text patch | Sheet/range, formulas/values, limits; chart в чате против chart в книге |
| W | Word | Host-native ranges и форматирование; не сводить к file patch | Target, область изменения, сохранение форматирования и read-back |
| PP | PowerPoint | Slide/object identity; общий text-search helper допустим | Slide/object selectors, замена текста против структуры слайда |
| O | Outlook | Поиск/индексация/черновик/изменение — разные эффекты | Mail scope, draft и разрешённые update поля; не обещать send |
| B | Browser verification | Не заменяет чтение source или package validation | Что проверено, что пропущено; успешный запуск не доказывает business behavior |
| PL | Plugin check/apply | Check удерживает candidate; apply активирует. Сохранять раздельно | Exact checked candidate, повторная проверка и подтверждение |

## Все статические tools

Каждая строка ссылается на источник schema/descriptor. Код семейства указывает
на конкретное решение и проверку описания в таблице выше. Это source inventory,
не доказательство успешного исполнения каждого handler на всех hosts.

| Tool ID | Семейство | Источник контракта |
|---|---|---|
| `common.capabilities_read` | C | [CapabilityToolCatalog.cs](../../src/RNAssistant.Office/Tools/CapabilityToolCatalog.cs) |
| `common.capabilities_search` | C | [CapabilityToolCatalog.cs](../../src/RNAssistant.Office/Tools/CapabilityToolCatalog.cs) |
| `common.file_copy` | A | [AuthoredFileToolCatalog.cs](../../src/RNAssistant.Office/Tools/AuthoredFileToolCatalog.cs) |
| `common.file_create` | A | [AuthoredFileToolCatalog.cs](../../src/RNAssistant.Office/Tools/AuthoredFileToolCatalog.cs) |
| `common.file_patch` | A | [AuthoredFileToolCatalog.cs](../../src/RNAssistant.Office/Tools/AuthoredFileToolCatalog.cs) |
| `common.html_assets_import` | HA | [HtmlWorkspaceToolCatalog.cs](../../src/RNAssistant.Office/Tools/HtmlWorkspaceToolCatalog.cs) |
| `common.html_assets_list` | HA | [HtmlWorkspaceToolCatalog.cs](../../src/RNAssistant.Office/Tools/HtmlWorkspaceToolCatalog.cs) |
| `common.html_assets_publish` | HA | [HtmlWorkspaceToolCatalog.cs](../../src/RNAssistant.Office/Tools/HtmlWorkspaceToolCatalog.cs) |
| `common.html_data_bind` | HD | [HtmlWorkspaceToolCatalog.cs](../../src/RNAssistant.Office/Tools/HtmlWorkspaceToolCatalog.cs) |
| `common.html_data_freeze` | HD | [HtmlWorkspaceToolCatalog.cs](../../src/RNAssistant.Office/Tools/HtmlWorkspaceToolCatalog.cs) |
| `common.html_data_refresh` | HD | [HtmlWorkspaceToolCatalog.cs](../../src/RNAssistant.Office/Tools/HtmlWorkspaceToolCatalog.cs) |
| `common.html_data_write` | HD | [HtmlWorkspaceToolCatalog.cs](../../src/RNAssistant.Office/Tools/HtmlWorkspaceToolCatalog.cs) |
| `common.html_workspace_apply_patch` | H | [HtmlWorkspaceToolCatalog.cs](../../src/RNAssistant.Office/Tools/HtmlWorkspaceToolCatalog.cs) |
| `common.html_workspace_delete` | H | [HtmlWorkspaceToolCatalog.cs](../../src/RNAssistant.Office/Tools/HtmlWorkspaceToolCatalog.cs) |
| `common.html_workspace_write_file` | H | [HtmlWorkspaceToolCatalog.cs](../../src/RNAssistant.Office/Tools/HtmlWorkspaceToolCatalog.cs) |
| `common.js_run` | X | [JsToolHandler.cs](../../src/RNAssistant.Office/Tools/JsToolHandler.cs) |
| `common.markdown_restore` | M | [MarkdownDocumentToolCatalog.cs](../../src/RNAssistant.Office/Tools/MarkdownDocumentToolCatalog.cs) |
| `common.markdown_save` | M | [MarkdownDocumentToolCatalog.cs](../../src/RNAssistant.Office/Tools/MarkdownDocumentToolCatalog.cs) |
| `common.office_run_macro` | X | [VbaToolCatalog.cs](../../src/RNAssistant.Office/Tools/VbaToolCatalog.cs) |
| `common.plan_doc_delete` | M | [PlanDocumentToolCatalog.cs](../../src/RNAssistant.Core/Tools/PlanDocumentToolCatalog.cs) |
| `common.plan_doc_restore` | M | [PlanDocumentToolCatalog.cs](../../src/RNAssistant.Core/Tools/PlanDocumentToolCatalog.cs) |
| `common.plan_doc_save` | M | [PlanDocumentToolCatalog.cs](../../src/RNAssistant.Core/Tools/PlanDocumentToolCatalog.cs) |
| `common.prompts_save` | P | [PromptToolCatalog.cs](../../src/RNAssistant.Office/Tools/PromptToolCatalog.cs) |
| `common.questions_ask` | U | [UserQuestionToolCatalog.cs](../../src/RNAssistant.Core/Tools/UserQuestionToolCatalog.cs) |
| `common.resources_derive` | D | [ResourceDefinitionToolHandler.cs](../../src/RNAssistant.Office/Tools/ResourceDefinitionToolHandler.cs) |
| `common.resources_find` | R | [ResourceToolCatalog.cs](../../src/RNAssistant.Office/Tools/ResourceToolCatalog.cs) |
| `common.resources_mapping_publish` | D | [ResourceDefinitionToolHandler.cs](../../src/RNAssistant.Office/Tools/ResourceDefinitionToolHandler.cs) |
| `common.resources_outline` | R | [ResourceToolCatalog.cs](../../src/RNAssistant.Office/Tools/ResourceToolCatalog.cs) |
| `common.resources_read` | R | [ResourceToolCatalog.cs](../../src/RNAssistant.Office/Tools/ResourceToolCatalog.cs) |
| `common.resources_schema_draft` | D | [ResourceDefinitionToolHandler.cs](../../src/RNAssistant.Office/Tools/ResourceDefinitionToolHandler.cs) |
| `common.resources_schema_publish` | D | [ResourceDefinitionToolHandler.cs](../../src/RNAssistant.Office/Tools/ResourceDefinitionToolHandler.cs) |
| `common.resources_search` | R | [ResourceToolCatalog.cs](../../src/RNAssistant.Office/Tools/ResourceToolCatalog.cs) |
| `common.skills_delete` | S | [SkillAuthoringCatalog.cs](../../src/RNAssistant.Office/Tools/SkillAuthoringCatalog.cs) |
| `common.skills_load` | S | [SkillLoadToolHandler.cs](../../src/RNAssistant.Core/Tools/SkillLoadToolHandler.cs) |
| `common.skills_reference_delete` | S | [SkillAuthoringCatalog.cs](../../src/RNAssistant.Office/Tools/SkillAuthoringCatalog.cs) |
| `common.skills_reference_upsert` | S | [SkillAuthoringCatalog.cs](../../src/RNAssistant.Office/Tools/SkillAuthoringCatalog.cs) |
| `common.skills_upsert` | S | [SkillAuthoringCatalog.cs](../../src/RNAssistant.Office/Tools/SkillAuthoringCatalog.cs) |
| `common.task_list_set` | U | [TaskListToolCatalog.cs](../../src/RNAssistant.Core/Tools/TaskListToolCatalog.cs) |
| `common.tools_delete` | T | [ToolAuthoringCatalog.cs](../../src/RNAssistant.Office/Tools/ToolAuthoringCatalog.cs) |
| `common.tools_upsert` | T | [ToolAuthoringCatalog.cs](../../src/RNAssistant.Office/Tools/ToolAuthoringCatalog.cs) |
| `common.vba_apply_patch` | V | [VbaToolCatalog.cs](../../src/RNAssistant.Office/Tools/VbaToolCatalog.cs) |
| `common.vba_delete_module` | V | [VbaToolCatalog.cs](../../src/RNAssistant.Office/Tools/VbaToolCatalog.cs) |
| `common.vba_rename_module` | V | [VbaToolCatalog.cs](../../src/RNAssistant.Office/Tools/VbaToolCatalog.cs) |
| `common.vba_restore_backup` | V | [VbaToolCatalog.cs](../../src/RNAssistant.Office/Tools/VbaToolCatalog.cs) |
| `common.vba_write_module` | V | [VbaToolCatalog.cs](../../src/RNAssistant.Office/Tools/VbaToolCatalog.cs) |
| `excel.add_sheet` | E | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) |
| `excel.add_table` | E | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) |
| `excel.clear_range` | E | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) |
| `excel.create_chat_chart` | E | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) |
| `excel.delete_chart` | E | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) |
| `excel.delete_rows_columns` | E | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) |
| `excel.filter_range` | E | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) |
| `excel.find_cells` | E | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) |
| `excel.format_range` | E | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) |
| `excel.inspect` | E | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) |
| `excel.rename_sheet` | E | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) |
| `excel.replace_cells` | E | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) |
| `excel.sort_range` | E | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) |
| `excel.summarize_columns` | E | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) |
| `excel.upsert_chart` | E | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) |
| `excel.write_range` | E | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) |
| `files.copy` | F | [WorkspaceConversationService.cs](../../src/RNAssistant.Application/Workspace/WorkspaceConversationService.cs) |
| `files.create` | F | [WorkspaceConversationService.cs](../../src/RNAssistant.Application/Workspace/WorkspaceConversationService.cs) |
| `files.delete` | F | [WorkspaceConversationService.cs](../../src/RNAssistant.Application/Workspace/WorkspaceConversationService.cs) |
| `files.move` | F | [WorkspaceConversationService.cs](../../src/RNAssistant.Application/Workspace/WorkspaceConversationService.cs) |
| `files.patch` | F | [WorkspaceConversationService.cs](../../src/RNAssistant.Application/Workspace/WorkspaceConversationService.cs) |
| `files.replace` | F | [WorkspaceConversationService.cs](../../src/RNAssistant.Application/Workspace/WorkspaceConversationService.cs) |
| `files.restore` | F | [WorkspaceConversationService.cs](../../src/RNAssistant.Application/Workspace/WorkspaceConversationService.cs) |
| `outlook.create_draft` | O | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) |
| `outlook.index_archive` | O | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) |
| `outlook.search_mail` | O | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) |
| `outlook.update_mail` | O | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) |
| `plugins.apply` | PL | [PluginTools.cs](../../src/RNAssistant.Application/PluginTools.cs) |
| `plugins.check` | PL | [PluginTools.cs](../../src/RNAssistant.Application/PluginTools.cs) |
| `powerpoint.add_object` | PP | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) |
| `powerpoint.add_slide` | PP | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) |
| `powerpoint.duplicate_slide` | PP | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) |
| `powerpoint.list_objects` | PP | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) |
| `powerpoint.move_slide` | PP | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) |
| `powerpoint.replace_text` | PP | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) |
| `powerpoint.search_text` | PP | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) |
| `powerpoint.set_text` | PP | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) |
| `web.verify` | B | [WorkspaceConversationService.cs](../../src/RNAssistant.Application/Workspace/WorkspaceConversationService.cs) |
| `word.add_comment` | W | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) |
| `word.add_table` | W | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) |
| `word.find_text` | W | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) |
| `word.format_text` | W | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) |
| `word.insert_page_break` | W | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) |
| `word.inspect` | W | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) |
| `word.replace_text` | W | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) |
| `word.write_text` | W | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) |

## Подтверждённые проблемы и ограничения выводов

1. `common.vba_code_editing` всё ещё рекомендует resources_search только внутри модуля;
   после MVP надо добавить scoped VBA search. Это конкретное отставание инструкции.
2. Office provider добавляет четыре resource tools каждому skill, даже task_tracking.
   Это кандидат сокращения избыточных dependencies, не доказанный runtime failure.
3. Word/PowerPoint/Outlook descriptions шире явно добавленных host dependencies.
   Проверить effective pack с bootstrap и capability loading до объявления missing-tool bug.
4. Workspace plugin body использует `plugins.check when available`; manifests содержат
   файловые tools, но не explicit optional plugin dependency. Нужен контракт optional/fallback,
   а не безусловное добавление required tools в средах, где plugin capability отсутствует.
5. Длинные VBA инструкции содержат полезные реальные ограничения, но смешивают routing,
   recovery и учебник языка. Сокращать только с проверкой качества выбора и recovery.
6. Skills могут избыточно предписывать порядок действий. Перенести советы в условные
   рекомендации; security/effect guards оставить runtime. Явные acceptance пользователя сохранять.

## Почему не объединено всё сразу

Предыдущее `keep` означало сохранить в scope поискового MVP, а не запрет на объединение.
Фраза «разные owners» сама по себе не доказывает необходимость разных публичных tools:
единый frontend с typed destination может делегировать тем же owners. Но это отдельный
контракт и миграция consumers, а не безопасное переименование.

Объединение уже сделано на уровне общего text-search engine и schema builder.
Следующий обоснованный кандидат — A + H + F (файловые операции). VBA source edit
можно оценить позже как адаптер, сохранив отдельно component rename/create и execution.
Объединять search/read/load, check/apply и source-write/execute в один action-router
пока нет оснований: число названий уменьшится, но выбор действий и effects останется.
Выбор определяется модельными ошибками, токенами и успешностью задач, не счётчиком tools.

## Что далее

| Этап | Файлы / owner | Зависимости | Минимальная проверка и приёмка |
|---|---|---|---|
| 1. Descriptions и dependencies | Core WorkspaceSkillProvider; Office BuiltInSkillProvider, ExcelBuiltInSkillProvider; host skill declarations; catalogs выше | Нынешний MVP | Host/skill effective-pack fixtures: нет unavailable required tools; documented optional fallback; scope search описан; нет лишних обязательных reads |
| 2. Сравнение выбора инструментов | tests/RNAssistant.Harness/Program.TextSearchTests.cs; docs/proposals/tools-architecture-plan.md | Этап 1 | Один корпус задач, модели и budget; выбор tool, success, число calls, context bytes, incomplete-result handling; без заранее объявленного выигрыша |
| 3. Проектирование file cutover | Application workspace; Office AuthoredFile/HtmlWorkspace catalogs; [ADR-0013](../decisions/ADR-0013-workspace-first-runtime.md) и [MIGRATION_MAP](../stabilization/MIGRATION_MAP.md) | Этап 2 и решение по destination/ownership | Явная матрица create/copy/patch/replace/delete/restore; никакого dual-write; owner и условие удаления adapters |
| 4. Миграция файловых consumers | WorkspaceConversationService, AuthoredFile/HtmlWorkspace handlers, skill bodies и UI presentation | Утверждённый контракт этапа 3 | Stale/ambiguous patch, encoding/newlines, partial failure, cancel, restore, preview/data refs; старый path удалён после переключения |
| 5. Alpha qualification | docs/operations/RELEASE_PROCESS.md, Windows runbook | Exact clean candidate + signer + Windows host | Windows x64/Office x64/VS2022 evidence; signed candidate; только затем tag/distribution |

Сверка: сохраняются v8, semantic targets, runtime-only revisions, existing events/CAS,
exact ToolRuntime binding и source-owner mutation journaling. Следующая файловая миграция
должна следовать принятому workspace-first направлению, а не создавать параллельный store.
Требуют отдельного согласования: публичный unified mutation contract, судьба managed
Markdown/Plan artifacts, расширение исполнения кода и изменение finish admission.
Version preparation не закрывает Windows/Office qualification.
