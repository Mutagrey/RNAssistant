# R61/11O — audit границы model-facing tools

Дата фиксации: 2026-09-03. Статус: 11O1 Resources + Capabilities, 11O2 Plan
questions/doc/task-list, 11O3 HTML, 11O4 Prompt/Tool/Skill authoring, 11O5
VBA/macro и 11O6 final core-pack завершены host-neutral; UI ещё не выполнен.

Для Resources + Capabilities, planning, HTML, authoring и VBA/macro families этот
документ фиксирует реализованные контракты 11O1–11O6, включая финальный
mode/host core-pack. Для оставшегося UI-среза до его атомарного переключения
действует текущий канонический контракт. R61 не вводит второй executor, generic
router, pipelines, aliases или dual schema.

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
| Canonical revision-pinned `rna://` URI / `ResourceRef` | Resource Fabric | Только читаемое semantic описание цели и предметный результат; exact reference не попадает в model projection |
| Revision, content hash, etag, collection fingerprint | Provider/runtime | Не является аргументом или model-visible metadata; используется для fail-closed concurrency |
| Cursor, offset, page token, representation scope | Read implementation | Bounded result и при необходимости semantic `Next`, без opaque token |
| ToolCallId, run/chat/document IDs, UUID, confirmation и prepared guards | Kernel/ToolRuntime | Только runtime-generated `tool_call_id` виден как transport correlation; остальные значения скрыты |
| VBA backup identity | VBA journal/runtime | Читаемый candidate/время или выбор «последняя для этого module»; raw backup ID не вводится caller-ом |

Если runtime-owned state должен пережить model step, он сохраняется в уже
существующем typed event/result chain. Отдельный mutable side index или скрытая
execution authority не вводится.

Граница видимости строже границы аргументов. После cutover exact `ResourceRef`,
URI, revision/hash, cursor, guard, snapshot/package revision и внутренние IDs не
материализуются ни в schema, ни в `RUNTIME_CONTEXT`, ни в обычный Tool Result, ни в
replayed model history. Durable result/event сохраняет их для provenance, replay,
continuation и read-back, а model projection содержит только semantic target и
domain data. Нельзя замаскировать ту же обязанность новым opaque `candidateId`.
Допустимы только exact public tool/skill id как реальная semantic identity и
runtime-generated `tool_call_id` для сопоставления принятого вызова с результатом;
последний не является argument и никогда не создаётся моделью.
Native `assistant.tool_calls` replay не вводит второй function id: он сохраняет тот
же exact public tool id и только аргументы текущей схемы. Синтетические transport
aliases вроде `rna_*` и replay старых URI/cursor arguments запрещены; несовместимая
сохранённая история требует явного new chat/reset до следующего model request.

## 3. Текущая поверхность и почему имеющихся тестов недостаточно

После 11O6 Excel Agent core/bootstrap pack публикует 21 schema: четыре bootstrap,
пятнадцать Excel и два routine VBA editing intent — whole-source write и exact
patch. Rename, restore, delete и arbitrary macro остаются точными runnable ids, но
их schemas загружаются только по semantic need через capability admission.
Публичная resource-пара принимает только
`query`/semantic `scope` и readable `target`/`representation`. Find остаётся
fixed top-20, но unfiltered VBA browse закрепляет project target первым, а exact
bound runtime публикует тот же target напрямую. Read собирает bounded provider
pages в одну полную representation; provider routing, exact URI/revision и
continuation принадлежат runtime.

Существующие deterministic harness scenarios подтверждают guards и wiring, но
часть model scenarios заранее подставляет provider, URI и cursor scripted
delegate-ом. Это доказывает механическую исполнимость контракта, а не его
понятность реальной модели. R61 поэтому измеряет не только handler success, но и
число calls, argument/format repairs, tool errors, continuation restarts и итоговую
успешность задачи.

11O0 добавил, а 11O1–11O5 обновили machine-checked
[property inventory](R61_TOOL_PROPERTY_INVENTORY.tsv): 69 уникальных built-in ids
и 72 effective host-варианта фиксируют exact descriptor revision, host, mode,
direct binding и все рекурсивные schema property paths. Четыре host-specific
варианта принадлежат `common.html_data_bind`. Поле, похожее на
runtime plumbing, не может появиться без явного решения; допустимые public
capability/tool/skill/mail identities отмечены отдельно от полей, которые R61
internalizes/removes. Descriptor revision одновременно фиксирует description,
defaults и validation, не создавая второй schema source.

