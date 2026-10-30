# Единые текстовые инструменты

Владелец публичного контракта — Core Tools; маршрутизации — Application Workspace
и Office Tools; записи, journal и read-back — существующий domain owner.
Library и лента используют этот контракт без нового durable store.

## Публичная поверхность

В каждом окружении начальный `resource-core-v1` содержит доступные по mode/policy:
`core.find`, `core.read`, `core.search`, `core.write`, `core.patch`, `core.replace`,
`core.ask`, `core.tools.search`, `core.tools.read`, `core.skills.load`.
Остальные tools подключаются через exact schema admission или зависимости skill.
Это относится и к `resources.move/copy/delete/restore/outline`, Excel/Word/PowerPoint,
HTML data и plugin tools. Core не обходит read-only, режим и доступность host.

| Tool | Контракт |
| --- | --- |
| `core.write` | `target`, `content`; неявный upsert: отсутствующий ресурс создаётся, существующий получает полный новый текст |
| `core.patch` | `targets: [{target, edits}]`; точные `old/new` и необязательные `anchors_before/anchors_after` |
| `core.replace` | `target`, `query`, `replacement`, `expectedMatches`, опционально `mode`, `matchCase`; все совпадения проверенного поиска |
| `resources.move` | `target`, `destination`; перенос или переименование внутри допустимого owner, без перезаписи назначения |
| `resources.copy` | `target`, `destination`; независимая копия, без перезаписи назначения |
| `resources.delete` | `target`; удаление по правилам owner, с сохранённым восстановлением там, где оно поддержано |
| `resources.restore` | `target`, опционально `version`; явное восстановление сохранённого состояния |

Модель выбирает семантический target, а не URI/revision/hash. Примеры: `src/app.js`
в проекте, `VBA module: Module1`, `HTML file: index.html`, `note: Review.md`,
`file: report.txt` в Office. Для существующих ресурсов предпочтителен точный target
из `core.find/read`. Короткий базовый target разрешается только при единственности;
совпадающие названия не дают права выбрать произвольный ресурс.

`core.write` требует полного текущего исходника в принятом контексте модели при
перезаписи. Отсутствие ресурса и версия существующего фиксируются до подтверждения
и проверяются перед записью. Создание во время ожидания не превращается в замену.
Пустой `content` допустим. `resourceType` — необязательная подсказка при создании
VBA-компонента (`StdModule`, `ClassModule`, `MSForm`); тип существующего не меняется.
Workspace owner не переиспользует отсутствующий путь с прежней resource identity:
для управляемого удаления сначала нужен явный restore. Никакого удаления истории нет.

## Patch: old/new и упорядоченные якоря

```json
{"targets":[{"target":"VBA module: Module1","edits":[{"old":"total + i","new":"total + i * 2","anchors_before":["Public Sub ProcessData()","    For i = 1 To 10"],"anchors_after":["    Next i","End Sub"]}]}]}
```

Единственный формат edit: обязательная непустая строка `old`, обязательная строка
`new` и необязательные массивы `anchors_before`/`anchors_after`. Все объекты закрыты
для неизвестных полей. Только существующие источники; создание, пустой файл и
полная перезапись — `core.write`, массовая замена — `core.replace`, lifecycle —
`resources.*`. Старые `before/after`, `context`, `op`, `text`, `position`, `anchor`,
`{patch}`, JSON-hunks и координаты не принимаются. Несовместимые pending approvals
отклоняются; история, исходники, evidence и пользовательские данные сохраняются.

`old` — точная подстрока или многострочный фрагмент фактически доставленного модели
исходника S0. `new: ""` удаляет фрагмент. Для вставки `new` сохраняет опорный `old`
и добавляет текст с явно указанными переводами строк. Например:

```json
{"targets":[{"target":"src/app.js","edits":[{"old":"return x*2","new":"return x*3"}]}]}
```

```json
{"targets":[{"target":"VBA module: Module1","edits":[{"old":"End Sub","new":"    Debug.Print total\nEnd Sub","anchors_before":["Sub ProcessData()"]}]}]}
```

Второй пример допустим, только если условия определяют один `End Sub`.
`Sub ProcessData()` не ограничивает поиск телом процедуры.

