# R32 — Vendor/UI evaluation

Дата оценки: 2026-08-29. Baseline: `dde18cf`; security hotfix DOMPurify —
`a5cd6ff`. Это docs-only решение для будущей Phase 9B. Новые UI vendors не
подключены, текущая Phase 6 и её следующий mutation/verifier slice не меняются.

## Решение

1. Диагностику исправляет порядок **9A truth/query → 9B bounded viewers → 9C run
   journal**, а не замена вкладок на красивое дерево. Сначала строится одна
   correlated projection по source event sequence/ids, затем общий viewer, затем
   хронологический экран.
2. `ViewerRegistry` допустим только как UI dispatch. Он получает уже разрешённый
   bounded payload и metadata от владельца экрана. Он не читает bridge/CAS сам,
   не хранит состояние и не знает transport/protocol.
3. Модель и tools не возвращают новый произвольный `{ kind, title, content }`
   envelope. Model-facing контракт остаётся Tool Result v1 + revision-pinned
   `ResourceRef`; UI projection выводит из него allowlisted viewer kind/MIME.
4. Для простых Project/VBA/Artifacts деревьев первый кандидат — Web Awesome Tree
   через `TreeAdapter`. Для JSON и хронологии он не используется: там нужны
   lossless tokens, bounds и линейная причинность.
5. Ни один из двух предложенных JSON viewers не проходит R32 fidelity/bounds gate.
   `JsonAdapter` должен владеть raw text и bounded token model; renderer остаётся
   компактным собственным компонентом, пока vendor не докажет полное соответствие.
6. Терминал в Diagnostics не добавляется. Текущие данные — structured events/logs,
   а не PTY/ANSI stream. `xterm.js` оправдан только при отдельном настоящем terminal
   artifact с процессом, input/output и lifetime contract.

Worker сам по себе не нарушает offline: это локальный background execution context,
а не сетевой сервис. Запрещены remote/unpinned worker scripts и runtime download.
Допустим exact vendored worker, загружаемый с mapped local HTTPS origin через
host-owned allowlist/factory, с CSP `worker-src 'self'`, bounded lifetime и обязательным
`terminate`. Текущий RNAssistant открывает UI через `file://`; Monaco и PDF.js прямо
не поддерживают worker в таком origin. Исправление возможно через
[WebView2 virtual host mapping](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/working-with-local-content),
но это отдельное изменение hosting/security boundary с Windows gate.

## Уже есть

| Vendor | Текущая версия | Использование | Решение |
|---|---:|---|---|
| DOMPurify | 3.4.14 | sanitization результата `marked` | оставить; 3.1.6 срочно заменён отдельным R35 hotfix |
| marked | 12.0.2 | Markdown | оставить; замена на markdown-it не даёт пользы R32 |
| highlight.js | 11.9.0 | code blocks | оставить до отдельного upgrade review |
| CodeMirror | 5.65.16 | JSON/VBA/Markdown/HTML editors | оставить; read-only viewer им не подменять |
| KaTeX | 0.16.11 | формулы | оставить; добавить в общий provenance manifest |
| ECharts | 5.6.0 | chart artifacts | оставить за существующим chart adapter |

Vendored runtime сейчас занимает около 2.0 MB. `web/vendor-notices.md` фиксировал
версии только части пакетов, без общего file manifest/hashes и полного набора license
texts. DOMPurify уже приведён к новому правилу; остальной inventory — R36 до 9B.

## P0/P1 кандидаты