Произвольные argument schemas установленных custom packages нельзя честно
зафиксировать в source baseline: они package-owned и меняются только новой exact
package revision. Их поля не удаляются автоматически по имени (`customerId` может
быть domain identity). 11O4 требует явное `Domain identity rationale:` для
plumbing-shaped inputs как при upsert/Library validation, так и при загрузке ранее
установленного package; непроверенный package fail-closed не становится callable.
Финальная Library/WQ проверка остаётся UI/evidence gate, а не вторым schema path.

## 4. 11O0 baseline и current inventory `common.*`

Source inventory содержит до 31 built-in `common.*` ids. Число условное:
`common.html_data_bind` публикуется только при наличии допустимых Office data-source
tools, Tool/Skill/Prompt authoring зависит от доступности stores/settings, а VBA —
от host. Все 31 schemas одновременно модели не передаются:

- Chat получает две resource schemas;
- Plan начинает с четырёх bootstrap schemas — resources и capabilities;
- Excel Agent core содержит шесть `common.*` schemas: те же четыре bootstrap плюс
  `common.vba_write_module` и `common.vba_apply_patch`; вместе с 15 Excel schemas
  это 21 schema;
- Word/PowerPoint Agent core содержит четыре bootstrap и те же два VBA editing
  schema; Outlook Agent содержит только четыре bootstrap;
- остальные exact ids видны в compact capability catalog, а полный schema
  загружается только через `common.capabilities_read`.

Progressive loading уменьшает token cost, но не делает лишний tool полезным и не
исправляет сложный schema после загрузки. Таблица сохраняет 11O0 baseline и
default-направление R61; resource/capability rows реализованы в 11O1, planning
rows — в 11O2, HTML rows — в 11O3, authoring rows — в 11O4, VBA/macro rows — в
11O5; остальные ids действуют до своего cutover. `KEEP` означает сохранить самостоятельный model
intent, `ON-DEMAND` — не держать schema в default core, `MERGE`/`SPLIT` — сменить
public responsibility атомарно, `INTERNAL/UI` — убрать из model-facing catalog без
удаления функции.

Не всякое поле с именем `id` является transport leak. Public tool/skill id и id
создаваемого custom package — user-visible domain identity: модель выбирает его из
catalog или явно именует новый объект. ToolCallId, run/chat/document UUID,
artifact/revision/backup ids и prepared guards остаются runtime-owned. Существующий
public id также нельзя «вспоминать» приблизительно: он выбирается из exact result.

