# Universal Browser: статус

## Единый локальный Browser — реализация пакета, 2026-11-13

Scope пользователя заменяет external P5/P6 roadmap ниже: один local page runtime,
local navigation, общий Browser UI/tools, явные saved checkpoints и StartSurface.
Ниже сохранена история прежнего внешнего Browser, не разрешение его дальнейшего развития.
Работа последовательная на `main`, исходный HEAD `e9bb52c1`; существующий незавершённый
preview/automation patch включён в scope по подтверждению пользователя.

| Этап | Фактический результат / следующий шаг |
|---|---|
| 08 / `702f3d28` | `16.14.4-dev`: LLM без User-Agent; 12 loopback requests, Harness 1/1 |
| 04 V1 | Actor/run-scoped candidates, package reuse, source guard до dispatch; три focused Harness cases (authority, preview/grants, published history) |
| F0 | Общий automation owner извлечён из native Page; multi-page discovery/semantic ambiguity guards. 11 existing Harness cases проходят. Далее host-neutral targets/runtime, typed bridge и local resolver; singleton delivery ещё не переключён |
| Ранний 02 | Удалить HTML HTTP proxy, внешнюю навигацию и browser-only deployment controls |
| F1–F4 | Local delivery + adapters, tools/media, Save/Activate/Delete, navigation/Dock; каждый срез продолжает текущий HEAD |
| F5 / 07 | Cleanup/canonical docs, затем отдельное сокращение экранов/Inspector |

Windows/WebView2 network enforcement (B02), native input и Office delivery открыты.
Активный local preview не может заявлять strict isolation без runtime qualification;
при неподдерживаемой границе требуется source-only с причиной. UI ещё не принят визуально.

## Подписанная конфигурация — In Review, 2026-11-12

