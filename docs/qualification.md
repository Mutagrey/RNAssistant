# Qualification Center и расширяемые test packs

Статус: WQ-A4 suite catalog реализован host-neutral. UI показывает versioned
common/provider/storage/UI/Excel/VBA/cross packs; каждый требует exact readiness
capability и остаётся недоступным, пока production owner и нужная среда не готовы.
Реальный WQ0 и остальные live suites остаются открытыми до Windows/Office/provider
прогона. Immutable build evidence остаётся WQ-A5.

## 1. Назначение

Qualification Center даёт пользователю один встроенный экран для проверки
RNAssistant на реальном Office host без ручного запуска PowerShell. Он объединяет:

- сведения о host-neutral harness, привязанные к exact build/commit;
- реальные agent tasks через обычный conversation runtime;
- host/COM probes и безопасные fault hooks;
- пошаговые действия пользователя в Office;
- детерминированные assertions по document state и typed runtime evidence;
- causal journal, JSON evidence и экспортируемый итоговый report.

Центр не заменяет быстрый `RNAssistant.Harness` и не исполняет его внутри VSTO.
Harness проверяет pure/host-neutral contracts до сборки кандидата. Встроенные packs
проверяют production wiring, Office, WebView, live provider и сквозные задачи.
Общий coverage registry показывает оба вида evidence и не объявляет полный pass,
если обязательный контур отсутствует, заблокирован или относится к другому build.

## 2. Пользовательский путь

В пустом новом чате появляется отдельная карточка **«Проверить RNAssistant»**. Она
открывает Qualification Center, не вставляет текст в composer. Второй вход находится
в Diagnostics.

Экран показывает:

1. текущий host, Office/WebView/build provenance и доступные capabilities;
2. наборы `Быстрая проверка`, `Полная проверка`, `Release candidate` и packs текущего host;
3. preconditions и требование использовать созданный runner-ом либо явно выбранный
   disposable document;
4. один текущий шаг с кнопками `Начать`, `Проверить`, `Далее`, `Повторить`, `Остановить`;
5. expected/actual, automatic/manual evidence strength и `PASS/FAIL/BLOCKED/NOT_RUN`;
6. связанный causal run journal и общий JSON viewer;
7. экспорт bounded redacted report.

WQ-A2 поставляет встроенный read-only quick pack
`common.ui-shell`. Он проверяет UI/bridge/runner/event round-trip с явным
manual checkpoint и typed verifier, но не квалифицирует Office, COM,
live provider, model loop и document tools. WQ-A3 добавляет release suite selector и
Windows-gated `excel.wq0.identity`: на другой платформе либо без same-build helper
пакет виден как недоступный и не может стартовать. WQ-A4 добавляет остальные
canonical quick/full/release manifests и coverage owners. Их exact readiness
capabilities выдаются только полностью реализованным production adapter-ом, поэтому
неподдержанный контур остаётся N/A. Exact immutable build commit остаётся `unavailable` до
BuildEvidenceManifest в WQ-A5; UI не фабрикует его из working tree.

Wizard может просить переключить книгу, выполнить Save As, закрыть/открыть документ,
подтвердить tool call, перезапустить add-in или визуально проверить layout. Команды,
helper processes и correlation выполняются приложением; пользователь работает только
с UI и Office.

## 3. Архитектурная граница

```text
Qualification UI -> typed bridge -> QualificationApplicationService -> QualificationRunner
                                                                      |-> PackCatalog + CoverageRegistry
                                                                      |-> normal ConversationRunService / AgentKernel
                                                                      |-> allowlisted HostProbe / FaultHook / Verifier
                                                                      |-> IEventStore + existing chat JSONL/CAS
                                                                      `-> ITrajectoryQuery / report export

