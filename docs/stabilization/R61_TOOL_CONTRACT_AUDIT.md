# R61/11O — audit границы model-facing tools

Дата фиксации: 2026-09-02. Статус: 11O0 source-built-in property inventory
зафиксирован; runtime family cutovers, dynamic custom-package review и UI ещё
не выполнены.

Этот документ не меняет runtime, public tool ids, schemas или UI. До атомарного
переключения конкретного семейства действует его текущий канонический контракт.
R61 не вводит второй executor, generic router, pipelines, aliases или dual schema.

## 1. Диагностический вывод

Наблюдаемый кластер относится прежде всего к границе Tool Contract / Resource
Fabric / VBA mutation, а не к COM/VSTO host:

| Симптом | Что означает сейчас | Архитектурный вывод |
|---|---|---|
| `resource_kind_unknown` | В `common.resources_list` передано значение `kind`, которого не знает выбранный provider | Защитная ошибка корректна, но свободный provider vocabulary не должен быть обязанностью модели |
| `resource_revision_changed` | Live resource или collection изменились либо continuation больше не соответствует наблюдённой revision | Drift должен обнаруживаться, но URI/revision/continuation обязан связывать runtime; по одному журналу нельзя отличить реальное изменение через VBE от ошибочного переноса состояния моделью |
| `resource_cursor_invalid` | Cursor использован для другой операции, query, URI или representation | Scope guard корректен; ошибка показывает, что runtime-owned continuation оказался в caller contract |
| `vba_patch_stale_source` | Exact hunk построен не по текущему live source | Fail-closed guard корректен; повторяемость усиливают частичное чтение и необходимость модели переносить состояние между read и mutation |
| успешный list с `items: []` | При невыбранном provider текущий gateway может вернуть provider discovery вместо ресурсов | Это не доказательство пустого VBA project; допустимое внутреннее discovery стало неудачным конечным model contract |

`invalid_model_response`/repair относится к ModelProtocol/endpoint и не должен
смешиваться с этим кластером без отдельного causal evidence.

Главная причина — не «слишком слабая модель» и не необходимость ослабить guards.
Публичный контракт заставляет caller воспроизводить транспортный протокол:

```text
provider -> kind -> URI -> revision -> cursor -> exact source -> mutation guard
```

Модель должна выбирать цель и действие. Точное связывание identity, snapshot,
continuation и guard принадлежит runtime.

## 2. Владение состоянием

| Данные | Владелец | Что видит модель |
|---|---|---|
| Имя VBA component, лист, диапазон, слайд, искомый текст, новое содержимое | Domain intent | Строго типизированное semantic значение |
| Provider routing и provider-specific `kind` vocabulary | Resource runtime | Только устойчивую semantic category, если она реально нужна для выбора |
| Canonical revision-pinned `rna://` URI / `ResourceRef` | Resource Fabric | Читаемый candidate/результат и provenance; URI не собирается и не копируется в следующий call |
| Revision, content hash, etag, collection fingerprint | Provider/runtime | Не является аргументом; используется для fail-closed concurrency |
| Cursor, offset, page token, representation scope | Read implementation | Bounded result и при необходимости semantic `Next`, без opaque token |
| ToolCallId, run/chat/document IDs, UUID, confirmation и prepared guards | Kernel/ToolRuntime | Не создаются и не переносятся моделью |
| VBA backup identity | VBA journal/runtime | Читаемый candidate/время или выбор «последняя для этого module»; raw backup ID не вводится caller-ом |

Если runtime-owned state должен пережить model step, он сохраняется в уже
существующем typed event/result chain. Отдельный mutable side index или скрытая
execution authority не вводится.

## 3. Текущая поверхность и почему имеющихся тестов недостаточно

В Excel Agent core/bootstrap pack сейчас публикуется 26 schemas: шесть bootstrap,
пятнадцать Excel и пять VBA/macro. Четыре `common.resources_*` дополнительно
передают caller-у provider, kind, URI, revision, representation, cursor и limits.
VBA provider материализует exact source внутри runtime, но публичный read по
умолчанию отдаёт 2,048 символов и требует продолжения cursor-ом для крупных
модулей.

