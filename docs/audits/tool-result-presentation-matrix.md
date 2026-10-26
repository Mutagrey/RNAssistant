# Матрица результатов tools и представление источников

Дата: 2026-10-26. Исходный commit: `98ada78d`.
Owner: Core ModelProtocol/model context; Office ToolResultPresentationService; общий Web UI.

## Объём и достоверность

Все **91 статический tool ID** из встроенных каталогов перечислены ниже без пропусков;
имена сверены с текущими исходниками. Дополнительные строковые совпадения `common.chat_*`,
`files.mkdir`, package install/remove — внутренние операции, старые routing names либо
skill IDs, а не дополнительные публичные registrations. Список доступных tools каждого
запроса по-прежнему определяется host/profile/settings/registry.
Динамические VBA/JS packages и пользовательские определения перечисляются в runtime;
их произвольные реализации не объявлены проверенными по статическому каталогу.

Матрица — аудит producer/projection/presentation paths и целевые проверки общих
инвариантов. Она **не означает 91 успешный запуск реального Office**, проверку каждой
ветви каждого адаптера или квалификацию модели. Минимальные проверки и gaps — ниже.

## Общий путь для каждой строки

| Представление | Источник и сохраняемое содержимое |
|---|---|
| Runtime result | `ToolResult { Status, Message, DataJson, Resources }`; DataJson — произвольное JSON value, не единый domain DTO. `Ok/Error/Unknown` не заменяют evidence эффекта |
| Durable history | `ToolResultWire` (внутренний v1 envelope), canonical call ID и typed evidence/effect/progress; events — authority, крупные тела — immutable CAS |
| Activity / лента | Отдельная UI projection результата. Краткое описание и статус уже даёт activity; typed blocks читаются лениво по точным chat/run/call; workspace route также проверяет workspace/session. Сохранённый E, действие и объект показываются в receipt, raw JSON берётся из того же canonical result/CAS |
| Model | Core `ModelResultProjection`, Office `ModelToolResultProjection` для domain families; compiler применяет currentness/budget/CAS; финальный `ModelReferenceProjection` добавляет E и допустимые R |
| Диагностика | Точный сохранённый HTTP request/response и typed evidence links. Наличие результата/CAS не означает, что тело вошло в конкретный запрос |
| UI source caption | `ToolSourcePreviewDto`: reference?, target, view?, coverage?, line?/column?, excerpt?, excerptTruncated. Это disposable display data, не новая база и не вход tool |

Errors сохраняют причину/recovery; unknown сохраняет неизвестный эффект и запрет
автоматического replay. После folding остаётся E/outcome; отсутствие тела не выдаётся
за прочтение. Неизвестные business shapes доступны в JSON, UI не придумывает diff,
source identity, координаты или успешность.

## Матрица семейств

В колонке UI описано текущее правило отображения ленты, а не гарантия, что
любой JSON получит специальную карточку. `R` разрешает только существующий ChatStore
binding. Document/workspace используют общий renderer; наличие diff зависит от доступного source owner.

