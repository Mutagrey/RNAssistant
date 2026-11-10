# Universal Browser: план реализации

Статус: P0 documentation подготовлен; P1 и P2a **реализованы / In Review**;
P2b/P2c–P7 **Planned / Proposed**. Дата: 2026-11-10. Пользователь продолжил работу после
docs-only P0; реализованы описанные ниже срезы. Основание:
[исходник](00_SOURCE_SPEC.md), [аудит](01_AUDIT.md), [ADR](02_ARCHITECTURE_ADR.md),
[контракты](03_CONTRACTS.md). Общая очередь UI остаётся в
[UI Migration Plan](../ui/UI_MIGRATION_PLAN.md); названия P0–P7 здесь относятся только
к Universal Browser, не к замороженному Dockview Demo.

## Этапы и приёмка

| Этап | Owner и результат | Gate и удаление заменённого пути |
|---|---|---|
| P0 Audit/ADR | Web/Application: реальная source/authority map, reuse/remove, contracts, Windows spike protocol | Docs-only diff/links/source integrity; native и runtime evidence остаются открыты |
| P1 HTML Artifact | Web: address/modes в общем PreviewSurface, один saved HTML consumer | Сохранены current features/limits; browser baseline + визуальное ревью; локальный mode toolbar удалён |
| P2 Targets/Sessions | P2a: import в самостоятельный workspace реализован; далее Application/Core + Web resolver/session owner и detach/close | Import bounds/read-back/no-replay; затем dirty/stale/exact scope/close checks, без замены editor data model |
| P3 Plugin | Plugin Application + Web: source/candidate/release adapters, общий navigation/modes UI | Grants/revoke/check/publish/restore неизменны; удалены переведённые provider UI controls, instance ownership остаётся |
| P4 Versions/Annotations | Domain owners + Web: bounded diff, version semantics, events-backed annotations и anchors | Branch/active pointer, unresolved/verified resolution, view-close retention; нет BrowserRevisionStore/Annotation DB |
| P5 External WebView2 | Native adapter + Application: renderer по результату spike, profiles/navigation/popup/download policy | Windows embedding matrix либо явный управляемый separate-window fallback; привилегированный Shell control не используется |
| P6 Agent automation | Application/ToolRuntime/Resource Gateway: semantic reads/actions/verify и evidence | Local fixture workflow, stale/unknown/user conflict/shared sessions, bounded output/redaction; без второго kernel/agent roles |
| P7 Qualification | Владельцы затронутых контуров: stress/security/performance/recovery | Host-neutral + отдельные Windows/Office results, визуальное принятие, закрытые removal gates; release решается отдельно |

Native spike подготовлен P0 и может выполняться независимо от P1–P4. P5 зависит от
его evidence; внутренние UI-срезы не ждут Windows. Не начинать одновременно миграцию
всех viewers. Каждый этап заканчивается обновлением canonical docs, фактическим
evidence и ровно одним следующим шагом в [Progress](06_PROGRESS.md).

## P1: конкретный production slice

Реализован от baseline `c6659ed2`; ниже сохранено задание среза.
Три browser fixtures до/после проходят, заменённый toolbar удалён. Результат и
открытое визуальное принятие — в [Progress](06_PROGRESS.md).

Owner: Web Browser/PreviewSurface; data owner — existing HtmlArtifactView/export.
Цель: сохранённый HTML artifact получает общие address/modes без потери своего
точного target, versions/download и ограниченного исходника.

