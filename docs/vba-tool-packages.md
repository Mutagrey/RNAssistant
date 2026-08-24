# VBA Tool Packages v1

VBA tools нужны для узких повторяемых Office-действий, которых нет среди built-in tools и которые неудобно выразить pipeline. Для обычной автоматизации предпочтителен built-in или pipeline: VBA требует Trust Access, несёт больший риск и проверяется только в реальном Windows + Office runtime.

## Где хранятся tools

Глобальный редактируемый пакет хранится в `%AppData%/RNAssistant/tools`:

```text
tools/excel/excel.echo_vba/
  tool.json
  src/
    RNA_Echo.bas
    RNA_EchoService.cls
  README.md
```

`tool.json` хранит metadata и component list, а исходники живут отдельными `.bas`/`.cls` файлами. Поддерживаются только standard modules и class modules. UserForms/FRX, document modules и другие host-owned components в v1 не поддерживаются.

RNAssistant также читает VBA project активного документа через Office object model. Валидный manifest превращает такой код в tool со scope `document`; он доступен только этому документу и не копируется в глобальную библиотеку автоматически.

## Manifest и entry function

Manifest — один JSON object в VBA-комментариях непосредственно перед entry function первого standard module:

```vb
Option Explicit

' <RNAssistantTool>
' {
'   "protocolVersion": 1,
'   "id": "excel.echo_vba",
'   "name": "Echo VBA",
'   "description": "Return typed arguments.",
'   "host": "Excel",
'   "packageVersion": "1.0.0",
'   "entryPoint": "RNATool_Echo",
'   "components": ["RNA_Echo", "RNA_EchoService"],
'   "argumentOrder": ["text", "count", "enabled"],
'   "parameters": {
'     "type": "object",
'     "properties": {
'       "text": { "type": "string", "description": "Text to echo." },
'       "count": { "type": "integer", "description": "Repeat count." },
'       "enabled": { "type": "boolean", "description": "Whether output is enabled.", "default": true }
'     },
'     "required": ["text", "count"],
'     "additionalProperties": false
'   },
'   "mutatesDocument": false,
'   "agentCanRun": true,
'   "requiresConfirmation": false
' }
' </RNAssistantTool>
Public Function RNATool_Echo( _
    ByVal text As String, _
    ByVal count As Long, _
    ByVal enabled As Boolean) As String

    RNATool_Echo = text & ":" & CStr(count) & ":" & CStr(enabled)
End Function
```

Обязательные правила:

- `protocolVersion` равен `1`; `host` — `Excel`, `Word` или `PowerPoint`.
- `components` содержит уникальные имена и первым указывает entry module.
- Имена VBA components и entry point начинаются с латинской буквы, содержат только буквы/цифры/underscore и не длиннее 40 символов.
- `parameters` — strict object JSON Schema с `required`, `additionalProperties:false`, явным типом и полезным `description` каждого параметра; `argumentOrder` в точности совпадает с его properties и с сигнатурой функции.
- Каждый параметр объявлен `ByVal` и имеет тип `String`, `Long`, `Double` или `Boolean`. JSON `integer` соответствует VBA `Long`.
- Entry function принимает не более 30 позиционных аргументов.
- Необязательный параметр имеет `default` в schema, потому что `Application.Run` получает позиционные аргументы.
- Entry point всегда `Public Function ... As String`. `Sub`, `Variant` и object return запрещены.
- Manifest — источник истины для id, версии, схемы и safety metadata. Код supporting modules не содержит второго manifest.

Рекомендуемые имена: entry module `RNA_<Tool>`, entry function `RNATool_<Tool>`, зависимости `RNA_<Tool>_<Role>`. Используйте `Option Explicit`, явные ссылки на workbook/document/presentation и восстанавливайте изменённые application-wide настройки в error handler.

## Что возвращает VBA

