# R38 — Bounded tree vendor switch

Дата: 2026-08-29. Baseline: `6ecd558`. Scope: Phase 9B3, один read-only
HTML workspace/artifact navigation consumer.

## Выбор

Web Awesome Tree 3.12.0 не принят при текущем hosting: официальный `dist-cdn`
является ESM graph из 48 относительных imports. В local Chrome probe из того же
`file://` origin `wa-tree` не зарегистрировался. Custom classic bundle добавил бы
собственный build/fork, а WebView2 virtual-host mapping меняет C#/security boundary
и требует отдельного Windows milestone. Ни один из этих paths не скрыт внутри 9B3.

Принят Wunderbaum 0.14.1: zero npm dependencies, classic UMD 102,824 bytes и CSS
21,756 bytes работают из `file://`. Exact git head, npm integrity, byte size,
SHA-256 и MIT license внесены в `web/vendor-manifest.json`; manifest теперь содержит
38 runtime files общим размером 2,126,868 bytes.

## Границы adapter

- `TreeAdapter` принимает только локальный массив plain nodes, exact stable keys и
  allowlisted item/icon kinds. Limits: 2,500 nodes/16 levels hard, consumer —
  1,800/12; text/key fields также bounded.
- Vendor не получает URL, lazy source, fetch, persistence или bridge. Ajax/lazy,
  edit, DnD, filter и grid API не публикуются; main CSP сохраняет
  `connect-src 'none'` и пустой worker allowlist `worker-src 'none'`.
- Adapter владеет mount/unmount, уничтожением vendor instance, selection rejection,
  ARIA, keyboard focus projection, local CSS-mask icons и delete callback. Названия
  выводятся как text; malicious HTML не создаёт DOM nodes.
- Domain owner сохраняет grouping/search/collapse state и callbacks select/delete.
  Старый `details`/`createResourceListItem` renderer удалён только у переключённого
  consumer; VBA и другие trees не менялись.

## Проверка

- `tests/web/tree-adapter.test.js`: 4/4 — input/bounds, lifecycle/ARIA/selection,
  domain grouping/state и load-order/no-network boundary.
- `tests/web/vendor-gate.test.js`: 5/5 — exact 38-file manifest, licenses/hashes,
  local CSS dependencies, CSP и fail-closed worker/vendor admission.
- Все `tests/web/*.test.js`: 58/58 pass (включая tree 4/4 и vendor gate 5/5).
- Local Chrome `file://`: vendor ready, 19 rendered rows, ARIA/selection/delete
  controls present, keyboard active descendant changed, malicious title remained
  text, horizontal overflow 0, fetch/XHR/WebSocket/EventSource calls 0. Light и
  dark responsive screenshots просмотрены; временные fixture/screenshots удаляются.

## Открыто

Windows x64 + Office + VS 2022 WebView2 qualification обязательна для real keyboard,
focus, DPI/theme и lifecycle. Web Awesome/virtual-host migration не требуется для
R32 и остаётся отдельным будущим решением. 9B4 compact diff и 9C chronological run
journal не входят в этот commit.