| Current tool | Нужен ли модели | Default R61 disposition |
|---|---|---|
| `common.resources_list` | Нужен сам intent «найти доступные ресурсы», но не provider discovery/paging | `DONE 11O1`: merged with search as `common.resources_find`; old id deleted |
| `common.resources_resolve` | Самостоятельного пользовательского действия обычно нет | `DONE 11O1`: internal exact preparation; public id deleted |
| `common.resources_search` | Нужен поиск по query/scope | `DONE 11O1`: merged as `common.resources_find`; old id deleted |
| `common.resources_read` | Нужен | `DONE 11O1 + whole-read correction`: semantic target/representation only; provider paging полностью internal; id retained |
| `common.capabilities_search` | Нужен bootstrap discovery | `DONE 11O1`: query/kind only; paging и limit внутренние |
| `common.capabilities_read` | Нужен для exact tool admission и skill loading | `DONE 11O1`: public tool/skill id и semantic reference path допустимы; offset/maxChars/revision/admission state внутренние |
| `common.questions_ask` | Нужен только Plan mode | `DONE 11O2`: prompt/options only; runtime генерирует UI-only question/option ids, model replay их не содержит |
| `common.plan_doc_save` | Нужен save active plan | `DONE 11O2`: create/update merged; модель передаёт complete title/Markdown/status, runtime связывает active id и exact guard |
| `common.plan_doc_restore` | Нужен только по явному запросу восстановить историю | `DONE 11O2`: модель выбирает readable version, runtime связывает exact source/current guard |
| `common.plan_doc_delete` | Нужен только по явному запросу удалить plan | `DONE 11O2`: empty arguments; active identity/revision и explicit-request guard внутренние |
| `common.task_list_set` | Нужен visible task state и terminal close | `DONE 11O2`: small typed save/close branches; runtime создаёт list ids и генерирует/сохраняет stable step ids |
| `common.html_workspace_write_file` | Whole-file authoring нужен | `DONE 11O3`: path/content only; file kind, preview selection, revision и preflight внутренние |
| `common.html_data_write` | Static JSON authoring нужен отдельно от file validation | `DONE 11O3`: name/json only; заменил data branch общего upsert |
| `common.html_workspace_apply_patch` | Exact source edit нужен | `DONE 11O3`: path + exact replace/replaceAll/insertBefore/insertAfter; regex/line variants удалены |
| `common.html_workspace_delete` | Нужен semantic delete | `DONE 11O3`: readable target определяет file/data; ambiguity fail-closed |
| `common.html_data_bind` | Live binding нужен | `DONE 11O3`: name/transform/headers only; runtime использует latest successful eligible accepted read того же run |
| `common.html_data_refresh` | Manual refresh иногда нужен | `DONE 11O3`: optional semantic name/all; policy внутренний |
| `common.html_data_freeze` | Отличимый intent: сохранить JSON и удалить binding | `DONE 11O3`: отдельный verified-write effect |
| retired `common.html_workspace_inspect`, `common.html_workspace_set_active`, `common.html_workspace_upsert` | Не нужны модели | `DONE 11O3`: удалены из catalog без aliases; preflight/selection остались internal UI/runtime |
| `common.prompts_read` | Нужен только при явном prompt/settings authoring | `DONE 11O4`: `ON-DEMAND KEEP` |
| `common.prompts_save` | Нужен только при явном authoring | `DONE 11O4`: один enumerated `promptKey` + complete value за call; девять optional полей удалены |
| `common.tools_definition_read` | Нужен для изменения exact custom tool | `DONE 11O4`: exact-id `ON-DEMAND KEEP`; list mode удалён как duplicate capabilities search |
| retired `common.tools_validate` | Перед upsert не нужен отдельный model step | `DONE 11O4`: удалён из model catalog; upsert и Library validation валидируют до write |
| `common.tools_upsert` | Нужен только для явного custom-tool authoring | `DONE 11O4`: id/mode/components/docs only; manifest и runtime владеют schema/metadata/conservative authority |
| `common.tools_delete` | Нужен по явному запросу | `DONE 11O4`: exact custom tool id semantic, confirmation сохранена |
| `common.skills_upsert/delete` | Skill core authoring нужен отдельно | `DONE 11O4`: exact core intents без reference branch |
| `common.skills_reference_upsert/delete` | One-reference authoring имеет отдельный scope | `DONE 11O4`: отдельные narrow intents; mixed anyOf удалён |
| `common.vba_restore_backup` | Нужен только для явного rollback | `DONE 11O5 + 11O6 ON-DEMAND KEEP`: exact readable target либо latest-for-module; raw backup id внутренний |
| `common.vba_write_module` | Whole-source write нужен | `DONE 11O5 + 11O6 CORE KEEP`: только module/full source и meaningful creation policy/type |
| `common.vba_rename_module` | Identity-preserving rename отличается от write | `DONE 11O5 SPLIT + 11O6 ON-DEMAND`: source/destination names only; guards и journal identity внутренние |
| `common.vba_apply_patch` | Exact minimal edit нужен | `DONE 11O5 + 11O6 CORE KEEP`: hunks `find/text`, constant `op=replace` и guards внутренние |
| `common.vba_delete_module` | Нужен только по явному запросу | `DONE 11O5 + 11O6 ON-DEMAND KEEP`: module name semantic, guard/backup lifecycle внутренние |
| `common.office_run_macro` | Нужен только когда пользователь просит execution | `DONE 11O5 + 11O6 ON-DEMAND KEEP`: high-risk самостоятельный effect; arguments semantic, runtime identity/evidence внутренние |

### `common.*` skill ids, которые не являются tools

11O0 source содержал девять built-in Common skills; после merge в 11O4 их восемь. Их compact ids участвуют в
capability selection, а загруженный Markdown напрямую управляет выбором tools и
arguments. Поэтому skill body является consumer tool contract и переключается в
том же atomic family slice; нельзя оставить инструкцию с удалённым id/аргументом.

| Current skill | Default R61 disposition |
|---|---|
| `common.task_tracking` | `KEEP`; переписать под объединённый active task-list intent, убрать model-owned list/step ids и resource URI |
| `common.text_search_replace` | `KEEP ON-DEMAND`; это guidance над host search/replace tools, не отдельный common tool |
| `common.vba_code_editing` | `DONE 11O5 KEEP`; semantic find/read, separate rename, patch без `op`, restore без raw backup id |
| `common.vba_userform_authoring` | `KEEP ON-DEMAND`; отдельная specialist responsibility оправдана, ссылки обновляются вместе с VBA family |
| `common.tool_authoring` | `DONE 11O4 KEEP`; единственный entry skill, включая VBA manifest/package rules |
| retired `common.vba_tool_authoring` | `DONE 11O4 MERGE` в `common.tool_authoring`; дублирующий skill удалён |
| `common.skill_authoring` | `DONE 11O4 KEEP`; учит четырём отдельным core/reference mutations без caller offset/maxChars |
| `common.prompt_authoring` | `DONE 11O4 KEEP ON-DEMAND`; учит one-key prompt save |
| `common.html_workspace_authoring` | `KEEP ON-DEMAND`; убрать обязательный inspect call, manual set-active, nested source-tool arguments и cursor choreography |