| Код | Семейство / runtime data | Модель | Лента / ограничения |
|---|---|---|---|
| C | **Capabilities** — kind/id + items или tool schema/skill/reference content | Core capability sanitization; Office дополнительно проверяет актуальность каталога | Список/текст при известной форме; schema — JSON. Тело capability не получает автоматический R |
| R | **Resources** — find: items; search: matches/coordinates/completeness; outline: parts/symbols; read: text/table/tree + coverage | Общая закрытая projection; ref только на однозначно связанном read/source view | Read: подпись источника + текст/таблица/дерево; find/search: target + snippet без R; outline: JSON |
| A | **Authored files** — target/name/bytes/changes, exact artifact revision отдельно | Generic data, exact runtime references убраны | Краткий исход; сравнение только из связанного owner evidence; details JSON |
| HA | **HTML assets** — Коллекции assets, import/publication receipts | Office HTML projection удаляет runtime state | Список при items; прочие сведения JSON; без автоматических R |
| HD | **HTML data** — binding/freeze/refresh/write receipts и domain data | Office HTML projection; authority остаётся runtime | Исход/таблица/JSON; эффект из runtime, не из полей данных |
| H | **HTML source mutations** — file/member receipts, change/recovery data | Office HTML projection; compiler выделяет current source views | Исход + подтверждённое сравнение; R не выдаётся всей операции |
| X | **Execution** — Произвольный JSON/text от JS/VBA, failures/recovery | Generic или VBA projection; arbitrary output не становится authority | Распознанный текст/список/таблица; прочее JSON. Ни один ref из script output не назначает R |
| M | **Markdown / Plan documents** — target/title/version/size, save/delete/restore outcome | Plan runtime IDs удалены; остальные domain поля сохранены | Краткий исход; сравнение из владельца при наличии; JSON |
| P | **Prompts** — typed authoring result | Office PromptToolProjection | Краткий исход и доступные поля; JSON |
| U | **Questions / Task list** — question set либо task-list/step status document | Question/task runtime IDs исключены из model data; user interaction имеет свой DTO | Вопросы — отдельные карточки; задачи — существующий task UI; E только по сохранённому terminal |
| D | **Resource definitions** — schema/mapping/derived definition receipts | Generic/owner projection; live data читается отдельно | Краткий исход name/kind, JSON; запись определения не означает чтение derived body |
| S | **Skills** — selected-skill receipt либо typed skill/reference authoring result | Core skill selection; Office SkillAuthoring projection | Краткий исход; instruction body попадает в pack своим путём; без guessed R |
| T | **Tool authoring** — typed tool publication/delete/diagnostic result | Office ToolAuthoring projection | Краткий исход/diagnostics/JSON, без превращения code fields в resource evidence |
| V | **VBA mutations** — component/source mutation and restore receipt, recovery | Office VBA sanitizer + typed recovery; read-back/source views отдельно | E + outcome; exact journal diff при наличии; никакого inferred before/after |
| E | **Excel** — inspect/aggregate object, find matches, cell/table/chart/sheet mutation receipts | Office Excel projection с известными полями | Таблицы/списки/краткий исход/JSON. Domain coordinates не превращаются автоматически в строку файла |
| F | **Workspace files** — path/targetPath/changed/restored/moved/deleted/code | Core workspace mutation allowlist | Краткий исход и owner comparison при наличии; read-back source отдельно |
| O | **Outlook** — mail search matches, archive totals, draft/update receipts | Generic + Office runtime-state removal для соответствующих операций | Список/исход/JSON; отправка/изменение не выводятся из текста результата |
| PL | **Plugins** — check: retained candidate summary; apply: publication summary | Generic; check и apply сохраняют разные effects | Краткий исход/JSON; check не объявляется активацией |
| PP | **PowerPoint** — slide/shape lists, source search matches, mutation receipts | Generic + Office runtime-state removal | Списки/исход/JSON; slide/shape-local координаты сохраняются как domain data |
| B | **Browser verification** — verification/evaluation report | Generic projection | Краткий исход/распознанный текст/JSON; вывод проверки не является source citation |
| W | **Word** — document inspection, source matches, mutations | Generic + Office runtime-state removal | Списки/исход/JSON; Word ranges не подменяются file line numbers |

## Полный индекс статических tools

Каждая строка наследует общий путь выше и указанную строку семейства.
Исходный каталог — ссылка на schema/registration; динамические экземпляры вынесены ниже.

| Tool ID | Строка матрицы | Каталог / registration |
|---|---|---|
| `common.capabilities_read` | C | [CapabilityToolCatalog.cs](../../src/RNAssistant.Core/Tools/CapabilityToolCatalog.cs) |
| `common.capabilities_search` | C | [CapabilityToolCatalog.cs](../../src/RNAssistant.Core/Tools/CapabilityToolCatalog.cs) |
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

## Динамические и особые результаты

