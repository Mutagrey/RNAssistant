# R32 — Сквозная диагностика и общий JSON viewer

Статус: требования пользователя от 2026-08-28. **9A, 9B1 и полный read-only
consumer inventory через Markdown JSON 9B2B4, R36 vendor gate и bounded tree switch
9B3 реализованы host-neutral 2026-08-29; 9B4 gate закрыт без Diff2Html, потому что
authoritative unified diff отсутствует; 9C journal UI реализован host-neutral,
Windows/WebView и полный scenario acceptance открыты.** [Evidence 9C](PHASE_9C_RUN_JOURNAL_UI.md). Baseline source review —
`85cc3f4`; перенос документации — поверх `b754443`. Реализация и qualification —
[Phase 9](STABILIZATION_MASTER_PLAN.md#phase-9--persistence-и-ui-projection), до release gate Phase 12.

## Что требуется исправить

На baseline до Phase 9 `app-trajectory.js` разносил raw events, model/tool projections
и payload по отдельным представлениям, а diagnostics/Agent использовали разные
`prettyJson`/`textContent`/copy paths. Эти paths уже заменены в 9B2A/9B2B1;
хронологический journal UI подключён в 9C. Исходное замечание подтверждает проблему
удобства диагностики, но само по себе не доказывает потерю событий.

Цель: один понятный журнал выбранного пользовательского запуска с раскрываемыми
строками и один переиспользуемый read-only JSON viewer во всех местах просмотра JSON.
Наличие trace не означает, что live stream, DOM delivery или effect уже проверены.

## Журнал запуска

- Из сообщения/ошибки чата одно действие открывает соответствующий запуск, без
  ручного ввода идентификаторов и переключения нескольких diagnostic views.
- Вверху — запрос пользователя, lifecycle, execution health, подтверждённые эффекты,
  failures/unknown effects и ожидание пользователя. Формулировки строятся из typed
  evidence; финальный текст модели не доказывает успешное применение.
- Основной вид — последовательные строки от запроса к результату. Строка показывает
  этап, понятное описание, состояние, время/длительность при наличии данных.
  Одно раскрытие показывает детали на месте; технические IDs и сырые события — глубже.
- Цепочка включает сохранённый materialized request, отдельные model attempts,
  raw response, verdict/rejection и причину repair, accepted calls, confirmation,
  dispatch, result, effect/read-back evidence и UI projection. Отсутствующий этап
  показывается как «нет данных»; отсутствие события нельзя объявлять успехом.
- Attempts одного шага группируются рядом: отклонённый ответ не выглядит выполненным,
  последующая успешная попытка не скрывает предыдущую ошибку. Видны исходный ответ,
  ответ после repair и фактически переданные executor arguments; они не склеиваются.
- Вызов, результат и mutation/resource revision связаны по сохранённым IDs/origin,
  не по имени tool, близости timestamps или догадке UI. Independent read batch
  сохраняет отдельные calls и фактический порядок событий, без ложной последовательности.
- «Модель предложила», «ожидает подтверждения», «не отправлен на выполнение»,
  «вызов выполнен», «изменение подтверждено», «без изменений» и «эффект неизвестен»
  различаются. Уровень verification и источник evidence доступны в деталях.
- Ошибка содержит слой/этап, код и исходный текст, сведения о возможном dispatch/effect
  и переход к связанным request/response/result. Raw provider error не заменяется
  выдуманным объяснением. Цвет дополняет текст/значок, а не заменяет их.
- Фильтры ошибок, model calls и tools сохраняют доступ к причинному контексту.
  Обновление не сбрасывает раскрытые строки и позицию чтения; автопрокрутка отключается,
  когда пользователь читает прошлые события. Поздний ответ другого чата игнорируется.
- Одна команда «Экспортировать этот запуск» использует существующий bounded export,
  включая связанные source events в пределах выбранных лимитов. Сохраняются режимы
  redaction и явное согласие на полный payload; неполный экспорт явно помечен.

## Общий JSON viewer

Размещение — `web/js/app-json-viewer.js`, allowlisted `app-viewer-registry.js` и
тематический CSS, без нового проекта, npm/bundler, сетевой зависимости или
отдельного storage. Это UI-компонент,
не owner diagnostic queries, protocol parsing, tool execution или clipboard policy.

| Возможность | Обязательное поведение |
|---|---|
| Дерево | Objects/arrays раскрываются по узлам; видны тип, ключ/индекс и число элементов. «Свернуть всё» и ограниченное раскрытие не создают весь большой DOM сразу |
| Представления | «Дерево», «Форматированный JSON», «Исходный текст»; исходник доступен независимо от успешного разбора |
| Подсветка | Разные цвета ключей, strings, numbers, booleans/null; читаемость в обеих темах, focus и keyboard navigation, `aria-expanded` |
| Копирование | Копировать весь исходник, отдельный JSON-узел и путь; для string отдельно «Копировать текст значения», без JSON-кавычек. Ошибка clipboard видна пользователю |
| Большие strings | Длинные HTML/код/текст раскрываются отдельно; preview не выдаётся за полное значение. Текст значения декодируется ровно на один JSON-уровень, без повторного unescape |
| Невалидный JSON | Исходный текст + позиция ошибки, если доступна; никаких auto-repair, вырезания хвоста или молчаливого превращения обрывка в полный object |
| Неполные данные | Раздельные состояния: ещё не загружено, loading, полный payload, ограниченный preview, redacted, недоступен/повреждён. Метки не дописываются внутрь JSON |

Компонент получает исходный текст, метаданные полноты/размера/источника и
callbacks владельца экрана. Fetch, авторизация, bounded CAS read/export и обработка
stale response остаются у владельца; viewer не обращается к bridge сам.

**Точность:** `JSON.parse` → `JSON.stringify` не является lossless представлением
исходника: большие числа могут округлиться, duplicate keys — потеряться. Raw text
неизменяем; tree/pretty/node copy должны сохранять исходные tokens/значения, включая
числа вне safe integer, порядок и повторяющиеся ключи. Если lossless tree невозможен,
явный raw fallback, без выдачи изменённого значения за оригинал. Для duplicate keys
показывается неоднозначность пути; выбор узла определяется исходным вхождением.

Rendered данные всегда текст, включая HTML/script и JSON keys: никакого выполнения
или небезопасного `innerHTML`. Вложенный JSON в string не разбирается автоматически.
Секреты, скрытые владельцем экрана, не появляются через node copy/raw toggle; полное
содержимое остаётся отдельным явно выбранным действием в рамках действующих прав.

Большой JSON раскрывается порциями с ограничением parsing/DOM/depth и возможностью
отмены. Лимиты должны быть заданы и проверены до switch; «развернуть всё» их не обходит.
Границы preview/полного payload не снимаются ради красивого дерева. Для truncated
preview нельзя обещать полное копирование: предлагаются явно названное копирование
preview и разрешённое получение/экспорт оригинала. Полнота определяется metadata,
а не тем, удалось ли разобрать текст как JSON.

## Готовый vendor или собственный компонент

Итог одинаковой source/package оценки shortlist зафиксирован в
[R32 vendor/UI evaluation](R32_VENDOR_UI_EVALUATION.md). `andypf/json-viewer` и
`summerstyle/jsonTreeViewer` не проходят authoritative diagnostics gate: оба
преобразуют данные через обычный JavaScript object/`JSON.parse`, поэтому не сохраняют
raw fidelity duplicate keys и больших чисел; кроме того, у них нет полного bounded
render/copy/accessibility contract. В 9B1 общий `JsonAdapter` реализован с исходным текстом,
bounded token model и собственным компактным renderer, пока иной vendor не докажет
тот же контракт без большого fork.

9B3 проверил Web Awesome Tree для отдельного tree-navigation spike: официальный ESM
graph не загрузился из текущего `file://` host, поэтому vendor отложен до отдельного
virtual-host milestone. Вместо custom bundle принят Wunderbaum 0.14.1 через bounded
local-array `TreeAdapter` для одного HTML workspace/artifact consumer. Он не является
JSON renderer или timeline. R36 manifest атомарно расширен assets/license/hashes;
следующие Project/VBA/tools consumers автоматически не переключаются.

9B4 проверил единственные VBA diff consumers и typed bridge DTO: они передают exact
before/after source, но не source-owned unified diff. Diff2Html не подключён, потому
что это потребовало бы второго diff algorithm либо выдало бы UI projection за
evidence. Existing bounded formatter сохранён; решение зафиксировано в
[R39](R39_DIFF_VENDOR_GATE.md) и не блокирует 9C.

Vendor поставляется локально с pinned version/commit и hash, лицензией в
`web/vendor-notices.md`; без CDN, telemetry и автоматической загрузки URL из данных.
Network/HTML/link features отключаются или кандидат отклоняется. Source raw/copy,
полнота/redaction и доступ к payload остаются под контролем нашего adapter/owner.
9B3 добавил только pinned Wunderbaum для одного bounded tree consumer; остальные
кандидаты shortlist не подключены. Каждый следующий vendor требует отдельного gate.

## Повторное использование и удаление дубликатов

| Существующий consumer | Switch на общий viewer |
|---|---|
| `app-trajectory.js` / diagnostics | **9B2A switched:** exact event/row `DataJson`, separate source evidence и JSON CAS payload используют общий viewer; non-JSON CAS остаётся inert text. VBA before/after diff не является JSON |
| `app-agent-data.js` | **9B2B1 switched:** tool arguments/results используют один lazy lossless viewer; прежний generic object/table/list/pretty renderer удалён. Chart card остаётся отдельным domain renderer со своим parser owner |
| `app-context-inspector.js`, `app-context.js` | **9B2B2 switched:** exact materialized request сохраняет raw/preview completeness; context state явно остаётся сериализованной UI projection. Оба viewer lifecycle bounded/lazy |
| `app-tools-actions.js` и tool result panels | **9B2B2 switched:** manual-run/package structured result использует viewer, loading/error остаются inert text; editable arguments/schema и execution semantics не менялись |
| `app-html-workspace-artifacts.js`, `app-vba-project.js` | VBA metadata **switched 9B2B2** как UI projection; artifact exact inline/metadata JSON **switched 9B2B3** с `InlineTruncated → preview`, typed MIME/kind и explicit non-JSON text fallback. HTML preview/editor не менялся |
| JSON code blocks сообщений (`app-markdown.js`) | **9B2B4 switched:** закрытые top-level fenced `json` blocks завершённых message/Agent diagnostic renderers используют lazy viewer post-sanitize с exact source match/copy; live/unclosed/mismatched blocks остаются обычным code |
| JSON editors/settings | Редактор и сохранение не заменяются read-only деревом; если есть preview, он использует общий viewer |

При switch составить полный inventory видимых JSON surfaces и удалить заменённые
pretty/copy/render paths в том же подэтапе. Сериализация транспорта, сравнения объектов,
обычный текст логов и editable inputs не являются дубликатами JSON viewer.

## Архитектурные границы и порядок

1. **9A — truth/query (done host-neutral):** existing `ITrajectoryQuery` строит временную correlated
   projection над проверенным stream. Сохраняются все `sourceEventIds/Seqs`,
   `AcceptedCallOrigin`, точные ResourceRef и ссылки на domain journals. Не вводятся
   второй журнал, durable UI index, отдельный model transport или replay tools.
   Порядок определяется sequence; snapshot/cursor не теряют строки между страницами
   и при новых append. Новый хронологический вид не меняет контракт existing raw query.
2. **9B — viewer (9B1 + 9B2 read-only inventory + R36 + 9B3 + 9B4 gates done host-neutral):** общий компонент и migration read-only consumers; targeted UI
   tests и локальная чистка. Diff2Html не admitted без source-owned unified diff;
   Windows qualification остаётся отдельным gate.
3. **9C — журнал (UI done host-neutral):** раскрываемые строки и direct navigation
   подключены поверх `run-causal`; raw/специализированные views сохранены как детали,
   JSON lifecycle остаётся у shared viewer. Qualification всей цепочки,
   reload/confirmation и actual WebView на Windows открыты, без inference из текста модели.

Если trace недостаточно, UI показывает пробел. Новые необходимые evidence events
добавляются у владельца соответствующей границы с typed contracts и тестом, а не
выдумываются projection. `ui.projected` не доказывает actual DOM delivery. R28 проверяется
отдельно по SSE → projection → bridge → WebView; красивый журнал не закрывает streaming.

## Acceptance gate

- [ ] Из ошибки чата открыть запуск и на одном экране раскрыть request → rejected
  attempt → repair/accepted call → executor arguments → result/effect, без ручных IDs.
- [ ] Проверить read batch, confirmation/reload, provider error, protocol exhaustion,
  no-op, unknown effect, cancelled-before-dispatch и result-append failure после write.
  Ни один случай не превращается в «применено» по финальному тексту/одному `ok`.
- [ ] Для длинного HTML различимы исходный ответ, фактические arguments, сохранённая
  revision и preview limits; копирование полного доступного значения сохраняет его
  точно. Синтетический тест не объявляется воспроизведением исходного R29 incident.
- [ ] JSON tests: вложенные arrays/objects, пустые/scalar значения, Unicode/CRLF,
  literal escapes, большие числа, duplicate keys, malicious HTML/keys, invalid и
  truncated JSON, clipboard failure, redacted input, большая ширина/глубина и bounds.
- [ ] Query/UI tests: пагинация без потерь/дублей, source evidence, live append,
  смена чата при pending load, reset/reload, сохранение раскрытия/focus/scroll.
- [x] Все read-only JSON consumers из inventory используют один компонент host-neutral;
  преобразования raw/pretty/copy и бывшие renderers не остаются параллельными paths.
- [ ] Targeted tests в существующих suites; отдельный тест компонента оправдан его
  самостоятельным поведением. Windows x64 + Office + VS 2022 / реальный WebView
  обязательны для UI delivery, clipboard и responsiveness; здесь не выполняются.

Canonical query/export contracts: [trajectory-query](../trajectory-query.md),
[trajectory-export](../trajectory-export.md), [session-events](../session-events.md).
