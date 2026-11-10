# Universal Browser: матрица проверок

Дата: 2026-11-04. Owner: владелец изменяемого среза; Windows — Office/WebView owner.
В P0 был только статический просмотр fixtures. В P1 три browser fixtures выполнены
на baseline и final; результаты ниже. Наличие теста не означает qualification.
Выбор проверок — по
[Harness guide](../../tests/RNAssistant.Harness/README.md).

| Область | Existing coverage / начальная точка | Что добавить при реализации | Gate |
|---|---|---|---|
| Saved HTML/plugin views | [browser-saved-views](../../tests/web/browser-saved-views.test.js): retained input/mode, 32K source, exact owner, late leases, restore guard, narrow/zoom | Общий read-only address, единственный mode switch, unavailable actions | P1 |
| Browser portal lifetime | [browser-lifetime](../../tests/web/browser-lifetime.test.js): одинаковый iframe, пять Dock restores, сохранённый input/load count | Только новые случаи при изменении placement; тест не доказывает native HWND | P1/P2 |
| Live HTML/plugin Browser | [browser-views](../../tests/web/browser-views.test.js): scoped annotation, frame lifetime и actions | Регрессия общего toolbar после P1; затем detach/reattach и session-close | P1/P2/P3 |
| Source/editor | [html-editor-views](../../tests/web/html-editor-views.test.js), [html-workspace-source](../../tests/web/html-workspace-source.test.js), [html-workspace-write](../../tests/web/html-workspace-write.test.js) | Session ownership, dirty buffers при закрытии, stale source и поздний write ack | P2 |
| Typed application contracts | Existing Harness HTML/resource/bridge checks | Resolver ambiguity, permission intersection, exact scope, close/reopen generation; fake renderer | P2 |
| Project import/backing | Existing workspace file authority / HTML export checks | Exact source, self-contained package, safe paths/bounds, no partial import of data refs, fault/read-back, reopen and unchanged source | P2a, Planned |
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