1. На актуальном чистом baseline запустить три browser fixtures из [матрицы](05_TEST_MATRIX.md#p1-checks).
   Снять narrow/zoom/focus screenshots и текущие load/read/source-size показатели.
   Существующий mock product служит fixture; новый Demo не создавать.
2. Расширить [app-preview-surface.js](../../web/js/app-preview-surface.js), `attach`,
   интерфейсом [P1 presentation](03_CONTRACTS.md#p1-presentation). Общие controls:
   read-only address и «Страница / Исходник»; existing title/path/status/actions остаются.
   Keyboard/aria используют общие form-control/view-switch.
3. В [app-browser-object-view.js](../../web/js/app-browser-object-view.js), `load/reload/stop/dispose`,
   связать loaded presentation с surface. Ready hidden DOM и текущий mode сохраняются;
   callbacks/leases поздней загрузки не применяются к закрытому view.
4. В [app-html-artifact-view.js](../../web/js/app-html-artifact-view.js), `load/mode/create`,
   вернуть presentation/selectMode вместо локальных mode buttons. File picker остаётся
   над исходником в provider content и скрыт в preview. Остаются один iframe,
   `renderSource` с pagination и position per file, artifact version picker/actions.
   Адрес — текущий target.resourceUri; выбор другой версии использует прежний Dock route.
5. В [app-preview-surface.css](../../web/css/app-preview-surface.css) использовать common
   tokens для document controls. Удалить заменённые `.browser-document-toolbar` rules;
   сохранить только необходимый layout file picker/source. Проверить consumers через rg.
6. В existing `browser-saved-views.test.js` добавить только отсутствующие assertions
   read-only address, единственного mode owner и отсутствия unsupported actions.
   Повторить три fixture, сравнить baseline, провести визуальное ревью.

P1 не меняет app.js boot, bridge, model tools, storage, annotation lifecycle, plugin
grants или HTML editor. Новых script files/loader/registry не требуется.

### Приёмка P1

- Artifact из каталога/истории открывает тот же exact chat/document/URI; version picker,
  копирование ссылки, export и сообщения ошибок работают через прежних owners.
- Input в preview, выбранный файл/source page и mode переживают switch/hide/move/split/
  layout restore; close освобождает provider и поздние leases. Это существующий P1
  view lifetime, ещё не обещание detached BrowserSession P2.
- Нет второго iframe/второго mode switch и полной копии source в DOM. Сохраняются
  лимиты 16 MiB preview transfer, 100 project files, 32 000 source chars/page,
  existing snapshot/download bounds и максимум шесть material views.
- 1280×900, 420 CSS px и 125% zoom: адрес/название не раздвигают Shell, controls
  доступны с клавиатуры, hidden surface не перехватывает focus. Размеры shared controls
  сверяются с принятым Dockview reference при одинаковом viewport/state.
- Diff и web navigation отсутствуют, пока capability не реализована. Read-only address
  не создаёт ложного ожидания открытия сайта. Plugin и live HTML продолжают тот же путь.
- Изменения готовы к review: запуск, 2–5 действий, изображения/наблюдения, ограничения.
  Визуальная приёмка и Windows qualification не считаются завершёнными по автотесту.

## P1: компактный toolbar и связь с Chat View — In Review, 2026-11-05

Согласованное продолжение: существующие icons/components, play/code вместо текста,
annotation/history/discuss icons; rare plugin actions в ActionMenu. ConversationPanel
открывается рядом средствами DockWorkspace, один exact экземпляр. Plugin annotation
target закрепляется явно; навигация не меняет адресата. Исправлен общий preserveChatOwner.
Удалены заменённые plugin action DOM/CSS и локальные размеры иконок. Нового adapter,
registry, store или bridge нет. [Результаты и оставшийся scope](06_PROGRESS.md).

P2 требует отдельного domain решения: независимый project owner, managed CAS или
рабочая папка как backing, сохранённые версии отдельно от текущих правок, ссылки
документов/чатов и GC roots. Публикация plugin остаётся отдельным действием, данные
плагина — у прежнего owner. UI не создаёт второй durable version/annotation store.
При появлении реального сравнения Diff использует иконку двух колонок с +/−.

## P2a: импорт самостоятельного проекта — In Review, 2026-11-10

Реализованы [project ownership/backing](02_ARCHITECTURE_ADR.md#p2a-самостоятельный-проект-и-backing--in-review-2026-11-10),
далее resolver и sessions. Scope P2a: явный import exact сохранённого HTML
в новый managed workspace; выбранная папка подключается существующим directory path.
Managed здесь — файлы в каталоге приложения плюс existing CAS history, не второй
CAS-only source store. Owner — Application Workspace + существующие HTML export/file owners.

1. Typed command `createHtmlProjectFromArtifact` передаёт exact source и managed backing.
2. Preflight проверяет entry point, безопасные пути/коллизии, 100 файлов / 16 МиБ;
   document-bound data, распознаваемые SDK/vendor/external refs отклоняются до записи.
3. Application import создаёт скрытый workspace, file owner пишет и сверяет SHA-256;
   только затем association публикуется. Сохранённый исходник и его история не меняются.
4. «Сохранение проекта» → «Создать проект из снимка» открывает existing project navigation.
   Повторный переход использует тот же workspace; поздний ответ не меняет навигацию.
   Новый чат/Run автоматически не создаётся; source editor и чат остаются у workspace owner.
5. Три focused import checks, typed bridge и existing file-path check проходят host-neutral;
   новый browser flow и saved Browser fixture проходят. Старый transfer roundtrip падает
   также на чистом baseline при следующем HTML-tool вызове; риск записан отдельно.

P2b — typed target resolver и capabilities существующих consumers. P2c — runtime
session owner, detach/reattach и отдельное закрытие session с UI для повторного открытия.
До P2c закрытие вкладки сохраняет прежний lifetime; новый ID не записывается в Dock layout.
Pointer-routing/Windows evidence остаются открытыми и не блокируют domain-срез P2a.
Import и managed source backing реализованы. Перенос зависимостей, browser resolver,
sessions, Windows/controller delivery и пользовательское визуальное принятие открыты.

## Развитие после P1

P2 добавляет новые typed browser records/commands в existing Core/Contracts и
Application BrowserSessionService, runtime resource binding — через Resource Gateway.
Web target adapters делегируют HtmlWorkspaceView/Source/Editor и MaterialView; raw HTML
остаётся inert до явного preview. Exact route keys/rna ссылки сохраняются. Runtime
session identity нельзя сериализовать в Dock layout как восстановленную живую страницу.

P3 использует PluginApplicationService и PluginStore без смены публикации/данных от
выбора вкладки. `plugin_source` и `plugin_release` — разные typed targets. Старые
provider-specific toolbar sections удаляются при переключении каждого consumer.

P4 расширяет существующие ArtifactVersions/HtmlWorkspaceNavigationService и TextDiff;
source pair разрешает owner. Annotation lifecycle становится typed events поверх
существующего attachment body. CAS retention учитывает новые refs. Новые persisted
форматы переключаются вместе с readers; прежние пользовательские данные не удаляются,
несовместимое состояние обозначается явно, без hidden fallback/dual-write.

P5 реализует .NET 4.8 native renderer port рядом с Office/WebView. BrowserHost verifier
остаётся optional CLI механизмом; общую CDP семантику переносить лишь при конкретном
consumer и локальной проверке, без механического копирования private verifier.

P6 регистрирует закрытые semantic tools/skill в existing catalog, не меняя v8 loop;
новые source snapshots идут через Resource Fabric/CAS и compiler, а не отдельную
историю. Diagnostics/Inspector расширяется существующими typed projections.

## Rollback и удаление временных путей

Работа последовательно на `main`, без автоматических commit/push. P1 — изолированный
UI diff без данных; rollback — его обычный revert. Пока изменение не принято,
не создавать дополнительный feature toggle для второго toolbar.

Постоянный target adapter — явная граница делегирования, не временный migration shim.
Если нужен временный dual path/fallback, до его подключения добавить в
[MIGRATION_MAP](../stabilization/MIGRATION_MAP.md) owner, consumers, flag при необходимости
и точный removal gate. При принятии среза переключить consumers и удалить старый код.
Docs-only P0 не вводит ни adapter, ни migration entry с ложным статусом «реализовано».

После durable P2/P4 rollback прекращает новую capability, но сохраняет читаемость
уже записанных facts/CAS refs; возможные внешние эффекты не откатываются replay.
Любая новая схема требует отдельного explicit version/reset решения перед кодированием
этого среза. Product bump выполняется один раз на завершённый продуктовый срез по
[Versioning](../operations/VERSIONING.md); docs-only P0 и промежуточные commits
обходятся без bump. Перед фактическим commit обязателен existing
ValidateVersionFormat. Commit/push в P0/P1 не выполнялись.
