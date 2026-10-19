(function (root) {
  "use strict";
  // Trusted application markup, shared by both entry points. Never accepts project HTML.
  const markup = String.raw`
<header class="dt-header">
            <div class="dt-header-copy"><p class="dt-eyebrow">DATA TEMPLATES</p><h2 id="dtPageTitle">Данные и шаблоны</h2><p class="dt-subtitle" id="dtPageContext">От разных источников к единой структуре.</p></div>
            <div class="dt-header-actions"><span class="dt-phase">Подбор источников</span><button type="button" id="dtProjectHeaderButton" aria-expanded="false">Проект</button></div>
          </header>
          <div data-dt-slot="workspace"></div>
          <div class="dt-navigation">
            <div class="dt-tabs" role="tablist" aria-label="Разделы проекта">
              <button type="button" role="tab" id="dtTabSources" data-dt-view="sources" aria-controls="dtViewSources" aria-selected="true">Источники</button>
              <button type="button" role="tab" id="dtTabTemplates" data-dt-view="templates" aria-controls="dtViewTemplates" aria-selected="false" tabindex="-1">Шаблоны</button>
              <button type="button" role="tab" id="dtTabParameters" data-dt-view="parameters" aria-controls="dtViewParameters" aria-selected="false" tabindex="-1">Параметры</button>
              <button type="button" role="tab" id="dtTabMapping" data-dt-view="mapping" aria-controls="dtViewMapping" aria-selected="false" tabindex="-1">Маппинг</button>
              <button type="button" role="tab" id="dtTabProcessing" data-dt-view="processing" aria-controls="dtViewProcessing" aria-selected="false" tabindex="-1">Обработка</button>
              <button type="button" role="tab" id="dtTabResults" data-dt-view="results" aria-controls="dtViewResults" aria-selected="false" tabindex="-1">Результаты</button>
              <button type="button" role="tab" id="dtTabFormat" data-dt-view="format" aria-controls="dtViewFormat" aria-selected="false" tabindex="-1">О формате</button>
            </div>
            <label class="dt-mobile-nav" for="dtViewSelect">Раздел проекта<select id="dtViewSelect"><option value="sources">Источники</option><option value="templates">Шаблоны</option><option value="parameters">Параметры</option><option value="mapping">Маппинг</option><option value="processing">Обработка</option><option value="results">Результаты</option><option value="format">О формате</option></select></label>
          </div>

          <div class="dt-operation dt-card" id="dtOperation" role="region" aria-label="Текущая операция" hidden>
            <div class="dt-operation-head"><div><span class="dt-operation-stage" data-operation-part="stage"></span><h4 data-operation-part="title"></h4></div><div class="dt-operation-actions"><span data-operation-part="elapsed"></span><button type="button" id="dtCancelIntake" data-operation-part="cancel">Отменить</button><button type="button" class="dt-text-button" data-operation-part="dismiss" hidden>Скрыть</button></div></div>
            <div class="dt-operation-meter" data-operation-part="meter"><progress data-operation-part="progress" aria-label="Прогресс текущего этапа"></progress><span data-operation-part="percent"></span></div>
            <p class="dt-operation-current" data-operation-part="current"></p>
            <div class="dt-operation-metrics"><span data-operation-part="count"></span><span data-operation-part="detail"></span></div>
            <p class="dt-operation-note" data-operation-part="note"></p>
          </div>
          <div class="dt-intake-status"><p id="dtIntakeStatus" role="status" aria-live="polite"></p></div>
          <div data-dt-slot="activity"></div>
          <div class="dt-view" id="dtViewSources" role="tabpanel" aria-labelledby="dtTabSources" tabindex="0">
            <div class="dt-section-heading"><div><h3 id="dtSourceHeading">Начните с источников</h3><p id="dtSourceIntro">Добавьте файлы, которые хотите привести к общей структуре.</p></div></div>
            <div data-dt-slot="sources"></div>
            <div class="dt-intake-layout">
              <div class="dt-dropzone" id="dtDropzone" role="region" aria-label="Добавление источников" aria-describedby="dtSelectionNote">
                <div class="dt-drop-icon" aria-hidden="true"><svg viewBox="0 0 40 40"><path d="M12 7H8a3 3 0 0 0-3 3v22a3 3 0 0 0 3 3h19a3 3 0 0 0 3-3v-5M15 5h13l7 7v12H15Z"/><path d="M28 5v8h7M20 19h9M20 23h5"/></svg></div>
                <h4>Перетащите файлы или папку</h4>
                <p class="dt-drop-caption">Один отчёт или целая подборка</p>
                <div class="dt-picker-actions"><button type="button" class="dt-primary" id="dtChooseFiles"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 5v14M5 12h14"/></svg>Выбрать файлы</button><button type="button" id="dtChooseFolder"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M3 7V5h6l2 2h10v12H3Z"/></svg>Выбрать папку</button></div>
                <div class="dt-format-labels" id="dtFormatLabels" aria-label="Форматы источников"></div>
                <input type="file" id="dtFiles" multiple hidden>
                <input type="file" id="dtFolder" webkitdirectory multiple hidden>
              </div>
              <aside class="dt-guide" id="dtIntakeGuide" aria-label="Порядок работы">
                <p class="dt-eyebrow">КАК ЭТО РАБОТАЕТ</p>
                <ol><li><span>01</span><div><strong>Соберите источники</strong><p>Проверьте состав файлов и выберите нужные.</p></div></li><li><span>02</span><div><strong>Настройте соответствия</strong><p>Шаблон свяжет исходные поля с параметрами.</p></div></li><li><span>03</span><div><strong>Проверьте результат</strong><p>У каждого значения будет связь с источником.</p></div></li></ol>
                <p class="dt-guide-note">Подбор файлов не запускает обработку. На вкладке «Шаблоны» выберите JSON-правила, затем запустите очередь в «Обработке».</p>
              </aside>
            </div>
            <div id="dtSourceQueue" class="dt-source-queue dt-card" hidden>
              <div class="dt-queue-toolbar"><div><h4>Файлы <span id="dtFileCount" class="dt-count"></span></h4><p id="dtSelectionCount"></p></div><button type="button" class="dt-text-button" id="dtClearSources">Очистить список</button></div>
              <p class="dt-load-note" id="dtSourceLoadNote" hidden></p>
              <div class="dt-queue-filters"><input id="dtSourceSearch" type="search" placeholder="Найти файл…" aria-label="Поиск источников"><select id="dtSourceFilter" aria-label="Формат источников"><option value="">Все форматы</option></select></div>
              <details class="dt-list-options"><summary>Фильтры и порядок<span id="dtListOptionsHint"></span></summary><div>
                <label>Показать<select id="dtSourceState"><option value="">Все файлы</option><option value="included">Выбранные</option><option value="excluded">Невыбранные</option><option value="attention">Требуют внимания</option></select></label>
                <label>Порядок<select id="dtSourceSort"><option value="added">По добавлению</option><option value="name">По имени</option><option value="size">Сначала крупные</option><option value="path">По пути</option></select></label>
              </div></details>
              <div class="dt-list-heading"><label><input type="checkbox" id="dtSelectVisible"> Выбрать показанные</label><span>Состав подборки</span></div>
              <ul class="dt-source-list" id="dtSourceList" aria-label="Подобранные файлы"></ul>
              <nav class="dt-pagination" id="dtPagination" aria-label="Страницы источников" hidden><span id="dtPageRange" role="status"></span><div><button type="button" id="dtPreviousPage" aria-label="Предыдущая страница">←</button><select id="dtSourcePage" aria-label="Страница источников"></select><button type="button" id="dtNextPage" aria-label="Следующая страница">→</button></div></nav>
              <p class="dt-no-matches" id="dtNoMatches" hidden>Таких файлов нет. Измените поиск или формат.</p>
            </div>
            <aside id="dtSourceDetail" class="dt-source-detail dt-card" hidden aria-label="Сведения об источнике"><div class="dt-detail-heading"><h4 id="dtDetailTitle"></h4><button type="button" class="dt-text-button" id="dtCloseDetail">Закрыть</button></div><dl id="dtDetailMetadata"></dl><p id="dtDetailNote"></p></aside>
            <p id="dtSelectionNote" class="dt-footnote">Подбор файлов не запускает обработку. На вкладке «Шаблоны» можно запустить очередь; список хранится в этой сессии.</p>
          </div>

          <div class="dt-view" id="dtViewTemplates" role="tabpanel" aria-labelledby="dtTabTemplates" tabindex="0" hidden>
            <div class="dt-section-heading"><div><h3>Шаблоны преобразования</h3><p>Повторяемые правила для файлов с похожей структурой.</p></div><span class="dt-phase">Следующий этап</span></div>
            <div class="dt-empty-state"><span class="dt-empty-symbol" aria-hidden="true">▤</span><h4>Здесь будет библиотека шаблонов</h4><p>Шаблон определит нужные листы, колонки или поля JSON. Черновик можно будет проверить на источнике перед публикацией.</p><p><a data-dt-probe>Проверить шаблон в отдельном инструменте</a></p><button type="button" data-dt-go="sources">Перейти к источникам</button></div>
          </div>
          <div class="dt-view" id="dtViewParameters" role="tabpanel" aria-labelledby="dtTabParameters" tabindex="0" hidden>
            <div class="dt-section-heading"><div><h3>Параметры и справочники</h3><p>Общие определения, независимые от названий в исходных файлах.</p></div><span class="dt-phase">Следующий этап</span></div>
            <div class="dt-empty-state"><span class="dt-empty-symbol" aria-hidden="true">≡</span><h4>Единый язык для ваших данных</h4><p>Для каждого параметра будут заданы смысл, тип, единица измерения и допустимые значения. Синонимы и спорные соответствия останутся видимыми.</p><button type="button" data-dt-go="format">Посмотреть модель данных</button></div>
          </div>
          <div class="dt-view" id="dtViewMapping" role="tabpanel" aria-labelledby="dtTabMapping" tabindex="0" hidden>
            <div class="dt-section-heading"><div><h3>Карта соответствий</h3><p>Связь исходных полей с параметрами проекта.</p></div><span class="dt-phase">Следующий этап</span></div>
            <div class="dt-mapping-guide" aria-label="Принцип маппинга"><div><small>ИСТОЧНИК</small><strong>Поле или ячейка</strong><p>Имя, код, единица</p></div><span aria-hidden="true">→</span><div><small>ПРАВИЛО</small><strong>Соответствие</strong><p>Условия и область действия</p></div><span aria-hidden="true">→</span><div><small>ОБЩАЯ СХЕМА</small><strong>Параметр</strong><p>Смысл, тип, измерения</p></div></div>
            <p class="dt-explanation">Это схема будущего редактора. После распознавания здесь появятся реальные поля, предложения соответствий и конфликты. У каждого правила будет видна область применения.</p>
          </div>
          <div class="dt-view" id="dtViewProcessing" role="tabpanel" aria-labelledby="dtTabProcessing" tabindex="0" hidden>
            <div class="dt-section-heading"><div><h3>Обработка источников</h3><p>Проверьте состав очереди и запустите выбранный шаблон.</p></div></div>
          </div>
          <div class="dt-view" id="dtViewResults" role="tabpanel" aria-labelledby="dtTabResults" tabindex="0" hidden>
            <div class="dt-section-heading"><div><h3>Данные и проверка</h3><p>Результат преобразования, его качество и происхождение.</p></div><span class="dt-phase">Следующий этап</span></div>
            <div class="dt-empty-state"><span class="dt-empty-symbol" aria-hidden="true">☷</span><h4>Результатов пока нет</h4><p>Здесь появятся предпросмотр данных, диагностика и экспорт. Выбор файлов сам по себе не запускает анализ и не создаёт результат.</p><button type="button" data-dt-go="sources">Перейти к источникам</button></div>
          </div>
          <div class="dt-view" id="dtViewFormat" role="tabpanel" aria-labelledby="dtTabFormat" tabindex="0" hidden>
            <div class="dt-section-heading"><div><h3>О форматах и общей модели</h3><p>Разные источники сохраняют свою структуру и происхождение.</p></div></div>
            <div class="dt-format-grid" id="dtFormatGuide"></div>
            <div class="dt-reference"><div class="dt-detail-heading"><h4>Каноническое наблюдение</h4><span class="dt-phase">Схема ядра</span></div><p>Единица результата связывает параметр, контекст и значение. Схема наблюдений уже используется ядром. Редакторы и публикация результатов ещё разрабатываются.</p><dl><dt>Параметр</dt><dd>Что измеряем: стабильный идентификатор и определение.</dd><dt>Измерения и период</dt><dd>К какому объекту, варианту и времени относится значение.</dd><dt>Значение и единица</dt><dd>Тип и единица заданы явно. Ноль, пропуск и ошибка различаются.</dd><dt>Роль</dt><dd>Наблюдение, отчётный итог или вычисленное значение.</dd><dt>Происхождение</dt><dd>Файл и точное место: ячейка, строка CSV или путь в JSON.</dd></dl><p class="dt-reference-note">Обычные таблицы объектов и операций также будут поддерживаться — текстовые поля не требуется превращать в показатели.</p></div>
            <details class="dt-format-details"><summary>Что уже доступно</summary><p>Подбор файлов и папок, поиск, страницы списка, прогресс и отмена. Проверка шаблонов доступна в отдельном инструменте ниже.</p><p>Ядро выполняет правила над матрицей, нормализованными записями и JSON, а также читает ограниченный профиль XLSX. Распознавание, редакторы маппинга, другие форматы Excel, чтение исходных CSV/JSON и общая очередь преобразований ещё разрабатываются.</p><p><a data-dt-probe>Открыть проверку шаблона</a></p></details>
          </div>
`;
  function mount(container, probeUrl, options) {
    if (container.querySelector("#dtViewSources")) return;
    container.innerHTML = markup;
    if (options && options.nativeSources) {
      ["#dtOperation", ".dt-intake-status", "#dtViewSources .dt-intake-layout", "#dtSourceQueue",
        "#dtSourceDetail", "#dtSelectionNote"].forEach(selector => container.querySelector(selector).remove());
    }
    container.querySelectorAll("[data-dt-probe]").forEach(link => {
      link.href = probeUrl; link.target = "_blank"; link.rel = "noopener";
    });
  }
  root.DataTemplatesShell = Object.freeze({ mount: mount });
}(typeof globalThis !== "undefined" ? globalThis : this));
