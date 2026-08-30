# WQ-A1 — Qualification host-neutral core

Дата: 2026-08-31. Parent `738da26` включает WQ-A0 contract `eba582b`, docs-only
Artifact Library `9ee73cb` и deferred Host Fabric/Local Automation contracts.

## Scope

WQ-A1 реализует только host-neutral основу Qualification Center:

- strict schema v1 parser для data-only pack manifests;
- strict coverage registry, host/suite catalog и missing-requirement/coverage projection;
- конечный `QualificationRunner` через narrow allowlisted action/verifier ports;
- durable pause/resume и fail-closed replay поверх существующего `IEventStore`;
- typed bridge request/response DTO без controller route и UI;
- fake action/verifier/journal regressions и actual `ChatStore`/CAS integration test.

WQ-A2 UI, production adapter к `ConversationRunService`, built-in packs, host probes,
fault hooks и Excel WQ0 helper не входят в изменение.

## Закрытые инварианты

Manifest отклоняет duplicate/unknown fields, JavaScript JSON extensions, неизвестные
kinds, unsafe workspace policy, invalid conditional fields, duplicate steps,
forward/cyclic dependencies, non-final cleanup group и pack без required assertion.
Pack закрепляет exact content SHA-256. Неизвестный coverage ID не публикуется; pack с
недоступным requirement видим как blocked, а не как pass.

Runner не знает tool IDs и не исполняет model loop. Automatic step допускается только
через `IQualificationActionExecutor`; assertion — только через
`IQualificationVerifier`. Required automatic assertion с typed expected/actual JSON
обязателен для terminal pass. Narrative, action success, manual acknowledgment,
missing/unknown evidence не дают pass.

Каждый automatic/user checkpoint имеет отдельный attempt. Mandatory step-start
сохраняется до action; mandatory completion — после него. Ошибка start append ничего
не dispatch-ит. Ошибка completion append после возможного effect блокирует run и
запрещает in-place retry. Replay возобновляет только durable user checkpoint или
границу между закрытыми steps; open automatic step остаётся blocked. После failure/
cancellation обычные steps пропускаются, финальная cleanup group выполняется bounded,
но не скрывает исходный outcome.

Если timeout/cancellation сработал, но начатая операция ещё не завершилась, runner
не фабрикует `step.completed`, не запускает cleanup параллельно возможному effect и не
пишет terminal pass/fail. Run остаётся fail-closed `blocked` с durable open step и
может быть только диагностирован либо повторён новым run.

## Persistence и wire

Closed event catalog расширен четырьмя Agent/authority/mandatory operations:

- `qualification.run.started`;
- `qualification.step.started`;
- `qualification.step.completed`;
- `qualification.run.completed`.

Они идут через тот же `IEventStore` и chat `*.events.jsonl`. Exact pack/build/host
provenance проверяется при replay. Большие expected/actual сохраняются в существующий
event payload CAS; SHA-256 сверяется при чтении. Новый store, mutable result file,
dashboard index, executor или history rewrite не добавлены.

Typed bridge DTO ограничивает inline evidence 64 KiB и сохраняет causal event
IDs/sequences. Controller route отсутствует до WQ-A2, поэтому shipped UI ещё не может
запустить qualification.

## Файлы и локальная чистка

- `src/RNAssistant.Office/Qualification/*` — новый самостоятельный owner;
- `src/RNAssistant.Office/Contracts/BridgeDtos.Qualification.cs` — будущая typed UI boundary;
- `SessionEventTypes` / closed descriptor catalog — четыре операции;
- old-style Office project, source-linked Harness и MockDemo включают новые sources;
- `Program.QualificationTests.cs` — focused regressions.

Заменённого production path не было: legacy alias/dual-write не добавлен, запись в
`MIGRATION_MAP.md` не требуется. `IQualificationRunJournal` — permanent narrow port к
тому же event stream, а не compatibility adapter.

## Проверка

- `qualification:` — 8/8 pass;
- `storage: typed event port` — 1/1 pass;
- `harness: production projects include all source files` — 1/1 pass;
- Harness Release compile — 0 errors, 4 existing CA1416 warnings в identity probe;
- MockDemo Release compile — 0 errors, 3 existing CA1416 warnings в PDF rendering;
- `ValidateVersionFormat`, `git diff --check` и затронутые local links/anchors — pass.

Полный harness не запускался: новый behavior имеет отдельный focused filter, а
cross-subsystem production wiring не менялось.

## Открытые gates

WQ-A2 должен подключить application/controller composition и UI shell только к этим
typed contracts; WQ-A3 добавит первый real Excel WQ0 pack и host/helper adapters.
AgentTask ещё не вызывает production conversation path, host probes/fault hooks не
реализованы, built-in catalog отсутствует. Windows x64 + Office x64 + VS 2022,
WebView2, live provider и COM не проверялись. R50 contained только для host-neutral
core; WQ0, R04, production 5B2/7D и release qualification остаются открытыми.
