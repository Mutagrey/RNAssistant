# Парное сравнение профилей tools — 2026-10-26

Owner: Core tool composition / Application workspace; diagnostic owner: Harness.
Срез production: `cd77e93e`. Цель — проверить цену discovery и уменьшения начального
набора, прежде чем менять `workspace-direct`. Это пилот, не квалификация моделей,
Office или больших реальных проектов.

## Протокол

`tests/cli/tool_profile_comparison.py` запускает общий WorkspaceConversationService
через Harness `--workspace-profile-live`. На каждый запуск создаются новый workspace,
чат и store. Две задачи: найти неизвестный путь к `buildInvoiceTitle` и изменить одну
строку; скопировать файл с Unicode-именем штатным `files.copy`. Корпус: 130 файлов,
включая 128 отвлекающих `.ts/.py/.cs/.bas`, кириллицу, немецкий/японский текст и CRLF.
Это текстовый корпус с разными расширениями, а не сборка четырёх языков.
SHA-256 исходного корпуса и точный текст задачи совпадают внутри каждой пары.

| Профиль | Начальная поверхность |
| --- | --- |
| broad | Production `workspace-direct`: 18 доступных non-Skill tools в этом workspace |
| compact | skills_load, resources_find/read/search, capabilities_search/read, questions_ask, files.create/patch |

В компактном профиле девять ID; первые семь имеют префикс `common.`. Skills и точные
admissions могут расширить оба набора штатным способом. Нет специальной инструкции,
подсказывающей модели инструменты, пути поиска или выбранный профиль. Production
настройки и default-профиль не изменяются.

Один запуск на сочетание модель × задача × профиль: восемь live-прогонов. Порядок
broad/compact чередуется по задаче и модели. Cloud: `gemma4:cloud`, `json_object`;
local: `rna-gemma4-12b-32k:latest`, `json_schema`. Внутри каждой пары одинаковы
runtime, schemas, skills, task, context 32768, output 4096, temperature 0,
`developer`/tool-result `user`, фактический wire `reasoning_effort=none`.
Лимиты: 12 model iterations, 20 tool steps, 90 секунд на запрос, 240 секунд на run.
Format repair может увеличить число HTTP-запросов относительно iterations.

Local digest: `cf94f3793da5f501e0a2948b34825db5baceed926438a78551ffaa83f85a4082`.
Cloud tag digest: `ef09f235533c96cd75e8deed88c628335cb69e2b3ce96275d0d7a67fe9887aba`;
это digest локального alias, не неизменяемых удалённых весов. Теги не обновлялись.
До локального этапа проверено отсутствие другой загруженной модели. Cloud context —
лимит планирования клиента; фактическая серверная ёмкость этим опытом не установлена.

## Независимая приёмка

`taskPassed` проверяет карту всех файлов и точные байты результата: ожидается только
одна правка либо один новый файл, остальные байты неизменны. Copy дополнительно
требует успешного `files.copy`. `passed` требует также штатного Completed,
Workspace acceptance Passed и отсутствия unknown effects/исключений. Правильный
файл при незавершённом протоколе не считается полным успехом. Ответ модели не
используется как доказательство выполнения. Scripted-прогон четырёх сочетаний
проверяет сам диагностический путь; он не измеряет качество модели.

## Результаты исходного среза

| Модель / задача / профиль | Байты точны | Задача / run | Tool calls / HTTP | Первый запрос, bytes | Все запросы, bytes | Σ prompt tokens |
| --- | --- | --- | ---: | ---: | ---: | ---: |
| cloud-edit-broad | нет | нет / нет | 4 / 13 | 44255 | 615310 | 122477 |
| cloud-edit-compact | нет | нет / нет | 4 / 12 | 32960 | 428148 | 85864 |
| cloud-copy-compact | да | да / да | 4 / 7 | 33059 | 256992 | 51273 |
| cloud-copy-broad | да | да / да | 4 / 6 | 44354 | 287313 | 56893 |
| local-edit-compact | да | да / нет | 4 / 12 | 34465 | 455754 | 36419 |
| local-edit-broad | да | да / нет | 4 / 12 | 43795 | 568047 | 40475 |
| local-copy-broad | нет | нет / нет | 4 / 12 | 43894 | 563627 | 39431 |
| local-copy-compact | да | нет / нет | 3 / 12 | 34564 | 450854 | 35074 |

Оба профиля: 1/4 полностью успешных run; точная правка edit отдельно выполнена
в обоих локальных прогонах. Это не доказательство равной надёжности.