Функция возвращает обычный полезный `String`: текст, id созданного объекта или компактный JSON бизнес-результата. Она не формирует agent `tool_calls` или общий ToolResult envelope. C# runtime сам оборачивает строку в результат с `ok`, `tool_call_id`, `name`, `status`, `message`, `data` и `error`.

При ошибке VBA должна поднять обычную ошибку (`Err.Raise`) с понятным сообщением. Не нужно встраивать JSON parser, сетевой клиент или собственный transport protocol.

## Жизненный цикл

- Run существующего document-local tool вызывает его напрямую.
- Если глобальный package отсутствует в VBA project, обычный Run временно импортирует его components, вызывает entry function позиционными typed arguments и удаляет временные components в `finally` даже после ошибки.
- Явный Install делает package постоянным только в macro-enabled документах (`.xlsm/.xlam`, `.docm/.dotm`, `.pptm/.ppam`). Временный install через UI/API запрещён: им управляет runtime.
- Перед постоянной перезаписью создаются VBA backups. Components получают ownership marker с id/version/hash.
- Uninstall удаляет только owned и не изменённые components. Чужой код, частичный package или hash drift удалять автоматически нельзя.
- Временный запуск не сохраняет книгу сам. Постоянный install изменяет VBA project, но сохранение документа остаётся отдельным действием Office/пользователя.
- Optimistic concurrency остаётся строгой, но hash не является model-facing аргументом. Каждый public mutation сам читает точное live state, привязывает внутренний guard к chat, document identity и module, сохраняет его через confirmation и повторно сверяет непосредственно перед mutation. Предварительный public read/search не является разрешением и не обязателен. После write/patch/restore выполняется повторное чтение: допустимы только несемантические преобразования VBE (регистр и пробелы вне строк/комментариев, CRLF и финальные пустые строки); фактический read-back hash возвращается в результате. `CodeModule.CountOfLines` не сравнивается с числом строк входной строки, потому что VBIDE может учитывать служебную финальную строку. Семантическое расхождение не возвращается как success, а сохранённый backup остаётся доступен для rollback. Package install/remove использует отдельный package hash, который игнорирует export headers и ownership markers.
- `expectedCodeSha256` остаётся только во внутреннем typed bridge вызове ручного Save из VBA editor: UI получает его из ранее загруженного модуля и не просит модель вычислять или копировать hash. Delete bridge больше не делает отдельный read/hash round-trip и использует тот же внутренний runtime guard, что public delete tool.

## Discovery и безопасность

Discovery читает VBProject активного документа, находит manifest в standard modules, затем разрешает объявленные `.bas`/`.cls` components. Дубликаты, отсутствующие зависимости, неподдерживаемые типы, неверная сигнатура и несоответствие schema делают tool недоступным.

Для чтения/import/remove VBProject в Trust Center должен быть включён `Trust access to the VBA project object model`. Новосозданный mutating tool по умолчанию должен иметь `agentCanRun:false` и `requiresConfirmation:true`. Не храните в исходниках секреты, credentials, machine-specific paths и скрытый network/shell запуск.

Для безопасной работы с кодом во всех VBA-capable hosts Agent видит восемь общих controller tools: `common.vba_list_modules`, `common.vba_read_module`, `common.vba_search_code`, `common.vba_write_module`, `common.vba_apply_patch`, `common.vba_delete_module`, `common.vba_list_backups`, `common.vba_restore_backup`. Host-prefixed `excel|word|powerpoint.vba_*` остаются внутренним COM backend. Старые public ids `vba_read_lines`, `vba_replace_text`, `vba_create_module` принимаются для совместимости и canonicalize к новому контракту, но Agent их не видит.

`common.vba_list_backups` возвращает bounded metadata-only список без дублирования сохранённого source в model context. Restore требует явный selector: точный `backupId` либо `moduleName` для намеренного выбора последнего backup этого модуля. Вызов без selector не восстанавливает неявный «последний backup вообще».

