# Каталог tools и skills после объединения — 2026-10-29

Owner: Core/Office catalogs, Application Workspace; проверка — Harness. Сравнение
`3765275b` (перед `2522a053`) с `ac81c49c` и исправлением имени workspace skill в этом срезе.
Это встроенные публичные ID всей библиотеки, включая условные и manual-only записи,
а не одновременно разрешённые вызовы модели. Динамические пользовательские tools,
установленные skills и команды plugin releases из Git не посчитать.

## Сколько осталось

| Каталог | До | Сейчас | Разница |
|---|---:|---:|---:|
| Excel | 60 | 47 | −13 |
| Word | 52 | 39 | −13 |
| PowerPoint | 52 | 39 | −13 |
| Outlook | 42 | 34 | −8 |
| Workspace | 18 | 15 | −3 |
| Объединение всех встроенных ID | 91 | 71 | −20 |

Office-строки показаны с выключенным `EnableAgentJavaScript` (default). Включение
добавляет один `js.run` на каждом Office host. Доступный browser добавляет
`web.verify`; source workspace зарегистрированного plugin — `plugins.check/apply`.
Все три условных семейства уже учтены в объединении **71**, складывать строки
таблицы нельзя. Начальный `CallableToolProfile.Core()` содержит **10** доступных
Core tools; lifecycle и специализированные tools подключаются через schema/skill.
Mode, policy, host и exact binding продолжают ограничивать выполнение.

**Skills: 16 → 16**. Excel/Word/PowerPoint видят по 10 built-ins, Outlook — 7,
Workspace — 4; общие определения входят в несколько каталогов. Весь skill body
загружается только при выборе, наличие skill в каталоге не означает его полного
присутствия в каждом запросе модели.

## Что исчезло, а что переименовано

**20** отдельных source/lifecycle операций заменены **6** общими:

| Общий инструмент | Прежние публичные ID |
|---|---|
| `core.patch` | `common.file_patch`, `common.html_workspace_apply_patch`, `common.vba_apply_patch`, `files.patch` |
| `core.write` | `common.file_create`, `common.html_workspace_write_file`, `common.markdown_save`, `common.vba_write_module`, `files.create`, `files.replace` |
| `resources.copy` | `common.file_copy`, `files.copy` |
| `resources.delete` | `common.html_workspace_delete`, `common.vba_delete_module`, `files.delete` |
| `resources.move` | `common.vba_rename_module`, `files.move` |
| `resources.restore` | `common.markdown_restore`, `common.vba_restore_backup`, `files.restore` |

Дополнительно появился `core.replace`: это counted literal/regex replacement,
а прежний `files.replace` был полной перезаписью и перешёл в `core.write`.
Первый этап: **91 − 20 + 6 + 1 = 78**. Продолжение удалило ещё 7 public ID:
`plan.document.save/restore/delete`, `prompts.save`, `skills.reference.upsert/delete`,
`html.data.refresh`. Итог: **71**, на 20 (22%) меньше исходных 91. Plan, prompt и
reference source теперь используют общие tools и прежних владельцев. Task List
остаётся типизированным; HTML live bindings обновляются при открытии handle.
Ещё **66** tools только переименованы по dotted convention; **5** ID не менялись.
Все 86 прежних заменённых имён отсутствуют в публичном каталоге; это не 86 удалённых возможностей.
В [карте миграции](../tool-name-migration.json) нет unmapped старых public tools.

## Skills, инструкции и исправленная чистка

- Исправлен случайный двойной префикс `skills.skills.workspace.files` →
  `skills.workspace.files`; встроенные ссылки и CLI fixtures переключены. Старое имя
  не alias; сохранённая selection требует выбора текущего skill. История и custom packages не удаляются.
- Общая грамматика patch, якоря, вставки/EOF и финальный EOL описаны в его schema.
  Workspace/VBA skills ссылаются на неё; удалены дубли общих правил. VBA сохраняет
  VBE normalization, whole-CodeModule read-back, CodeOnly и остальные предметные ограничения.
- Проверка ссылок читает полные dotted ID, включая Core/Workspace и поставляемые
  Office инструкции/default prompts. Старый regex обрезал ID после первой точки
  и не проверял многие новые namespaces. В проверенных поставляемых инструкциях
  не осталось ссылок на retired tools; пользовательские prompts/packages не проверялись.
- Два catalog fixtures проверяют общие schemas/Core и отсутствие приватных VBA
  регистраций. Skill Library fixture использует действующий общий chat projection,
  проверяет обновление каталога, сохранение выбора и отказ старого payload.
- Оставшиеся `files.create/replace/move/...` в WorkspaceFileService — внутренние
  journal/effect operations, включая восстановление удержанной истории. Их нельзя
  удалять как будто это публичные инструменты. `CapabilityNaming` хранит retired ID
  для явного отказа; audit/protocol history тоже не подлежит массовому переименованию.

## Что имеет смысл сократить дальше