`common.text_search_replace` из широкого string search нельзя считать пропущенным
tool: он создаётся `BuiltInSkillProvider` как skill. Host-specific skills и custom
skills всё равно входят в последующий property/consumer inventory, но не смешиваются
с этим exact Common baseline.

Из исходных common tools `resources_resolve`, `html_workspace_inspect`,
`html_workspace_set_active` и `tools_validate` уже удалены из model-facing surface.
Resource list/search и Plan create/update объединены; смешанные skill/reference
branches разделены; VBA write/rename стали отдельными точными intents. Поэтому raw
registry count может уменьшиться незначительно или вырасти после оправданного split:
главный результат — меньший default callable pack и узкие schemas, а не искусственно
минимальное число названий.

11O5 намеренно сохранил временный инвариант «все Excel/VBA core schemas сразу» и
добавил отдельный rename schema. 11O6 сравнил его с on-demand admission: routine
Excel сохранил прямой двухшаговый read без schema load/repair, initial request
estimate уменьшился на 1 034 tokens, а explicit macro добавляет ровно один
capability-read step и сохраняет confirmation/unknown-effect contract. Поэтому
четыре explicit VBA intents удалены только из core membership, не из runnable
catalog или execution authority.

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

Resource read continuation выполняется только внутри bounded policy: public read
возвращает полную representation либо ошибку и не публикует модели `Next`. Для
других operation-specific paged UI/runtime reads допустимо semantic действие
`Next`, но opaque binding остаётся в typed result/event chain. При неоднозначной
цели runtime запрашивает semantic target и останавливается fail-closed, а не
угадывает URI. Для read-only drift policy может начать новое чтение и явно
сообщить об этом.
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

- 11O1 атомарно заменил четыре public ids на `common.resources_find/read`; aliases
  и второй schema path отсутствуют. Provider list/resolve/search/read сохранены
  только как внутренние операции gateway.
- Find принимает optional literal query и малый semantic scope, возвращает не более
  20 readable targets и различает true-empty, partial и unavailable. Query —
  фильтр, не inventory; unfiltered VBA browse всегда закрепляет project target
  первым.
- Exact bound VBA-capable document публикует readable
  `RUNTIME_CONTEXT.document.vba_project_target`, поэтому project-wide чтение не
  требует предварительного find. Read принимает этот runtime target или target из
  find и optional representation; внутренние pages/revision/cursor собираются в
  один полный model-facing результат либо явную ошибку. Exact `ResourceRef`
  остаётся только durable evidence.
- VBA discovery/read остаётся только через единый Resource Fabric. Вторые VBA read
  ids и host-prefixed aliases не возвращаются.

### Plan questions, document и Task List

- 11O2 сохраняет `common.questions_ask`, но модель задаёт только 1–3 уникальных
  prompt/options; runtime создаёт question/option IDs для текущей UI-паузы. Ответ
  пользователя возвращает semantic question text, selected labels и free text.
- Create/update Plan объединены в `common.plan_doc_save`; restore принимает только
  user-visible `version`, delete — пустой object. Exact active/source artifact,
  linear-head guard и tombstone остаются внутри `PlanDocumentService`.
- Create/update/close Task List объединены в `common.task_list_set` с отдельными
  typed `save`/`close` branches. Модель передаёт complete goal/steps/status или
  terminal outcome; runtime создаёт list id и сопоставляет stable step ids.
- Старые пять lifecycle ids удалены без aliases. Prompt schema 18, UI actions,
  task-tracking skill, accepted-history preflight и model Tool Result projection
  переключены атомарно; старые вызовы требуют explicit new chat/reset.

### HTML workspace и data binding

- 11O3 публикует семь Agent-only verified-write tools: отдельные
  `common.html_workspace_write_file` и `common.html_data_write`, exact patch,
  semantic delete, bind, refresh и freeze. Старые inspect/set-active/upsert ids
  удалены без aliases; UI selection и bounded static preflight остались internal.
