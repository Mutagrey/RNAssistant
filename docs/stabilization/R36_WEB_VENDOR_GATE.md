# R36 — Web vendor provenance/offline gate

Дата: 2026-08-29. Baseline: `e278db8`. Scope: существующие assets главного
WebView UI; новых vendors и новых UI consumers этот этап не добавляет.

## Что закрыто

- `web/vendor-manifest.json` является machine-readable allowlist: exact package
  version/git head/npm integrity, licenses, browser dependency decision, размер и
  SHA-256 каждого из 36 runtime files.
- Локальные license/notice texts добавлены для marked, highlight.js, CodeMirror,
  KaTeX, ECharts и Feather Icons; существующие DOMPurify licenses сохранены.
- Feather Icons зафиксирован как source-only provenance адаптированных inline SVG.
  Feather runtime/dependencies и Lucide не загружаются.
- KaTeX CSS очищен от 40 отсутствующих `.woff`/`.ttf` fallback URL. Остались ровно
  20 локальных manifested WOFF2; upstream CSS hash и hash локальной производной
  сохранены в manifest.
- Main UI явно держит `connect-src 'none'`, `font-src 'self'` и при пустом worker
  allowlist — `worker-src 'none'`. Runtime WASM/workers сейчас отсутствуют.

Локальный worker разрешён архитектурой и не является сетью. Его нельзя включить
неявно: тот же atomic slice должен добавить pinned file/hash/license, manifest id,
host-owned allowlist/factory, cancellation/`terminate`, CSP и zero-network test.
Web Awesome Tree worker не требует.

## Проверка

- `node tests/web/vendor-gate.test.js`: 5/5 pass — packages/licenses, exact files,
  CSS assets/fonts, index/CSP и fail-closed worker/vendor admission.
- Остальные существующие `tests/web/*.test.js`: 49/49 pass.
- Local Chromium загрузил текущий `web/index.html`: DOMPurify, marked, KaTeX,
  auto-render, highlight.js, ECharts и CodeMirror доступны; remote/failed vendor
  requests и page errors — 0.
- Проверены local links, `git diff --check` и `ValidateVersionFormat`.

## Границы

User-approved HTTP HTML workspace остаётся отдельным host bridge и не ослабляет
main UI CSP. R36 не выбирает vendor, не меняет model/tool contracts и не закрывает
Windows WebView2 qualification. Следующий отдельный этап — 9B3 bounded-navigation
spike Web Awesome Tree через `TreeAdapter`; первый новый vendor обязан расширить
этот manifest и пройти тот же gate.