- cloud: первый edit-запрос 44255 → 32960 bytes (25.5% меньше); provider prompt tokens 8716 → 6515.
- local: первый edit-запрос 43795 → 34465 bytes (21.3% меньше); provider prompt tokens 2550 → 2207.

## Причины и решение

Cloud edit: оба профиля искали в корне без `recursive=true`, затем выполнили обычный
`resources_find`, получили пустую выдачу и не изменили файл. Подтверждена ошибка
маршрутизации: ToolRuntime применяет schema default `recursive=false`, после чего
WorkspaceResourceHandler видит наличие ключа и выбирает file-only FindPaths вместо
обычного просмотра каталогов. Корень с подкаталогами выглядит пустым. Прямой тест
provider.Find этого не обнаруживает; нужна проверка через реальную schema/default
нормализацию runtime. Сам нерекурсивный поиск соответствует текущему контракту,
но описание и сообщение о полноте недостаточно явно ограничивают вывод выбранной
глубиной. Исправление этого дефекта следует отделить от выбора профиля.

Cloud copy compact самостоятельно находит files.copy через capabilities_search/read,
получает одно durable admission и выполняет точную копию. Broad выбирает workspace.files,
сначала вызывает copy без наблюдения источника, получает отказ, читает и повторяет
успешно. Поэтому количество принятых вызовов одинаково, но их полезность различна.
Меньшая поверхность сама по себе не объясняет все различия траекторий.

Local edit: оба профиля нашли источник рекурсивным поиском с continuation, прочитали
его и записали точную правку без посторонних изменений. Затем модель повторяла finish
с путём/прозой вместо допустимых tool result ID в evidence. Оба run достигли лимита
итераций. Это повторение проблемы model/protocol completion, а не неизвестный эффект
или повторная запись: каждый run содержит один WriteOk и ноль WriteUnknown.

Local copy broad ошибочно выбирает type=attachment и не находит файл workspace.
Local copy compact находит и читает файл, но использует files.create вместо явно
запрошенного files.copy. Даже при совпадении байтов это не выполнение ограничений
задачи. Таким образом, отсутствие copy в начальном наборе не гарантирует, что модель
сама выберет discovery; она может применить уже доступную, но неподходящую операцию.

Production broad default сохраняется. Компактная композиция работоспособна и снижает
первый запрос, но этих двух задач недостаточно для смены default. Найденный дефект
исправлен и облачные пары повторены ниже. Следующий этап — проверить локальные ошибки
выбора операции/finish и добавить независимые задачи Plan, выбранных skills, plugin
и bound-Office contributions. Не вводить keyword-router, модельные обходы протокола или частичные
skills ради прохождения пилота.

Provider usage суммируется отдельно от размера JSON-запросов; это не локальный
budget estimate. Prompt tokens включают повторный/кэшированный ввод, а не уникальный
контекст или цену. Нельзя напрямую сравнивать cloud/json_object с local/json_schema:
схемы передаются разными путями и provider accounting может различаться. Время
зависит от прогрева и prompt cache; один прогон не даёт статистического превосходства.

## Исправление и повтор облачных пар

Следующий срез: `be82dbf6` плюс source.patch из отдельного evidence. Workspace find
не подставляет recursive=false при отсутствии аргумента. Обычный просмотр снова
возвращает каталоги; явные recursive/glob-запросы сохраняют file-only поведение.
Описания find/search объясняют глубину и границы вывода об отсутствии совпадений.
Это одна общая правка, без инструкций для конкретной модели и без изменения profiles.
Регрессионный тест через ToolRuntime падает до и проходит после исправления;
шесть text-search checks и workspace discovery/admission/restart проходят.

Повторены четыре облачных сочетания с идентичными задачами, корпусом и настройками.
Все четыре полностью прошли. В обеих edit-траекториях модель теперь сама выбирает
recursive=true, продолжает поиск, читает и патчит файл. Этот повтор оценивает вместе
исправление и уточнение описаний; он не выделяет вклад каждого изменения.

| Cloud после исправления | Задача / run | Tool calls / HTTP | Первый запрос, bytes | Все запросы, bytes | Σ prompt tokens |
| --- | --- | ---: | ---: | ---: | ---: |
| cloud-edit-broad | да / да | 4 / 5 | 44599 | 233576 | 46351 |
| cloud-edit-compact | да / да | 4 / 5 | 33304 | 176842 | 35292 |
| cloud-copy-compact | да / да | 4 / 6 | 33403 | 218475 | 43581 |
| cloud-copy-broad | да / да | 5 / 7 | 44698 | 332908 | 66116 |