| Вариант | Модель / durable | UI / приёмка |
|---|---|---|
| Пользовательский VBA/JS tool | Exact descriptor/policy/binding + общий result envelope; arbitrary data | Краткий исход и распознанные bounded blocks/JSON; поле ref внутри бизнес-данных не становится ссылкой |
| Read text/source/metadata/structure/formulas | ResourceReadProjection, исходная representation и coverage | Подпись target + view/range + цитата только из фактически имеющегося текста |
| Table/tree | Typed table/tree + coverage, no forced text conversion | Таблица с точными display strings; дерево с pointer/type/value, явная частичность |
| PDF/page/char slices | Page/character coverage остаётся самостоятельным видом | Подпись страниц или символов, не выдуманные file line numbers |
| Media / attachments | Отдельные selected content parts и exact request manifest | Существующие media viewers; данная правка не создаёт text quote/R caption из бинарного вложения |
| Several resources | Typed evidence остаётся списком; compiler может выдать отдельные source views | Нет присвоения R списку по позиции. Одна подпись read требует одного однозначного наблюдения |
| Search/find items | Targets/snippets/coordinates остаются navigation data | Компактная подпись есть; **R отсутствует**, пока producer не сохраняет per-item scoped provenance |
| Large/omitted/missing CAS | Оригинал не заменяется новыми байтами; budget/availability явные | Ограниченная preview либо unavailable notice; E/outcome сохраняются, R к подставленному activity text не приклеивается |
| No data / scalar / unknown shape | Исходный JSON value и message сохраняются | Activity summary/status + JSON; особый visual block не обещается |

## Формат подписи и навигация

```text
R3@v1 — src/A.cs, строка 42: «искомый текст»
src/B.cs, строка 17, столбец 2: «другое совпадение»
```

Первая строка возможна только при точном binding. Вторая — честный вид поискового
совпадения без per-item provenance. Несколько срезов одной ревизии имеют один R,
но разные coverage; строковые диапазоны показываются с исключённым end (`42..45`
означает строки 42–44). Цитата — ограниченный префикс исходного текста без HTML execution;
многоточие обозначает только сокращение preview. Полное доступное тело раскрывается
в той же карточке, не загружается заново с live head и не заменяется соседней ревизией.

В ленте источник берётся из **того же canonical read**, что содержит evidence;
activity preview с совпадающим именем не используется для binding. Ссылка-карточка
раскрывает сохранённый фрагмент. Переходы E/R из произвольного текста ответа и
автоматическая навигация к строке live-редактора в этот scope не входят.

В диагностике общая подпись появляется в «Источники и полнота» фактического read/source
message, когда saved link подтверждает body inclusion, ref/view/coverage совпадают,
а текст взят из точного request. Для omissions/unknown binding показываются существующие
статус/coverage без выдуманной цитаты. Карточка ресурса по-прежнему даёт технические
сведения обо всех наблюдениях; это не заявление о доставке всех его байтов.

## Дополнительный аудит мутаций и прочих исходов — 2026-10-26

| Исход / данные | Текущее отображение | Ограничение |
|---|---|---|
| `VerifiedChange` | «Изменения подтверждены»; сохранённый diff при наличии exact before/after | Доказанный эффект и доступность diff — разные факты |
| `VerifiedNoChange` | «Без изменений»; пустое подтверждённое сравнение — «Изменений нет» | Отсутствие сравнения само по себе не означает no-op |
| `Unknown` | «Результат не подтверждён — нужна проверка»; Inspector объясняет проверку перед повтором | Запланированный текст может показываться только как предварительное сравнение |
| Отказ до dispatch / ошибка | Краткая причина и error code; полный текст и параметры в деталях | Описание неизвестного кода остаётся общим; outcome определяется evidence, не текстом |
| Создание/удаление/переименование/copy | Diff owner показывает действие и объект; rename — старое → новое имя; large-file hunks/copy имеют отдельные пояснения | Это coverage retained sources/VBA journal, не универсальный diff всех Office объектов |
| Excel/Word/PowerPoint/Outlook, authoring/settings receipts | Typed effect + исходный result message/JSON; известные content shapes дают текст/таблицу/список | Есть общий action/target/outcome и подробный E receipt; domain counts не выдумываются из произвольного JSON |
| Capabilities, skills, задачи, вопросы, custom/scalar data | Известные summaries/блоки, отдельные question/task UI; остальное исходный текст/JSON | Произвольные `before`, `after`, `ref` не интерпретируются как authority |

Проверенные владельцы: [activity rendering](../../web/js/app-agent-activity.js),
[result presentation](../../src/RNAssistant.Office/Services/ToolResultPresentationService.cs),
[retained comparisons](../../src/RNAssistant.Office/Services/RunChangesService.cs),
[diff UI](../../web/js/app-run-changes.js) и
[Inspector effect explanation](../../web/js/app-run-journal.js).

