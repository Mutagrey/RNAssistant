# Дизайн-система и компоненты RNAssistant

Межагентное обновление HTML использует существующие stale-source/recovery состояния
редактора и обычную кнопку обновления исходников; новых компонентов и геометрии нет.

## Редакторы в Dockview — In Review, 2026-11-03

Instance CodeEditor используется для live HTML и ограниченного просмотра исходников.
CodeMirror заполняет доступную область без внешних рамок, скругления и дополнительных
отступов; прежние bounds/history/source-mode сохраняются. Название выбранного HTML-файла
показано один раз в селекторе. Browser/PreviewSurface остаётся общим с плагинами.

Library/VBA header имеет minimum из `--page-header-height`, а фактическую высоту —
по содержимому. При узкой группе и zoom кнопки переносятся, не перекрывая строку
режимов. Геометрия навигационных колонок применяется только к прямым детям layout,
а не к редакторам внутри Dockview. Маркер аннотации у обрезанного левого края
перемещается над/под целью, не накрывая соседнюю кнопку справа. Popup комментария
перехватывает только собственную область; pointer-events всего iframe не выключаются.
Показ уже видимой цели не прокручивает страницу.

## Сообщение другого агента — In Review, 2026-11-03

Общий bubble сохраняет геометрию и типографику user bubble. Отличие — семантические
`--agent-bubble-bg`/`--agent-bubble-border`, основанные на общей violet palette и surface,
и текстовая ссылка «Агент a-… · название чата». Короткий стабильный ID отличает
одинаковые названия. Цвет не является единственным признаком.
История и pending Inbox используют один renderer; заголовок выводится через textContent.
Панель исчерпанного лимита использует общие spacing/text tokens, переносит кнопку и
не захватывает фокус. Отдельных значений для Office/Workspace/plugin нет.
Общее numeric-поле принимает 0; подпись явно поясняет «0 — отключить».
Активная работа/ожидание используют общие status/activity/tool components;
для `agents.wait` — подпись «Жду сообщения агентов», без отдельного spinner/style.
Срез spawn/tab-independent discovery (2026-11-04) не добавляет визуального семейства:
созданные участники используют обычный Chat View, те же bubbles/activity/receipts.
Закрытие вкладки не получает скрытого действия Stop. Геометрия компонентов не меняется.
Сравнение computed padding/radius с обычным bubble на одинаковом viewport прошло;
общий Shell zoom overflow отмечен отдельно в RISK_REGISTER.

Обновлено: 2026-11-04. Owner: Web presentation; визуальные решения утверждает
пользователь. Canonical document tokens, компонентов и их повторного использования.
Стратегия — [Architecture](UI_ARCHITECTURE.md), экраны — [Screen Map](UI_SCREEN_MAP.md).
Направление Approved; продолжение общего chat-среза разрешено. S1b проходит визуальное ревью по эталону завершённого Demo.

## Визуальная основа

Действующий production UI — основа сообщений, composer, материалов, форм и меню.
Согласованные изменения оболочки берём из Demo, улучшения связываем с конкретным
неудобством. Самостоятельный редизайн экранов и возвращение отклонённых A/B не нужны.
Макет P3-инспектора не является утверждённым образцом настоящего Inspector.

| Решение | Статус |
|---|---|
| Кнопки сворачивания/разворачивания областей сверху; бренд в topbar убран | Approved |
| «+» выбирает экран; две кнопки создают область справа/снизу с выбором | Approved |
| «Закрыть группу» действует только на эту группу | Approved |
| Вид Dockview до A/B, возможность выбрать тему/акцент | Approved — направление |
| Крестики и split icons меньше, область нажатия остаётся удобной | Approved — направление; точные размеры In Review |
| Настройки вкладкой Dockview, gear снизу слева, сохранение hover menus | Approved — текущий S1c (`26f4d87f`), пользователь «Меня устраивает» |
| Имя документа в topbar, responsive thresholds | In Review |
| P3 production-чат в Dockview без повторного заголовка | In Review; iframe только Demo evidence |
| S1a: общие подписи ввода, edit/reply/stop, reasoning и окно истории Office/Workspace/plugin | Реализовано, детали presentation In Review; не полная унификация capabilities |

## Общий Chat View — In Review, 2026-11-03

Production markup служит единственным шаблоном Chat View. Factory создаёт scoped
nodes с уникальными DOM IDs и `data-chat-node`; стили общих ролей используют
последний, а не глобальные IDs. Размеры, spacing, кнопки и состояние composer
остаются в общих tokens/CSS, без отдельных Office/Workspace/plugin overrides.
Два composer рядом — два экземпляра одного компонента. Базовый стиль P1/P2,
малые close/split icons и вкладка выбора сохранены; нового варианта оболочки нет.
В chooser добавлены реальные chat targets. Лимит 12 вкладок и точная presentation
этой точки остаются In Review до пользовательского согласования.

Активная строка дерева и вкладка Office-чата текущего документа отражают выбор
сразу, до завершения native selection. Загруженная лента сохраняется при его
промежуточном ответе; отдельного экрана ожидания и новых loading-styles нет.
Фоновые tokens сохраняют DOM и фокус неизменного дерева. При смене selection общий
NavigationTree обновляет существующую строку; выбор из узкой навигации закрывает
overlay, включая синхронную замену дерева. Новых размеров и локальных CSS нет.