Локальные пары после исправления не повторялись: их исходные ошибки finish и выбора
attachment/create остаются открытыми. Этот ограниченный повтор не заменяет сравнение
на расширенном корпусе или Office qualification и не обосновывает смену default.

[Evidence повтора](evidence/tool-profile-findfix-20261026.tar.gz) и
[SHA-256](evidence/tool-profile-findfix-20261026.tar.gz.sha256).

## Воспроизведение и evidence

```sh
dotnet build tests/RNAssistant.Harness/RNAssistant.Harness.csproj --no-restore
python3 tests/cli/tool_profile_comparison.py /tmp/rna-profile-scripted-NEW --scripted
python3 tests/cli/tool_profile_comparison.py /tmp/rna-profile-live-NEW
```

Требуются уже установленные точные model tags; runner ничего не скачивает и не
выгружает чужую модель. `--model cloud|local` ограничивает этап. Локальный HTTP proxy
сохраняет тела запросов/ответов без заголовков авторизации. Evidence содержит исходные
задачи/корпус, before/after hashes, проекты, session/event/CAS, acceptance, HTTP usage,
source commit/patch, диагностические sources, hash Harness binary и checksums.
Архив включает неудачные прогоны; выборочного удаления попыток нет.

[Полный архив пилота](evidence/tool-profile-pilot-20261026.tar.gz) и [SHA-256](evidence/tool-profile-pilot-20261026.tar.gz.sha256).
Archive SHA-256: `9ab0a560a2934531819fe12ef80f475a88f777a34e70bcfff87e9840bf8e138b`.

## Typed short-reference repeat — 2026-10-26

The [reference contract](../decisions/ADR-0018-typed-model-references.md) was tested
on the same installed tags/digests, 32768-token context, temperature, synthetic
multilingual fixtures and broad/compact profiles. This is a new candidate run,
not an isolated causal comparison: directory discovery was already repaired and
the full request changes with reference guidance/projection. Broad defaults remain.

| Model / profile | Edit | Copy | HTTP calls edit / copy |
| --- | --- | --- | --- |
| Gemma cloud / broad | Pass | Pass | 8 / 6 |
| Gemma cloud / compact | Pass | Fail: used create | 7 / 3 |
| Gemma local / broad | Pass | Fail: source not found | 6 / 6 |
| Gemma local / compact | Pass | Fail: used create | 6 / 3 |

All eight runs reach protocol completion; only five satisfy task acceptance.
Seven have exact expected bytes; the copy requirement additionally demands
`files.copy`, so creating identical content does not pass. Local broad copy makes
37 accepted calls but only 8 charged tool steps; repeated calls are suppressed by
existing progress guards. Accepted-call count is not a dispatch count.

Both local edit cases now finish with valid `e` citations, whereas the original
pilot reached iteration limits after correct edits. Four finish proposals also
cited `r…@v…`; admission rejected them and the model corrected the references.
Three cloud execute proposals copied `id` from historical calls; raw validation
rejected them before dispatch. One cloud repair used a scalar evidence string and
required another format repair. These remaining errors are evidence against
claiming that short labels alone solve model protocol/tool-selection quality.

Cloud compact copy regressed from the directory-fix repeat; this single sample
cannot attribute the change to one prompt or projection field. It reinforces the
decision not to switch the default tool profile. No heuristic task-success rule,
GUID fallback or replay was introduced to make the pilot pass.

[Evidence archive](evidence/tool-short-refs-20261026.tar.gz) and
[SHA-256](evidence/tool-short-refs-20261026.tar.gz.sha256) contain four passing
scripted scenarios and all eight live trajectories, exact requests/responses,
acceptance, model identities, executed Harness hash and source patches (including
new untracked source files). Live source base is `afd006db`; unrelated parallel PDF
work had landed after the initial audit base `c5eb1618`. Subsequent changes add
fixture coverage and keep Inspector's unbound trace revisions in its diagnostic
`R` namespace, avoiding collisions with persisted `r` versions. The model wire and
execution/admission candidate exercised by this live run is unchanged.

### Status after reference revision 2

The archived runs above exercised the withdrawn lowercase revision 1. Their raw
requests, source patches and hashes remain immutable; they do not qualify the new
uppercase contract. Revision 2 removes Inspector allocation entirely: one ChatStore
allocator covers message and saved-request observations; Inspector groups canonical
identities. Uppercase contract and persistence/UI behavior have focused deterministic
checks. New live-model quality evidence remains open; no broad/compact default changes.