Существующие deterministic harness scenarios подтверждают guards и wiring, но
часть model scenarios заранее подставляет provider, URI и cursor scripted
delegate-ом. Это доказывает механическую исполнимость контракта, а не его
понятность реальной модели. R61 поэтому измеряет не только handler success, но и
число calls, argument/format repairs, tool errors, continuation restarts и итоговую
успешность задачи.

11O0 добавил machine-checked
[property inventory](R61_TOOL_PROPERTY_INVENTORY.tsv): 73 уникальных built-in id
и 76 effective host-вариантов фиксируют exact descriptor revision, host, mode,
direct binding и все рекурсивные schema property paths. Четыре дополнительных
варианта принадлежат host-specific `common.html_data_bind`. Поле, похожее на
runtime plumbing, не может появиться без явного решения; допустимые public
capability/tool/skill/mail identities отмечены отдельно от полей, которые R61
internalizes/removes. Descriptor revision одновременно фиксирует description,
defaults и validation, не создавая второй schema source.

Произвольные argument schemas установленных custom packages нельзя честно
зафиксировать в source baseline: они package-owned и меняются только новой exact
package revision. Их поля не удаляются автоматически по имени (`customerId` может
быть domain identity). R61 Tool-authoring/Library slice обязан показать их в том же
property audit, потребовать явное rationale для plumbing-shaped inputs и
fail-closed оставить непроверенный package вне release evidence. Это оставшийся
dynamic inventory gate, а не причина задерживать независимый built-in family
switch.

## 4. Полный current inventory `common.*`

Source inventory содержит до 35 built-in `common.*` ids. Число условное:
`common.html_data_bind` публикуется только при наличии допустимых Office data-source
tools, Tool/Skill/Prompt authoring зависит от доступности stores/settings, а VBA —
от host. Все 35 schemas одновременно модели не передаются:

- Chat получает четыре resource schemas;
- Plan начинает с шести bootstrap schemas — resources и capabilities;
- Excel Agent core содержит 11 `common.*` schemas: те же шесть bootstrap плюс пять
  VBA/macro; вместе с 15 Excel schemas это текущие 26 core schemas;
- остальные exact ids видны в compact capability catalog, а полный schema
  загружается только через `common.capabilities_read`.

Progressive loading уменьшает token cost, но не делает лишний tool полезным и не
исправляет сложный schema после загрузки. Ниже зафиксировано default-направление
R61. `KEEP` означает сохранить самостоятельный model intent, `ON-DEMAND` — не
держать schema в default core, `MERGE`/`SPLIT` — сменить public responsibility
атомарно, `INTERNAL/UI` — убрать из model-facing catalog без удаления функции.
Финальные ids утверждаются после eval; текущие ids действуют до cutover.

Не всякое поле с именем `id` является transport leak. Public tool/skill id и id
создаваемого custom package — user-visible domain identity: модель выбирает его из
catalog или явно именует новый объект. ToolCallId, run/chat/document UUID,
artifact/revision/backup ids и prepared guards остаются runtime-owned. Существующий
public id также нельзя «вспоминать» приблизительно: он выбирается из exact result.

