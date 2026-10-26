# V8 architecture stabilization: проверенный срез 2026-10-26

Owner: Core Agent/ModelProtocol/Storage; application и test owners — в
[матрице](v8-ownership-dependency-matrix.md). Baseline `main`,
`ba44ad1b573aa4a5f6daf0f79c0b6c3c35a5a98a`, исходно чистый worktree.
SHA из приложенного плана `3d7b5eb…` уже не актуален. Изменения выполнены
последовательно на main по текущим AGENTS.md, без commit и смены версии.
Этот документ — evidence конкретного среза; после принятия изменений остаётся
историей, а текущие контракты принадлежат canonical docs. Не является release qualification.

## Выводы по всем гипотезам плана

| ID / приоритет | Статус и доказательство | Решение / invariant / owner |
|---|---|---|
| A / P2 fallback | Confirmed static, **не новый дефект**. `ModelProtocolClient.cs:15,37,98` сохраняет fallback в экземпляре. `ConversationRunService.cs:78` создаёт protocol через factory; workspace создаёт его в WorkspacePorts (`WorkspaceConversationService.cs:1116`). Canonical protocol описывает fallback до конца run. Тесты bounded fallback, invalid schema, repair проходят | KEEP run-local policy. Переключение endpoint на том же искусственно переиспользуемом экземпляре не является production lifecycle. Concurrent calls одного клиента не квалифицированы и не добавлены в контракт. Не создавать capability cache |
| B / P1 skill admission | Исторический false-positive admission уже закрыт staged full-request callback. На HEAD остался второй **слишком строгий** half-input budget gate в `SkillTaskService.Select`. Новый fixture подтверждает: кандидат больше половины budget помещается в полный запрос обоих formats | Удалён half-budget gate. Production `SkillLoadToolHandler` требует existing full admission callback. Отказ не меняет selection. Owner: SkillTaskService + существующий compiler каждой application composition |
| C / P1 append recovery | Confirmed static duplication: normal/scalar catch отдельно восстанавливали cursor. Scalar stale-writer дефект уже исправлен baseline; его нельзя выдавать за новую потерю данных. Новые fault fixtures проверяют before-write, after-record, after-flush и неполный batch | Один private typed AppendAttempt и RecoverOwnAppend. Только полностью проверенный собственный batch может продвинуть cursor; чужой tail и partial batch не могут. Исключение остаётся исключением. Owner: ChatStore |
| C / P1 relocation D07 | Confirmed static, **отложено**: `ChatStore.cs:673–676` сначала append binding, затем File.Move; MoveDocument обходит несколько чатов. Crash-safe atomic move всего набора не доказан | Нужен отдельный layout/migration decision. Стабильный ChatId — направление, не выполненная миграция. Не добавлены mapping store, dual-write или автоматическое восстановление |
| D / P1 fixtures | Reproduced stale assertions: ReferenceEquals после projection, runtime GUID вместо E alias в replay и отсутствующая execution reference в preview fixture; MockDemo tool/done; 11 CLI scripts с retired wire/ожиданиями; Web fixture без app-utils/app-agent-model | Fixtures переведены на v8 и реальный terminal E receipt. Сохранены negative permission, stale read, unknown replay, frozen repair, exact bytes и data-preservation assertions. Production parser не расширен |
| E / P1 wire/Inspector | **Расхождение не воспроизведено**. `LlmClient.cs:116,137` использует один serialized byte[] для trace и transport. Saved Inspector и preview имеют разные обязанности | KEEP owners. CLI сравнивает actual HTTP bytes с CAS trace на каждом шаге, включая repair; Harness проверяет strict/object и выбранные skills через новый store/Inspector после сохранения. Новый compiler/serializer не нужен |
| F / P2 UI lifecycle | Shared chat owner уже существует; риск из истории не равен текущему дефекту. Targeted VM/browser checks выполнялись в Wave 0; единственный исправляемый drift — неполная composition workspace-chat-v8 fixture | KEEP production UI. Не вводить второй lifecycle manager; Windows WebView2 switching/dispose остаётся отдельным evidence |
| G / P2 records | Confirmed active seam: `ResourceGatewayService.cs:294`, `ArtifactTransferPackage.cs:45`; HTML bindings/custom JS и provider metadata используют historical representations. Discovery скрывает records (`ResourceGatewayService.Intent.cs:892`), чтение не стало мёртвым | KEEP до explicit migration decision и fixtures сохранённых bindings, transfer, restart/export. Удаление reader сейчас не обосновано |
| H / P2 large files | Documented limitation, **новый perf benchmark не проводился**. Snapshot-aware patch существует; Office source acquisition может материализовать полный текст. General save clone/rebuild остаётся | Не переписывать patch engine. RSS/stage timings и narrow extraction — отдельный scope; отсутствие измерений не выдавать за ускорение |

