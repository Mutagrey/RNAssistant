# Universal Browser: матрица проверок

Дата: 2026-11-11. Owner: владелец изменяемого среза; Windows — Office/WebView owner.
В P0 был только статический просмотр fixtures. В P1 три browser fixtures выполнены
на baseline и final; результаты ниже. Наличие теста не означает qualification.
Выбор проверок — по
[Harness guide](../../tests/RNAssistant.Harness/README.md).

| Область | Existing coverage / начальная точка | Что добавить при реализации | Gate |
|---|---|---|---|
| Saved HTML/plugin views | [browser-saved-views](../../tests/web/browser-saved-views.test.js): retained input/mode, 32K source, exact owner, late leases, restore guard, narrow/zoom | Общий read-only address, единственный mode switch, unavailable actions | P1 |
| Browser portal lifetime | [browser-lifetime](../../tests/web/browser-lifetime.test.js): одинаковый iframe, пять Dock restores, сохранённый input/load count | Только новые случаи при изменении placement; тест не доказывает native HWND | P1/P2 |
| Automatic lifetime, решение 2026-11-11 | Saved/portal/editor/import fixtures: hide/move/restore, close/reopen, late cleanup и dirty guards | Готовый iframe удалён; reopen читает source один раз, ввод сброшен; нет Run cancellation, поздний body не заменяет новую view. Без session list/API | P2c host-neutral passed; Windows Open |
| Live HTML/plugin Browser | [browser-views](../../tests/web/browser-views.test.js): scoped annotation, frame lifetime и actions | Hide/move/restore сохраняют renderer; close освобождает его, reopen загружает заново; dirty source guard | P1/P2c/P3 |
| Source/editor | [html-editor-views](../../tests/web/html-editor-views.test.js), [html-workspace-source](../../tests/web/html-workspace-source.test.js), [html-workspace-write](../../tests/web/html-workspace-write.test.js) | Session ownership, dirty buffers при закрытии, stale source и поздний write ack | P2 |
| Typed application contracts | `browser target:` + existing HTML/resource checks; saved/editor browser fixtures | Exact kind/scope, head drift, ambiguous/missing selected source, read-only capabilities, rejected response/lease cleanup | P2b host-neutral passed; sessions/Windows Open |
| Project import/backing | `HTML project import:` (3 checks), typed artifact bridge, [html-project-import](../../tests/web/html-project-import.test.js) | Exact source/head drift, safe paths/bounds, unsupported refs before write, fault/read-back/no-replay, reopen, late UI and unchanged source | P2a host-neutral passed; Windows/visual Open |
| Plugin isolation | [plugins](../../tests/web/plugins.test.js), [plugin-view-selection](../../tests/web/plugin-view-selection.test.js), saved-release fixture; Harness plugin tests | Wrong frame/origin/session, release vs candidate, revoke during action, чужие grants | P3 + Windows |
| HTML versions/authority | Existing `shared HTML:` Harness family, publication/resource tests | Branch/active pointer, view vs restore, exact compare pair; plugin restore не меняет source | P4 |
| Annotation capture | [html-annotations](../../tests/web/html-annotations.test.js), [html-visual-selection-draft](../../tests/web/html-visual-selection-draft.test.js); Harness `visual selection: workspace intake and common resource read` | Events replay, view close/restart retention, ambiguous reanchor, verified resolution, CAS reachability | P4 |
| Native renderer | [Windows spike](02_ARCHITECTURE_ADR.md#native-spike); existing plugin hidden controller не является доказательством | Child/composition, Office HWND/DPI/IME/focus, popup/download/profile, crash/dispose, counters/memory | P5: Open |
| Agent workflow | Existing ToolRuntime/Resource Gateway/v8 evidence checks; verifier — источник local page checks | Open → read → fill → click → verify на локальной fixture, без вызова live model | P6 |
| Stale/concurrency | Existing exact binding/admission/unknown effect patterns | New document, changed/ambiguous element, iframe, timeout before/after dispatch, human priority, private/shared sessions | P6 |
| Privacy/context | Existing bounded VisualSelection и compiler/CAS tests | Password/token/private field masking, screenshot refusal, redacted diagnostics, bounded pagination и отсутствие двойного DOM в request | P6 |
| Final qualification | Existing Dock/performance and Office manual workflows | Cross-chat/multi-agent stress, document close/reopen, supported Office apps, repeated memory/cleanup cycles | P7: Open |

<a id="p1-checks"></a>
## Минимальные команды P1

После начала реализации — baseline до правки и один повтор после. Browser suites
выполняются **последовательно**; они сами запускают local fixture server.
Использовать существующий Playwright/browser setup, не добавлять npm/bundler в продукт.
При необходимости `BROWSER_EXECUTABLE` задаёт установленный Chromium.

```bash
node tests/web/browser-saved-views.test.js
node tests/web/browser-views.test.js
node tests/web/browser-lifetime.test.js
```

В saved-view fixture проверить expected load/read count, сохранение input, mode/file/page,
bounded source, поздний transfer cleanup, exact chat/document/URI и plugin restore.
Дополнить только assertions нового общего UI. Не заводить тест, повторяющий устройство
descriptor, вместо проверки пользовательского поведения. C#/VSTO tests для P1 не нужны.
При изменении только docs эти команды не выполнять.

## Результаты P2c — 2026-11-11

Production baseline `a7254360`; код приложения не менялся. Последовательно прошли:
`browser-saved-views`, `html-editor-views`, `browser-lifetime`, `html-project-import`.
Использованы локальный Chrome и существующий synthetic bridge, без сборок/Harness/Office.

- Saved HTML: после close iframe и portal surface отсутствуют; повторное открытие
  создаёт новый iframe, возвращает исходный ввод и делает ровно одно body read.
  Смена активного чата не меняет chat/document/URI открываемого source.
- Закрытие во время body read отменяет reader signal. Даже если транспорт вернул
  body позднее, отправляется `closeArtifactTransfer`, новая view того же source
  сохраняет свой ввод; прежняя view не возвращается. Late export cleanup уже покрыт.
- Live editor: dirty close отклонён; clean close удаляет editor/iframe, reopen
  создаёт новые и читает source один раз. Несохранённый буфер другого editor сохранён.
- Portal: пять Dock restores — один iframe load и сохранённый ввод. Existing checks
  сохраняют hide/switch, narrow/zoom, exact scope и read-only guards.
- Browser close не отправляет Run cancellation. Existing import fixture подтверждает
  late Created после close, отсутствие повторного import и автоматического запуска чата.

Добавлены только недостающие assertions в два existing fixtures. В новой проверке
другого dirty editor сначала исправлен locator: скрытый `onlyWhenVisible` editor
нужно активировать перед чтением DOM. Это ошибка проверки, не дефект приложения.
Проверены Web cleanup/команды освобождения и DOM lifetime, без heap/native handle
профилирования и реального Office Run. Windows/WebView2, pointer routing и прежнее
визуальное принятие остаются Open; новый UI не вводился. Версия не повышается:
изменены только tests/docs.

## Результаты P2b — 2026-11-11

Baseline `f918a56f`; saved Browser и HTML editor fixtures прошли до изменения.
После него прошли `browser-saved-views`, `html-editor-views`, `html-project-import`.
Saved checks добавляют foreign document/URI до body download с lease cleanup;
source-only не создаёт iframe и скрывает неподдержанные действия. Editor проверяет
read-only preview/annotation denial и отказ чужому document response без замены source.
[Замеры](../ui/review/browser-p2b.json), [source-only](../ui/review/browser-p2b-source-only.png),
[read-only live](../ui/review/browser-p2b-live-readonly.png).

Пять host-neutral checks: `browser target:`, `html editor: exact source` и три
`HTML project import:`. Изолированная net8 сборка — `f918a56f` + только P2b;
параллельный window Compile конфликт в основном checkout исключён. Нет Office/VSTO.
`html import: inert preview and exact provenance` проходит новые descriptor/source-only
assertions, затем падает на старом счётчике `before body hydration`: expected 2, got 3.
Тот же сбой воспроизведён с исходными reader/fixture из `f918a56f`; тест не засчитан
как pass. [Отдельный риск](../stabilization/RISK_REGISTER.md#browser-p2b--2026-11-11).
Pointer routing над iframe, пользовательское визуальное принятие и Windows/controller
delivery открыты. Preview source-only screenshot — capability fixture, не новый режим
настройки пользователя; размеры общих controls не менялись.

Ревью: saved HTML → source/preview → reload; затем live HTML → source и обратно;
проверить сохранённый ввод и открыть тот же снимок после смены активного чата.

## Результаты P2a — 2026-11-10

Harness перед commit: изолированный `c3a9427e` + только P2a,
без параллельных незавершённых storage-изменений.
Прошли пять focused host-neutral checks: три `HTML project import:`,
`bridge: typed artifact viewer`, `workspace files: paths and historical view`.
Проверяются реальные CAS/source reads, 15 preflight refusals, read-back drift и fault
после dispatch. Net8 Harness собран с C# 7.3; это не Office/controller qualification.
Browser: `html-project-import.test.js` и `browser-saved-views.test.js` прошли в local
Chromium; import clicks идут над Source. Pointer routing над iframe остаётся Open.

Отдельный `artifact transfer: project roundtrip and independence` падает на утверждении
`imported project can be continued through normal tools` и в чистом archive `c3a9427e`.
Он не засчитывается как pass; [риск](../stabilization/RISK_REGISTER.md#html-project-import--in-review-2026-11-10).

Ревью: открыть сохранённый HTML → меню сохранения → создать проект → вернуться
к снимку и открыть уже созданный проект. Проверить отказ на SDK/data-bound пакете.
[Широкий вид](../ui/review/browser-project-import-wide.png),
[420 px / 125%, неподтверждённый результат](../ui/review/browser-project-import-narrow.png).

## Результаты P1 — 2026-11-04

Chrome 157.0.8103.38 / macOS, production Shell + synthetic bridge. Baseline `c6659ed2`:
все три команды прошли; final тоже прошли. Дополнен только saved-view fixture:
read-only input/Enter, один switch/iframe, отсутствие unsupported actions, ready
source/preview на 420 px / 125%, очистка controls после failed reload.

Два HTML reads и два plugin opens при пяти выборах сохранены; исходник 32 000 chars;
Browser iframe остаётся с одной загрузкой после пяти layout restores. Reference
inline mechanism в той же fixture по-прежнему загружается шесть раз. Это контроль
lifetime, не заявление об общем ускорении. [Полные замеры](../ui/review/browser-p1.json).
Ready source/preview и wide/narrow screenshots просмотрены локально. Пользовательское
визуальное принятие и Windows/Office/WebView2 evidence открыты; builds не запускались.

## Визуальное ревью P1

Запуск: существующий `python3 -B prototypes/serve_ui.py --port 0`, затем адрес
`preview/web/?scenario=ready`, выведенный сервером. Saved HTML test data берутся из
существующего fixture; на настоящих данных — сохранённый проект из каталога материалов.
Процедура после реализации:

1. Открыть saved HTML через Материалы; проверить точный read-only адрес, title/version,
   reload, menu и отсутствие web navigation/Diff.
2. Ввести текст в preview; выбрать «Исходник», файл и следующую страницу; вернуться
   в preview и убедиться, что текст сохранён.
3. Move/split/hide/reopen и restore layout; проверить mode/input, затем close и late load cleanup.
4. На 420 CSS px / 125% zoom пройти toolbar клавишами Tab/Enter; проверить geometry,
   отсутствие горизонтального overflow и скрытого focus owner.
5. Открыть live HTML и plugin view; проверить их прежние reload/actions/annotations.

Для сравнения components использовать одинаковые viewport/state/theme с принятым
Dockview reference. Измерять geometry/load/read counts; не объявлять ускорение по
одному screenshot или automation wall time. В отчёте отделять локальный browser
результат от пользовательского принятия и Windows/Office evidence.

<a id="toolbar-review"></a>
## Визуальное ревью toolbar и Chat View

Production fixture: `python3 -B prototypes/serve_ui.py --port 0`, путь `/preview/web/`.
Saved HTML fixture/evidence готовит `tests/web/browser-saved-views.test.js`;
для снимков задать `BROWSER_SCREENSHOT` (префикс пути). Preview использует mock bridge.

1. Материалы → saved HTML → фокус адреса → вне поля; проверить расширение без сдвига
   страницы. Иконка у адреса показывает версию/CAS; постоянной подписи нет. ▶ / `</>`
   сохраняют выбранный файл/страницу и ввод.
2. Нажать «Обсудить» дважды: один существующий Chat View рядом, прежний draft и input
   страницы сохранены. На narrow используются вкладки одной группы.
3. В live plugin явно открыть source chat, выбрать другой чат/страницу, добавить
   аннотацию: она остаётся в draft первоначального получателя. Отправка остаётся ручной.
4. Открыть clock и `⋯`, закрыть через Escape, вернуть вкладку/раскладку; проверить
   light/dark, 420 px и 125%, narrow tools под ⋯. Upload plugin и restore сохраняют
   прежние domain guards; HTML export скачивает файлы выбранного снимка.
5. В живом HTML изменить текущую публикацию из другого агента: активная view обновится,
   скрытая — при возврате. Reload перечитывает current; dirty buffer сохраняется.

Saved fixture использует keyboard activation для layout save/restore: быстрые mouse
clicks в overlay над iframe воспроизводят известный pointer-routing gap. Успех fixture
не закрывает этот mouse/Windows evidence.
`BROWSER_POINTER_PROBE=1` включает именно эти mouse clicks без задержек/повторов.

Saved history regression (2026-11-10): проект без captured head, публикация после
открытия вкладки, поздний ответ закрытого меню, stale cursor → первая страница,
другой активный чат при прежнем exact target, retained iframe/input. Новые клики
истории проверяются над source view. Host-neutral Harness filters:
`shared HTML: saved history` и `bridge: typed artifact viewer`; первая проверка
использует 52 реальные CAS публикации и foreign document. Windows delivery отдельно.

Coverage: browser-saved-views (reuse/group placement/draft), browser-views (live annotations),
browser-lifetime (iframe retention), plugin-view-selection (адресат, independent views,
возврат из менеджера, late close), html-editor-views (buffers/modes/guards).
[Общие geometry и frozen Demo](../ui/review/browser-toolbar.json).

## Windows evidence

Минимум Windows x64 + Office x64 + VS 2022; фиксировать версии Office и WebView2
Runtime на экране. Spike сначала проверяет embedding в реальном task pane, P7 —
поддерживаемые host-приложения. Только фотографии экрана: screen diagnostics содержит
scenario ID, вариант renderer, итог, counts и причину отказа. Не просить logs/files/dumps.
Недоступное evidence оставлять Open по конкретному сценарию; P1–P4 от него не блокируются.

## Проверка текущего docs-only шага

Проверить `git diff --check`, внутренние относительные ссылки/anchors новых документов
и добавленных canonical sections, а также побайтовое совпадение `00_SOURCE_SPEC.md`
с приложением пользователя. Source-code ссылки относятся к baseline из аудита.
Результат записать в [06_PROGRESS](06_PROGRESS.md). Builds/harness/Office validation
и release/version операции в эту задачу не входят.
