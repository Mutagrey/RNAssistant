> Исходное предложение пользователя, сохранено 2026-10-22. Ниже текст вложения
> сохранён без изменений; это не новый нормативный контракт и не повторный cutover.
> Owner: model-context/LLM diagnostics. Текущий scope — только Wave 0.
> Решения accept/adapt/reject и следующий ограниченный этап — в
> [аудите](../../audits/resource-context-current-state.md).
> При расхождении применять действующие canonical docs и решения аудита.
> Документ остаётся историей предложения после закрытия или отклонения его этапов;
> он не становится вторым backlog. Статус и риски принадлежат существующим реестрам.

---

# RNAssistant — Resource Fabric, Context Compiler и Context Explorer
**План аудита и внедрения · 21 октября 2026**

Статус: проект для проверки агентом, не новый нормативный ADR. Проверенный ориентир: `Mutagrey/RNAssistant`, `f6c3f5f01ec314695ad60264847f578e855342d7`. Перед началом сверить HEAD.

## 0. Инструкция агенту

1. Прочитать `docs/resource-fabric.md`, `docs/architecture.md`, `docs/stabilization/resource-cutover/MASTER.md`, нормативные URF / Authority / Evidence-Context specs, ADR, migration map и тесты.
2. Сохранить этот документ в принятом каталоге планов, включить в индекс. Принятые решения отражать в существующих canonical docs/ADR, не создавать параллельную архитектуру.
3. **Сначала аудит, потом код.** Подготовить `docs/audits/resource-context-current-state.md`: трассировка вызовов, владельцы, дубли, воспроизводимые баги, фактические request payload, список решений `accept/adapt/reject` с обоснованием.
4. Реализовывать небольшими этапами, с регрессионными тестами. Не заявлять Windows/Office/WebView2 qualification без реального прогона.
5. Не создавать второй CAS, AuthorityStore, ContextCompiler, resource registry, graph DB или независимый источник currentness.

## 1. Цели и инварианты

Цели: устранить классы ошибок неполного чтения, устаревших источников, непонятной материализации, компакции и бюджетирования; сделать контекст наблюдаемым; упростить архитектуру без потери гарантий.

Вне scope: новый LLM response protocol (`understand/execute/finish`), векторная БД, глобальная переделка tools.

**Инварианты:**
- `ResourceIdentity != RevisionId != ContentHash != PayloadRef`.
- `locator != logical identity`, `conversation != authority scope`.
- `stored != available != current != delivered != complete != verified`.
- Историческая ревизия не становится текущей от совпадения bytes.
- Неизвестный эффект write нельзя автоматически повторять.
- Один Gateway, AuthorityStore, EvidenceStateReducer, ContextCompiler и model request assembly.
- Compiler не делает live Excel/VBA/provider I/O.
- Request строится из согласованного frozen authority/catalog/event snapshot.
- Compaction не изменяет immutable event log и CAS.

## 2. Владельцы и границы

| Компонент | Владеет | Не владеет |
|---|---|---|
| Domain provider | Чтение/запись, формат, readback, recovery | Выбором размера LLM контекста |
| Resource Gateway | Find, search, read, views/selectors | Current head |
| CAS | Immutable payload bytes, integrity | Семантической ревизией |
| Authority | Heads, commits, generations, drift, unknown | Промптом |
| Evidence | Exact observation, coverage, provenance | Выполнением задачи |
| Context Compiler | Correctness, selection, hydration, budget, serialization | Live I/O |
| Context Receipt | Фактически доставленные части | Отдельной истиной о ресурсах |
| Context Explorer | Read-only диагностика | Мутациями |

Трасса: `provider → gateway → observation/CAS → authority/evidence → frozen snapshot → compiler → receipt → LLM`. Проверить ~1200-строчный `ModelContextCompiler.cs`: выделять лишь чистые политики, если это уменьшает дублирование; не создавать второй compiler.

## 3. Короткие ссылки

Канонические IDs остаются opaque. Короткие alias — **только представление**, разрешаемое через immutable mapping со scope/snapshot. Не переназначать `R1` после компакции или перезапуска. Persisted links используют canonical IDs.

| Alias | Сущность |
|---|---|
| `R1@v3` | Resource + exact revision |
| `E12` | Exact observation/evidence |
| `O8` | Tool invocation/operation |
| `Q14` | Model request/context snapshot |
| `T1` | Task |
| `S3` | Step |
| `C2` | Acceptance criterion |
| `D4` | Decision/alternative, только при наличии owner |
| `M5` | Memory/claim, только при наличии owner |
| `A2` | Artifact, связанный с R |
| `P9` | CAS payload, только диагностика |

Это **не новые классы**. Сначала поддержать реально существующие `R/E/O/Q`. `S` означает step, не skill; skills: `skill:<id>`. `P` не предоставляет право чтения/изменения ресурса.

