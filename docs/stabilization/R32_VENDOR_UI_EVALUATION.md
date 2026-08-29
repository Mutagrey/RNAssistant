# R32 — Vendor/UI evaluation

Дата оценки: 2026-08-29. Baseline оценки: `dde18cf`; security hotfix DOMPurify —
`a5cd6ff`. R36 provenance/offline baseline закрыт host-neutral поверх `e278db8`;
9B3 tree switch выполнен отдельно поверх `6ecd558`.

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
4. Для bounded Project/VBA/Artifacts navigation принят Wunderbaum через собственный
   `TreeAdapter`. Web Awesome Tree отложен: официальный ESM graph не загрузился из
   текущего `file://` origin, а host switch/custom bundle не входит в 9B3. Для JSON
   и хронологии tree vendor не используется: там нужны lossless tokens, bounds и
   линейная причинность.
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
`terminate`. Пока worker manifest пуст, main UI явно держит `worker-src 'none'`.
Текущий RNAssistant открывает UI через `file://`; Monaco и PDF.js прямо
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
| KaTeX | 0.16.11 | формулы | оставить; R36 manifest фиксирует WOFF2-only локальную производную CSS |
| ECharts | 5.6.0 | chart artifacts | оставить за существующим chart adapter |
| Wunderbaum | 0.14.1 | bounded HTML workspace/artifact navigation через `TreeAdapter` | 9B3: оставить у одного consumer; optional ajax/lazy/edit/DnD/grid/persistence API adapter не публикует |

Vendored runtime занимает 2,126,868 bytes в 38 файлах. R36 добавил
[`vendor-manifest.json`](../../web/vendor-manifest.json) для всех 36 runtime files,
полные local licenses/transitive decisions, KaTeX WOFF2-only cleanup и Feather
source-only attribution. [Evidence](R36_WEB_VENDOR_GATE.md). 9B3 расширил manifest
двумя Wunderbaum assets и локальной MIT license. Lucide не bundled.

## P0/P1 кандидаты

| Кандидат | Проверено | Итог |
|---|---|---|
| [Web Awesome Tree 3.12.0](https://webawesome.com/docs/components/tree/) | MIT; stable; latest Edge; selection/lazy/icons/ARIA. Официальный `dist-cdn` — ESM graph из 48 относительных JS imports, 204,087 bytes + 16,773-byte theme; default icon path способен fetch. Реальный local Chrome probe из текущего `file://` host не зарегистрировал `wa-tree` | **Отложить до отдельного virtual-host milestone.** Не собирать custom classic bundle и не менять C#/WebView security boundary внутри tree consumer. Повторно оценить после mapped local HTTPS + Windows gate |
| [Wunderbaum 0.14.1](https://github.com/mar10/wunderbaum) | MIT; zero dependencies; classic UMD 102,824 bytes + CSS 21,756; file-origin, local-array, keyboard и virtualization probe прошёл. Upstream API/CSS помечены beta; bundle содержит optional ajax/edit/DnD/grid code | **Принят в 9B3 для одного bounded consumer.** Adapter принимает только local arrays, ограничивает nodes/depth/text, не публикует URL/lazy/edit/DnD/grid/persistence и добавляет ARIA/локальные иконки. CSP `connect-src 'none'`; Windows WebView2 gate открыт |
| [Monaco Editor 0.56.0](https://github.com/microsoft/monaco-editor) | MIT; npm unpacked около 98 MB; language services используют workers; upstream указывает, что worker не создаётся с `file://`; AMD deprecated | **Не подключать сейчас.** Worker допустим локально после virtual-host switch, но Monaco дублирует работающий CodeMirror и слишком велик для R32. Вернуться только при отдельном editor milestone с измеренной пользой |
| [Diff2Html 3.4.56](https://github.com/rtfpessoa/diff2html) | MIT; parser/browser bundle 77,747 bytes + CSS 17,331; unified/git diff, line/side-by-side | **Условный кандидат для compact read-only diff.** Feed only bounded diff text; output проходит adapter/sanitization; UI bundle с highlight не брать, поскольку highlight.js уже есть |
| [andypf/json-viewer 2.8.0](https://github.com/andypf/json-viewer) | MIT; IIFE 40,093 bytes; красивое Shadow DOM tree/copy/search. Source использует `JSON.parse`/`JSON.stringify`, принимает URL и вызывает `fetch`, keyboard handlers отсутствуют | **Отклонить для authoritative diagnostics.** Теряет duplicate keys/large numbers/raw fidelity и имеет запрещённый URL path |
| [summerstyle/jsonTreeViewer](https://github.com/summerstyle/jsonTreeViewer) | MIT; около 18 KB JS + 2 KB CSS; object tree. README прямо предлагает `JSON.parse`; нет packaged releases, bounds, copy contract или полноценной accessibility | **Отклонить.** Малый размер не компенсирует несовместимость с R32 |

Wunderbaum подходит для дальнейшего measured switch Project/VBA/tools trees, но 9B3
переключил только HTML workspace/artifact navigation. Каждый следующий consumer —
отдельная проверка модели и cleanup. Diagnostics run journal остаётся линейным
expandable list, а JSON tree создаёт DOM порциями из нашего token model. Один tree
vendor не должен скрыто стать владельцем трёх разных моделей данных.

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

1. **R36 (done host-neutral):** manifest/license/hash/transitive/offline gate для уже vendored assets закрыт; каждый новый vendor расширяет тот же allowlist отдельно.
2. **9A (done host-neutral):** correlated run projection и direct navigation contract.
3. **9B1 (done host-neutral):** lossless bounded `JsonAdapter` + raw/pretty/tree/copy tests.
4. **9B2A (done host-neutral):** diagnostics event/evidence/JSON payload switch; удалён его старый pretty/plain-pre path.
5. **9B2B1 (done host-neutral):** Agent arguments/results switched; generic object/table/pretty renderer удалён, chart parser локализован у domain owner.
6. **9B2B2 (done host-neutral):** Context/materialized request, manual Tools results и VBA metadata switched; editable/transport paths исключены.
7. **9B2B3 (done host-neutral):** artifact inline/metadata JSON switched; bridge truncation становится explicit preview, non-JSON/HTML paths сохранены отдельно.
8. **9B2B4 (done host-neutral):** завершённые top-level fenced JSON blocks Markdown switched post-sanitize; live/unclosed/mismatched blocks остаются code.
9. **9B3 (done host-neutral):** Web Awesome ESM отклонён для текущего `file://` host;
   pinned Wunderbaum UMD + CSS подключены через bounded local-array `TreeAdapter` к
   одному HTML workspace/artifact tree. Старый renderer этого consumer удалён;
   zero-network, keyboard/ARIA/themes и manifest gates прошли локально. Windows
   WebView2 qualification и возможный virtual-host milestone открыты.
10. **9B4:** Diff2Html только для существующего compact-diff consumer, если exact
   unified diff доступен без второго diff algorithm.
11. **9C:** один chronological run journal; raw/specialized views остаются деталями.

Общие PDF/image/table/diagram/layout viewers не входят автоматически в R32 и
подключаются отдельными measured slices после stable diagnostics core.
