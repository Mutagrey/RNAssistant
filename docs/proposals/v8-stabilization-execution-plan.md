# V8 stabilization: согласованные вертикальные срезы

Owner: Core/Application maintainers. Дата 2026-10-26; baseline
`ba44ad1b573aa4a5f6daf0f79c0b6c3c35a5a98a`.
Основание: [аудит](../audits/v8-architecture-drift-2026-10-26.md),
[ownership/dependency matrix](../audits/v8-ownership-dependency-matrix.md).
Пользователь после Wave 0 поручил реализацию. Scope ограничен существующими owners;
D07 layout, records migration, broad composition/bridge cleanup и perf/extraction
не входят в текущий cutover. После принятия этого diff документ становится историей;
новые задачи ведутся в existing BACKLOG, а не расширяют этот план автоматически.

## Реализованные срезы и порядок проверки

| Срез / зависимости | Конкретные файлы и изменения | Проверка / ожидаемый результат |
|---|---|---|
| S0 — evidence, без runtime refactor | `tools/audit-v8-dependencies.py`; `docs/audits/v8-*`, evidence JSON | HEAD, project/import edges, lexical symbols/calls/subscriptions; unresolved dynamic edges явно отмечены. Ни один файл не удаляется по отсутствию lexical consumer |
| S1 — fixtures / S0 | Harness `Program.SkillPackagesTests.cs`, `Program.SessionEventStoreTests.cs`; MockDemo `Program.ResourceWorkflowTests.cs`; `tests/cli/conversation_transport.py` и 11 backlog scripts + shared consumers; `tests/web/workspace-chat-v8.test.js` | v8 execute/finish и actual terminal E aliases; title traffic отдельно от queue. Negative unknown/stale/permissions сохранены. Full-response equality заменяет object identity, schema checks читают current union |
| S2 — skill admission / S1 | `Core/Services/SkillTaskService.cs`, `Core/Tools/SkillLoadToolHandler.cs` | Удалён half-budget gate; обязательный callback tool load. Tests: >half candidate fits complete request, schema/object, actual full overflow refusal, selection unchanged/idempotent, cold restart; existing Office fake and CLI |
| S3 — persistence / S1 | `Core/Storage/ChatStore.cs`, `ChatStore.SqliteProjection.cs`; Harness `Program.MetadataConcurrencyTests.cs` | Общий AppendAttempt/RecoverOwnAppend. Before-write не меняет cursor; полный own append после exception узнаётся по event/hash и flush; partial/foreign tail не усыновляется; stale writer остаётся stale, replay сохраняет canonical facts |
| S4 — unknown inspection / S1,S3 | `Core/Agent/AgentProgressTracker.cs`, `Core/Tools/ToolExecutionRecord.cs`; `Program.AgentKernelTests.cs` | Unknown targets/time восстанавливаются из terminal progress/effects. Unrelated, historical immutable, stale, partial, incomplete reads не снимают floor; все targets нужны. Confirmation/restore не стирают unknown/nonretry |
| S5 — exact context / S1 | `Program.ModelContextInspectorTests.cs`; `tests/cli/tool_result_projection_smoke.py` | Same saved bytes после создания нового store/Inspector; HTTP bytes == CAS trace каждого request/repair; strict/object и selected skills. Preview не объединяется с saved Inspector |
| S6 — обнаруженная composition ошибка / S1 | `Application/Workspace/WorkspaceConversationService.cs` | Catalogue-only WorkspacePorts без session не создаёт attachment provider. CLI env + real session scenarios проходят |
| S7 — документация / S2–S6 | `skills.md`, `session-events.md`, `conversation-protocol.md`, README, PROGRESS/BACKLOG/RISK_REGISTER, Harness README | Текущие owners, removal и remaining gates отражают code; никаких нового migration adapter/store |

S0–S7 реализованы в одном рабочем diff, без автоматического commit. Для отдельных
коммитов допустима граница по invariant выше; tests и соответствующий canonical doc
должны оставаться вместе. До commit выполнить обязательный ValidateVersionFormat;
эта работа commit/release/tag не создаёт.

## Миграция, остановка и rollback

Wire v8, event format, ChatId layout и existing CAS не меняются. В typed progress
добавлено optional CompletedUtc из уже существующего terminal record. Missing timestamp
не заменяется текущим временем: такой old unknown остаётся unresolved. Нет event rewrite,
автоматического retry, replay effect, hidden fallback или удаления пользовательских данных.

Если append выбросил исключение, caller должен остановиться и перечитать canonical
journal. Даже при доказанном собственном durable batch метод не сообщает ложный успех:
cursor recovery не является повтором application operation. Частичный batch может оставить
валидный префикс events; он виден после reload, но старому in-memory projection не выдаётся
authority следующего append. SQLite/CAS projections остаются derived.

Rollback кода не откатывает уже записанные events/эффекты. Сохранять canonical log и CAS;
воспроизвести отказ на копии состояния, исправить owner, перечитать projection.
Нельзя «восстанавливать» unknown tool автоматическим повтором. Новых fallback adapters
не введено, поэтому MIGRATION_MAP не получает искусственных записей.

## Следующие отдельные решения, не выполненная работа

| Приоритет / owner | Следующий конкретный change | Удаляемый путь и acceptance gate |
|---|---|---|
| P1 D07 / Storage | ADR stable ChatId layout + old-layout import/refusal | metadata+File.Move binding path. Crash every boundary, multi-chat conflict/partial move, cold catalog; отсутствие потери данных |
| P2 D06 / Storage | Измерить clone/rebuild cost на long history, затем touched-operation projections | Только измеренно лишние полные rebuild; parity/fault tests, counters и RSS/time baseline; без второго store |
| P2 records / Resource+HTML+JS | Inventory сохранённых bindings/packages, явная migration | records reader/capability только после exact/head, derived, Excel/Outlook/JSON, custom JS, restart/export fixtures |
| P2 D02/D03/D04 / Application+Bridge | При следующем изменении конкретного construction/DTO пути | Названный duplicate construction/casing path; producer+consumers вместе; lifecycle и Windows delivery |
| P2 large files / Source owners | 10–12 MiB reproducible stages/RSS; решить необходимость narrow extract | Existing revision-aware patch остаётся; ambiguous boundary refusal, byte/revision equality; не добавлять generic patch engine |
| Qualification / host owners | Windows x64/Office x64/VBE/WebView2 и live-model comparison | Host-neutral evidence не закрывает COM, delivery, renderer, real cancellation/crash или model quality |

## Приёмка

Сохранённые команды/stdout: [evidence](../audits/evidence/v8-architecture-2026-10-26/README.md).
Приёмка текущего scope — targeted checks зелёные, current docs и links согласованы,
старые decision/recovery branches удалены, нет новых competing owners. Это не
приёмка всех исторических BACKLOG items и не утверждение полного suite/Windows release.