| Current tool | Нужен ли модели | Default R61 disposition |
|---|---|---|
| `common.resources_list` | Нужен сам intent «найти доступные ресурсы», но не provider discovery/paging | `MERGE` с `resources_search` в один semantic find; provider/kind/cursor/limit — runtime |
| `common.resources_resolve` | Самостоятельного пользовательского действия обычно нет | `INTERNAL`; exact URI/member resolution выполняется между find/read, public остаётся только если отдельный eval докажет иной use case |
| `common.resources_search` | Нужен поиск по query/scope | `MERGE` с list; модель задаёт только query и semantic scope |
| `common.resources_read` | Нужен | `KEEP`; semantic candidate/target и при необходимости representation, но не URI/revision/cursor/maxChars |
| `common.capabilities_search` | Нужен bootstrap discovery | `KEEP`; query/kind, paging и limit внутренние |
| `common.capabilities_read` | Нужен для exact tool admission и skill loading | `KEEP`; public tool/skill id и semantic reference path допустимы, offset/maxChars/revision/admission state внутренние |
| `common.questions_ask` | Нужен только Plan mode | `KEEP` mode-specific; question/option ids генерирует runtime, модель задаёт prompt/options и meaningful selection semantics |
| `common.plan_doc_create` | Нужен save plan, но не отдельный create lifecycle | `MERGE` с update в один active-plan save/upsert |
| `common.plan_doc_update` | Нужен save plan | `MERGE`; active plan id и expected revision artifact id внутренние |
| `common.plan_doc_restore` | Нужен только по явному запросу восстановить историю | `ON-DEMAND KEEP`; модель выбирает читаемый revision candidate, exact ids/guards связывает runtime |
| `common.plan_doc_delete` | Нужен только по явному запросу удалить plan | `ON-DEMAND KEEP`; active identity/revision внутренние, explicit-request guard сохраняется |
| `common.task_list_create` | Нужен visible task state, но active list один | `MERGE` с update в один task-list set/upsert; runtime создаёт list/step ids |
| `common.task_list_update` | Нужен | `MERGE`; модель передаёт goal/ordered steps/status, не stable ids |
| `common.task_list_close` | Нужен terminal outcome, отдельная active identity не нужна | `MERGE` в тот же task-list state tool с малой typed close-веткой; active list id внутренний |
| `common.html_workspace_inspect` | Static preflight нужен, отдельный model call обычно нет | `INTERNAL/UI`; запускать после write/patch/preview, оставить Library diagnostic только если independent troubleshooting eval это требует |
| `common.html_workspace_upsert` | Whole-content authoring нужен, но file и JSON data имеют разные validation | `SPLIT` на file write и data write; resourceType не должен создавать большой union branch |
| `common.html_workspace_apply_patch` | Exact source edit нужен | `KEEP`; сократить до exact operations, advanced regex/line variants не держать в основном schema, whole write остаётся выбором |
| `common.html_workspace_delete` | Нужен semantic delete | `ON-DEMAND KEEP`; target candidate/path определяет file/data без ручного transport id |
| `common.html_workspace_set_active` | Это UI/session selection, не content intent | `INTERNAL/UI`; write может вернуть/выбрать созданный entry, пользователь переключает preview в UI |
| `common.html_data_bind` | Live binding нужен, текущий nested tool-call authoring слишком общий | `KEEP` после redesign: bind prior accepted read/candidate; убрать `sourceTool` + произвольный `sourceArguments` из обязанностей модели |
| `common.html_data_refresh` | Manual refresh иногда нужен | `ON-DEMAND KEEP`; optional semantic data name/all, policy/defaults внутренние |
| `common.html_data_freeze` | Отличимый intent: сохранить JSON и удалить binding | `ON-DEMAND KEEP`; не объединять с refresh из-за другого effect |
| `common.prompts_read` | Нужен только при явном prompt/settings authoring | `ON-DEMAND KEEP` |
| `common.prompts_save` | Нужен только при явном authoring | `ON-DEMAND KEEP`; один `promptKey` + typed value за call вместо девяти независимых optional полей |
| `common.tools_definition_read` | Нужен для изменения exact custom tool | `ON-DEMAND KEEP`; compact list mode удалить как duplicate capabilities search, exact public tool id допустим |
| `common.tools_validate` | Перед upsert не нужен отдельный model step | `INTERNAL/UI`; upsert обязан валидировать до write и вернуть те же diagnostics, Library может иметь dry-run |
| `common.tools_upsert` | Нужен только для явного custom-tool authoring | `ON-DEMAND KEEP` после сокращения: убрать parallel advanced `parameters`, constant executor и self-granted safety/authority fields; runtime валидирует и назначает conservative policy |
| `common.tools_delete` | Нужен по явному запросу | `ON-DEMAND KEEP`; exact custom tool id semantic, confirmation сохраняется |
| `common.skills_upsert` | Skill core и reference authoring нужны, но это две ответственности | `SPLIT` на core upsert и reference upsert; current mixed anyOf удалить при atomic cutover |
| `common.skills_delete` | Whole skill и one-reference delete различаются scope/risk | `SPLIT` на exact core delete и reference delete; current optional `referencePath` branch удалить |
| `common.vba_restore_backup` | Нужен только для явного rollback | `ON-DEMAND KEEP`; readable backup candidate/module intent, raw backupId внутренний |
| `common.vba_write_module` | Whole-source write нужен | `KEEP`, но `SPLIT` rename в отдельный intent; write получает module/source и только meaningful creation policy/type |
| `common.vba_apply_patch` | Exact minimal edit нужен | `KEEP`; hunks `find/text`, constant `op=replace` внутренний; guards/read snapshot внутренние |
| `common.vba_delete_module` | Нужен только по явному запросу | `ON-DEMAND KEEP`; module name semantic, guard/backup lifecycle внутренние |
| `common.office_run_macro` | Нужен только когда пользователь просит execution | `ON-DEMAND KEEP`; high-risk самостоятельный effect, не держать в default VBA authoring core только по исторической причине |