Приветствие — один `renderChatEmptyState` для всех sources. Общие
`--chat-empty-*` в `app-product-tokens.css` переносят P2: акцентный `chat` SVG
29 px без плашки, заголовок 20 px/1.35, блок до 440 px, padding 32/16 px,
отступы иконки/заголовка 16/12 px, пояснение 12 px/1.7 и внешний отступ 8vh.
Кнопки используют `button compact`; отдельные dark/narrow overrides удалены.
При высоте ленты до 360 px общий container rule убирает внешний отступ и
уменьшает вертикальный padding до 16 px. Блок не сжимается с потерей начала,
текст и действия остаются в прокручиваемой ленте. Presentation — In Review.

## Эталон оболочки: завершённый Dockview Demo

Нижнее меню активных запросов использует общий ActionMenu: название чата и
приглушённая подпись проекта/документа и стадии, отдельная кнопка Stop справа.
По уточнению пользователя 2026-11-03 **без иконок чата в строках**, ширина по тексту
до `--shell-runs-menu-max-width: 340px`, длинные строки с многоточием. Максимум
дополнительно ограничен viewport с учётом zoom; popup открывается вверх. Геометрия
строк, кнопок и spacing — общие menu/control tokens. Проверены 390/1440 px,
125% CSS zoom и keyboard focus; результат In Review.

Пользователь уточнил 2026-11-02: **Demo — эталон стиля и компоновки Dockview**,
и попросил перенести его описание в текущие документы. Основа — завершённые
P1/P2 (`95afa3b8`, `3e2433c9`), после возврата оформления до A/B и уменьшения
крестиков/кнопок. Это уточняет прежний статус «Approved — направление»:
оболочку не переосмысляем заново. Приёмка её production-адаптации остаётся In Review.