Якоря — упорядоченные **полные логические строки без EOL**, строго до начала или
после конца `old`. Возможны промежутки, повторения и пустые строки; это текстовые
условия, не AST-границы. Без якорей достаточно уникального `old`. Каждый диапазон
и используемый якорь должны входить в доставленную coverage текущей версии.
Недоставленная часть источника не служит скрытым контекстом; внутренние guard reads
и история tool calls не дают модели права на правку невидимого текста.

Допустимо ровно одно различное место правки. Несколько цепочек якорей к нему
допустимы; разные диапазоны дают `ambiguous_context`. Нет выбора первого/ближайшего
совпадения, fuzzy/regex fallback или переноса на другую ревизию. Все edits разрешаются
на одном S0; пересечения отвергаются до записи. Зависимость от результата другого
edit требует отдельного вызова по доставленному verified after-source или новому чтению.

Exact matching имеет приоритет. Для VBA при отсутствии exact совпадения сохраняется
проверенная канонизация **полных строк**, сохраняющая значимые tokens, literals и
comments. Внутристрочные фрагменты остаются exact. Канонизация не меняет EOL и не
отменяет revision/authority guard: конкурентное изменение требует нового чтения.

### Границы и переводы строк

- `old/new` буквальны: LF, CRLF и CR различаются, EOL автоматически не добавляется
  и не выбирается по соседней строке. Границы не разрывают Unicode scalar или CRLF.
- Неприкосновенные области, BOM, смешанные EOL и final newline сохраняются.
  Перевод строки изменяется только явно указанным `old/new`.
- Вставка в начало/конец непустого файла использует наблюдавшийся непустой фрагмент.
  Замена `body` на `body\nfooter` добавляет разделитель, на `bodytail` — нет.
  Скрытой ветки insert и `old: ""` нет.
- Частичное наблюдение разрешает только покрытые фрагменты и полные строки-якоря;
  оно не разрешает полную перезапись или использование недоставленных якорей.

### Подготовка, подтверждение и выполнение

`ResourcePatchBatchHandler` координирует существующих owners. Для всех целей до
записи проверяются resolution, policy, наблюдённая версия, coverage, якоря,
пересечения и итоговый candidate. Повторные секции одного разрешённого ресурса,
включая aliases, отклоняются. Любой отказ на этой стадии отвергает весь пакет.

Prepared state хранит exact источники, owner guards и bounded before/after preview.
Подтверждение показывает этот пакет по целям; сокращение preview явно отмечено.
После подтверждения все цели проверяются заново до первой записи. Каждый owner
снова проверяет свой источник непосредственно перед своей записью. Подготовка
одного ресурса не гарантирует, что предыдущая запись не изменит следующий.

Запись последовательная, остановка на первом отказе или unknown. Это **не общая
атомарная транзакция**. Нет автоматического отката или повтора пакета. Сначала
нужна сверка фактического состояния; зависимые изменения выполняются отдельными
вызовами с промежуточным чтением. В одном вызове объединяются связанные независимые
правки. HTML сохраняет общий snapshot workspace, но patch дополнительно фиксирует
существующий authority head каждого файла. После записи соседнего файла проверяются
тот же member, его revision/generation и точный исходный текст. Изменение с возвратом
прежнего текста также отклоняется. Актуальность выбранного workspace проверяется
владельцем перед каждой публикацией; другой чат или исторический snapshot не дают
права продолжать запись. Исходный snapshot для preview/истории не подменяется.
Публичный contract и batch prepared state имеют version 3, binding — `.resource.v5`.
Внутренний Office owner state остаётся version 2; прежний публичный patch pending-state несовместим
и требует новой подготовки, без удаления истории.

Результат `type=rnassistant.patch` содержит `phase`, `writeStarted`, `partial` и
строку для **каждой** цели, включая остаток:

| `status` | Факт |
| --- | --- |
| `Prepared` | Подготовлено, ожидает подтверждения; результат записи не заявлен |
| `Changed` | Изменение подтверждено read-back |
| `Unchanged` | Изменений не потребовалось |
| `Rejected` | Отказ без эффекта этой цели |
| `Unknown` | Эффект этой цели не подтверждён |
| `NotStarted` | Выполнение этой цели не начиналось |

