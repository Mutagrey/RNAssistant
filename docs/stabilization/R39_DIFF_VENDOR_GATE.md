# R39 — Compact diff vendor gate

Дата: 2026-08-29. Baseline: `2c3b1ee`. Scope: Phase 9B4 evaluation, без runtime
или vendor change.

## Фактический контракт

- `web/js/app-vba.js` строит editor preview вызовом
  `RNAssistantVbaDiff.format(before, after)`.
- `web/js/app-trajectory.js` получает hydrated exact `BeforeCode` и
  `IntendedAfterCode`, затем вызывает тот же formatter.
- Typed bridge DTO передаёт source texts и SHA-256, но не unified diff.
- `app-vba-diff.js` сам строит bounded single-change line projection. Это
  существующий UI formatter, а не durable mutation evidence.

Других runtime consumers `RNAssistantVbaDiff` или полей unified/diff text нет.

## Решение

Diff2Html 3.4.56 не подключён. Он разбирает и отображает готовый unified/git diff,
но не вычисляет authoritative diff из before/after. Добавление собственного
unified-diff generator создало бы второй algorithm; преобразование текущей
упрощённой projection выдало бы синтетический payload за exact evidence.

Existing formatter и CSS сохранены без изменений. `web/vendor-manifest.json`
остаётся на 38 runtime files. 9C chronological journal не зависит от Diff2Html и
использует typed `run-causal` evidence.

## Повторная оценка

Допустима только после отдельного source-owned contract, который поставляет полный
или явно bounded unified diff с completeness/source metadata. Тогда adapter должен
принимать только этот текст, не обращаться к bridge/network и не менять evidence.
Windows x64 + Office + VS 2022 VBA/WebView qualification остаётся открытой.