1. **Приватный `DomainOperation` bridge → typed owner commands**: меньше строковой
   маршрутизации и приватных argument dictionaries, без изменения 71 публичного ID.
   Removal gate уже зафиксирован в [MIGRATION_MAP](../stabilization/MIGRATION_MAP.md#universal-text-tools--2026-10-28).
2. **Закончить owner/integration fixtures** со старыми private/public ожиданиями.
   Не удалять проверки unknown, journal или recovery ради зелёного полного Harness;
   текущий объём — в [BACKLOG](../stabilization/BACKLOG.md#интеграционные-fixtures-после-перехода-на-core-tools--2026-10-28).

Дальнейшее сокращение skills стоит оценивать по фактическому размеру selected request.
Короткий `skills.text.search.replace` позволяет получить узкую Office-процедуру без
полного host skill. UserForms, browser verification, tool/skill/prompt authoring
задают разные условия использования и подтверждения. Их объединение уменьшит число
названий, но может увеличить контекст. `html.data.*`, `html.assets.*`, Excel/Word/PPT
структурные операции не эквивалентны редактированию исходного текста.

## Воспроизведение и границы проверки

```sh
dotnet run --project tests/RNAssistant.Harness -- --capability-inventory /tmp/rna-capabilities.json
```

Диагностика строит Office/Workspace каталоги в временных stores; Office adapters — fake.
Plugin source additions получены через реальную регистрацию временного plugin.
Условный `web.verify` учтён по source declaration, browser не запускается.
Baseline получен тем же diagnostic helper в архивной копии `3765275b`; рабочая ветка
и пользовательские stores не переключались. JSON содержит точные ID каждой поверхности.
Сборка/проверки текущего среза выполнены в изолированной копии HEAD + его изменений,
чтобы не смешивать параллельную незавершённую bridge-правку.

Проверено: два catalog сценария, ссылки built-in skills/default prompts, загрузка
пакетов всех Office hosts, CodeOnly/VBA instructions, strict VBA request budget;
Web Skill Library — 5 проверок. Полный Harness, CLI smoke и Office/VSTO validation не запускались.
Открыты реальный Windows/Office/WebView2, межмодульный VBE casing, COM partial effect,
CodeOnly UserForms и качество инструкций на Qwen/GLM/DeepSeek. Эти host-neutral
проверки не квалифицируют VBE и не измеряют качество модели.

## Текущие ID по группам

| Группа | Количество | ID |
|---|---:|---|
| core | 10 | `core.ask`, `core.find`, `core.patch`, `core.read`, `core.replace`, `core.search`, `core.skills.load`, `core.tools.read`, `core.tools.search`, `core.write` |
| excel | 16 | `excel.cells.find`, `excel.cells.replace`, `excel.chart.delete`, `excel.chart.upsert`, `excel.chat.chart.create`, `excel.columns.summarize`, `excel.inspect`, `excel.range.clear`, `excel.range.filter`, `excel.range.format`, `excel.range.sort`, `excel.range.write`, `excel.rows.columns.delete`, `excel.sheet.add`, `excel.sheet.rename`, `excel.table.add` |
| html | 6 | `html.assets.import`, `html.assets.list`, `html.assets.publish`, `html.data.bind`, `html.data.freeze`, `html.data.write` |
| js | 1 | `js.run` |
| office | 1 | `office.macro.run` |
| outlook | 4 | `outlook.archive.index`, `outlook.draft.create`, `outlook.mail.search`, `outlook.mail.update` |
| plugins | 2 | `plugins.apply`, `plugins.check` |
| powerpoint | 8 | `powerpoint.object.add`, `powerpoint.objects.list`, `powerpoint.slide.add`, `powerpoint.slide.duplicate`, `powerpoint.slide.move`, `powerpoint.text.replace`, `powerpoint.text.search`, `powerpoint.text.set` |
| resources | 9 | `resources.copy`, `resources.delete`, `resources.derive`, `resources.mapping.publish`, `resources.move`, `resources.outline`, `resources.restore`, `resources.schema.draft`, `resources.schema.publish` |
| skills | 2 | `skills.delete`, `skills.upsert` |
| task | 1 | `task.list.set` |
| tools | 2 | `tools.delete`, `tools.upsert` |
| web | 1 | `web.verify` |
| word | 8 | `word.comment.add`, `word.inspect`, `word.page.break.insert`, `word.table.add`, `word.text.find`, `word.text.format`, `word.text.replace`, `word.text.write` |

## Все встроенные skills

- `skills.chat.visualization`
- `skills.excel.analysis.reporting`
- `skills.html.workspace.authoring`
- `skills.outlook.email.assistant`
- `skills.powerpoint.deck.building`
- `skills.prompt.authoring`
- `skills.skill.authoring`
- `skills.task.tracking`
- `skills.text.search.replace`
- `skills.tool.authoring`
- `skills.vba.code.editing`
- `skills.vba.userform.authoring`
- `skills.word.document.editing`
- `skills.workspace.files`
- `skills.workspace.plugin.authoring`
- `skills.workspace.web.verify.repair`

Продолжение подсчёта: `--capability-inventory /tmp/rna-capabilities-final.json`
подтвердило 71 tool / 16 skills на текущем diff, включая все условные семейства.
`resources.outline` и `html.assets.list` сохранены: первое даёт координаты/навигацию,
второе — состав asset package, это не эквивалент общего поиска текста.
Library группирует только по первому сегменту, полный ID остаётся видимым.
Карта миграции обновляет только structured dependencies по явному запуску;
старые arguments и bodies требуют ручного пересмотра. Для бывшего refresh
`core.read` означает новое чтение; отдельной операции refresh больше нет.