`target` связывает результат с секцией; `currentTarget`, если возвращён owner,
даёт актуальный semantic target новой версии для следующего чтения. Top-level
`ok` допустим только при завершении всех целей; остановка с уже подтверждёнными
изменениями — `error` и `partial=true`, любая неопределённость — `unknown`.
Модель сопоставляет `TOOL_RESULT.tool_call_id` с принятым вызовом и проверяет
`data.targets` **по каждой цели**. Сам вызов и подтверждение ничего не доказывают.
Отказ в подготовке сообщает, что ни одна цель этого вызова не записана;
после частичного выполнения `Rejected/NotStarted` не засчитываются как сделанные,
а предыдущие `Changed` остаются отдельными подтверждёнными фактами.
Если owner сообщил об изменении без exact after-state именно этой цели,
пакет возвращает `unknown`; ссылка на соседний ресурс того же provider не служит
подтверждением и не попадает в следующий source context.
Подтверждённые дочерние effects сохраняются даже при последующем отказе/unknown.
Их публикации/journals — прежние; общий effect лишь содержит ссылки на эти факты.
HTML after-source раскрывается из существующего retained workspace snapshot как
тот же file source, который возвращает `core.read`. В пакетном результате выбираются
только подтверждённые текущие member revisions; соседние и Unknown-цели не получают
source evidence. Ссылка `current-html-file` связывает этот immutable source с exact
after-state; проверка document scope сохраняется.
Лента и CLI показывают эти цели отдельно; summary не превращает известный частичный
отказ в дополнительный unknown. Доставленные verified after-source сохраняют свой
semantic target. Для больших файлов без включённого after-source нужен новый
`core.read` перед следующей зависимой правкой.

«Исходный snapshot» относится к **одному вызову**. Следующий вызов строится уже
по результату предыдущего: `old` и якоря берутся из доставленного
актуального after-source либо нового `core.read`. Остальные предыдущие изменения
сохраняются; намеренная отмена выражается обратными edits по текущему тексту.
При сомнении модель перечитывает цель, а не восстанавливает её из старого чтения.
Read-back подтверждает применение текста, а не правильность алгоритма в коде.

Подтверждённый исходник отдельной цели может доставляться модели и сохраняться
через compaction при общем `error`/`unknown` пакета. Отбор идёт по статусу цели,
её exact after-reference и актуальности authority; неопределённый after-source
не выдаётся за подтверждённый. Отсутствие актуального наблюдения VBA диагностируется
как необходимость нового чтения, даже если старые строки уже не находятся.

Результат использует только фазы `preparation` и `execution`. `partial=true` означает
остановку после подтверждённого изменения хотя бы одной цели. Ошибка подготовки,
включая вторую цель, оставляет все прочие цели `NotStarted`, `writeStarted=false`,
`partial=false`. Ошибка JSON/schema до разрешения целей возвращает `targets: []`
и общую `diagnostic`; фиктивные цели не создаются. Успешных `editResults[]` нет.

Диагностика содержит `targetIndex`, где он уже требуется пакету, и `edit` — индекс
в исходном `edits[]` с единицы. `message` — одна ограниченная причина без stack,
полного исходника и runtime URI/hash/revision; отсутствующие поля опускаются.
`candidates` — до пяти реальных мест с `line` и ограниченным `context`; это подсказки,
не источник observation authority. `ownerCode` сохраняет реальный код владельца.

| `code` | `nextAction` |
| --- | --- |
| `invalid_edit`, `overlap` | `fix_patch` |
| `context_not_found`, `observation_required`, `source_changed` | `read_target` |
| `ambiguous_context` | `refine_context` |
| `owner_error` | `fix_patch`, `read_target` по owner recovery или `inspect_owner_error` |
| `owner_error` при статусе `Unknown` | `verify_effect` |

`nextAction` — подсказка, не разрешение на повторный dispatch. Unknown сохраняется
в существующем runtime outcome/recovery; перед следующей записью требуется
reconciliation, автоматического replay пакета нет. Ошибка после dispatch не
превращается в `Rejected`. Preview/candidates и journal evidence не доказывают,
что after-source доставлен: это определяет context compiler.

Core и Office model projections сохраняют те же исходы и bounded diagnostics,
включая отказ без object data. CLI, inline diff и Inspector показывают статус,
номер ошибочной правки и кандидаты как текст. Нет отдельного result protocol
или второго хранилища; канонический факт остаётся в прежнем TOOL_RESULT/effect.