## Дополнительные находки и реализация

1. **P1 / reproduced unknown inspection loophole.** `AgentProgressTracker.Observe`
   снимал общий bool после любого successful IndependentLocalRead. Теперь pending
   targets берутся из typed effect impacts/recovery; снимаются только свежим,
   complete whole evidence того же ResourceIdentity, без immutable historical
   source. Timestamp terminal fact переносится через ToolExecutionProgress.
   Несколько targets требуют нескольких подходящих наблюдений. Missing identity/time
   остаются unresolved; unrelated read не может придумать authority. Unknown health
   и запрет повторного dispatch не очищаются. Это inspection floor, не доказательство
   достижения пользовательской цели. Тесты: unrelated, immutable, stale/incomplete,
   multiple targets, confirmation/restore и запрет replay.
2. **P2 / reproduced CLI env composition.** `AvailableToolIds` создаёт WorkspacePorts
   без session; безусловный WorkspaceChatAttachmentResourceProvider выбрасывал
   exception. Provider создаётся только при session; catalogue-only env теперь
   работает. Проверка — unified_agent CLI; tool execution по-прежнему имеет session.
3. **P2 / reproduced fixture false-positive route.** Replay fixture с GUID в finish
   мог падать на reference admission до unknown guard. Теперь finish использует
   `ExecutionReference.Alias`, поэтому проверяется заявленная ветка.
4. **P2 / reproduced fixture assertions.** Title generation сдвигала scripted queue;
   terminal error ошибочно сопровождался satisfied; schema catalog находится в v8
   schema, а unavailable skills остаются metadata-visible. Исправлены setup и
   assertions по текущему контракту. Load отсутствующей capability проверяется
   реальным отказом; не удалена проверка запрета исполнения.

## Before / after

| Срез | До | После / удалено |
|---|---|---|
| Skill budget | half-budget estimator + full compiler admission | Один authoritative full admission; удалён второй gate и неиспользуемый settings argument; loader без callback fail-closed |
| Commit recovery | Два catch/recovery; разные правила normal/scalar | Один RecoverOwnAppend; собственный batch, hash/event identity и durable flush; cursor snapshot целиком |
| Unknown inspection | Глобальный bool, сброс по policy любого read | Список pending typed targets как run-local projection фактов; без нового durable store |
| Test transport | Повторные устаревшие ответы/ручные refs | Shared conversation_transport формирует v8 и реальные refs; production v7 fallback не добавлен |
| Inspector/UI | Разные owners ошибочно выглядят дублями | Owners сохранены; добавлена проверка reload, исправлены зависимости fixture |

Новых runtime файлов, interfaces, services, stores или csproj entries нет.
Появившиеся во время работы сторонние UI changes сохранены и исключены из scope
реализации; граф фиксирует worktree bytes, а не приписывает их этой задаче.
Размер diff не используется как доказательство упрощения: сокращено число
альтернативных decision/recovery paths, при этом fault coverage добавляет код.
Commit list пуст: пользовательский worktree не коммитился автоматически.
`git diff --stat` — воспроизводимый размер реализации; generated inventory учитывается
отдельно. Perf before/after: **не измерялось**, обещаний O(1)/RSS gain нет.

## Проверки и границы evidence

Команды и stdout сохранены в
[evidence](evidence/v8-architecture-2026-10-26/README.md).
Исходная выборка Wave 0: 36/39 Harness, 6/7 Web; три Harness failures были
fixture drift (snapshot identity и два replay references). Это выборка, не полный
baseline suite. Исторические общие числа из RISK_REGISTER не переносятся на HEAD.

Финальная выборка покрывает kernel, three append/concurrency cases, skills,
request budget, saved request composition, model refs, replay и fallback;
CLI использует controlled local HTTP, без внешнего provider. MockDemo использует
fake Office adapter. Host-neutral build warnings PDF/fakes не объявлены устранёнными.

Открыты: Windows x64 + Office x64/VBE/WebView2; реальные model A/B; аварийное
завершение процесса/ОС в COM + terminal append; D07 relocation; records migration;
long-history incremental projections и memory benchmark. Fault injection воспроизводит
exception boundaries, но не power-loss durability железа. Старые progress facts без
CompletedUtc не получают выдуманную дату; неизвестная inspection остаётся unresolved.