`common.vba_read_module` без диапазона читает весь bounded source, а с `startLine`/`lineCount` — точный диапазон до 500 строк. `common.vba_write_module` получает полный source и по умолчанию делает upsert: существующий компонент обновляется с backup, отсутствующий создаётся. Если в текущем chat уже был read/search этого модуля, runtime сам сравнивает сохранённый snapshot перед whole-source write: при drift он один раз требует осознанно перечитать/слить изменения либо повторить вызов как намеренную полную перезапись — hash модели не передаётся. Optional `mode=createOnly|updateOnly` сохраняет строгую семантику там, где она действительно нужна. `componentType` используется только при создании. Синтаксически неверное новое имя нормализуется в стабильный ASCII VBA identifier длиной до 40 символов с коротким детерминированным hash-suffix; это избегает коллизии с обычным валидным именем, а повтор того же вызова обновляет тот же компонент. Результат всегда возвращает фактическое имя.

Поиск поддерживает literal/regex, timeout и bounded output. `patch` передаётся native JSON array. Model-facing schema использует discriminated `anyOf`: каждая из операций `replace`, `replaceAll`, `replaceFirst`, `insertBefore`, `insertAfter`, `replaceLines`, `regexReplace` содержит только релевантные ей поля. Replacement content задаётся полем `text`; старый alias `replace` нормализуется runtime только для совместимости. Literal fragments приводятся к переносам текущего модуля, `replace` требует одно совпадение, insertion anchors должны быть непустыми и уникальными, а inserted block автоматически отделяется переносами от соседнего кода. Insertion не используется вместо replacement; координаты последовательных line operations считаются по результату предыдущей операции, один финальный перенос в replacement text считается terminator. После range-read предпочтителен `replaceLines`, а не большой multi-line literal. Raw control characters запрещены; значения вроде vertical tab формируются выражением `ChrW$(11)` во время выполнения VBA. Mutation сам читает текущее состояние; отдельный read нужен только модели для понимания кода, не runtime для разрешения операции. Перед изменением существующего source или удалением создаётся backup, результат write/patch возвращает фактический read-back hash.

Whole-module запись удаляет текущие строки и вставляет канонический CRLF source через `CodeModule.InsertLines(1, ...)`, затем немедленно читает модуль обратно. BOM, NUL/другие control characters и Unicode line separators отклоняются до удаления исходного кода.

Whole-source upsert может создавать `StdModule`, `ClassModule` и пустой `MSForm` (UserForm). Для UserForm RNAssistant читает и изменяет только code-behind через `CodeModule`: visual Designer, controls, layout, properties и бинарные `.frx` assets не входят в этот protocol и не попадают в code backup. Удалять можно только `StdModule` и `ClassModule`; document modules и UserForms не удаляются через RNAssistant. Public VBA mutations доступны Agent, но требуют подтверждения при выключенном auto-confirm: backup восстанавливает source, но не является security boundary для исполняемого или auto-run VBA. Низкоуровневые whole-module replacement/insert/run_macro tools в Agent catalog не входят.

Правила генерации и редактирования также встроены как skills `common.vba_tool_authoring` и `common.vba_code_editing`, чтобы модель получала их только при релевантном сценарии.

## Обязательная проверка на Windows

Изменения COM/VBA lifecycle нельзя полноценно проверить на macOS. Перед merge нужны Windows x64 + Office x64 + VS 2022 smoke tests для Excel, Word и PowerPoint: одинаковый компактный `common.vba_*` catalog, whole/range `vba_read_module`, upsert create/update и нормализация имени, legacy aliases, LF/CRLF/final newline и граничные blank lines, кириллица, mutation → confirmation → external edit stale rejection без public pre-read, create-only race, exact backup restore, UI save с автоматически переданным snapshot, patch/clear/rollback, создание пустого MSForm и редактирование его code-behind без изменения Designer, Office busy/modal state, discovery, typed `Application.Run`, cleanup после успеха/ошибки, permanent install/uninstall, Trust Access off и macro-free document.