`16.13.0-dev`: generic deployment-config.bin; Developer password unlock, key path,
sites/manual-any, save/folder/lock. Старые версии политики не применяются даже с
корректной подписью; агент не получает manual-any authority. PFX/PowerShell path удалён.
Разовые Mac C# и Windows GUI utilities используют общую генерацию encrypted .bin;
public trust embedded после генерации, private key gitignored. Реальный пароль владелец задаёт локально.
11 Harness + production Settings fixture проходят; Mac utility generation/rotation
проверены с temporary issuer. Windows GUI/Office/WebView2 compile/delivery Open.
[Контракт/настройка](03_CONTRACTS.md#signed-access), [evidence](05_TEST_MATRIX.md#signed-access).
Windows utility теперь собирается отдельным `build-deployment-keys.cmd`. Public trust
пока отсутствует, поэтому сборка предупреждает о закрытом Developer/external Browser;
после генерации ключей нужен reload проекта и rebuild. Путь private .bin становится
default редактора из build metadata. Ошибки Office из фото исправлены в исходниках;
повторная Windows compilation остаётся открытой.
Первый запуск Windows key utility показал общий отказ самопроверки до записи файлов.
Генерация .NET Framework переведена на CNG RSA 3072, этап ошибки теперь виден в GUI;
повторное фото всё ещё показывает прежний общий отказ. Сериализация закрытых RSA
параметров теперь явная; BIN сохранён. Точная причина на Windows и повторная проверка
новой GUI сборки остаются открытыми.
Далее P6h image delivery; diff/P4b отложены. P6g отправлен в origin: `d1e7b4a2`.


Дата: 2026-11-11. Scope: P0–P3 baseline, P4a source diff baseline, P5a native pilot,
P6a/P6b read/local actions, P6c visualization, P6d user control/access, P6e navigation,
P6f external actions и P6g retained research observations.

## P6g: исследование нескольких страниц — In Review

`16.12.0-dev`: source URL/title/time и immutable snapshotTarget через existing
ResourceAuthority/CAS/context. Facts A/B остаются доступны после navigation,
compaction и close/restart; historical read не даёт live action authority.
Text node расширен до 2000 символов при прежнем total budget; полного DOM/image
capture нет. 10 Harness + 3 Chrome fixtures проходят; [evidence](05_TEST_MATRIX.md#p6g).
Windows native compilation/delivery Open. P6f закоммичен `c75218b2`.
Далее P6h safe screenshots/image delivery; дальнейший diff/P4b остаются отложенными.

## P6f: external actions и link navigation — In Review

`browser.fill/click` работают на разрешённых страницах с bounded verification;
`browser.navigate` принимает url либо observed linkName. Exact href остаётся runtime-only,
проверяется повторно, затем используются existing site/redirect/load guards. Cursor и
manual takeover сохранены. 9 Harness + 3 Chrome fixtures проходят; [evidence](05_TEST_MATRIX.md#p6f).
Windows compilation/delivery Open. Предыдущие срезы закоммичены отдельно:
P4a `95b40bc5`, P6e `38959ae6`; P6f product `16.11.0-dev`, не release.

По вопросу пользователя сверены [model results и image snapshots](03_CONTRACTS.md#model-results):
сейчас есть bounded semantic view и отдельные dev web.verify PNG/trace; external image
delivery модели нет. Старое mutable Browser observation может выпасть из context.
P6g выше добавляет retained research results/источники; P6h safe screenshots ещё Planned.

## P6e: agent navigation — In Review

В existing странице добавлен `browser.navigate` с delivered read, initial/redirect/final
site access и новым runtime document binding после загрузки. Trusted input останавливает
agent dispatch; timeout/unknown не replay и не освобождают поздние redirect. 9 Harness
cases в отдельной копии `de343788` + P6e и 2 Chrome fixtures проходят; [evidence](05_TEST_MATRIX.md#p6e).
Windows compilation/delivery Open. Bounded external fill/click и semantic links
добавлены последующим P6f выше; ограничения полного research API сохраняются.

## P4a: baseline завершён; дальнейшая доработка отложена

Режим «Сравнение» читает выбранную immutable пару HTML/plugin versions через existing
owners; bounded text, файл/статус, без preview data и изменений публикации. 3 Harness
cases и 2 production Chrome fixtures проходят; wide/narrow/125% просмотрены.
[Evidence](05_TEST_MATRIX.md#p4a). Visual acceptance/Windows delivery открыты.
По уточнению пользователя diff и P4b не в приоритете; продолжение — P6e navigation.
Owner: Web Browser/PreviewSurface; Application/Core — sessions/contracts/evidence;
Office/WebView — experimental P5a native adapter. P1, P2a и P2b реализованы, **In Review**.

## P6d: ручное управление и сайты агента — In Review, 2026-11-11

Ручной ввод блокирует mutations страницы и запрашивает паузу exact bound Run через
existing Inbox; другой агент не может её снять. «Продолжить агентом» инвалидирует
старые observations/preparations; неизвестный эффект и Stop сохраняют свои guards.
Отдельный список сайтов принимает exact HTTP(S) origins и `*.domain`; default deny
для external model reads, свободная ручная навигация, отдельные HTML network grants.

8 Browser Harness checks, existing pause authority/race check и три Chrome fixtures
проверяют срез; wide/narrow/125% UI просмотрен. Native event/Office/Windows delivery,
iframe input и визуальное принятие остаются открытыми. External actions всё ещё local
probe only. Product `16.7.0-dev`, фиксируется по запросу пользователя. [Контракт](03_CONTRACTS.md#p6d-user-control),
[запуск/ревью](05_TEST_MATRIX.md#p6d). Нового store/scheduler/registry нет.

## P6c: видимые действия агента — In Review, 2026-11-11

Реализован запрошенный анимированный указатель: движение к actual target, outline,
click pulse, статус по runtime terminal и исчезновение через 2,5 s без действий.
Ручной ввод/мышь, scroll/resize, native hide/navigation убирают декорацию сразу.
Один общий view в closed shadow root native page, existing product tokens;
model API, отдельный store, системная мышь и способ DOM fill/click не меняются.

7 Harness checks и два Chrome fixtures проходят. Просмотрены actual page content
wide и narrow/125% screenshots; visual acceptance и Windows/WebView2 delivery открыты.
[Scope](04_MIGRATION_PLAN.md#p6c-action-visualization), [контракт](03_CONTRACTS.md#p6c-action-visualization),
[запуск/4 действия ревью](05_TEST_MATRIX.md#p6c). Product `16.6.4-dev`, commit `67a45b29`.

## P6b: local actions — In Review, 2026-11-11

Готов первый local workflow read → fill → read → click → verify: два semantic tools,
exact delivered observation, одноразовая native preparation, повторные element/user
checks и read-back. Existing ToolRuntime/policy и mutation journal сохраняют эффект;
unknown не повторяется и блокирует дальнейшие mutations страницы до manual reopen.
Office/bound Workspace используют один handler, UI и второй session store не добавлены.

Семь focused Harness checks проходят на изолированной копии baseline `478edb71` с
Browser-срезом: рабочий tree одновременно меняется в storage/UI. Два Chrome fixtures
проверяют production isolated-world script. Windows compile/WebView2/controller
qualification остаётся Open; P6 не объявлен поддержкой произвольных сайтов.
[Контракт](03_CONTRACTS.md#p6b-local-actions), [evidence](05_TEST_MATRIX.md#p6b).
Product `16.6.0-dev`; P6b закоммичен `fa9e0176`.

## P6a: semantic read — In Review, 2026-11-11

Реализован read-only consumer одной страницы P5a: `core.find/read` через existing
Gateway, с chat ownership, exact document guard и navigation generation. Native
capture — isolated world, bounded semantic data без текстовых значений controls и
private regions; sourceComplete=false. Existing CAS/authority сохраняют evidence;
новых session stores/API управления сессиями нет. Bound Workspace использует
`type=office`; standalone Workspace без Office binding пока не подключён.
Пять адресных Harness checks и два Chrome fixtures проходят. Capture/controller
delivery и compilation на Windows остаются Open. [Контракт](03_CONTRACTS.md#p6a-semantic-read),
[evidence](05_TEST_MATRIX.md#p6a). Product `16.5.0-dev`; код и локальные проверки завершены.

## P5a: WinForms в Dockview — In Review, 2026-11-11

По выбору пользователя начинаем с целевого Dockview, без предварительного отдельного
окна. Под `RNA_BROWSER_PILOT=1` готова одна ручная внешняя вкладка с общим toolbar,
HTTP(S), назад/вперёд/reload, временным InPrivate-профилем и экранной диагностикой.
Page/policy отделены от native placement; fallback-окно появится только при выявленном
блокере. Agent tools, persistent profiles и второй session store не вводятся.

Два host-neutral Harness checks и новый browser fixture проходят; existing Browser
lifetime также проходит. Wide/narrow Shell screenshots просмотрены с synthetic native.
Windows compilation, фактические HWND/DPI/focus/IME/overlays/process cleanup и
визуальное принятие открыты. P5 целиком не закрыт. Следующий шаг P5a — native evidence
при первом доступном Windows-запуске; P3/P4 продолжаются независимо.
[Запуск и точные границы проверки](05_TEST_MATRIX.md#p5a).

## P3b: актуальность восстановления release — In Review, 2026-11-11

P3a закоммичен `8d337666` (`16.3.2-dev`). В следующем срезе исправлен stale restore
сохранённой plugin-вкладки: current head читается при открытии меню, guard больше
не хранится в Dock route. Поздний ответ/ошибка каталога не разрешают действие;
конфликт после чтения не повторяется автоматически. Instance и iframe сохранены.
Existing `browser-saved-views` red → green, включая 1280/100% и 420/125%; новых API
или stores нет. [Evidence/ревью](05_TEST_MATRIX.md#результаты-p3b--2026-11-11).
Закоммичен `13a4a5b8` (`16.3.3-dev`); прежние релевантные browser passes переиспользованы,
pre-commit version check прошёл. Visual acceptance и Windows/WebView2 evidence открыты.

## P3a: ошибка исходников без подмены release — In Review, 2026-11-11

Первое открытие plugin workbench после failed source check больше не подставляет
active release. Ошибка остаётся видимой без iframe; прежний рабочий preview при
failed refresh сохраняется. Watcher, manual retry и явная команда active release
проверены. Исправлена существующая ветка PluginView; новых services/DTO/UI нет.
Focused red → green и независимые plugin views прошли; общий browser fixture
остановился на прежней focus assertion до нового сценария. [Evidence/ревью](05_TEST_MATRIX.md#результаты-p3a--2026-11-11).
Остаток P3, визуальное принятие и Windows/WebView2 evidence открыты.

## P2c: автоматический lifetime — проверен host-neutral, 2026-11-11

Пользователь утвердил автоматическое управление: switch/hide/move/split сохраняют
страницу и ввод; close вкладки освобождает renderer/leases; reopen загружает target
заново. Отдельные список sessions/команда завершения и лимит закрытых живых страниц
не нужны. Run, сохранённые данные и dirty source guards остаются у прежних owners.
Начатый незавершённый код detached sessions удалён; P2b закоммичен `36a44073`.
План/контракты и canonical UI docs приведены к [решению](02_ARCHITECTURE_ADR.md#lifetime-authority-и-восстановление).
P2c проверил existing lifecycle: четыре browser fixtures прошли, production-правки
не понадобились. В existing saved/editor fixtures добавлены close/reopen готовой
страницы и поздний body после повторного открытия. Новый iframe, одно source read,
сброс preview-ввода, dirty guards и отсутствие Run cancellation подтверждены.
[Evidence и границы](05_TEST_MATRIX.md#результаты-p2c--2026-11-11). Изменены tests/docs,
без нового runtime/UI и bump версии. Windows/Office и прежнее визуальное принятие открыты.

## P2b: цели и возможности HTML — In Review, 2026-11-11

Общий Application resolver проверяет exact source/scope/kind по metadata существующих
owners. Typed descriptor приходит в прежних preview/page/workspace responses, без
отдельного запроса, store или дополнительной гидрации. Saved snapshot остаётся
неизменяемым, raw HTML — inert source; доступное live чтение сохраняется при запрете записи.
UI проверяет адресата до применения и использует подтверждённые capabilities;
неподдержанный preview не создаёт iframe, поздний/чужой ответ не заменяет источник.

Пять focused host-neutral checks и три browser fixtures прошли. Старый raw import
fixture падает также без P2b, вынесен в риск. [Evidence и ревью](05_TEST_MATRIX.md#результаты-p2b--2026-11-11).
Визуальное принятие и Windows/controller delivery открыты. Model semantic bindings,
plugin grants, managed source preview и runtime session lifetime этим срезом не заявлены.

## P2a: самостоятельный проект из снимка — In Review, 2026-11-10

«Сохранение проекта» → «Создать проект из снимка» копирует exact сохранённые
HTML/CSS/JS в managed workspace. Application import использует существующие
WorkspaceStore/WorkspaceFileService; исходники — файлы, история/evidence — CAS.
Каталог открывает проект после read-back всех файлов. Исходный artifact не меняется.
Пределы: 100 файлов / 16 МиБ UTF-8; data bindings, распознаваемые SDK/vendor/external
references отклоняются до записи. `Created` доказывает копирование исходников,
не исполнение страницы; сборка/preview самостоятельного проекта ещё впереди.

Typed результаты Created/Rejected/Unknown различают подтверждённую копию, отказ
до записи и возможный эффект. Unknown сохраняет папку для явной проверки без replay.
Поздний ответ не меняет активную вкладку; повторное открытие использует созданный
workspace. Автоматического чата/Run нет. Новый revision store или adapter не введён.

Пять focused host-neutral checks и два browser fixtures прошли; старый transfer
roundtrip падает также на чистом `c3a9427e` и записан отдельным риском. Office/VSTO
не запускались. [Scope и остаток](04_MIGRATION_PLAN.md#p2a-импорт-самостоятельного-проекта--in-review-2026-11-10),
[проверки и скриншоты ревью](05_TEST_MATRIX.md#результаты-p2a--2026-11-10).
Визуальное принятие пользователем, pointer routing и Windows delivery остаются Open.

## Продолжение: проверки P1 и граница P2a — 2026-11-10

`browser-views` исправлен под текущий responsive toolbar: «Обсудить» открывается
через «Инструменты страницы», когда кнопка скрыта. Полный live HTML/plugin сценарий
прошёл на локальном Chrome 157.0.8103.51: annotations, exact recipient, retained iframe,
restore/move, narrow/zoom и late cleanup. Это исправление теста, не pointer routing.

`BROWSER_POINTER_PROBE=1` теперь проверяет доставку pointerdown/up/click и печатает
parent/iframe recipients. Повтор поймал все три события в «Сохранённый HTML A» вместо
«Сохранить раскладку», без таймаута на следующем действии. Обычный saved fixture прошёл
(2 exact HTML reads, retained input, source ≤32K, 420 px/125%). Изолированные простые страницы
без RNAssistant не воспроизвели сбой; точная причина и Windows evidence остаются Open.
Ненадёжные CSS/таймерные обходы в production не добавлены.

На этом этапе подготовлен конкретный [P2a](04_MIGRATION_PLAN.md#p2a-импорт-самостоятельного-проекта--in-review-2026-11-10):
самостоятельный workspace из exact снимка, managed source files под existing workspace
owner, CAS для версий/evidence. Последующая реализация import описана выше.

## История сохранённого HTML — исправлена host-neutral, 2026-11-10

Typed getSavedHtmlHistory разрешает current document publication из exact target;
существующие DocumentArtifactStore и ArtifactLibraryProjectionService возвращают
метаданные порциями по 50. Нет чтения HTML bodies, нового store или смены chat selection.
Clock доступен без captured head и перечитывает список при каждом открытии. Поздние
ответы старого меню не применяются; retry stale cursor начинает с первой страницы.
Открытый снимок, iframe, файл исходника и ввод сохраняются.

Прошли CAS history fixture (52 публикации другого чата, foreign document, paging,
stale cursor), typed artifact bridge и browser-saved-views (свежая версия, no captured
head, late menu response, другой активный чат, сохранённый iframe/input, 420 px/125%).
C# проверен в isolated checkout d0ab9d09 + этот fix; в основном checkout параллельные
agent/storage edits. Windows delivery и визуальное принятие пользователем открыты.
[Меню истории](../ui/review/browser-history.png): v3 — текущая публикация, v1 — открытый снимок.

Pointer-routing остаётся Open: быстрые клики по menuitem попадают в sandboxed iframe
даже с нативным modal dialog. `BROWSER_POINTER_PROBE=1` сохраняет воспроизведение;
обычный saved fixture использует keyboard layout actions, новые history actions —
мышь над source view. Ненадёжные CSS/top-layer эксперименты удалены.

## Строка браузера и обновление HTML — In Review, 2026-11-10

Реализовано уточнение пользователя: слева назад/вперёд/reload/аннотация, затем адрес
и provider actions. Фокус раскрывает адрес на всю строку и показывает инструменты;
blur/Escape сворачивает. До 600 px правая группа открывается через ⋯. Постоянная
подпись снимка и отдельная title/path row удалены. Название/версия/хранение открываются
у адреса через общий ActionMenu. CAS/files — место хранения; export не объявляется
созданием независимого workspace. Публикация plugin сохраняет прежние проверки.

Исправлен Web bug живого HTML: reload вызывал только reloadPreview локальной копии,
а resourceChanged уведомлял только привязанные данные. Теперь reload читает metadata
через existing getHtmlWorkspace/RefreshSelected, document notice обновляет чистую view,
hidden view читает при activate. Новое событие во время read требует свежего результата.
Dirty buffer сохраняется; смена исходника блокирует stale write. C#/bridge не менялись.

Проверены saved Browser, live HTML editor и plugin selection в локальном Chromium.
Новые HTML checks покрывают manual reload, другой agent/chat, hidden activation,
событие во время read, чужой scope, повторную generation и dirty buffer. Saved fixture
проверяет ширину адреса, неизменную геометрию iframe, focus/menu/narrow/125%, retained
input/modes и late cleanup. Два HTML читаются дважды, два release открываются дважды;
source page 32K, overflow 0. Ранее пройденный lifetime повторно не запускался.

[Строка](../ui/review/browser-address-wide.png),
[фокус адреса](../ui/review/browser-address-focused.png),
[420 px / 125%](../ui/review/browser-address-narrow.png),
[сведения](../ui/review/browser-address-info.png),
[live HTML](../ui/review/browser-address-live.png), [замеры](../ui/review/browser-address.json).

Открыто: saved exact view перечитывает выбранный снимок, его lifetime не превращён в
follow-latest. Независимый managed workspace, CAS → workspace/plugin, Diff и native URL
остаются следующими domain-срезами. Свежая saved history реализована адресным чтением
метаданных в срезе выше; выбранный exact snapshot автоматически не заменяется.
Pointer routing поверх iframe по-прежнему Open: saved layout restore проверяется
клавиатурой, быстрый mouse click воспроизводит старый риск. Windows/Office и визуальное
принятие пользователем открыты. Build/commit/push не выполнялись.

## Продолжение P1: toolbar и обсуждение — In Review, 2026-11-05

Пользователь одобрил концепцию и выбрал **отдельный Chat View рядом в Dockview**.
Общий PreviewSurface получил compact icons: play/code, selection/clock/layers/chat;
редкие plugin actions перенесены в общий ActionMenu. Публикация использует прежний guarded handler; после уточнения — upload icon. HTML source modes сохраняют aria/title/pressed и прежние guards.
История HTML/plugin также перенесена в ActionMenu: собственные details/popover и CSS
удалены; проверены pagination, Escape/focus и меню при 420 px / 125% без обрезания.

«Обсудить» повторно открывает exact существующий Chat View рядом; narrow использует
одну группу. Draft/composer не клонируются, iframe не перезагружается. Plugin view
закрепляет workspace/session по явному выбору исходного чата, ошибки и annotation
drafts адресуются ему. Исправлен preserveChatOwner: переход из менеджера к Browser
больше не возвращает старый чат через отложенную загрузку project catalog.

Пройдены Chromium проверки: browser-saved-views, browser-lifetime,
plugin-view-selection, html-editor-views. browser-views остаётся нестабильным. Проверены reuse/draft retention, две загрузки
двух saved HTML/releases, 32K source pages, одна iframe load за пять restores,
аннотация после выбора другого чата, late cleanup, narrow/125% и keyboard.
Baseline и повторные live HTML annotation проверки периодически завершаются timeout;
последняя попытка красная. Зафиксировано попадание pointer events в iframe под
кнопкой сохранения без вызова stage; устойчивого исправления нет.
[Открытый риск](../stabilization/RISK_REGISTER.md#browser-annotation-pointer-routing--open-2026-11-05).

[Компоновка с чатом](../ui/review/browser-toolbar-wide.png),
[420 px / 125%](../ui/review/browser-toolbar-narrow.png),
[сверка controls с frozen Demo](../ui/review/browser-toolbar.json).
Концепция Approved; production visual acceptance **Open**. CAS/project ownership,
управляемые и файловые workspace, настоящий Diff, browser sessions и external native
URL не реализованы этим presentation-срезом. Runtime/bridge/storage не менялись;
Windows/Office/WebView2 qualification остаётся отдельной. Commit/push/build не выполнялись.

## Вход и документы

| Документ | Назначение |
|---|---|
| [00_SOURCE_SPEC](00_SOURCE_SPEC.md) | Точная копия задания пользователя, historical target input |
| [01_AUDIT](01_AUDIT.md) | Реальные routes/owners, reuse/remove и открытые gaps |
| [02_ARCHITECTURE_ADR](02_ARCHITECTURE_ADR.md) | Proposed решения, native alternatives и Windows spike |
| [03_CONTRACTS](03_CONTRACTS.md) | Proposed semantic API/runtime bindings и P1 presentation interface |
| [04_MIGRATION_PLAN](04_MIGRATION_PLAN.md) | Очередь P0–P7, конкретный P1, проверки/removal/rollback |
| [05_TEST_MATRIX](05_TEST_MATRIX.md) | Existing coverage, минимальные новые сценарии, Windows gates |

Исходное приложение: `RNAssistant_Universal_Browser_Agent_Spec(1).md`, сохранено
2026-11-04 без изменения байтов. SHA-256:
`0776c4c3d8f5a02c596b1a5b1d83554e8aa6f9a443b191ba7b27f86250f373d6`.
Аудит закреплён за `b9173e0a7c060e7c60fceb2e2a4ce39996f4b5db` (`main`).
Прежний предварительный обзор был на `ee43b98a`; источник задания ссылается на ещё
более ранний `2c8de087`. Эти hashes не заменяют фактический baseline аудита.

## Исходный P0/P1: выполнено и статус решений

- Сохранён исходник и подготовлены шесть документов по фактическому коду.
- Согласовано пользователем: сначала документы + план; затем продолжение P1.
  Страницы — вкладки общего Dockview, без собственных вложенных tabs.
- Учтены existing Browser/PreviewSurface, exact saved views, HTML lineage, plugin
  publication/grants и annotation intake. Повторная миграция удалённых viewers не нужна.
- Зафиксированы отличия от задания: runtime-only IDs, core reads, SessionEvent вместо
  отдельного AgentEventLog, разные semantics HTML restore и plugin active-release restore.
- P1 реализован от `c6659ed2`: PreviewSurface владеет read-only address и modes,
  BrowserObjectView — lifecycle связи, HtmlArtifactView — содержимым/mode/file/page.
  Локальный mode toolbar и его CSS удалены; повторной модели/iframe нет.
- Other providers, exact links/versions/export, limits и data authority сохранены.
  Остальные архитектурные решения Proposed; native renderer не выбран без Windows evidence.

P0 documentation подготовлен; P1 browser implementation проверен локально.
На момент исходного P1 срезы P2–P7 ещё не начаты; текущий статус — в начале документа.
Native/Office qualification не закрыта.
Глобальные приоритеты стабилизации и очередь других UI-срезов не меняются.

## Исходный P0/P1: проверки и ограничения

P0: source integrity, diff и 88 внутренних ссылок/anchors проверены без ошибок,
builds/tests не запускались. P1: JS syntax/diff и три browser fixtures на baseline
и после изменения прошли: `browser-saved-views`, `browser-views`, `browser-lifetime`.
Среда: Chrome 157.0.8103.38 на macOS, synthetic bridge, без моделей/Office.

Сохранены два чтения двух HTML и два открытия двух plugin releases при пяти выборах,
32 000 source chars/page, retained input и одна загрузка Browser iframe после пяти
layout restores. Expanded saved fixture проверяет readonly/Enter, единственные controls,
ready narrow/zoom и очистку старого descriptor при failed reload. Live HTML/plugin
annotations проходят. [Замеры до/после](../ui/review/browser-p1.json).

Локально просмотрены ready source/preview screenshots. [Wide source](../ui/review/browser-p1-source-wide.png),
[420 px / 125%](../ui/review/browser-p1-source-narrow.png): overflow 0, address 30 CSS px,
mode button 28 CSS px, font 12 px. Frozen Demo report/source измерен при тех же
viewport/zoom: button 24 px, font 12 px. Сохранён существующий production view-switch,
новых size overrides нет; визуальное принятие этой компоновки остаётся открытым.
Builds, harness и VSTO/Office validation не выполнялись; Windows evidence отдельно.

Открыты UB-1–UB-5 из [аудита](01_AUDIT.md): native embedding; semantic bindings;
session/annotation lifecycle и screenshot privacy; code/data revision distinction;
raw HTML/plugin authority. Windows evidence только по фотографиям, без запроса файлов.
Новые adapters/dual paths не введены; MIGRATION_MAP не изменяется преждевременно.
Параллельные изменения agent runtime/UI не входят в P0/P1 и не изменялись в их рамках;
исторический аудит сохраняет свой baseline. Commit/push не выполнялись.

## Ровно следующий шаг

На визуальном ревью [P6c](05_TEST_MATRIX.md#p6c), [P6d](05_TEST_MATRIX.md#p6d) и
[P4a baseline](05_TEST_MATRIX.md#p4a); P6e navigation реализован, [Windows evidence открыт](05_TEST_MATRIX.md#p6e).
P6f/P6g реализованы: A → B → сравнение retained фактов с источниками, отдельно от live
action authority. Следующий срез P6h — screenshots/privacy/image delivery.
Select/scroll/key/wait и model open/focus/close
остаются отдельными сценариями, не завершённым универсальным API.
Полнота iframe/shadow DOM, pagination и другие внешние действия остаются отдельным scope.
Отдельный P3c с общим descriptor без consumer не нужен; доработка diff и persistent
annotations отложены пользователем. [Принятая очередь](04_MIGRATION_PLAN.md#порядок-оставшейся-работы--2026-11-11).
Native Windows evidence P5a, визуальное принятие внутренних views и известный
focus/pointer gap остаются отдельными открытыми задачами, а не поводом повторять
все проверки при каждом срезе. Instance/grant/publication owners сохраняются.