Лимиты: 16 целей, 100 edits на цель и по 100 якорей в `anchors_before`/`anchors_after`,
262144 UTF-16 символа полного JSON аргументов;
prepared state до 128 Ki символов, confirmation data до 32 Ki. Превью ограничено
по цели; слишком большой подготовленный пакет отклоняется до записи. Workspace
UTF-8 сканируется потоково, результат пишется во временный файл; память зависит
от patch и совпавших фрагментов, не размера файла. Подстроки ищутся потоково, включая длинные строки; полные строки для
контекста буферизуются только до ограниченного размера. Цепочки не перечисляются
комбинаторно; для проверки неоднозначности достаточно двух различных кандидатов. Преобразование координат в байтовые offsets
может перечитывать source для каждого edit: время растёт с числом edits.
Authored-файлы Office также патчатся потоково через `DocumentArtifactStore`:
строгие UTF-8/16/32, исходный BOM, смешанные EOL и отсутствие final newline
сохраняются. Подготовленный кандидат находится в существующем CAS; перед записью
owner повторно проверяет head и восстанавливает тот же результат по исходному
snapshot, сверяя hash/длину. История и полный read-back используют тот же CAS и
retained decoded view. Память зависит от patch и буферов, временный диск — от файла;
несколько проходов по источнику остаются ценой подготовки/проверки.
Логический head Markdown/authored file содержит ссылку на snapshot, не исходник.
Diff и after-source раскрывают только единственную опубликованную зависимость
`immutable-snapshot`. Model evidence привязан к текущему logical head. Небольшой
исходник передаётся полностью; для большого authored file передаётся до 32000
фактических символов с неполной coverage. Окончательная доставка определяется
компилятором контекста, а зависимая правка вне coverage требует `core.read`.

Markdown/HTML aggregates в Office и `core.write/replace/resources.copy` authored-файлов
пока материализуют полный source. Их пиковая память растёт с размером источника.
VBA требует полного кода в памяти для проверки и целой записи CodeModule.
`core.patch` и `core.replace` используют один guarded whole-module writer; прежний
`VbaPatchEngine`, JSON-parser и patch-specific service/guards удалены. Строгая
проверка snapshot, journal до COM и полный read-back остаются у существующего owner.
Старый Workspace `PatchExact`/`PatchLargeExact` удалён; типизированные edits идут
через общий resolver и единственный staged writer. Общие проверки байтов, journal
и recovery сохранены.
Все owners получают один тип `ResolvedTextEdit` с runtime offsets; прежний
дублирующий `TextPatchEdit` и DTO `TextEditContext` удалены. HTML owner получает
тот же готовый план; прежние
JSON-операции и `StructuredTextPatchEngine` удалены. Диагностика поиска принадлежит
общему механизму, а проверка файла/ёмкости и публикация — HTML owner.
Private `file.source.patch` остаётся внутренней командой authored-file owner,
принимает тот же typed `TextPatch` и не публикует отдельную модельную schema.
`TextPatternEngine` обслуживает Word/PowerPoint replacements; это другой domain,
его формат не переводится на `core.patch`.

### Аудит перехода на contract 3 — 2026-10-30

Исходный HEAD `990c795f`: 10 проверок `universal mutations:` проходили до изменения
контракта. Сверены catalog, parser, resolver, owners, сериализованные batch results,
model projection, skills и UI consumers.

| Поверхность | До изменения | Итог |
| --- | --- | --- |
| Catalog / `TextEditIntent` | `before/after/context`, отдельный insert | Одна закрытая форма `old/new/anchors_before/anchors_after` |
| `TextPatch` | Неявная нормализация EOL, отдельная ветка insert | Буквальный streaming matcher и один план по S0 |
| Resolved edits | `TextPatchEdit` и копия в `ResolvedTextEdit` | Только `ResolvedTextEdit` с long offset |
| Diagnostics | `context_not_found → refine_context`, unknown с отдельным code/action | `read_target`, `owner_error → verify_effect`; общий bounded DTO |
| CLI / run changes | Статусы без полной диагностики | Номер edit, причина, действие и bounded candidates из того же результата |
| `file.source.patch` | Приватная команда authored-file owner | Сохранена внутренней; модельная схема не опубликована |
| `TextPatternEngine` | Общие search/replace и Office text consumers | Сохранён для этих consumers; альтернативного patch resolver нет |