Примеры UI-адресации, **не API**:
```text
R1@v3
R1@v3#lines=40-90
R2@v1#pages=1-3
R3@v2#procedure=getData
R4@v5#range=Data!A1:D100
R5@v2#json=/items/3/name
R6@v1#crop=10,20,200,100
```
Внутри применять типизированные selectors и actual coverage; `v3` — локальный ordinal, не hash. Экранировать имена и проверять однозначность VBA procedure.

Граф **производный из событий**:
```text
O8 --reads--> R1@v3 --observed_as--> E12
E12 --covers--> R1@v3#lines=40-90
O9 --mutates--> R1@v3 --produces--> R1@v4
Q14 --delivered--> E12
C2 --verified_by--> E18
R7 --derived_from--> R2@v1
```

## 4. Универсальные views и selectors

| Тип | Views | Selectors и риски |
|---|---|---|
| VBA | metadata, outline, source, procedure, diff | component/procedure/lines; VBIDE normalization, CRLF, ambiguous anchors |
| Text/MD | metadata, outline, source, diff | lines, chars, bytes, heading; длинные строки |
| JSON/YAML | metadata, tree, source | JSON Pointer, depth, array slice |
| Excel | metadata, sheet-outline, range, formulas, chart | sheet/range/table/chart; recalculation |
| PDF | metadata, outline, text, page-image, table, figure | pages/block/bbox; OCR and visual provenance |
| Word | metadata, outline, text, table, image | section/paragraph/table; embedded objects |
| Image | metadata, image, crop, description | rectangle/frame; caption != original |
| HTML/JS/CSS | metadata, outline, source, diff | function/selector/lines |
| PST/email future | headers, body, attachment | message/MIME part; privacy |

View не обязательно новая source revision. OCR/summary/embedding получают provenance (source revision, extractor/version, parameters), а при изменении источника требуют проверки актуальности. Unsupported selector возвращает typed error и варианты.

## 5. Единый read observation и model delivery

Адаптировать существующие `ResourceEvidence/ResourceCoverage/ResourceRef`, не создавать вторую модель:
```json
{
  "resource":"opaque-id", "revision":"opaque-revision",
  "view":"procedure",
  "requestedSelector":{"kind":"procedure","name":"getData"},
  "actualCoverage":{"kind":"procedure","name":"getData"},
  "selectionComplete":true, "resourceComplete":false,
  "payloadRef":"opaque", "evidenceId":"opaque",
  "estimatedTokens":1100
}
```
`requestedSelector` не равен `actualCoverage`; полная процедура не означает полный модуль. `delivery` — свойство **конкретного request**, а не чтения:
```json
{
  "ref":"R1@v3","view":"source","delivery":"metadata_only",
  "coverage":"none","reason":"budget",
  "availableViews":["outline","procedure","source"]
}
```
Либо `delivery=body`, exact coverage, `complete=false`, evidence ref. Metadata-only никогда не маркировать как полное прочтение.

**Adaptive read:** `find` обнаруживает, `search` ищет, `read` возвращает view. Существующий `outline` сохранять до анализа совместимости, затем оценить `read(view=outline)`. Показывать estimated tokens, total pages/lines, views. Если whole view нужен и помещается — читать целиком; иначе outline/search/semantic slice/page range с явным покрытием. Разделить source-capture limits, provider-read limits, hydration limits и model token budget. Не повышать текущие 16k-char/1-MiB границы без bounded-memory тестов. Длинные одиночные строки требуют char/byte selectors. VBA: exact revision, сохранение line endings, уникальные anchors, post-write readback. PDF: отдельные coverage для текста, таблиц и изображений.

## 6. Context Compiler и компакция

Сверить с нормативным MASTER:
```text
freeze authority/catalog/event high-water
→ replay durable facts
→ ContextAtoms + causal tool frames
→ correctness/currentness filter
→ fold terminal mutation frames + dedup
→ relevance selection
→ selective CAS hydration
→ structured compaction
→ budget/admission
→ serialize request
→ immutable ContextReceipt
```
Разделить (1) durable event log/CAS, (2) working task memory с source refs, (3) временную budgeted model projection. Дедуплицировать повторные чтения **в контексте**, не удаляя события. Не разрывать native tool-call/result pairs. Фактический порядок model messages должен сохраняться в receipt. Старые координаты строк действительны только для старой ревизии.

## 7. Context Receipt — что модель реально получила