Исправлено после аудита:

1. Фактические и предварительные счётчики разделены. В fixture фактическое сравнение
   показывает `+7/−4`, предварительное — `+1/−1`. Planned-only не получает actual total;
   hunks/copy без полного сравнения не получают выдуманный `+0/−0`.
2. Workspace использует scoped `getProjectToolResultPresentation` и общий renderer.
   Backend проверяет workspace/session и exact run/call; чужой scope отвергается.
   Canonical result/CAS даёт и typed blocks, и ограниченный исходный JSON для всех
   семейств, даже когда activity не содержит DataJson. Второй raw body из activity
   не дублируется. Pending/ошибки не выдаются за completed.
3. `Core.Tools.ToolDisplayCatalog` теперь владеет общими display metadata и target
   selection; `ToolResultSummaryProjection` — summaries известных результатов.
   Старый Office catalog и дублирующие summarize/target paths удалены. В свёрнутой
   строке видны действие, конкретный объект и исход; runtime attempt сохраняет свой
   call identity. Чтения разных навыков и referencePath различимы до раскрытия.

Примеры (presentation, не model wire):

```text
Изучаю · excel-analysis
Чтение навыка · excel-analysis · Загружено
Запись диапазона · Продажи!B2:D121 · Изменения подтверждены
Выполнение макроса · ОбновитьОтчёт · Результат не подтверждён — нужна проверка
```

При раскрытии — `E7 · действие · объект`, сохранённые content/diff blocks и
`Результат JSON`. E не заменяет effect evidence; R@v относится только к связанному
source/read-back. Workspace route не читает Office journal: при отсутствии сохранённого
сравнения показывает это явно. Полный diff всех файловых и нетекстовых мутаций,
per-domain counts и Windows/WebView2 delivery не объявляются реализованными.

Проверки исправления: 9 целевых Harness checks (4 presentation, workspace accepted/
failed-read history, scoped project history, catalog metadata, causal projection);
3 Chrome suites — tool result, activity layout, run changes. Сборка без ошибок,
2 прежних PDF platform warnings. Проверены [строки вызовов](evidence/tool-attempt-targets.png)
и [раздельные счётчики](evidence/mutation-comparison-counts.png). Источники — synthetic
fixtures, не реальные Office-документы. Полный Harness/Office validation не запускались.

## Проверки и оставшиеся gaps

Результат текущей проверки: **14/14 Harness checks**, обе Web suites ниже прошли
в Chrome. [Лента](evidence/source-caption-chat.png),
[узкая лента](evidence/source-caption-chat-narrow.png) и
[диагностика](evidence/source-caption-diagnostics.png) проверены визуально на
синтетических данных. Это evidence представления и общих инвариантов, не запуска
всех 91 tools и не тест модели.

- `tool presentation:`: exact run/call, bounded content, inert literals, questions/CAS,
  journal correlation; новый case — все три result roles, точный read из CAS вместо
  чужого activity preview, scoped references, ambiguous evidence, missing payload,
  Error/Unknown без успешной source citation, search без invented R, tree preview.
- `model refs:`: единый allocator и E admission, scope/revision, slices, omission,
  repair/fallback и canonical preservation; UI и модель теперь используют один resolver.
- Web `tool-result-presentation.test.js`: подпись/раскрытие exact slice, exclusive line
  bounds, unbound match и XSS; существующие cancellation/revision/chat guards сохраняются.
- Web `model-context-layout.test.js`: подпись из actual request + typed links и визуальные
  состояния. Это Chrome fixture, не Office/WebView2 qualification.
- Existing coverage семейства: `text search:`, `resources: model projection preserves literal source`,
  `resources: table projection distinguishes selected rows`, `resource cutover: schema mapping derived publication`,
  `plan mode: question presentation survives storage`, guarded file/VBA/HTML mutation tests.
  Их наличие не означает, что все они перезапускались для данной UI-правки.

Открыто: per-item provenance для search/find и host-specific search; captions для media;
полная проверка реальных domain payloads на Windows/Office; live-model A/B текущего
контракта; generic business shapes за пределами известных блоков остаются JSON.
Ни один из этих gaps не скрывается выдачей ссылки по target, позиции или тексту.