Фактические сериализованные результаты до/после сравнивались для success,
ambiguity, preflight отказа второй цели и partial/unknown. Сохранились фазы
`preparation/execution`, порядок всех целей и реальные `writeStarted/partial`.
При позднем отказе первая цель остаётся Changed; при Unknown второй цели третья
остаётся NotStarted. Исключение guard до второй записи не наследует dispatch первой.
Новые DTO/stores, aliases и compatibility adapters не добавлены.

Проверки и команды — [Harness](../tests/RNAssistant.Harness/README.md).
Windows/VBE, COM read-back и поведение реальной модели этим аудитом не квалифицированы.

## Поиск и массовая замена

`core.search` и `core.replace` используют `ResourceTextMatcher`. Literal — ordinal
с опциональным IgnoreCase; regex — .NET CultureInvariant, по одной физической
строке, timeout 100 ms. Совпадения не перекрываются. Запрос ограничен 500 символами
и одной строкой; исходные LF/CRLF и неизменённые байты сохраняются. В literal
replacement символы `$1` буквальны, в regex — стандартные подстановки .NET.

Перед заменой нужен завершённый поиск **одного target** с теми же query/mode/case,
точным итоговым числом и той же версией. Runtime хранит одно временное подтверждение
поиска; рестарт требует нового поиска. Нужен следующий model step после результата,
а не соседняя операция в том же ответе. `expectedMatches` — 1–10000; смена версии,
числа, неполный scan/source и zero-length regex не допускают запись. Для больших
источников за бюджетом полного поиска используйте `core.patch` с доставленным
ограниченным фрагментом. Это текстовая замена:
она может затронуть комментарии и строки и не обещает semantic rename.
Отдельных regexp tools и обязательных language parsers нет.

## Владельцы и границы

| Источник | Поддерживаемые операции / особенности |
| --- | --- |
| Workspace UTF-8 | write/patch/replace/copy/move/delete/restore; staged commit и read-back; большие правки потоковые; managed trash; destination parent для move должен существовать |
| Live VBA | write/patch/replace/copy/move/delete/restore; copy/rename только в текущем проекте, ограничения типов/ownership сохранены; UserForms только CodeOnly |
| HTML workspace file | write/patch/replace/delete; существующий workspace snapshot и preflight; file move/copy/restore через этот контракт пока не поддержаны |
| HTML data binding | delete по точному target; содержимое/связи остаются структурированными `html.data.*` |
| Authored text/code file | write/patch/replace/copy; patch потоковый, encoding/BOM сохраняет owner; исходный upload неизменяем, для редактирования нужна копия |
| Markdown document | write/patch/replace/restore; restore принимает номер версии |
| Plan | write/patch/delete/restore; target `plan: <уникальное название>`, numbered history и существующий journal |
| Prompt template | write/patch/replace по обнаруженному `prompt: <key>`; defaults неизменяемы, текущий field и publication проверяются до записи |
| Custom skill reference | write/patch/replace/delete; `skill reference: <id> / references/<name>.md`, package revision guard; built-ins неизменяемы, UTF-8 BOM/CRLF сохраняются |

Неподдержанная операция возвращает явный отказ до dispatch. Generic tools не
эмулируют перенос комбинацией copy+delete. Task list, tool/skill package
и ячейки/объекты Office сохраняют специализированные операции:
их контракт включает структуру, ownership или отдельную execution authority.

Plan source — проекция прежнего owner, без второго store:

```text
---
title: "Название в виде JSON-строки"
status: draft
---
# Полный Markdown
```

Обязательны четыре строки header, непустой body и status `draft` либо `ready`.
`core.write` создаёт отсутствующее название или заменяет полностью наблюдённый
source существующего плана. Другое название создаёт отдельный план; изменение title
в header переименовывает выбранный. Коллизии названий, включая restore, отвергаются.
`core.read` по Plan по умолчанию возвращает source; `representation=text` — только
body, который не разрешает изменение header. Workspace принимает `plan:` также
без `type`; discovery использует `type=plan`. UI передаёт отдельный runtime guard
показанной ревизии, а кнопка создания требует отсутствия цели; это не аргументы модели.