Источники переноса: исторические разделы [Оболочка](UI_LAYOUT_UX.md#оболочка),
[Раскладки и состояние](UI_LAYOUT_UX.md#раскладки-и-состояние),
[Проверка P2](UI_LAYOUT_UX.md#проверка-p2) и [журнал решений](UI_DECISIONS.md).
Визуальные опоры: [P1 Shell](review/p1-shell.jpg),
[P2 вкладки и действия](review/p2-tab-actions.jpg), [тема/акцент](review/p2-accent-dark.jpg).
Фактические старые значения доступны в `prototypes/ui/tokens.css` и `ui.css`;
источник новых production-значений — только `app-product-tokens.css`.

- Верхняя полоса без бренда: слева документ/проект или название текущего раздела;
  значок Office-документа соответствует host активного чата (Excel, Word, PowerPoint,
  Outlook), а при отсутствии чата — текущей среде;
  справа компактная группа navigation / Inspector / maximize, затем «Панели»,
  «Компоновка». Настройки по согласованию S1c перенесены вниз rail, диагностика над ними. Отступы и иконки соответствуют reference.
- Узкая вертикальная полоса разделов и отдельное дерево слева. У активного раздела
  спокойная заливка; по уточнению 2026-11-03 левый акцентный маркер удалён. Дерево не является Dockview-группой.
  Диагностика в левом rail использует общий `diagnostics` glyph: лупа с линией
  пульса внутри; размер, цвет и толщина линий задаются общим компонентом иконок.
- Светлая нейтральная подложка, небольшие отступы вокруг рабочей области и между
  группами, тонкие разделители, небольшие радиусы. Без новой A/B-палитры и без
  плоской сетки S1b, которую пользователь отклонил.
- Компактная строка вкладок. Название чата находится во вкладке; ниже остаются
  полезные сведения о документе/проекте. Второй крупный заголовок чата не нужен.
- Заголовок группы: «+», оригинальные Codicons справа/снизу, «⋯»; уменьшенный
  крестик вкладки. «Закрыть группу» находится в меню, не дублирует крестик рядом.
- «+» открывает **вкладку выбора с поиском**, а не прежнее popup-меню. Поведение
  описано в [Screen Map](UI_SCREEN_MAP.md#команды-оболочки-s1b).
- Нижняя строка сохраняет роль reference: источник/режим интерфейса, состав
  компоновки, переход в диагностику. Mock обозначается только в preview;
  успешность модели или Office по состоянию layout не выводится.
- Содержимое сообщений, composer, подтверждения, материалы и настоящий Inspector
  остаются production-компонентами. Статический Inspector P1 не переносится.

Срез использует общий набор нейтральных цветов, исходные accent/theme controls,
и `--shell-*` для размеров оболочки. Публичные `--dv-*` только связывают Dockview
с этими tokens. [app-product-navigation.css](../../web/css/app-product-navigation.css)
владеет Shell chrome, [app-dock-workspace.css](../../web/css/app-dock-workspace.css) —
Dockview adapter/chooser. Feature CSS экранов сохраняет отдельного владельца.
Размеры hit targets и знаков разделены; точные production breakpoints и поведение
на реальной Office-панели ещё проверяются.

StatusBar использует `--shell-status-height`; его controls имеют `height: 100%`
и `min-height: 0`. Общий минимум кнопки 34 px не должен растягивать control внутри
строки 24 px. В Chromium после исправления по фото от 2026-11-03 высота кнопки
23 px с учётом границы строки совпадает с Demo; внешний overflow устранён.

## Источники tokens

Исходная production-база — [app-product-tokens.css](../../web/css/app-product-tokens.css):
семантические поверхности, текст, границы, accent/status, spacing, типографика,
радиусы, размеры и обе темы. CSS хранит фактические значения; таблицы значений
в Markdown не дублируем. [app-accent.js](../../web/js/app-accent.js) уже общий
каталог акцентов и foreground calculation.

Сейчас [app.css](../../web/css/app.css), [app-theme.css](../../web/css/app-theme.css),
app-product-components/screens и feature CSS образуют каскад; aliases text/ink,
line/border нельзя удалять до переключения consumers. В срезе компонента:
найти его правила → назначить один источник → переключить consumers → удалить
заменённые selectors, не добавляя ещё один override sheet.

[Demo tokens](../../prototypes/ui/tokens.css) — зафиксированный эксперимент,
не второй источник для production. Нужные Dockview tokens добавляются к общей
production-базе и связываются с публичными --dv-* hooks. Отдельная тема каждого
вида запрещена. Data Templates сохраняет standalone defaults до общего
portable token entry, после чего host и standalone используют один источник.

## Оформление и акцент

Светлая/тёмная темы, компактная читаемая иерархия, умеренные границы, общий focus.
Статус передаётся текстом/иконкой вместе с цветом; reduced-motion учитывается.
Выбор темы/акцента не пересоздаёт views и не меняет runtime settings или layout.

Исторические снимки P2: [светлая тема](review/p2-settings.jpg),
[тёмная с акцентом](review/p2-accent-dark.jpg), [tab controls](review/p2-tab-actions.jpg).
P1/P2 оболочка выбрана пользователем эталоном; её значения перенесены в общие
production tokens. Адаптер tab spacing/close icon ограничен Dockview 8.8.3 и повторно
проверяется при обновлении vendor; другие компоненты от него не зависят.

## Применение правил сейчас

Запуски 2026-11-03 — In Review: общий NavigationTree вместо локальных disclosures.
Опция общего дерева `toggleOnSelect` включает выбор и раскрытие/сворачивание группы
одним нажатием строки (также Enter/Space); Запуски используют этот режим.
`reserveToggleSpace` сохраняет общую колонку иконок у групп и листьев журнала.
`lazyChildren` откладывает создание свёрнутых веток, сохраняя общий keyboard order
и selection. Прокрутка выбора адресует строку, не всю вложенную ветку; дозагрузка
сохраняет reading position. В короткой панели верхнее дерево не имеет фиксированного
минимума, вытесняющего reader; status row резервирует общий `--control-compact`.
Обзор группы использует существующие `rna-table` / `rna-table-link`: названия,
время и результаты выровнены без локальных отступов кнопок. Статусы и пояснения
не повторяются в заголовке и строках; таблица раскрывает полезную структуру,
а не копирует иерархию технических событий.
Корневые группы сохраняют акцент и межгрупповой отступ; вложенные группы используют
обычные `tree` tokens, шрифт, цвет и высоту строки без дополнительного нижнего
интервала. Selected parent/leaf имеют одинаковый общий цвет и фон. Иконки — общий
каталог; крупный parent icon применяется только на первом уровне.
Причина повтора использует обычные текст, кнопки и JSON viewer reader; новые
карточки, цвета и локальные размеры не добавляются. Причина видна до загрузки тела.
«Формат ответа модели принят» обозначает только parser acceptance. Semantic отказ
finish использует существующие feedback/terminal message; отдельной отметки его
допуска у исходного запроса пока нет. Нормализованная цитата в следующем запросе и
точный исходный payload не подменяют друг друга.
RunJournal CSS владеет только компоновкой дерева/reader. Нет второй палитры,
своих selected/hover или копии списка событий в деталях. Повторные названия,
счётчики и служебные пояснения убраны; JSON compact toolbar переносится целиком
через общие стили viewer, не сжимая статус до вертикального столбца букв.
Привязка диагностики видна одной строкой document/project + chat. Это обычная
компактная кнопка-ссылка к исходному чату; IDs остаются в tooltip. Выбор другого
источника использует существующее дерево чатов и его общий слот действий,
без второго каталога/selector и без новой строки постоянных кнопок.

Лента 2026-11-03 — In Review. Общий `.agent-step-reason` использует `--text-sm`
и `--muted`, переносит длинный текст и не добавляет карточку/иконку на каждый шаг.
Reason выводится как текст, без HTML-интерпретации; одинаковый с `message` текст
не дублируется. Принятые пояснения остаются на месте после finish, детали calls
сворачиваются лениво. Claims/счётчики используют одно компактное раскрытие;
жёлтые unknown/partial notices и их старые CSS-обёртки удалены. Вызов макроса
со штатным возвратом выглядит как законченный вызов, без утверждения об эффекте.
Ошибки, подтверждения и вопросы сохраняют действующие controls.

Потоковый текст имеет тихую подпись «Черновик ответа · …» в существующем статусе,
без новой карточки. Сброс попытки может удалить этот черновик; принятые пояснения
и `reason` сохраняются. Обновление соседнего шага сохраняет выделенный текст и
раскрытия без временного отсоединения неизменённых узлов.

Исправление protocol results/Stop по фото 2026-11-02 сохраняет общий transcript,
composer и tokens. Удаляются ошибочные сообщения и разрывы блоков запуска; статус
отмены использует существующую строку и кнопку. Новых controls/CSS overrides нет.

S2b.12 меняет вход данных общего transcript, без новых UI-компонентов, controls или
CSS. История/stream/reasoning используют прежние renderer и tokens. Два адресных
source не означают две реализации чата; фабрика DOM-представления — следующий шаг.

S2b.11 сохраняет общий transcript, composer и существующий вид stream/reasoning.
Убрана выбранная копия live-данных, без новых controls/CSS. Token paints сохраняют
узлы истории, фокус и раскрытые блоки; отмена/повтор удаляют непринятый draft.
HTML source repair также не меняет дизайн: исправлены чтение и экспорт исходника.

Оптимизация создания Office-чата меняет только доставку данных: пустая переписка и
catalog delta используют существующие NavigationTree/composer/transcript. Новых
controls, геометрии и локальных CSS overrides нет; остальные строки дерева сохраняются.

Общие tokens, Icons/Accents, PanelToggle, NavigationTree, composer/transcript,
Inspector и ViewerRegistry уже используются в production. Это действующие правила
каждого среза, а не отдельная финальная чистка. При замене переключаем consumers
и удаляем старый путь в том же изменении. Отдельный пакет UI Kit не создаётся заранее.
Полная переносимость controls и оставшиеся дубли форм/меню/CSS уточняются в S2–S5
по реальному второму consumer, а не объявляются завершёнными.

В уточнении S1b удалены дубли закрытия дерева из `app.js` и прежние fullscreen/compact
CSS paths. Preview layouts получают общие navigation columns; величина занимаемого
места приходит из ShellLayout. NavigationTree сообщает `rna-navigation-select`
(bubbling, без domain payload) после принятого выбора мышью/клавиатурой, включая
синхронную перерисовку дерева; toggle папки и отказ `onSelect === false` не дают событие.
Shell слушает его только внутри `.global-nav`; сам Tree не импортирует Shell/bridge.

Оригинальные Codicons не перерисовывались. Причина увеличенных кнопок — унаследованные
`flex-basis`/`min-height` общего `.icon-btn`: 34 px вместо Demo 25 px. Группа теперь
передаёт общий `--control` из shell token; SVG/mask остаётся 14 px. Второй button
component или копия SVG не создавались.

После повторного визуального замечания исправлена оставшаяся геометрия S1b:

- `app-product-tokens.css` владеет диапазоном/шагом ширины navigation, разделителем,
  внутренними отступами, размерами строк/иконок Tree и геометрией Dockview.
  CSS и JS читают эти значения; числа из `data-default-left`/`data-min-left`
  шести Shell layouts удалены. `data-navigation` обозначает только роль.
- `app-product-components.css` владеет строками, отступами вложенности,
  selected/hover/focus и действиями NavigationTree для всех consumers.
  Вложенные списки не рисуют вертикальную линию и не добавляют внутренний padding.
  Действия на desktop появляются поверх правого края, не резервируя ширину у
  названия; на touch занимают место постоянно. Длинное название плавно проходит
  под наведением или клавиатурным фокусом до конца доступной области; reduced motion
  сохраняет обычное усечение и полную подсказку. Высота строки — 32 px при pointer.
  Office/Workspace используют одну chat icon. Plugin overrides высоты/шрифта удалены.
- `app-product-navigation.css` оформляет общий navigation splitter;
  `app-dock-workspace.css` связывает tokens с Dockview и нормализует vendor `rem`
  padding вкладок, чтобы масштаб текста чата не менял их геометрию.

Повторный аудит завершает перенос Shell целиком: topbar/rail/footer имеют один
CSS owner; sidebar headers используют общую геометрию. Chooser повторяет P1/P2
(вверху области, responsive grid, поиск/Enter, empty state). Menus не имеют
отдельного Dock renderer: используется портальный вариант существующего ActionMenu.
Нейтральные surfaces, selection blend и shadow взяты из reference; выбранный
пользователем accent не сбрасывается в Demo default. UI «Демо» для выбора mock
сценария/ширины не является production-командой; theme/accent — в настройках.

Это исправление существующих общих владельцев, без второго UI Kit или renderer.
Остальные feature-specific CSS ещё существуют: полное устранение всех дублей
не объявляется завершённым. Сравнение с Demo и снимки — в
[Migration Plan](UI_MIGRATION_PLAN.md#уточнение-s1b-общая-геометрия-после-визуального-ревью).

## Settings и hover menus S1c

Текущий результат **Approved**: пользователь «Меня устраивает»; commit `26f4d87f`. Gear сохраняет общий
размер/состояния rail button. Hover cards используют поверхность/строки того же
menu component, общие spacing, icon, radius, shadow и theme tokens. У tooltip
только подпись; у интерактивного меню есть заголовок и действия. Версия остаётся
внизу меню настроек. Отдельные размеры plugin/settings menus удалены.

Категории Settings используют NavigationTree и существующий поиск. Компактный
select потребляет те же nav items и общий `.rna-form-control`, без второй карты
разделов. Responsive зависит от контейнера Dockview. Footer «Применить» остаётся
видимым при dirty/saving, не перекрывает заголовок или поля. Геометрия содержимого
принадлежит `app-product-screens.css`; Shell не управляет внутренней навигацией.
[Снимок с меню](review/s1c-settings-hover.png), [узкая группа](review/s1c-settings-narrow.png).

JSON-профиль агента использует существующие `setting-field`, `setting-note`,
`details` и JSON viewer. Один select формата без дублирующих колонок истории/роли;
миграция — текстовый status, пример — раскрываемый блок. Новые tokens/CSS не нужны.

## Общие controls модели S2a

Owner: существующие composer/model-picker/model-capabilities. Один menu renderer,
строки, badges и подписи используются Office/Workspace/plugin без отдельных CSS.
«По умолчанию» наследует общие настройки; явный выбор принадлежит чату. Running,
awaiting approval, preference save и read-only делают control недоступным.
Подтверждённый отказ не меняет выбранную строку; потерянный ACK требует чтения
сохранённого выбора перед отправкой. Неизвестная каталогу модель остаётся
видна с объяснением. Reasoning учитывает возможности выбранной модели.

Menu позиционируется относительно общей toolbar composer, ограничено её шириной
и использует существующий spacing token. Это устраняет обрезание у левой границы
узкой Dockview-группы. Новой библиотеки controls или локальных owner styles нет.

Статус реализации — **In Review**. Production UI на mock bridge:
[1360 px](review/s2a-model-menu.png), [420 px / dark](review/s2a-model-menu-narrow.png).
Screenshots подтверждают отображение, не работу Office или реального provider.

## Каталог существующих компонентов

L1 = host-independent component; L2 = browser requirements; L3 = host integration.
Указанная пригодность — source-аудит, не полная standalone qualification.
Новые public interfaces вводятся только для реального второго consumer.

| Компонент / источник | Повторное использование и граница | Состояния / lifecycle |
|---|---|---|
| [ShellLayout](../../web/js/app-layout.js) | Shell chrome/navigation; renderWorkspace получает context/layout snapshot, bindWorkspace — callbacks трёх workspace-команд; без mirror store | navigation state, responsive, topbar/footer, notice cleanup |
| [Dock Workspace](../../web/js/app-dock-workspace.js) | Один Dockview; descriptors conversation/inspector/settings и временный chooser; placement и domain-view adapters, не chat store | move/resize/close/restore, narrow, pending layout save, DOM lifecycle |
| [Icons](../../web/js/app-icons.js), [accent](../../web/js/app-accent.js) | Общие уже сейчас; кандидаты L1 без Shell | Theme/size/accessible label у consumer; shared catalog |
| Buttons/toolbar/forms/menu CSS: app-product-components, app-theme, app-composer | Сохранять привычные controls; извлечь затронутые rules, не копировать по views | default/hover/focus/pressed/disabled/invalid/loading |
| [NavigationTree](../../web/js/app-navigation-tree.js) | Реальный общий renderer, options/callbacks + icons; L1 candidate | selected/expanded/filter/keyboard; unmount освобождает state |
| [TreeAdapter](../../web/js/app-tree-adapter.js) | Resource/domain adapter поверх NavigationTree, не второй widget | read/loading/error, destroy; host calls не уходят в UI Core |
| [EditorTabs](../../web/js/app-editor-tabs.js), [ActionMenu](../../web/js/app-action-menu.js) | Внутренние режимы/меню; не docking engine | focus/keyboard/active; callback actions |
| [ViewerRegistry](../../web/js/app-viewer-registry.js), [ViewerUI](../../web/js/app-viewer-ui.js) | Сохранить register/mount/unmount форматов | destroy старого controller и decorations при замене |
| [JSON](../../web/js/app-json-viewer.js), [text/Markdown](../../web/js/app-text-viewer.js), text-diff | Повторно используют registry; portable rendering выделять без bridge | bounded DOM/depth, search/copy/source, unmount |
| [ResourceViewer](../../web/js/app-resource-viewer.js), [SequenceViewer](../../web/js/app-sequence-viewer.js) | Общие PDF/image/sequence/task-list; resource/lease adapter остаётся L3 | lazy pages, zoom, unavailable, destroy/release; не загружать всё заранее |
| [PreviewSurface](../../web/js/app-preview-surface.js) | Общая шапка/локация/reload для HTML и plugin preview | load/error/reload; frame owner не принадлежит chrome-компоненту |
| [OperationView](../../web/data-templates/ui/operation-view.js) | Готовый presentation-only progress: create/render/dispose, callbacks cancel/dismiss; L1 candidate | Фактический snapshot; unknown total indeterminate, без таймерной симуляции |
| [Questions](../../web/js/app-questions.js), [RunChanges](../../web/js/app-run-changes.js) | Общие conversation composites, пока domain-aware | awaiting/submitting/rejected/failed, owner/receipt сохраняются |
| [ConversationView](../../web/js/app-conversation-view.js), Messages/WorkspaceChat adapters, composer | Общие transcript lifecycle и SnapshotOwner; Workspace у view, Office transcript/preferences/context и material metadata через временный selected accessor. Общие builders/footer заимствуют данные | target-scoped cache/frames/scroll/actions; hide сохраняет projection, replace/dispose освобождает; Office source/view factory и singleton adapter ещё мигрируют |
| [Composer presentation](../../web/js/app-chat-composer.js), [reasoning control](../../web/js/app-model-render.js) | Один renderer состояний ввода и controls; owners передают факты и capabilities | idle/edit/awaiting reply/read-only/running/cancelling; не хранит второй draft |
| ChatComposer в том же `app-chat-composer.js` | Один controller bindings/edit state, draft presentation и команд model/mode/reasoning на form; exact target и сервисные adapters | S2b.2/S2b.5/S2b.6: edit/draft retention, scoped ACK/read-back, стабильный cursor, Enter/IME/disabled Send; единые controls, без нового store и экранных overrides |
| [ConversationObserver](../../web/js/app-conversation-observer.js), `ConversationView.observe` | S2b.4: общий lifecycle UI-чтений; source adapters задают transport и callbacks | exact binding, stale reply/error, pause/catch-up/dispose; не отменяет domain run |
| [ConversationState](../../web/js/app-conversation-state.js), history buttons в Messages | Один cap 240 и prepend/refresh для обоих доменов; explicit latest bypass; S2b.7 — адресный приём snapshot | wrong-target/stale page rejection, bounded data, reading anchor; source не хранит второй snapshot, durable history не обрезается |
| [ChatInspector](../../web/js/app-chat-inspector.js) | Один domain view, настоящие context/capabilities/usage/run | Scope/generation, lazy detail, disclosure/focus, suspend |
| [ModelContext](../../web/js/app-model-context.js) | Domain reader, сохранять существующие cache/abort/bounds | metadata → lazy body, retained views, stale guards |
| [RNPlugin SDK](../../web/plugin-host/sdk.js) | Существующий Ready/command/files/results protocol; не новый SDK | L3 host, L2 browser adapter отдельно; permissions у host |

Пустое поле ввода занимает две строки после очистки длинного черновика; длинный
текст прокручивается внутри поля с пределом по высоте chat view. В узкой группе
кнопки модели, отправки и остановки остаются внутри composer. Содержимое вкладки
Материалов занимает всю высоту Dockview, включая пустое состояние HTML-редактора.

Нельзя обобщать Chat/Inspector в UI Core только из-за общих кнопок. Сначала
выделяются независимые controls с реальными consumers и инъекцией зависимостей.
Не создавать универсальный VirtualList/DataTable, если существующих bounded
renderers/Tabulator достаточно для конкретного сценария.

## Структура запроса — production, 2026-11-02

[Макет](review/request-structure.html) — ревью содержимого view, не новая Shell
или развитие замороженного Demo. [Маршруты и типы](UI_SCREEN_MAP.md#структура-запроса--production-2026-11-02).

- Общий NavigationTree: selected/focus, клавиатура, whole-row selection, spacing,
  иконки. Локальное оглавление не становится global-nav; его выбор не закрывает
  Shell navigation. Максимум раздел → сообщение, содержимое читается рядом.
- EditorTabs/общие controls: Состав/Ответ/Изменения, Markdown/JSON/Текст.
  Viewer API расширен внешним управлением toolbar; standalone controls других consumers сохранены. Внутренние controls не скрываются через CSS, renderer общий. В макете Markdown использует общий
  sanitized renderer, JSON/text — ViewerRegistry; это не новый production API.
- Роль/размер/тип — короткие подписи. Причина отказа — текст; статус не только цвет.
  Без новых цветов групп, крупных карточек и повторного текста в каждом раскрытии.
- Feature CSS владеет только оглавление/reader layout и переходом список/detail
  по ширине view. Tree/buttons/forms берут геометрию из общих tokens/components.
  Новый splitter и собственная тема не вводятся.

Ревью: обе темы, narrow/zoom, длинные имена, JSON-строка, partial/unavailable,
видимый фокус и возврат к списку. Формат не маскирует исходный тип; текущий snapshot
не становится runtime proof. Production lifecycle/performance evidence — в Migration Plan.

### Кнопки, формы и меню диагностики

Scope подтверждён пользователем: диагностика и её реальные общие consumers.
Production включён по прямому указанию; визуальная проверка выполнена на synthetic bridge. Новая тема диагностики и локальные варианты кнопок не нужны.
В production используются EditorTabs, mode-toggle, rna-form-control, NavigationTree
и портальный ActionMenu; CSS макета задаёт только раскладку/состояние narrow.

Заменённые дубли (старые paths удалены):

| Удалённый путь | Действующий owner |
|---|---|
| `app-trajectory.css`: собственные размеры icon-btn, SVG, menu buttons/body и fields | `app-product-components.css` + product tokens; в trajectory остаётся layout/filter composition |
| ModelContext buttons смешивают `button secondary compact`; details helper создаёт только `secondary` | Один вариант общего button для соответствующей роли, не новые размеры у каждого helper |
| Компактный JSON viewer самостоятельно собирает details-menu с actions | Общий ActionMenu с адаптером команд viewer; parse/format/copy остаются у JSON viewer |
| Отдельные toolbar controls в source/messages и их metadata details | Один reader toolbar и actions выбранного объекта; никакого параллельного «копировать то же» |
| Feature CSS задаёт мелкие интервалы и control density буквально | Общие tokens/components для повторяемой геометрии; feature CSS только размещает элементы |

Расширение общего compact control допускается там, где текущего варианта не хватает:
значения живут в tokens/components и используются всеми затронутыми consumers.
Не копировать размеры в HTML/JS и не компенсировать старые rules новыми overrides.
Native dropdown списка остаётся form control, меню команд — ActionMenu; смешивать
их только ради похожего вида не надо. Просмотр форматного JSON сохраняет отдельный
выбор дерево/текст/исходный: это формы одного viewer, не глобальные разделы.

Production JSON (включая меню узла), журнал/связи событий и reader используют
портальный ActionMenu. `openButtons` принимает существующие DOM-команды, сохраняя
их handlers и disabled state. При unmount меню закрывается. Viewer API `toolbar:false`
передаёт controls выбранного сообщения reader; standalone consumers сохраняют toolbar.
`sourceLabel` отличает декодированную строку от точного JSON. Compact controls используют
`--control-compact`; log filters — общие pressed buttons без отдельной dark-палитры.

Снимки макета: [Markdown](review/request-structure-wide.png),
[JSON-строка](review/request-structure-json.png),
[меню запроса](review/request-structure-menu.png),
[узкий dark view](review/request-structure-narrow.png).
Локально проверены реальные Tree/EditorTabs/ActionMenu/ViewerRegistry в макете:
форматы, полный JSON сообщения, исходник запроса, Escape/возврат фокуса, меню
в пределах viewport, partial/missing и ширины 320–1200 px. Это ревью layout,
история проектного ревью. Production screenshots и измерения перечислены в Migration Plan.
Запуск из корня: `python3 -m http.server 8898 --bind 127.0.0.1`, затем
`/docs/ui/review/request-structure.html`. Ни bridge, ни модели не подключаются.

## Контракт компонента и ревью

### NavigationPane S3b.1 — production, In Review

[NavigationPane](../../web/js/app-navigation-pane.js) объединяет общий header,
поиск, кнопки команд, footer состояния и NavigationTree; source adapters передают
данные и callbacks. Header — 41 px, controls и focus/selected/menu — существующие
tokens/ActionMenu. Общие title ellipsis и search styles работают без Shell.
Отдельные Materials header/menu CSS и перенос одного menu DOM между sidebar и
каталогом удалены: оба trigger используют одни command handlers через ActionMenu.
Scroll/keyboard/expanded остаются у NavigationTree и source adapter. Повторный
mount не создаёт обработчики; dispose снимает listeners/timer/tree binding.

Ожидание удаления чата использует существующие status/disabled состояния дерева
и префикс «Удаление…» в названии (виден при ellipsis); новые размеры, spinner и
overrides не вводятся.

Проверены реальные consumers и независимый VBA-shaped fixture без Office/bridge;
[ревью и ограничения](UI_MIGRATION_PLAN.md#s3b1-общая-навигация--in-review-2026-11-02).

### Material object view S3b.2 — In Review

Используются существующие Dock tabs, ResourceEditor header/title/meta/actions,
ActionMenu, ViewerRegistry и форматные controls. Новые styles ограничены flex/scroll
компоновкой `.material-object-view`; tokens задают inset/spacing/type/color.
У переносимых Chat/Materials components удалена зависимость от `#tab-chat`/
`#tab-artifacts`: внешний source не меняет оформление view. Sidebar Материалов
использует flex tree body, поэтому «Все материалы» не растягивается при скрытом search.
Whole-row selection остаётся у NavigationTree; Escape возвращает focus из action menu.

[Wide/light](review/s3b2-light-1280.png), [narrow/dark](review/s3b2-dark-420.png):
1280/420 px, light/dark, 125% zoom, source title overflow и keyboard menu проверены
в Chromium. Несколько JSON toolbars используют локальный индекс действий; decorators
не запускают взаимную перерисовку при вложенных viewers. Browser/Office evidence
и cold restart presentation mementos остаются открытыми.

### Общая панель навигации и object views — Proposed, 2026-11-02

Материалы/Библиотека/будущие Файлы переиспользуют один каркас панели: source/scope,
поиск/фильтры, compact toolbar, дерево или табличный список, paging и состояния.
Основа — действующие NavigationTree, TreeAdapter там, где он подходит данным,
ActionMenu, forms и tokens. Новый tree widget, собственная тема каждого источника
и универсальный framework provider-ов не нужны. Предметная группировка остаётся
у adapter: library categories не изображают файловые папки, revision не file path.

Общая строка кликабельна целиком; expander раскрывает группу, явное открытие/Enter
открывает объект. Поиск не загружает тела всех найденных объектов. Selected row,
keyboard focus и active Dock tab — разные состояния. Scope, поиск, раскрытия и
scroll возвращаются при смене источника; ошибки/partial/cancel не выглядят пустым
успешным каталогом. Возможность команды задаёт предметный owner, а не тип иконки.

Общий каркас просмотра содержит название, источник/версию, статус и действия
объекта; controls формата принадлежат ViewerRegistry renderer. Для HTML/plugin
расширяется существующий PreviewSurface: одинаковые loading/ready/error/reload,
единое размещение browser surface, provider-specific действия рядом. При embed
viewer toolbar не дублируется внешним header. Адрес/название показывает источник,
не превращается в разрешение произвольной навигации или доступа к файлам.

Ревью проверяет одновременно два разных объекта, смену навигационного источника,
несохранённый editor, узкий режим, обе темы, move/split/restore и клавиатуру.
Простого сходства отдельных screenshot недостаточно: важны сохранность состояния,
отсутствие hidden side effects и bounded reads/retained memory.

Для общего Browser в S3b используются те же Dock tabs, header/actions и controls.
Чат и preview — независимые views; preview не получает собственный composer
или вторую оболочку. HTML и plugin проверяются вместе на narrow/wide, focus/close
и сохранность frame session; отдельный plugin-only path не является целью среза.

Для выделяемого компонента здесь фиксировать source/public API, реальные consumers,
владельца данных, states, keyboard/focus, tokens, события, cleanup, portability и
пример. Mount не стартует domain operation без команды пользователя/владельца.
Версия public API нужна при появлении внешнего consumer, не для каждого helper.

Узкий режим, увеличение текста, menu clipping, hit areas, tooltip и Escape
проверяются на конкретном срезе. Не путать хороший screenshot с исправлением
late responses, утечек или Office qualification. После согласования обновлять
компонент и tokens в том же срезе; эксперимент остаётся Proposed/In Review.
Отдельный UI_COMPONENT_CATALOG.md сейчас не нужен: этот каталог имеет одного owner.

ActionMenu portalled API: `open(trigger, items, {label, className})`, item —
`label/icon/disabled/status/onSelect` или separator; `closeAll()` и `closeWithin(root)`
для deactivation/dispose. `render()` существующих inline menus сохранён.
Компонент зависит только от Icons/DOM, не от NavigationTree/Shell/bridge.
Arrow Up/Down, Home/End, Tab и Escape доступны; открытие нового меню закрывает
предыдущее, Escape/выбор возвращают focus, resize/pagehide удаляют popup.
API проверен в standalone component fixture; public plugin export ещё не заявлен.

### Browser S3b.3a

Общий renderer — [Browser](../../web/js/app-browser-viewer.js); общий toolbar —
[PreviewSurface](../../web/js/app-preview-surface.js). Provider не задаёт собственный
splitter, ширину, overlay или переключатель инспектора. Dock tabs/menus/buttons и
focus используют общие компоненты/tokens. Toolbar содержит источник/версию, действия
и отдельную строку статуса; до 450 CSS px действия переходят на следующую строку,
имя источника остаётся читаемым. Причина ошибки переносится, не маскируется под успех.

Browser placeholder доступен с клавиатуры: Enter переводит фокус к содержимому.
Hidden view/открытая узкая навигация не перехватывают ввод; при drag поверхность
временно скрыта от hit testing, document не выгружается. Собственные CSS задают
только portal geometry и компоновку content/toolbar. Аннотации используют прежние
VisualInspector/VisualAnnotations и черновик адресного чата.


### Сохранённые Browser objects S3b.3b

Используются те же PreviewSurface, Dock tabs, ActionMenu, `view-switch`,
`rna-form-control compact` и TextViewer с общим CodeMirror для исходника. `ActionMenu.createTrigger` создаёт единый
trigger для обычного материала, HTML и версии плагина. ViewerUI применяет существующий
form-control к поиску общих viewers; отдельного набора цветов/кнопок нет.

Порядок: имя и источник/версия → статус → ограничение/характер данных → содержимое.
У HTML один уровень выбора «Страница / Исходник», список файлов виден только в исходнике.
Текст имеет диапазон страницы, поиск и copy текущей страницы. Нет скрытого полного
исходника в DOM и второй iframe под кодом. Browser CSS добавляет только flex/overflow/
spacing для нового content layout. Длинные названия сокращаются в шапке с полным title;
сообщения ошибок переносятся. Read-only snapshot явно отличается от редактора.


### Исходники и оформление плагина — In Review, 2026-11-03

Единственный owner поверхности кода — `app-product-components.css`: `--code-bg`,
`--mono`, gutter и syntax tokens. CodeMirror заполняет stage, без внешней рамки,
радиуса и отступа; старые theme/base overrides и минимальные высоты VBA удалены.
`app-editors.js` предоставляет instance readonly mount с ResizeObserver/dispose;
HTML/XML/CSS modes — локальные файлы уже закреплённого CodeMirror 5.65.16.
Статус страницы исходника нейтрален, а не warning. Toolbar сохраняет поиск/copy/paging.

У дерева одна кнопка меню в строке; она видна также при selection/keyboard focus.
Единственное поле названия плагина находится выше всегда раскрытой палитры;
повторной шапки и внутренних разделителей нет, интервалы — общие space tokens.
Импорт SVG сохраняет собственное поле «Название SVG»; кнопки раскрытия и добавления
используют `button compact` и общий plus. Карточки и preview рядом с выбором цвета
используют один renderPlugin. Новый выбор SVG возвращает режим «Исходные цвета SVG».