- Patch принимает только path и exact replace/replaceAll/insertBefore/insertAfter.
  Delete принимает readable path/data name и fail-closed отвергает ambiguity.
- Bind принимает name и optional transform/headers. Runtime выбирает последний
  успешный eligible accepted Office read того же Agent run, проверяет exact
  call/result pair и полный result artifact; model не передаёт source tool,
  arguments, URI, cursor, revision или candidate id. Refresh повторно проверяет
  сохранённый exact source schema и принимает только optional semantic name.
- Durable workspace/result сохраняет revision, resource refs, binding source и
  guards. Model projection удаляет их. HTML switch ввёл prompt schema 19,
  whole-resource correction — 20, а authoring switch — текущую 21; history
  preflight требует explicit new chat/reset для старых calls.

### Prompt, Tool и Skill authoring

- `common.prompts_save` принимает ровно один `promptKey/value`; runtime связывает
  guard и сохраняет остальные prompt settings без model-owned multi-field merge.
- Tool authoring содержит exact read/upsert/delete. Отдельный
  `common.tools_validate` и list mode удалены; upsert всегда валидирует полный
  package до write, получает host/schema/metadata из manifest и применяет
  conservative confirmation/effect authority.
- Dynamic package с plumbing-shaped argument становится callable только при
  явном `Domain identity rationale:` в schema description. То же правило действует
  при загрузке уже установленного package, поэтому прямой file install не обходит
  проверку.
- Skill core и reference mutations разделены на четыре exact ids. Built-in
  consumers переключены вместе; `common.vba_tool_authoring` слит в
  `common.tool_authoring`.
- Model result/replay сохраняют semantic package id/reference path, но удаляют
  revision/hash/storage evidence. Старые multi-field/mixed/retired calls требуют
  explicit new chat/reset.

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
    Все восемь текущих built-in Common skill bodies проверяются на retired ids/arguments и
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
14. Fully materialized request, Tool Results и replayed history после каждого
    cutover не содержат `ResourceRef`, `rna://`, revision/hash, cursor/offset,
    guards или internal IDs. Harness создаёт exact resource/capability state только
    за runtime boundary и не подставляет его scripted model delegate-ом. Отдельно
    разрешены и проверяются только public tool/skill id и runtime `tool_call_id`.

## 9. Порядок исполнения и gate

1. Source-built-in часть property-level inventory завершена в 11O0: все effective
   Common/host variants проверяются по exact revision/mode/host/binding/property
   paths, а исходные девять Common skill consumers перечислены выше. Dynamic
   installed-package review выполнен в 11O4; callable-pack comparison остаётся
   отдельным core-pack gate и не подменяется source snapshot.
2. Resources + Capabilities завершены host-neutral в 11O1: minimal schemas,
   runtime-owned resolution/continuation/revision validation, durable exact
   evidence и semantic model projection переключены атомарно; старые resource ids
   и handlers удалены без aliases.
3. Plan questions/doc/task-list завершены host-neutral в 11O2: semantic schemas,
   runtime-owned identities/guards, model projection, prompt/skill/UI consumers и
   удаление пяти старых lifecycle ids переключены атомарно.
4. HTML workspace/data binding завершён host-neutral в 11O3: семь semantic intents,
   accepted-read binding, automatic preflight и удаление model-facing diagnostics/
   UI selection paths переключены атомарно.
5. **Done host-neutral 11O4:** Prompt, Tool и Skill authoring переключены вместе с
   internal validation, installed-package review и conservative authority.
6. **Done host-neutral 11O5:** VBA/macro family переключена на шесть exact intents;
   rename отделён от write, patch operation и backup identity принадлежат runtime,
   incompatible retained calls требуют reset/new chat.
7. **Done host-neutral 11O6:** минимальный mode/host core пересчитан по
   deterministic eval evidence; optional exact schemas остаются доступны через
   capability admission, policy/binding/effect не меняются.
8. **Done host-neutral 11O7:** UI-only built-in documentation загружается по exact
   id/revision, typed Library Test строится из effective schema, semantic `Next`
   хранит continuation только в bounded isolated runtime session, а
   Implementation/Test layout остаётся внутри right pane.
9. Собрать final live-provider, Windows WebView2/Office и WQ-PACK evidence только на
   post-cutover catalog.

Ближайший шаг — final post-cutover Windows rebuild, live-provider, WQ-PACK и
WebView2/Office qualification, включая 11O7 narrow-pane/continuation UX и
накопленные R62/R63 retests. Phase 12 до этой qualification не начинается.
