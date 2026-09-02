# R61/11O — audit границы model-facing tools

Дата фиксации: 2026-09-02. Статус: обязательный docs-only вход для
отдельного R61 после текущего Windows rebuild и до финального Milestone WQ.

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

## 4. Целевая граница

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

## 5. Когда объединять и когда разделять tools

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

## 6. Решения по семействам для отдельной реализации

### Resources

- До cutover сохраняются четыре текущих id; R61 отдельно решает их конечную
  публичную форму и удаляет заменённые аргументы атомарно, без aliases.
- `list` должен перечислять semantic resources. Provider discovery не должно
  маскироваться успешным пустым `items`; истинно пустой результат отличается от
  discovery/unavailable/access/filter mismatch.
- Provider routing становится внутренним. Если caller действительно выбирает
  категорию, это небольшой стабильный enum предметной области, а не свободная
  строка provider-specific `kind`.
- `search` получает query и semantic scope. `read` принимает выбранный candidate
  или semantic target; URI/revision/cursor/page size связывает runtime, а результат
  всё равно содержит exact `ResourceRef` как evidence.
- `resolve` остаётся public только если аудит докажет самостоятельное semantic
  действие. Если это лишь переход между list/search и read, он становится
  внутренней подготовкой.
- VBA discovery/read остаётся только через единый Resource Fabric. Вторые VBA read
  ids и host-prefixed aliases не возвращаются.

### VBA mutations

- Whole-source write и exact-hunk patch остаются двумя явными вариантами модели.
  Whole-source write не запрещается только потому, что patch безопаснее для малой
  правки.
- Patch остаётся exact и fail-closed. Runtime сам читает live state и строит guard;
  stale source не вызывает fuzzy apply, automatic rebase или fallback в write.
- Константный `op: "replace"` у patch является кандидатом на удаление из intent
  schema. Ветки write/upsert и rename проходят split-or-keep аудит, потому что у них
  разные target/effect и обязательные поля.
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

## 7. Обязательные eval scenarios

R61 не закрывается только schema snapshot-тестами. Минимальный набор включает:

1. Модель находит два названных VBA modules, читает их и добавляет тест, ни разу не
   вводя provider vocabulary, URI, revision или cursor.
2. Resource list не возвращает `items: []` только потому, что сначала требовалось
   выбрать provider; настоящий пустой project обозначен отдельно.
3. Большой VBA module читается полностью без переноса cross-resource continuation
   state в model arguments.
4. Реальное изменение source через VBE между read и mutation обнаруживается; нет
   silent rebase, retry или whole-write fallback.
5. Exact patch остаётся fail-closed, а whole-source write остаётся доступным
   осознанным выбором модели.
6. `resource_kind_unknown`, cross-scope `resource_cursor_invalid` и caller-induced
   revision mismatch становятся структурно невозможны в model schema; внутренние
   guards продолжают иметь прямые regression tests.
7. Для каждого before/after scenario сравниваются task success, число model/tool
   calls, repairs, tool errors, latency и effect evidence.

## 8. Порядок исполнения и gate

1. Зафиксировать exact inventory всех effective schemas по mode/host и owner каждого
   аргумента.
2. Переключить Resource Fabric intent/preparation boundary и удалить старые public
   plumbing arguments.
3. Переключить VBA/macro family, сохранив exact mutation safety и выбор patch/write.
4. Проверить и переключить остальные families по одному responsibility slice.
5. Добавить UI-only built-in documentation, typed Library Test и исправить
   Implementation/Test layout.
6. Собрать final live-provider, Windows WebView2/Office и WQ-PACK evidence только на
   post-cutover catalog.

Ближайший шаг не меняется: сначала завершается уже открытый Windows rebuild. Кодовая
реализация R61 начинается отдельно после согласования inventory; Phase 12 до этого
не начинается.