Build pipeline -> immutable BuildEvidenceManifest -> Qualification UI
```

- `QualificationRunner` — application orchestration. Он не реализует model loop,
  tool dispatch, confirmation, document locking, storage или effect classification.
- WQ-A1 размещает strict manifest/catalog/coverage и конечный runner в
  `RNAssistant.Office/Qualification`. Runner принимает только narrow allowlisted
  action/verifier ports.
- WQ-A2 добавляет `QualificationApplicationService`, typed controller/bridge routes,
  встроенный exact allowlisted shell pack и один UI. Application service каждый
  раз восстанавливает run из validated chat events; production adapter к
  conversation/host runtime и host probes ещё не подключены.
- WQ-A3 добавляет `IQualificationHostPort`. UI-thread и dedicated-STA wrappers
  сохраняют owner apartment; exact action/assertion IDs реализует только host owner.
  Verifier получает завершённые action evidence из уже записанного event stream,
  а не из UI или model narrative.
- WQ-A4 регистрирует закрытый список suite families и по одному exact capability
  полной готовности pack-а. Наличие manifest/coverage owner само по себе не выдаёт
  capability; частичная реализация не допускает запуск и отображается как N/A.
- `agentTask` всегда проходит через обычные `ConversationRunService`, `AgentKernel`,
  `ToolRuntime`, `HostRuntime` и production domain handlers. Test mode не расширяет
  callable tools и не отключает confirmation/policy.
- Host probes и fault hooks имеют узкие typed IDs и реализации в host owner. Manifest
  не содержит CLR type, command line, JavaScript, PowerShell или произвольный tool ID.
- Assertions читают source-owned typed outcome, read-back snapshot, session events и
  host observation. Текст модели не является доказательством pass.
- UI только управляет сценарием и отображает уже рассчитанный result. Он не выводит
  effect из narrative, tool name, CSS state или тайминга.

Решение зафиксировано в
[ADR-0010](decisions/ADR-0010-qualification-evidence-authority.md).

## 4. Packs и manifest

Built-in packs поставляются как versioned read-only JSON manifests. Первый schema
не поддерживает пользовательский executable code. Новые step/assertion kinds
добавляются через код, review и tests.

Минимальная форма:

```json
{
  "schemaVersion": 1,
  "id": "excel.wq0.identity",
  "revision": "1",
  "title": "Excel document identity",
  "hosts": ["Excel"],
  "suite": "release",
  "workspacePolicy": "explicit-disposable-copy",
  "requirements": ["windows-x64", "office-x64", "independent-client-helper"],
  "coverage": ["R04", "WQ0", "WQ-SESSION.identity"],
  "steps": [
    { "id": "baseline", "kind": "hostProbe", "action": "excel.identity.capture" },
    { "id": "switch", "kind": "userAction", "instructionKey": "excel.switch-active" },
    { "id": "same-target", "kind": "assertion", "assertion": "excel.identity.same-target" }
  ]
}
```

Обязательные поля проходят strict validation: неизвестное поле/kind, duplicate step,
cycle, отсутствующий requirement/coverage ID, unsafe workspace policy или недоступная
capability блокируют pack целиком. Manifest получает content hash; run закрепляет
точные `packId + revision + hash`.

Schema v1 ограничен 100 последовательными steps. `dependsOn` может ссылаться только
на уже объявленный step: forward reference и cycle не принимаются. `cleanup` образует
только финальную группу. `userAction`, `assertion`, `agentTask` и остальные kinds имеют
разные закрытые формы; executable fields, raw tool IDs и неизвестные properties
отклоняются до catalog publication. Catalog показывает pack с отсутствующим runtime
requirement как blocked, а coverage registry отдельно сообщает обязательные ID без
scenario owner.

### Step kinds

| Kind | Owner | Назначение |
|---|---|---|
| `precondition` | Runner/host | build, host, bitness, capability, document safety |
| `fixture` | allowlisted fixture owner | создать disposable input без пользовательских данных |
| `agentTask` | normal conversation runtime | полноценная задача модели через production path |
| `hostProbe` | host capability | read-only COM/WebView/provider observation |
| `userAction` | UI | явное действие в Office с checkpoint до продолжения |
| `confirmation` | normal confirmation UI | проверить pause/resume/cancel без обхода policy |
| `restart` | application host | сохранить resume evidence и проверить replay |
| `fault` | qualification-only allowlist | точечная ошибка на заявленной boundary |
| `assertion` | source/domain verifier | детерминированный expected/actual result |
| `cleanup` | fixture owner | закрыть runner-owned resources; не скрывать failure |

Runner — конечный state machine: `ready -> running -> awaiting_user -> verifying ->
passed|failed|blocked|cancelled`. После возможного effect автоматический retry запрещён.
Повтор создаёт новый attempt с новым ID и ссылкой на предыдущий; старое evidence не
перезаписывается.

Перед каждым automatic step сохраняется mandatory `step.started`. Только после этого
runner вызывает action/verifier port. Потеря `step.completed` оставляет open possible
effect, переводит projection в blocked и запрещает in-place resume/retry. Безопасно
возобновляются только durable user checkpoint либо граница между закрытыми steps.
После failure/cancel остальные обычные steps пропускаются, но финальная cleanup group
выполняется bounded и не меняет исходный terminal outcome.
Если timeout/cancellation не завершил уже начатую operation, `step.completed` и
terminal event не фабрикуются, cleanup не запускается параллельно возможному effect,
а durable open step остаётся blocked без in-place resume.

## 5. Evidence и хранение

Каждый scenario использует отдельный явно помеченный qualification chat, связанный с
текущим document session. Closed event kinds фиксируют run start, step start, typed
step completion с observation/assertion evidence и terminal record в существующем
`*.events.jsonl`; большие immutable payloads используют тот же CAS. Это сохраняет
один durable source и позволяет resume/restart.

WQ-A1 вводит четыре mandatory authority operations:
`qualification.run.started`, `qualification.step.started`,
`qualification.step.completed`, `qualification.run.completed`. Они пишутся через
closed `IEventStore`; expected/actual свыше inline bound уходят в тот же CAS с проверкой
SHA-256. Qualification event projection сверяет exact pack/build provenance. Open
automatic step после replay остаётся blocked и не исполняется повторно.

Qualification projection каждый раз строится из validated stream. Отдельная БД,
mutable result file, durable dashboard index и dual-write запрещены. Сводка suite
остаётся UI projection; экспорт — одноразовый bounded bundle через существующие
trajectory/report primitives.

WQ-A2 создаёт отдельный qualification chat в том же document session.
Обычный `sendChat`/edit turn в нём запрещён; после restart latest run
находится по durable `qualification.run.started`, без второго index. Bridge
ограничивает expected/actual до 64 Ki characters на field и 256 Ki characters
на весь report preview, с явным `reportTruncated`; полное evidence остаётся
в event stream/CAS.

Terminal assertion содержит:

- build commit/version/channel и pack revision/hash;
- host/Office/WebView/bitness и capability snapshot;
- document/runtime identity без secrets;
- step/attempt IDs и source event sequences/IDs;
- expected, actual и evidence strength (`automatic` или `manual`);
- domain effect (`verified_change`, `verified_no_change`, `error`, `unknown`), если применимо;
- probe/fault-hook result и cleanup result;
- redaction/truncation state.

`BLOCKED`, missing evidence и `unknown` не становятся pass. Manual visual checks
остаются явно manual и не подменяют automatic assertions другой boundary.

BuildEvidenceManifest создаётся сборочным/release contour после host-neutral checks и
включает exact commit, configuration, checks, timestamps и file hashes. Приложение
только показывает и проверяет подпись/provenance этого manifest; оно не запускает
`dotnet`, MSBuild, Node или shell из VSTO.

## 6. Safety

- Mutating packs работают только с runner-created document либо после отдельного
  подтверждения disposable copy. Target marker и bound identity проверяются перед
  каждым effect.
- Никакого автоматического backup/rollback как доказательства безопасности. Runner
  проверяет final state и закрывает созданный документ без сохранения либо удаляет
  только файл с собственным ownership token.
- Confirmation, document gate, singleton-call policy, effect read-back и unknown
  semantics остаются production-owned.
- Fault hooks доступны только qualification build/flag, перечислены в allowlist,
  имеют одну boundary и не принимают произвольный payload/code/path.
- Packs не загружаются с URL и не исполняют пользовательские scripts. Будущие custom
  packs могут комбинировать только существующие safe step/assertion IDs.
- Reports по умолчанию metadata-redacted; document text, prompts, paths и CAS bodies
  включаются только явно.

## 7. Первый pack: встроенный Excel WQ0

`excel.wq0.identity` становится эталоном runner-а. Пользователь выбирает disposable
книги и нажимает кнопки wizard-а. Pack собирает:

- VSTO proxy текущей книги на owner STA;
- две независимые x64 client leases через узкий same-build qualification helper;
- desktop/native owner observation;
- active switch, Save As, close/reopen, second window и attach/detach checkpoints;
- different books и same-name books в разных Excel processes;
- release/cleanup и отсутствие document mutation.

Один run фиксирует текущий in-process owner call site. Полный WQ0 требует два
согласованных run одного exact build: из VSTO и из Desktop/native attachment;
отсутствующий call site остаётся `BLOCKED`, а не выводится из другого запуска.

Helper не является generic process runner: один versioned request/response contract,
one-time local channel, explicit HWND/target, no network, no shell и bounded output.
Identity decoder/lease имеют одного owner; существующий PowerShell probe остаётся
engineering fallback до switch, затем duplicate reader удаляется.

Pass требует согласованного `(process id, process start, OXID, OID)` для одной live
книги, различия разных lifetimes/targets и полный cleanup. `IPID`, path, HWND,
IUnknown address и generated GUID не принимаются как shared identity. Результат WQ0
разрешает отдельный production 5B2 design/switch, но сам его не закрывает.

## 8. Начальный catalog полноценных задач

| Pack family | Что проверяет |
|---|---|
| `common.quick` | новый chat, режимы, live model, resources, tool discovery, confirmation/cancel, run journal |
| `provider.live` | strict response, refusal, streaming, repair/reset, long payload, runtime call IDs, batch safety |
| `storage.recovery` | mandatory append barriers, CAS, restart/replay, multi-window revision, export |
| `excel.wq0.identity` | общий live workbook identity и lifetime до 5B2 |
| `excel.read-write` | inspect/read, scalar/formula/table write, no-op/error/unknown и exact read-back |
| `excel.complex-task` | resources -> analysis -> multi-step edits -> confirmation -> verified workbook result |
| `vba.lifecycle` | list/read/patch/write/rename/delete/restore/package, recovery и Trust Access failures |
| `ui.webview` | new-chat runner, keyboard/focus/DPI, confirmation, JSON/raw copy, reload/live append |
| `cross.full-run` | одна сложная задача через model, ToolPack, document effect, events, trajectory и restart |
| `<host>.capabilities` | только реально зарегистрированные Word/PowerPoint/Outlook families; absent capability = N/A, не pass |

Каждая complex task поставляется с versioned fixture и deterministic final-state
verifier. Можно менять prompt wording и модели, сохраняя invariant assertions.
Нестабильное качество модели оценивается серией запусков и отдельными метриками;
один удачный ответ не закрывает runtime gate.

## 9. Coverage и расширение

Coverage registry связывает каждый mandatory invariant/risk/capability с:

- owner layer и host;
- harness test либо qualification scenario/assertion;
- обязательностью для quick/full/release suites;
- последним exact build evidence;
- Windows/manual требованиями.

Новый model-facing tool, host capability, event kind или UI projection не считается
покрытым, пока registry не содержит проверку happy path, failure и effect/unknown там,
где возможна mutation. Architecture test запрещает неизвестные coverage IDs и
обязательные capabilities без scenario owner.

## 10. Этапы реализации

1. **WQ-A0 — contract — done:** ADR, pack/evidence/safety contracts и scope.
2. **WQ-A1 — host-neutral core — done:** strict manifest parser, catalog, coverage
   registry, runner state machine, typed bridge DTO, closed events и fake
   probes/verifiers; без Office/UI switch. [Evidence](stabilization/WQ_A1_QUALIFICATION_CORE.md).
3. **WQ-A2 — UI shell — done host-neutral:** карточка нового чата,
   Diagnostics entry, Qualification Center, stepper, durable resume, exact journal/
   shared JSON navigation и bounded report над read-only `common.ui-shell`.
   [Evidence](stabilization/WQ_A2_QUALIFICATION_CENTER.md).
4. **WQ-A3 — Excel WQ0 — host-neutral implementation done:** единый identity owner,
   in-process observation, narrow same-build x64 helper, release-suite UI и удаление
   duplicate diagnostic decoder. Реальная Windows qualification остаётся открытым
   gate. [Evidence](stabilization/WQ_A3_EXCEL_WQ0.md).
5. **WQ-A4 — suites — host-neutral catalog done:** common/provider/storage/UI,
   Excel/VBA/cross manifests, runner-owned fixture steps, deterministic assertion IDs
   и fail-closed coverage/capability gates. Live adapters и evidence остаются
   обязательными gates Milestone WQ. [Evidence](stabilization/WQ_A4_SUITE_CATALOG.md).
6. **WQ-A5 — release integration:** immutable BuildEvidenceManifest и release suite;
   Phase 12 получает только complete/compatible evidence.

Каждый этап — отдельный commit. Host-neutral tests не закрывают Windows gates; один
pack/host failure исправляется у его owner и повторяет только затронутый scenario,
затем общий smoke перед release.