### `common.*` skill ids, которые не являются tools

Отдельно source содержит девять built-in Common skills. Их compact ids участвуют в
capability selection, а загруженный Markdown напрямую управляет выбором tools и
arguments. Поэтому skill body является consumer tool contract и переключается в
том же atomic family slice; нельзя оставить инструкцию с удалённым id/аргументом.

| Current skill | Default R61 disposition |
|---|---|
| `common.task_tracking` | `KEEP`; переписать под объединённый active task-list intent, убрать model-owned list/step ids и resource URI |
| `common.text_search_replace` | `KEEP ON-DEMAND`; это guidance над host search/replace tools, не отдельный common tool |
| `common.vba_code_editing` | `KEEP`; удалить provider/kind/URI/cursor/backupId choreography, описать semantic find/read и отдельный rename intent |
| `common.vba_userform_authoring` | `KEEP ON-DEMAND`; отдельная specialist responsibility оправдана, ссылки обновляются вместе с VBA family |
| `common.tool_authoring` | `KEEP`; сделать единственным entry skill для поддерживаемого custom-tool authoring |
| `common.vba_tool_authoring` | `MERGE` в `common.tool_authoring` как bounded reference/section: единственный executor уже VBA, два entry skills дублируют выбор и правила |
| `common.skill_authoring` | `KEEP`; обновить под разделённые core/reference mutations и убрать caller offset/maxChars |
| `common.prompt_authoring` | `KEEP ON-DEMAND`; обновить под one-key prompt save и новый resource/capability contract |
| `common.html_workspace_authoring` | `KEEP ON-DEMAND`; убрать обязательный inspect call, manual set-active, nested source-tool arguments и cursor choreography |

`common.text_search_replace` из широкого string search нельзя считать пропущенным
tool: он создаётся `BuiltInSkillProvider` как skill. Host-specific skills и custom
skills всё равно входят в последующий property/consumer inventory, но не смешиваются
с этим exact Common baseline.

Из текущих common tools первичные кандидаты на удаление из model-facing surface —
`resources_resolve`, `html_workspace_inspect`, `html_workspace_set_active` и
`tools_validate`. List/search и create/update pairs являются кандидатами на merge;
write/rename и смешанные skill/reference branches — на split. Поэтому raw registry
count может уменьшиться незначительно: главный результат — меньший default callable
pack и резко более узкие schemas, а не искусственно минимальное число названий.

Текущий инвариант «все Excel/VBA core schemas сразу» не меняется этой записью.
R61 обязан отдельно сравнить его с on-demand admission на реальных VBA и обычных
Excel tasks; изменение core membership требует явного atomic contract switch, а не
скрытого удаления tool.

## 5. Целевая граница

