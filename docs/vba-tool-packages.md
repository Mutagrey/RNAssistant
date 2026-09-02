# VBA Tool Packages v1

VBA tools нужны для узких повторяемых Office-действий, которых нет среди built-in tools. Pipelines отключены на время стабилизации. Для обычной автоматизации предпочтителен built-in: VBA требует Trust Access, несёт больший риск и проверяется только в реальном Windows + Office runtime.

## Где хранятся tools

Глобальный редактируемый пакет хранится в `%AppData%/RNAssistant/tools`:

```text
tools/excel/excel.echo_vba/
  tool.json
  src/
    RNA_Echo.bas
    RNA_EchoService.cls
    RNA_EchoForm.form.vba
  README.md
```

`tool.json` хранит metadata и component list, а исходники живут отдельными `.bas`/`.cls`/`.form.vba` файлами. При загрузке глобального пакета runtime до публикации в catalog сверяет точное множество объявленных имён с manifest и source directory, проверяет допустимые storage types, требует entry source первым `StdModule` и отклоняет отсутствующий, лишний, дублированный или нечитаемый component. `pipeline.json` к VBA executor не относится и не сохраняется как совместимый sidecar. Package protocol v1 поддерживает standard modules, class modules и пустые code-only `MSForm`. `.form.vba` содержит только code-behind; exported `.frm`, `.frx`, document modules и другие host-owned components не поддерживаются.

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
- Entry component всегда `StdModule`. Supporting components могут быть `StdModule`, `ClassModule` или code-only `MSForm`; тип и source хранятся в `tool.json`/`src`, а manifest сохраняет стабильный ordered name list.
- Имена VBA components начинаются с латинской буквы, содержат только буквы/цифры/underscore и не длиннее VBE-лимита 31 символ; entry point использует тот же алфавит и проектный лимит 40 символов.
- `parameters` — strict object JSON Schema с `required`, `additionalProperties:false`, явным типом и полезным `description` каждого параметра; `argumentOrder` в точности совпадает с его properties и с сигнатурой функции.
- Каждый параметр объявлен `ByVal` и имеет тип `String`, `Long`, `Double` или `Boolean`. JSON `integer` соответствует VBA `Long`.
- Entry function принимает не более 30 позиционных аргументов.
- Необязательный параметр имеет `default` в schema, потому что `Application.Run` получает позиционные аргументы.
- Entry point всегда `Public Function ... As String`. `Sub`, `Variant` и object return запрещены.
- Manifest — источник истины для id, версии, схемы и safety metadata; duplicate JSON fields отклоняются. Код supporting modules не содержит второго manifest.

Рекомендуемые имена: entry module `RNA_<Tool>`, entry function `RNATool_<Tool>`, зависимости `RNA_<Tool>_<Role>`. Используйте `Option Explicit`, явные ссылки на workbook/document/presentation и восстанавливайте изменённые application-wide настройки в error handler.

## Что возвращает VBA

Функция возвращает обычный полезный `String`: текст, id созданного объекта или компактный JSON бизнес-результата. Она не формирует agent `tool_calls` или общий ToolResult envelope. C# runtime сам оборачивает строку в результат с `ok`, `tool_call_id`, `name`, `status`, `message`, `data` и `error`.

При ошибке VBA должна поднять обычную ошибку (`Err.Raise`) с понятным сообщением. Не нужно встраивать JSON parser, сетевой клиент или собственный transport protocol.

