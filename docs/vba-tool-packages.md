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
'       "text": { "type": "string" },
'       "count": { "type": "integer" },
'       "enabled": { "type": "boolean", "default": true }
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
- `parameters` — формальный object JSON Schema; `argumentOrder` в точности совпадает с его properties и с сигнатурой функции.
- Каждый параметр объявлен `ByVal` и имеет тип `String`, `Long`, `Double` или `Boolean`. JSON `integer` соответствует VBA `Long`.
- Entry function принимает не более 30 позиционных аргументов.
- Необязательный параметр имеет `default` в schema, потому что `Application.Run` получает позиционные аргументы.
- Entry point всегда `Public Function ... As String`. `Sub`, `Variant` и object return запрещены.
- Manifest — источник истины для id, версии, схемы и safety metadata. Код supporting modules не содержит второго manifest.

Рекомендуемые имена: entry module `RNA_<Tool>`, entry function `RNATool_<Tool>`, зависимости `RNA_<Tool>_<Role>`. Используйте `Option Explicit`, явные ссылки на workbook/document/presentation и восстанавливайте изменённые application-wide настройки в error handler.

## Что возвращает VBA

Функция возвращает обычный полезный `String`: текст, id созданного объекта или компактный JSON бизнес-результата. Она не формирует `AgentDecision`, `tool_calls` или общий ToolResult envelope. C# runtime сам оборачивает строку в нормализованный результат с `ok`, `status`, `summary`, `data` и `error`.

При ошибке VBA должна поднять обычную ошибку (`Err.Raise`) с понятным сообщением. Не нужно встраивать JSON parser, сетевой клиент или собственный transport protocol.

## Жизненный цикл

- Run существующего document-local tool вызывает его напрямую.
- Если глобальный package отсутствует в VBA project, обычный Run временно импортирует его components, вызывает entry function позиционными typed arguments и удаляет временные components в `finally` даже после ошибки.
- Явный Install делает package постоянным только в macro-enabled документах (`.xlsm/.xlam`, `.docm/.dotm`, `.pptm/.ppam`). Временный install через UI/API запрещён: им управляет runtime.
- Перед постоянной перезаписью создаются VBA backups. Components получают ownership marker с id/version/hash.
- Uninstall удаляет только owned и не изменённые components. Чужой код, частичный package или hash drift удалять автоматически нельзя.
- Временный запуск не сохраняет книгу сам. Постоянный install изменяет VBA project, но сохранение документа остаётся отдельным действием Office/пользователя.

## Discovery и безопасность

Discovery читает VBProject активного документа, находит manifest в standard modules, затем разрешает объявленные `.bas`/`.cls` components. Дубликаты, отсутствующие зависимости, неподдерживаемые типы, неверная сигнатура и несоответствие schema делают tool недоступным.

Для чтения/import/remove VBProject в Trust Center должен быть включён `Trust access to the VBA project object model`. Новосозданный mutating tool по умолчанию должен иметь `agentCanRun:false` и `requiresConfirmation:true`. Не храните в исходниках секреты, credentials, machine-specific paths и скрытый network/shell запуск.

Для безопасной работы с кодом доступны host-prefixed controller tools: `vba_list_modules`, `vba_search_code`, `vba_replace_text`, `vba_apply_patch`, `vba_create_module`, `vba_delete_module`, backup list/restore. `vba_list_modules` возвращает только имена, типы и размеры компонентов; исходник читается по имени через host `vba_read_module`. Поиск поддерживает literal/regexp, а `regexReplace` в structured patch — capture groups, timeout и лимит замен. Перед каждой записью/удалением создаётся backup; delete дополнительно требует актуальный `expectedCodeSha256` из `vba_read_module` или `vba_search_code`.

Создавать и удалять можно только `StdModule` и `ClassModule`. Document modules и UserForms разрешено читать, искать и патчить, но нельзя создавать/удалять через RNAssistant. Все VBA mutations требуют подтверждения.

Правила генерации и редактирования также встроены как skills `common.vba_tool_authoring` и `common.vba_code_editing`, чтобы модель получала их только при релевантном сценарии.

## Обязательная проверка на Windows

Изменения COM/VBA lifecycle нельзя полноценно проверить на macOS. Перед merge нужны Windows x64 + Office x64 + VS 2022 smoke tests для Excel, Word и PowerPoint: discovery, typed `Application.Run`, cleanup после успеха/ошибки, permanent install/uninstall, hash drift, Trust Access off и macro-free document.