| Кандидат | Проверено | Итог |
|---|---|---|
| [Web Awesome Tree 3.12.0](https://webawesome.com/docs/components/tree/) | MIT; stable; latest Edge; selection/lazy/icons/ARIA. Cherry-picked transitive `dist-cdn` graph: 48 JS files, 204,087 bytes + 16,773-byte theme. Tree imports checkbox/icon/spinner/tree-item; default icon path способен fetch, system icons встроены | **Условно принять для tree-only spike.** Только статические локальные imports, system/inline curated SVG, без autoloader/default remote icon library. Zero-request test и WebView2 keyboard/theme check обязательны |
| [Wunderbaum 0.14.1](https://github.com/mar10/wunderbaum) | MIT; zero dependencies; UMD 102,824 bytes + CSS 21,756; performant tree/treegrid, keyboard. Upstream quick start помечает API/CSS как beta | **Резерв для measured large tree/treegrid.** Не default: лишние edit/DnD/grid capabilities и нестабильный API при текущей простой навигации |
| [Monaco Editor 0.56.0](https://github.com/microsoft/monaco-editor) | MIT; npm unpacked около 98 MB; language services используют workers; upstream указывает, что worker не создаётся с `file://`; AMD deprecated | **Не подключать сейчас.** Worker допустим локально после virtual-host switch, но Monaco дублирует работающий CodeMirror и слишком велик для R32. Вернуться только при отдельном editor milestone с измеренной пользой |
| [Diff2Html 3.4.56](https://github.com/rtfpessoa/diff2html) | MIT; parser/browser bundle 77,747 bytes + CSS 17,331; unified/git diff, line/side-by-side | **Условный кандидат для compact read-only diff.** Feed only bounded diff text; output проходит adapter/sanitization; UI bundle с highlight не брать, поскольку highlight.js уже есть |
| [andypf/json-viewer 2.8.0](https://github.com/andypf/json-viewer) | MIT; IIFE 40,093 bytes; красивое Shadow DOM tree/copy/search. Source использует `JSON.parse`/`JSON.stringify`, принимает URL и вызывает `fetch`, keyboard handlers отсутствуют | **Отклонить для authoritative diagnostics.** Теряет duplicate keys/large numbers/raw fidelity и имеет запрещённый URL path |
| [summerstyle/jsonTreeViewer](https://github.com/summerstyle/jsonTreeViewer) | MIT; около 18 KB JS + 2 KB CSS; object tree. README прямо предлагает `JSON.parse`; нет packaged releases, bounds, copy contract или полноценной accessibility | **Отклонить.** Малый размер не компенсирует несовместимость с R32 |

Web Awesome Tree подходит для Project Explorer, VBA modules, tools и artifacts при
bounded node count. Diagnostics run journal остаётся линейным expandable list, а
JSON tree создаёт DOM порциями из нашего token model. Один tree vendor не должен
скрыто стать владельцем трёх разных моделей данных.

## Остальной shortlist

| Назначение | Решение |
|---|---|
| Markdown | **marked + DOMPurify оставить.** markdown-it 15.0.1 добавляет runtime dependencies и migration без нужной функции |
| [PDF.js](https://mozilla.github.io/pdf.js/getting_started/) | **Условный кандидат вне R32.** Worker не является запретом, но требует virtual-host switch и exact local `workerSrc`; optional WASM/CMaps/fonts и URL factories должны быть отключены либо отдельно vendored/allowlisted. Нужен отдельный PDF contract и bounds |
| PhotoSwipe | **Кандидат позже** для обычных изображений. Подключать core напрямую, без lazy dynamic import; размеры изображения известны до открытия |
| OpenSeadragon | **P3**, только подтверждённые tiled scans/карты; для обычных screenshots лишний |
| Tabulator | **Кандидат позже** для измеренно больших typed tables. Только local data; ajax/edit/download/persistence отключены adapter-ом. Не использовать как journal |
| ECharts | **Уже есть.** Не обновлять и не дублировать; network/map extensions запрещены |
| Mermaid | **Отложить.** Большой dependency/security surface; если появится реальный use case — `securityLevel: strict`, no click/HTML, bounded source, sanitized SVG |
| Cytoscape.js | **P3**, только при отдельном dependency graph use case |
| Dockview / GridStack | **Не использовать для Diagnostics.** Draggable layout ухудшает воспроизводимость bug report. Текущего split/layout достаточно; Dockview возможен лишь для отдельного IDE milestone |
| Split.js | **Не добавлять:** существующий split уже закрывает потребность |
| MiniSearch | **Не строить второй индекс diagnostics.** Поиск по полному stream остаётся у `ITrajectoryQuery`; допустим только ephemeral filter уже загруженных строк после измерения |
| xterm.js | **Не использовать для логов.** Это terminal emulator, которому нужен реальный PTY/ANSI contract; structured JSON/events отображаются timeline/viewers |
| SortableJS / Viselect | **Отложить** до подтверждённых reorder/group-selection interactions; diagnostics read-only |
| Lucide | **Принять как curated SVG source.** Хранить только используемые SVG/paths локально, без runtime dynamic import/font/sprite fetch; единый `IconAdapter`, version/license/hash |

## Viewer boundary

```text
Tool Result v1 / ResourceRef / typed UI DTO
                  │
          owner resolves bounded data
                  │
            ViewerRegistry
        ┌─────────┼──────────┐
     JsonAdapter CodeAdapter DiffAdapter ...
        │            │          │
   own token DOM  existing CM  optional D2H
```

- Registry принимает exact allowlisted `viewerKind`, MIME, completeness/redaction,
  byte/character counts и already-loaded content. Unknown kind даёт safe text/file
  fallback.
- Adapter не вызывает `fetch`, XHR, WebSocket, EventSource, dynamic import, bridge
  или clipboard без callback владельца. Worker создаётся только через host factory,
  который принимает exact manifest id, разрешает pinned same-origin file и владеет
  cancellation/termination.
- Vendor никогда не видит secrets, скрытые поля или полный CAS payload, если owner
  выдал только preview.
- Viewer state (expanded nodes, focus, scroll) — ephemeral projection, не durable
  source of truth.

## Offline и provenance gate

Перед каждым vendor switch обязательны:

1. Exact package version/commit, upstream URL, npm/tarball integrity, SHA-256 каждого
   vendored runtime файла, полный LICENSE/NOTICE и список transitive runtime assets.
2. Только локальные pinned JS/CSS/SVG/worker assets. CDN/autoloader/remote icon
   library, telemetry, update check и URL input запрещены. WASM/fonts выключены
   либо проходят отдельный exact-asset review; произвольный worker URL запрещён.
3. Main UI сохраняет `connect-src 'none'`; adapter tests подменяют `fetch`, XHR,
   WebSocket и EventSource на fail-fast и подтверждают zero calls. Worker factory
   отклоняет URL вне manifest и проверяет ожидаемые create/terminate boundaries.
4. Bounds до parse/render: raw bytes/chars, depth, nodes, children page, long strings,
   DOM rows и cancellation. «Expand all» не снимает limits.
5. Keyboard/focus/ARIA, обе темы, clipboard failure и stale async response проверяются
   targeted tests; реальный WebView2/Windows остаётся обязательным gate.

## Порядок реализации

1. **До первого vendor switch:** закрыть R36 manifest/license/hash для уже vendored assets. Собственный 9B1 adapter не добавляет vendor и не снимает этот gate.
2. **9A (done host-neutral):** correlated run projection и direct navigation contract.
3. **9B1 (done host-neutral):** lossless bounded `JsonAdapter` + raw/pretty/tree/copy tests.
4. **9B2:** switch всех read-only JSON surfaces и удалить старые pretty/copy paths.
5. **9B3:** отдельный Web Awesome Tree spike для bounded navigation; при провале
   offline/WebView gates оставить существующее дерево или измерить Wunderbaum.
6. **9B4:** Diff2Html только для существующего compact-diff consumer, если exact
   unified diff доступен без второго diff algorithm.
7. **9C:** один chronological run journal; raw/specialized views остаются деталями.

Общие PDF/image/table/diagram/layout viewers не входят автоматически в R32 и
подключаются отдельными measured slices после stable diagnostics core.
