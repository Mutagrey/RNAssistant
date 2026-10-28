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
| `core.patch` | `target`, `hunks`; точные фрагменты одного исходного snapshot |
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

## Hunks

```json
{
  "target": "src/app.js",
  "hunks": [
    {
      "oldText": "return count + 1;",
      "newText": "return count + 2;",
      "contextBefore": "function next(count) {\n  ",
      "contextAfter": "\n}"
    },
    { "oldText": "", "newText": "\n// reviewed\n", "boundary": "end" }
  ]
}
```

`contextBefore/After` — неизменные непосредственно соседние строки/символы.
Если этого недостаточно, `startLine` и `startColumn` указывают начало `oldText`:
строка и UTF-16 колонка с 1, колонка по умолчанию 1. Отступы учитываются; разрыв
суррогатной пары запрещён. Координаты не заменяют проверку старого текста и контекста.

Все 1–100 hunks относятся к оригиналу. Повторения (включая перекрывающиеся старты),
несовпадение координат, пересечение hunks и две вставки в одну позицию отклоняют
план до записи. Нет fuzzy matching, исправления whitespace или пересчёта координат
после предыдущего hunk. `newText=""` удаляет фрагмент. Вставка с `oldText=""`
требует непустого уникального контекста либо `boundary=start|end`; boundary не
смешивается с контекстом/координатами. Для частичного чтения изменяемый диапазон
должен принадлежать доставленному фрагменту. Граничная вставка требует полного
наблюдения. Чтение из соседнего вызова того же ответа ещё не является доставкой.
Вставка `boundary=start` сохраняет исходный BOM перед новым содержимым.
Отсутствующее/устаревшее наблюдение возвращает typed `RefreshRequired`, привязанный
к ресурсу: чтение другого файла не разрешает повтор. Для VBA после такого отказа
recovery просит полный источник; исходный patch может использовать точный excerpt.

Публичный формат — структурированные context hunks, а не буквальный `git diff`:
не нужны заголовки `---/+++` и вычисленные длины `@@`. UI получает сравнение
сохранённых источников и строит обычные строки `+`/`−`.

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
источников за бюджетом полного поиска используйте hunks. Это текстовая замена:
она может затронуть комментарии и строки и не обещает semantic rename.
Отдельных regexp tools и обязательных language parsers нет.

## Владельцы и границы

| Источник | Поддерживаемые операции / особенности |
| --- | --- |
| Workspace UTF-8 | write/patch/replace/copy/move/delete/restore; staged commit и read-back; большие hunks потоковые; managed trash; destination parent для move должен существовать |
| Live VBA | write/patch/replace/copy/move/delete/restore; copy/rename только в текущем проекте, ограничения типов/ownership сохранены; UserForms только CodeOnly |
| HTML workspace file | write/patch/replace/delete; существующий workspace snapshot и preflight; file move/copy/restore через этот контракт пока не поддержаны |
| HTML data binding | delete по точному target; содержимое/связи остаются структурированными `html.data.*` |
| Authored text/code file | write/patch/replace/copy; encoding/BOM сохраняет owner; исходный upload неизменяем, для редактирования нужна копия |
| Markdown document | write/patch/replace/restore; restore принимает номер версии |

Неподдержанная операция возвращает явный отказ до dispatch. Generic tools не
эмулируют перенос комбинацией copy+delete. Plan, task list, tool/skill package,
prompt settings и ячейки/объекты Office сохраняют специализированные операции:
их контракт включает структуру, ownership или отдельную execution authority.

VBA получает полный проверенный candidate, но COM меняет физические диапазоны
строк снизу вверх. Ограниченный LCS сохраняет неизменные строки; для очень больших
сравнений используется один изменённый средний диапазон. Journal хранит полные
before/intended, затем идёт полный read-back. Несколько COM-вызовов не являются
атомарной транзакцией: возможный эффект без подтверждения остаётся `unknown` и
не повторяется автоматически. Windows/VBE и UserForm execution требуют отдельной
проверки на Windows; host-neutral Harness её не заменяет.

## Имена, Library и миграция

Built-in IDs — нижний регистр, сегменты через точку, действие последним:
`core.write`, `html.data.write`, `excel.range.write`. Skills имеют префикс
`skills.`, например `skills.vba.code.editing`. Новые custom IDs валидируются;
существующие custom IDs не переписываются автоматически.

Library и Inspector строят вложенные группы; tools и skills разделены. Префикс
не выдаёт права: Core / Подключён / По запросу / Недоступен отражают каталог и
текущий Arsenal. Diff внутри шага — одна колонка, 3 строки контекста, сначала
40 строк с раскрытием следующих, безопасный textContent и горизонтальный scroll.
План при unknown помечается как предварительный; он не становится проверенным
результатом. Для слишком большого или отсутствующего сравнения видна причина.

Старые public IDs не являются aliases. Несовместимая история требует нового чата,
при этом сами сообщения/данные сохраняются. Exact admissions пересобираются по
новому профилю. [Таблица переименования](tool-name-migration.json) — только данные
для явной миграции authoring, не registry dispatch.

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