В Plan/Ask/Deny разрешены только `core.write/patch`, у которых все цели — Plan,
через exact built-in handler/binding. Смешанный пакет с файлом/VBA отвергается до
подготовки. `resources.delete/restore` остаются Agent lifecycle с обычной policy.
Task List остаётся структурированным `task.list.set`. Изменения reference/prompt
публикуют текущий aggregate catalog: следующая цель того же каталога может стать
устаревшей после первой записи; нет автоматического rebase. Для зависимых правок
нужны отдельные вызовы с чтением. `html.data.refresh` удалён: UI перечитывает
workspace, актуальные live bindings открываются существующим runtime.

VBA использует тот же формат, с токенным сопоставлением регистра идентификаторов
и безопасных пробелов между токенами. Строковые литералы, bracketed names,
апострофные и `Rem` комментарии остаются значимыми; глобального IgnoreCase нет.
Эквивалентные совпадения всё равно должны быть единственными. Нормализация применяется
только к matching и существующей проверке read-back, **не** к проверке актуальности
подготовленного исходника. Неизменный контекст берётся из фактического source.

Все edits вычисляются в памяти. Полученный полный candidate передаётся существующему
CodeModule: DeleteLines всего кода, затем InsertLines полного результата. Старый
bottom-up/LCS path удалён. `core.patch` передаёт полный код прямо guarded whole-module
writer, без повторного разбора прежних операций замены. Journal записывает before/intended до COM, после идёт
полный read-back. Сбой между COM-вызовами без подтверждённого результата — unknown,
без автоматического replay/restore. UserForms остаются CodeOnly. Реальные VBE,
межмодульное изменение регистра, ошибка между DeleteLines/InsertLines и поведение
UserForm требуют отдельной Windows/Office проверки; Harness её не заменяет.

## Имена, Library и миграция

Built-in IDs — нижний регистр, сегменты через точку, действие последним:
`core.write`, `html.data.write`, `excel.range.write`. Skills имеют префикс
`skills.`, например `skills.vba.code.editing`. Новые custom IDs валидируются;
существующие custom IDs не переписываются автоматически.

Library и Inspector группируют только по первому сегменту (`core`, `excel`, `html`);
у skills начальный `skills.` пропускается, остальные сегменты не создают подгрупп.
Tools и skills разделены, полные ID сохраняются в строках. Префикс
не выдаёт права: Core / Подключён / По запросу / Недоступен отражают каталог и
текущий Arsenal. Diff внутри шага — одна колонка, 3 строки контекста, сначала
40 строк с раскрытием следующих, безопасный textContent и горизонтальный scroll.
Предложение при подтверждении и факт после выполнения отображаются отдельно по целям.
Компактный diff читает сохранённые before/after; для больших файлов проверяет
изменённые фрагменты также по сохранённому after. Rejected/NotStarted остаются
видимыми рядом с успешными целями. План при unknown помечается как предварительный; он не становится проверенным
результатом. Для слишком большого или отсутствующего сравнения видна причина.

Старые public IDs не являются aliases. Несовместимая история требует нового чата,
при этом сами сообщения/данные сохраняются. Exact admissions пересобираются по
новому профилю. [Таблица переименования](tool-name-migration.json) — только данные
для явной миграции authoring, не registry dispatch.
Отклонённый исторический `core.patch` с аргументами `{patch}` остаётся в истории и
не выполняется повторно. Новое выполнение требует `targets/edits`; несовместимый
pending state и URI target блокируются без изменения истории.

Для structured dependencies пользовательских skills есть отдельный offline script:

```sh
python3 tools/migrate-tool-names.py /path/to/custom/skills
python3 tools/migrate-tool-names.py /path/to/custom/skills --apply
```

Первый вызов — dry-run. Второй сохраняет `SKILL.md.before-tool-names-v1`, меняет
только `rnassistant.requiredTools/optionalTools`, удаляет устаревшие revision pins
и дубликаты. Custom ID, body, references и чаты остаются как были; упоминания
старых API в свободном тексте требуют осмысленного редактирования. Миграция не
запускается автоматически при открытии приложения.