Normalization/hash rules теперь принадлежат `Core.Tools.VbaTextCanonicalizer`, а не manifest parser. `NormalizePackageCode`/`PackageCodeSha256` сохраняют прежнее исключение export headers/ownership markers; `PackageComparableCodeSha256` дополнительно использует прежние VBE-comparable правила. Source/transport и raw CAS bytes не переписываются; [представления текста](vba-mutation-journal.md#text-representations) разделены. 6A не меняет install/run/remove или journal protocol.

## Граница стабилизации package lifecycle

По [аудиту 6H](stabilization/PHASE_6H_VBA_PACKAGE_SCOPE.md) исполнение уже
существующих global/document-local VBA tools, временный install/run/cleanup,
persistent install/remove/status и package recovery остаются в stable core. Они
должны получить одного typed owner в 6I. Это не включает создание/редактирование
dynamic tool definitions, новые package функции или pipelines: authoring остаётся
отдельным Phase 11 contour после read-only selected-endpoint inspector и Host Fabric
pinning; его revision/history/import UX определён в
[Tool Library](tool-library.md). `mode=rename` не является package feature и
переходит в typed mutation owner отдельным 6J.

6I закрывает R41 host-neutral. Session install и cleanup остаются двумя атомарными
package mutations, но имеют один durable `LifecycleId`; тот же id входит в exact
`RNAssistantSession` marker вместе с package id/version/hash. Typed probe различает
`document_local`, persistent `installed`, `session_cleanup_required`, partial,
modified и `recovery_required`, объединяя live marker/source/type с append-only
journal. Поэтому потерянный terminal/cleanup блокирует macro и persistent overwrite
даже если marker повреждён или удалён. Recovery не повторяет, не удаляет и не
перезаписывает VBA автоматически; cleanup требует новой policy-authorized journalled
Uninstall над точным неизменённым session-owned package. Старый marker без lifecycle
распознаётся для явной cleanup, но не получает выдуманную durable correlation.

11J2 переводит вход package lifecycle на `ToolPackageSource` contract v1. Он
содержит полный code/component/schema/host/scope snapshot, отдельные human
`PackageVersion` и deterministic content `Revision`; Agent исполняет exact id через
binding `vba.custom.package.execute.v1`, а Tools UI install/remove/status использует
тот же source. Result contract v1 отдельно несёт status, source revision, dispatch и
effect. Обычная строка из arbitrary VBA macro не доказывает effect, поэтому после
dispatch execution остаётся `unknown`; install/remove получают verified
change/no-change только из journal/read-back. Старые package/result projections и
PascalCase UI fallback удалены. Immutable history и Host Fabric этим не заявляются.

## Жизненный цикл

- Run существующего document-local tool вызывает его напрямую.
- Если глобальный package отсутствует в VBA project, обычный Run временно импортирует его components, вызывает entry function позиционными typed arguments и удаляет временные components в `finally` даже после ошибки.
- Явный Install делает package постоянным только в macro-enabled документах (`.xlsm/.xlam`, `.docm/.dotm`, `.pptm/.ppam`). Временный install через UI/API запрещён: им управляет runtime.
- До COM dispatch install/remove пишет один package transaction manifest со всеми CAS-backed before/intended component states. Install передаёт exact prepared existence/type/comparable-source/marker state backend-у; missing guard блокируется, а post-prepare drift даёт `stale_vba_package` до первой component mutation. Persistent operations проецируют rollback backups; components получают ownership marker с id/version/hash, а session marker также содержит lifecycle id. Pure parser marker-а остаётся явным read-only контрактом `Office.Vba`, который использует `OfficeHosts.Vba` guard; parser не дублируется и friend-assembly доступ не требуется.
- Uninstall удаляет только owned и не изменённые components. Existing `MSForm` дополнительно должен быть проверен как blank code-only Designer state. Чужой код, Designer controls, type collision, частичный package или hash drift удалять/перезаписывать автоматически нельзя.
- Временный запуск не сохраняет книгу сам. Постоянный install изменяет VBA project, но сохранение документа остаётся отдельным действием Office/пользователя.
- Optimistic concurrency остаётся строгой, но hash не является model-facing аргументом. Каждый public mutation сам читает точное live state, привязывает внутренний guard к chat, document identity и module, сохраняет его через confirmation и повторно сверяет непосредственно перед mutation. Предварительный public read/search не является разрешением и не обязателен. После write/patch/restore выполняется повторное чтение: допустимы только несемантические преобразования VBE (регистр и пробелы вне строк/комментариев, CRLF и финальные пустые строки); фактический read-back hash возвращается в результате. `CodeModule.CountOfLines` не сравнивается с числом строк входной строки, потому что VBIDE может учитывать служебную финальную строку. Семантическое расхождение не возвращается как success, а сохранённый backup остаётся доступен для rollback. Package install/remove использует отдельный package hash, который игнорирует export headers и ownership markers.
- `expectedCodeSha256` остаётся только во внутреннем typed bridge вызове ручного Save из VBA editor: UI получает его из ранее загруженного модуля и не просит модель вычислять или копировать hash. Delete bridge больше не делает отдельный read/hash round-trip и использует тот же внутренний runtime guard, что public delete tool.

## Discovery и безопасность

Discovery читает VBProject активного документа, находит manifest в standard modules, затем разрешает объявленные `.bas`/`.cls`/code-only `MSForm` components. Дубликаты, отсутствующие зависимости, Designer state у `MSForm`, неподдерживаемые типы, неверная сигнатура и несоответствие schema делают tool недоступным.

Для чтения/import/remove VBProject в Trust Center должен быть включён `Trust access to the VBA project object model`. Новосозданный mutating tool по умолчанию должен иметь `agentCanRun:false` и `requiresConfirmation:true`. Не храните в исходниках секреты, credentials, machine-specific paths и скрытый network/shell запуск.

Для безопасной работы с кодом во всех VBA-capable hosts Agent открывает provider `vba` через общие `common.resources_list/resolve/search/read`. Ресурсы имеют kinds `vba-project`, `vba-component`, `vba-backup`; component/backup source читается bounded character chunks по canonical `rna://` URI, а literal search возвращает snippets и те же URI. Public `common.vba_*` содержит только четыре mutation tools: `common.vba_write_module`, `common.vba_apply_patch`, `common.vba_delete_module`, `common.vba_restore_backup`. Старые VBA read/list/search/create/range/replace-text ids и host-prefixed public варианты удалены без aliases. С 11T9A runtime вызывает narrow `IVbaHostBackend` над exact bound document session; host-prefixed backend ids и command/result roundtrip удалены. С 11T9B эти public tools имеют exact native bindings: guard хранится как bounded opaque pending state, accepted arguments не переписываются, а controller VBA executor и public legacy result projection отсутствуют.

`common.resources_list` с provider `vba` и kind `vba-backup` возвращает bounded metadata-only список без дублирования сохранённого source в model context; `common.resources_read` загружает source только по точному URI. Restore требует явный selector: точный `backupId` из metadata либо `moduleName` для намеренного выбора последнего backup этого модуля. Вызов без selector не восстанавливает неявный «последний backup вообще».

`common.resources_read` читает source по opaque revision-bound character cursor с максимумом 32,000 символов на chunk и явными `complete/truncated/nextCursor`; cursor связывает offset с live content hash и возвращает `resource_revision_changed`, если модуль изменился между chunks. `common.vba_write_module` имеет две взаимоисключающие strict-schema ветки. Whole-source write требует `moduleName` и полный `code`; по умолчанию `mode=upsert` обновляет существующий компонент с backup либо создаёт отсутствующий, optional `createOnly|updateOnly` охраняют существование, а `componentType` используется только при создании. Rename требует ровно `moduleName`, `newModuleName`, `mode=rename` и не принимает `code/componentType`: runtime нормализует destination, проверяет отсутствие коллизии, привязывает confirmation guard сразу к обоим именам, пишет двухименный prepared journal, вызывает скрытый `VBComponent.Name` backend и сверяет, что source/type сохранились. При backend failure он пытается вернуть исходное имя; interruption reconciliation различает old-present/new-absent и old-absent/new-present без replay. Rename поддерживает `StdModule`, `ClassModule` и пустой code-only `MSForm`, но не document modules, и не переписывает текстовые ссылки вида `OldModule.Member`. Его нельзя эмулировать через write+delete. Если в текущем chat уже был resource source read/search исходного модуля, runtime сравнивает snapshot перед write/rename; model-facing hash не передаётся. Синтаксически неверное новое имя нормализуется в стабильный ASCII VBA component name длиной до 31 символа с коротким детерминированным hash-suffix. Результат всегда возвращает фактическое имя.

Resource search поддерживает bounded case-insensitive literal discovery; regex остаётся возможностью специализированных Office search tools, но не VBA provider. `common.vba_apply_patch` изменяет только существующий компонент: отсутствие модуля возвращает structured recovery с `creationTool=common.vba_write_module`; patch не создаёт пустой компонент, потому что тип и полный source нового компонента принадлежат whole-source upsert. `patch` передаётся native JSON array без промежуточной сериализации в строку. Model-facing patch contract намеренно содержит только ordered exact `replace` hunks `{op,find,text}`. Line-number, fuzzy, first-match, implicit insertion, replace-all и regex patch modes не публикуются. Каждый `find` приводится только к текущему LF/CRLF style и обязан ровно один раз встречаться в актуальном in-memory source; отсутствие возвращает `vba_patch_stale_source`, неоднозначность — `vba_patch_ambiguous`, в обоих случаях без backup и записи. Runtime не trim-ит `text` и не пересобирает строки: для insertion модель повторяет точный anchor в `text` и добавляет блок до/после него, для deletion передаёт пустой `text`. Все hunks применяются последовательно к одному полному snapshot в памяти, после чего выполняется одна guarded whole-module запись. Existing module нельзя переписывать целиком из truncated read или partial context. Raw control characters запрещены; значения вроде vertical tab формируются выражением `ChrW$(11)` во время выполнения VBA. Mutation сам читает текущее состояние; resource read нужен модели для получения точного `find`, а не runtime для разрешения операции. Перед изменением существующего source или удалением создаётся backup, результат write/patch возвращает фактический read-back hash.

R61 не запрещает whole-source write и не навязывает patch: модель сохраняет явный
выбор между полным намеренным source и exact-hunk изменением. Provider/URI/revision/
cursor и mutation guards переходят во внутренний typed preparation context, но
patch остаётся fail-closed; automatic patch-to-write fallback, fuzzy rebase и
автоматический retry mutation запрещены. Константный `op=replace` и объединённые
write/rename branches проходят отдельный split-or-keep аудит; новые procedure-edit
tools и legacy VBA read aliases не входят в behavior-preserving baseline. Полная
классификация: [R61 tool contract audit](stabilization/R61_TOOL_CONTRACT_AUDIT.md).

Whole-module запись непосредственно перед изменением VBE повторно читает source и сравнивает internal expected hash. При drift операция завершается `stale_vba_module` без записи. Затем runtime удаляет текущие строки, вставляет канонический CRLF source через `CodeModule.InsertLines(1, ...)` и немедленно читает модуль обратно. Встроенный редактор не открывает `truncated` source и не помечает неуспешный Save как сохранённый. BOM, NUL/другие control characters и Unicode line separators отклоняются до удаления исходного кода.

Whole-source upsert может создавать `StdModule`, `ClassModule` и пустой `MSForm` (UserForm). Для `CodeOnly UserForm` RNAssistant читает и изменяет code-behind через `CodeModule`; source может детерминированно создавать runtime controls через `Controls.Add`, задавать layout/runtime properties и подключать события через typed `WithEvents` или удерживаемые event-sink classes. Designer-time controls/properties и бинарные `.frx` assets не входят в protocol и не попадают в code backup. После edit/restore уже загруженный form instance нужно `Unload` и создать заново; source rollback не отменяет уже выполненные обработчиком изменения Office document. Удалять можно только `StdModule` и `ClassModule`; document modules и UserForms не удаляются через public facade. Public VBA mutations доступны Agent, но требуют подтверждения при выключенном auto-confirm: backup восстанавливает source, но не является security boundary для исполняемого или auto-run VBA. Низкоуровневые whole-module replacement/insert tools в Agent catalog не входят. Полный профиль описан в [vba-userforms.md](vba-userforms.md).

Excel, Word и PowerPoint публикуют один общий model-facing tool `common.office_run_macro`. Он запускает любой существующий макрос по точному имени, принимаемому `Application.Run`, без manifest/allowlist, и до 30 позиционных scalar-аргументов. 11T9A удалил прежние `excel.run_macro`, `word.run_macro` и `powerpoint.run_macro` backend-команды; с 11T9B вызов идёт через exact native handler и bound typed backend, а Outlook этот runtime не публикует. Это high-risk external-effect operation с обязательным confirmation при выключенном auto-confirm: произвольный VBA может менять документ, файловую систему или внешнее состояние, а source backup не откатывает выполненные эффекты. Пустой output допустим для `Sub`; backend success означает только возврат `Application.Run` без исключения, а runtime effect после dispatch всегда остаётся `unknown`.

Правила генерации и редактирования также встроены как skills `common.vba_tool_authoring`, `common.vba_code_editing` и `common.vba_userform_authoring`, чтобы модель получала их только при релевантном сценарии.

## Обязательная проверка на Windows

Изменения COM/VBA lifecycle нельзя полноценно проверить на macOS. Перед merge нужны Windows x64 + Office x64 + VS 2022 smoke tests для Excel, Word и PowerPoint: provider `vba` list/resolve/search/bounded source/backup reads, четыре mutation-only `common.vba_*` tools без host source backend ids, upsert create/update и нормализация имени, удалённые VBA ids возвращают unknown-tool, LF/CRLF/final newline и граничные blank lines, кириллица, mutation → confirmation → external edit stale rejection после resource read, create-only race, exact backup restore, UI save с автоматически переданным snapshot, patch/clear/rollback, создание пустого MSForm и редактирование его code-behind без изменения Designer, runtime `Controls.Add` и events, code-only package install/update/uninstall, Designer collision rejection, interrupted package reconciliation, Office busy/modal state, live resource/mutation serialization, discovery, public arbitrary `common.office_run_macro` с confirmation и positional arguments, bound typed host backend with no command alias, package typed `Application.Run`, cleanup после успеха/ошибки, Trust Access off и macro-free document.