```text
Model / Library Test
  minimal semantic intent
          |
          v
typed runtime preparation
  bound document + exact ResourceRef + revision + cursor + guard
          |
          v
existing ToolRuntime policy / confirmation / handler / evidence
```

Подготовка не является новым tool и не выдаёт дополнительную authority. Она
работает после strict validation model arguments, в exact bound session, и передаёт
доменному handler-у один typed execution context.

Read continuation выполняется внутри bounded policy. Если явное продолжение всё же
необходимо, UI/runtime публикует operation-specific semantic действие `Next`, но
opaque binding остаётся в typed result/event chain и не появляется в аргументах
модели. При неоднозначном pending read runtime запрашивает semantic target и
останавливается fail-closed, а не угадывает URI. Для read-only drift policy может
начать новое чтение и явно сообщить об этом.
Mutation никогда автоматически не retry/rebase, не применяет fuzzy patch и не
подменяет patch whole-source write.

## 6. Когда объединять и когда разделять tools

Количество tools само по себе не является целью. Решение принимается по semantic
responsibility:

- разделять, если различаются effect, confirmation, retry/idempotency policy,
  result shape или набор обязательных аргументов;
- сохранять variants в одном tool только для одного действия над одной целью с
  одинаковыми effect/result и малым строгим discriminator;
- внутренние routing, resolve, paging и guard calls объединять внутри runtime, а не
  показывать как последовательность действий модели;
- не создавать action mega-tool: большой union schema с несвязанными branches
  ухудшает выбор и validation так же, как избыточное число общих tools;
- в model step публиковать только pack, релевантный mode/host и принятой capability
  revision; полный Library catalog не равен callable set.

## 7. Решения по семействам для отдельной реализации

### Resources

- До cutover сохраняются четыре текущих id. Default target — один semantic find
  вместо list/search, один read, а resolve становится внутренней подготовкой; exact
  public shape подтверждается comparative eval и переключается атомарно без aliases.
- `list` должен перечислять semantic resources. Provider discovery не должно
  маскироваться успешным пустым `items`; истинно пустой результат отличается от
  discovery/unavailable/access/filter mismatch.
- Provider routing становится внутренним. Если caller действительно выбирает
  категорию, это небольшой стабильный enum предметной области, а не свободная
  строка provider-specific `kind`.
- `search` получает query и semantic scope. `read` принимает выбранный candidate
  или semantic target; URI/revision/cursor/page size связывает runtime, а результат
  всё равно содержит exact `ResourceRef` как evidence.
- `resolve` по умолчанию становится внутренней подготовкой. Public tool допустим
  только если independent scenario докажет самостоятельное semantic действие.
- VBA discovery/read остаётся только через единый Resource Fabric. Вторые VBA read
  ids и host-prefixed aliases не возвращаются.

### VBA mutations

- Whole-source write и exact-hunk patch остаются двумя явными вариантами модели.
  Whole-source write не запрещается только потому, что patch безопаснее для малой
  правки.
- Patch остаётся exact и fail-closed. Runtime сам читает live state и строит guard;
  stale source не вызывает fuzzy apply, automatic rebase или fallback в write.
- Константный `op: "replace"` у patch удаляется из target intent schema. Rename
  отделяется от write/upsert, потому что у них разные target/effect и обязательные
  поля; exact ids утверждаются после eval.
- Delete, restore и macro run сохраняют отдельные responsibilities. Для restore
  модель выбирает читаемый candidate либо «последнюю для module»; raw backup ID
  связывает runtime.
- Procedure-aware editor был бы новым поведением и не входит в behavior-preserving
  baseline R61. Его нельзя использовать как оправдание возврата legacy read tools.

### Остальные tools

Каждое семейство проходит тот же property-level inventory. Exact runnable tool id
может быть semantic выбором; catalog/package revision, cursor, package hash,
artifact guard и execution IDs — нет. Tool merge/split допускается только вместе с
удалением старого path и targeted contract/effect tests.

## 8. Обязательные eval scenarios

R61 не закрывается только schema snapshot-тестами. Минимальный набор включает:

1. Resource find возвращает ресурсы, а не provider-discovery `items: []`; настоящий
   пустой scope обозначен отдельно. Model arguments не содержат provider vocabulary,
   URI, revision или cursor.
2. Большой VBA/HTML/text resource читается полностью без переноса cross-resource
   continuation state. `resource_kind_unknown`, cross-scope
   `resource_cursor_invalid` и caller-induced revision mismatch структурно
   невозможны, а внутренние guard regressions сохраняются.
3. Capability находится и загружается без caller cursor/offset/limit; exact public
   tool/skill id не превращается в invented internal id, schema admission остаётся
   durable и revision-matched внутри runtime.
4. Plan question отображается без model-generated question/option ids; single и
   multiple selection сохраняют требуемую UX semantics.
5. Create/update active plan выполняются одним save intent без plan/artifact ids;
   explicit restore/delete выбирают читаемую revision, а runtime применяет exact
   guards.
6. Task list проходит create/update/close lifecycle без list/step ids в model
   schema, сохраняя стабильные ids в persisted/UI projection.
7. HTML workspace создаётся whole-source и меняется exact patch-ем; preflight
   выполняется автоматически. Live data binding использует prior accepted read, а
   не nested `sourceTool/sourceArguments`; refresh/freeze сохраняют разные effects.
8. Prompt authoring читает settings и сохраняет один typed prompt key за call,
   не заставляя модель заполнять anyOf из независимых optional полей.
9. Custom tool читается, сохраняется и удаляется без отдельного
   `common.tools_validate` call. Invalid definition ничего не пишет, diagnostics
   возвращаются upsert-ом, а модель не может сама понизить conservative authority.
10. Skill core и reference create/update/delete проходят через отдельные узкие
    schemas без смешанного core/reference anyOf и без потери неизменённых данных.
    Все девять built-in Common skill bodies проверяются на retired ids/arguments и
    проходят scenario после загрузки через capability reader.
11. Модель находит два названных VBA modules, читает их и добавляет тест. Реальное
    изменение через VBE между read и mutation обнаруживается без silent rebase,
    retry или whole-write fallback; exact patch и intentional whole-source write
    остаются разными доступными вариантами.
12. Rename, restore, delete и arbitrary macro execution не попадают в default
    callable pack без semantic need; после exact admission сохраняются их effect,
    confirmation и evidence contracts.
13. Для каждого before/after scenario сравниваются task success, model/tool calls,
    schema-load calls, argument/format repairs, tool errors, input tokens, latency и
    effect evidence. Обычные Excel/Plan/Chat задачи проверяют, что сокращение pack не
    добавило лишнюю capability-discovery цепочку.

## 9. Порядок исполнения и gate

1. Source-built-in часть property-level inventory завершена в 11O0: все effective
   Common/host variants проверяются по exact revision/mode/host/binding/property
   paths, а девять Common skill consumers перечислены выше. Dynamic installed
   custom-package property review и callable-pack comparison остаются своими
   gates Tool-authoring и core-pack slices; они не подменяются source snapshot.
2. Переключить Resources + Capabilities intent/preparation boundary и удалить
   старые public plumbing arguments/ids.
3. Переключить Plan questions/doc/task-list lifecycle без caller-owned ids/guards.
4. Переключить HTML workspace/data binding и удалить model-facing diagnostics/UI
   selection paths.
5. Переключить Prompt, Tool и Skill authoring, включая internal validation и
   conservative authority.
6. Переключить VBA/macro family, сохранив exact mutation safety и выбор patch/write.
7. Повторно вычислить минимальный mode/host core pack по eval evidence; optional
   exact schemas остаются доступны через capability admission.
8. Добавить UI-only built-in documentation, typed Library Test и исправить
   Implementation/Test layout.
9. Собрать final live-provider, Windows WebView2/Office и WQ-PACK evidence только на
   post-cutover catalog.

Ближайший шаг не меняется: сначала завершается уже открытый Windows rebuild. Кодовая
реализация R61 начинается отдельно после согласования inventory; Phase 12 до этого
не начинается.