Сохранять manifest **после фактической сериализации**:
```json
{
 "requestRef":"Q14","authoritySnapshot":"opaque",
 "messages":[
  {"ordinal":0,"role":"system","kind":"core","estimatedTokens":800},
  {"ordinal":1,"role":"tool","operationRef":"O8","evidenceRef":"E12",
   "resourceRef":"R1@v3","delivery":"excerpt",
   "coverage":{"kind":"lines","start":40,"end":90},"estimatedTokens":1100}
 ],
 "omissions":[{"resourceRef":"R2@v1","reason":"budget","recoverable":true}]
}
```
Покрывать actual text, roles, order, truncation, image/PDF attachments. Не дублировать Base64 и секреты в диагностике: hash, byte length, redacted preview, защищённый доступ к retained payload. Estimated tokens отдельно от измеренных. Golden tests сверяют receipt с serialized request.

## 8. Context Explorer

**Timeline:** реальный порядок сообщений, role, operation, resource/evidence refs, token cost, body vs metadata, omitted reasons, frozen generations.

**Resource Inspector:** canonical ID/alias, version/head, kind, views, requested/actual coverage, completeness, CAS/current/delivery status, provenance, сравнение revisions, diff, controlled rematerialization.

**Graph:** task → step → operation → evidence → resource revision → request, derived views и verification links. Граф строится из уже существующих записей, read-only. Поиск по `R/E/O/Q`, «почему модель не видела источник?», budget breakdown, redacted diagnostic export. UI не обходит permissions.

## 9. Упрощение без регрессий

Проверить и убрать подтверждённые: дубли currentness, обходные request builders, лишние legacy adapters, повторную hydration, конвертации tool-result → resource → model-result, фиксированные excerpts там, где whole view безопасен, несовместимые coverage Office/workspace, prompt patches вместо явных статусов. **Не удалять** Authority generations, leases, mutation guards, unknown-after-dispatch, CAS integrity, frozen snapshots и causal frames.

## 10. Acceptance tests

- Whole small VBA реально доставлен модели.
- `lines 40–90/240` явно partial; никакого whole claim.
- После mutation old evidence stale/unknown, unsafe write blocked.
- Restore прежних bytes создаёт новую revision.
- Long one-line source: char/byte slice либо явная typed error.
- VBA CRLF/Unicode: стабильные offsets и round-trip.
- Repeated reads: события сохранены, большие тела не дублируются.
- Compaction/restart: refs восстанавливаются, historical != current.
- PDF 120 pages: pages 1–3 не считаются whole; image/table provenance.
- CAS missing/corrupt: typed error, не live historical fallback.
- Concurrent chats: shared authority invalidation.
- Unknown write effect: no blind retry.
- Too-small budget: explicit omission/error.
- Native tool calls/results сохраняют причинность.
- ContextReceipt совпадает с фактическим HTTP payload.
- Explorer не раскрывает sensitive payload без прав.

Тесты: unit/integration/golden request snapshots/property-based/bounded-memory. Windows/Office/VBE/WebView2 — отдельный gate.

## 11. План внедрения

**Wave 0 — Audit:** call graph `find→read→CAS→evidence→compaction→compiler→HTTP→next model step`; ownership map, baseline metrics, доказанные дефекты, decisions register. Без рефакторинга.

**Wave 1 — Observability:** точный `Q` request snapshot и receipt, golden tests, реальные omission reasons.

**Wave 2 — Coverage/visibility:** нормализовать недостающие `requested/actual coverage`, `selection/resource completeness`, `delivery/omission` в существующих типах.

**Wave 3 — Adaptive materialization:** metadata стоимости, безопасное whole read, typed selectors, длинные строки, PDF/image views, bounded tests.

**Wave 4 — Compiler cleanup:** убрать дубли и обходы, выделить чистые политики без второго компилятора; тесты causal ordering, archive, restart, budget.

**Wave 5 — Context Explorer MVP:** timeline + resource inspector + receipt; затем graph, diff, redacted export.

**Wave 6 — Qualification:** регрессии, большие файлы, реальный Windows/Office/WebView2 прогон, фиксация ограничений.

После каждой волны: изменения, тесты, регрессии, открытые риски, обновление документации, следующий минимальный шаг.

## 12. Критерии завершения проекта

1. Можно доказать для любого `Q` точный состав фактического запроса.
2. Нельзя спутать `stored/current/delivered/complete`.
3. Старые и частичные источники не дают ложных прав на мутацию.
4. Большой ресурс читается whole либо по явным bounded views без неявной потери покрытия.
5. Context Explorer показывает фактические refs, coverage, omissions, versions.
6. Не добавлены дублирующие stores/compilers/authority owners.
7. Пройдены соответствующие regression suites; непроверенные host gates явно открыты.
8. Документация, ADR и migration map соответствуют коду.

**Первый шаг агенту:** выполнить только Wave 0, представить таблицу `accept/adapt/reject` и минимальный план Wave 1; не начинать широкий рефакторинг до проверки текущего устройства.
