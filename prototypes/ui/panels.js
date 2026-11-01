(function () {
  "use strict";
  const D = window.RnaDemo, E = D.escape, I = D.icon, B = D.button, F = D.fixtures;
  D.views = {
    sales: { title: "Анализ сентября", icon: "chat", type: "chat" },
    summary: { title: "Сводка по регионам", icon: "chat", type: "chat" },
    report: { title: "Итоги сентября.md", icon: "file", type: "material" },
    chart: { title: "Динамика продаж", icon: "chart", type: "material" },
    inspector: { title: "Инспектор", icon: "selection", type: "inspector", tool: true },
    browser: { title: "Браузер", icon: "external", type: "browser", tool: true },
    files: { title: "Файлы проекта", icon: "folder", type: "files" },
    library: { title: "Анализ продаж", icon: "book", type: "library" },
    diagnostics: { title: "Диагностика", icon: "chart", type: "diagnostics", tool: true },
    settings: { title: "Настройки", icon: "settings", type: "settings" }
  };
  // Presentation state survives panel moves, closing and layout replacement in this tab.
  // It is intentionally not serialized into Dockview or localStorage.
  const states = new Map();
  function state(id) {
    if (!states.has(id)) states.set(id, { scroll: 0, source: false });
    return states.get(id);
  }
  function context(label, action) { return '<div class="view-context"><span>' + E(label) + '</span>' + (action || '') + '</div>'; }
  function badge(label, kind) { return '<span class="badge ' + (kind || '') + '">' + E(label) + '</span>'; }
  function resource(id, name, detail, icon) { return '<button class="resource-row" data-open="' + id + '">' + I(icon || 'file') + '<span><strong>' + E(name) + '</strong><small>' + E(detail) + '</small></span>' + I('right') + '</button>'; }

  function inspector() {
    return context('Объект · Анализ сентября', badge('Демо')) + '<div class="view-scroll inspector-body"><div class="inspector-intro"><span class="eyebrow">Контекст разговора</span><h2>Всё необходимое<br>для этого запроса</h2><p>Документ, источники и результат в одном месте.</p></div><section class="info-section"><h3>Документ</h3><div class="document-item"><span class="excel-mark">X</span><div><strong>' + E(F.document) + '</strong><small>Демонстрационный документ</small></div></div><dl class="facts"><div><dt>Лист</dt><dd>Продажи</dd></div><div><dt>Диапазон</dt><dd>A1:F1249</dd></div><div><dt>Период</dt><dd>Сентябрь 2026</dd></div></dl></section><section class="info-section"><h3>Материалы</h3>' + resource('report', 'Итоги сентября.md', 'Результат анализа', 'file') + resource('chart', 'Динамика продаж', 'Сравнение периодов', 'chart') + '</section><section class="info-section"><h3>Источники</h3><div class="source-fact"><span class="status-dot"></span>Москва.xlsx<span>128 строк</span></div><div class="source-fact"><span class="status-dot"></span>Казань.xlsx<span>96 строк</span></div><div class="source-fact warning"><span class="status-dot"></span>Самара.xlsx<span>Проверить</span></div>' + B('Открыть файлы', 'data-open="files"', 'folder', 'quiet') + '</section><p class="subtle-note">Инспектор закреплён за этим примером. Его можно перенести в любую группу вкладок.</p></div>';
  }
  function chart() { return '<div class="chart" role="img" aria-label="Выручка: июнь 9,6, июль 10,2, август 10,8, сентябрь 12,8 млн рублей">' + [['Июн', '9,6', 60], ['Июл', '10,2', 66], ['Авг', '10,8', 72], ['Сен', '12,8', 90]].map((v, n) => '<div class="chart-column"><b>' + v[1] + '</b><div class="chart-track"><i style="height:' + v[2] + '%" class="' + (n === 3 ? 'highlight' : '') + '"></i></div><span>' + v[0] + '</span></div>').join('') + '</div>'; }
  function report(s) {
    return context('Материалы / Анализ сентября', badge('Markdown')) + '<div class="view-toolbar"><div class="segmented"><button data-report-mode="preview" aria-pressed="' + !s.source + '">Просмотр</button><button data-report-mode="source" aria-pressed="' + s.source + '">Исходник</button></div><span>Версия 2 · демо</span></div><div class="view-scroll"><div class="document-page">' + (s.source ? '<pre class="source-code">' + E(F.report) + '</pre>' : '<span class="eyebrow">Сентябрь 2026 · Аналитическая записка</span><div class="prose report-prose">' + D.markdown(F.report) + '</div><h3>Динамика выручки</h3>' + chart() + '<div class="callout">' + I('info') + '<p>По Самаре есть пропуски. Уточните исходные данные перед итоговой публикацией.</p></div>') + '</div></div>';
  }
  function browser() {
    return '<div class="browser-address">' + I('external') + '<input aria-label="Адрес демонстрационной страницы" value="demo://reports/september" readonly>' + badge('Локально') + '</div><div class="view-scroll"><div class="browser-document"><div class="browser-wordmark">NORTH / ANALYTICS</div><span class="eyebrow">Пример веб-страницы</span><h1>Сентябрь<br>в цифрах.</h1><p>Выручка, заказы и ключевые изменения за месяц.</p><div class="browser-stat"><strong>12,8 <small>млн ₽</small></strong><span>↑ 18,4% к августу</span></div>' + chart() + '<p class="subtle-note">Статическая demo-страница. Интернет не используется.</p>' + B('Открыть отчёт рядом', 'data-open="report"', 'file', 'quiet') + '</div></div>';
  }
  function files() {
    return context('Проект / Исходные данные', badge('3 файла')) + '<div class="view-scroll padded"><span class="eyebrow">Рабочие файлы</span><h2>Источники отчёта</h2><p class="muted">Пример структуры проекта. Реальные файлы не читаются.</p><div class="file-list">' + F.sources.map(source => '<div class="file-row">' + I('table') + '<div><strong>' + E(source.name) + '</strong><small>' + E(source.size) + ' · ' + source.rows + ' строк</small></div>' + badge(source.status, source.rows ? 'success' : 'warning') + '</div>').join('') + '</div>' + resource('report', 'Итоги сентября.md', 'Связанный материал', 'file') + '</div>';
  }
  function library() { return context('Библиотека / Навыки', badge('Макет P1')) + '<div class="view-scroll padded"><span class="eyebrow">Навык</span><h2>Анализ продаж</h2><p class="muted">Сравнивает периоды, находит изменения и готовит краткий отчёт с источниками.</p><dl class="facts"><div><dt>Категория</dt><dd>Аналитика</dd></div><div><dt>Источник</dt><dd>Пример библиотеки</dd></div></dl><h3>Сценарий</h3><ol class="steps"><li>Сверить исходные данные.</li><li>Сопоставить выручку и заказы.</li><li>Подготовить выводы и материал.</li></ol><div class="callout">' + I('info') + '<p>Подробный редактор библиотеки — на следующем этапе. Этот навык не запускается.</p></div></div>'; }
  function diagnostics() { return context('Диагностика / Пример журнала', badge('Макет P1')) + '<div class="view-scroll padded"><h2>Ход работы</h2><p class="muted">Фикстура завершённого сценария, не журнал реального запуска.</p><ol class="event-list"><li><time>12:42:01</time><span class="status-dot"></span><div><strong>Прочитаны исходные данные</strong><small>Москва · Казань · 224 строки</small></div></li><li><time>12:42:04</time><span class="status-dot"></span><div><strong>Сравнены периоды</strong><small>Сентябрь / август</small></div></li><li><time>12:42:08</time><span class="status-dot"></span><div><strong>Подготовлен пример отчёта</strong><small>Итоги сентября.md</small></div></li></ol>' + resource('report', 'Открыть отчёт', 'Демонстрационный результат', 'file') + '</div>'; }
  function settings() {
    const theme = document.documentElement.dataset.theme, accent = document.documentElement.dataset.accent;
    const choices = '<button class="accent-choice" data-accent-choice="default" aria-label="Акцент: исходный" aria-pressed="' + (accent === 'default') + '">Исходный</button>' + window.RNAssistantAccents.choices.map(choice => {
      const label = theme === 'light' && choice.lightLabel ? choice.lightLabel : choice.label;
      return '<button class="accent-choice" data-accent-choice="' + choice.id + '" aria-label="Акцент: ' + E(label) + '" aria-pressed="' + (accent === choice.id) + '" style="--swatch:' + choice[theme] + '"><span class="accent-swatch" aria-hidden="true"></span><span class="accent-label">' + E(label) + '</span></button>';
    }).join('');
    return context('Настройки / Интерфейс', badge('Демо')) + '<div class="view-scroll padded settings-body"><h2>Внешний вид</h2><p class="muted">Изменения применяются сразу ко всем открытым панелям.</p><div class="setting-row"><label for="settings-theme"><strong>Тема</strong></label><select id="settings-theme" data-theme-choice>' + [['light', 'Светлая'], ['dark', 'Тёмная']].map(([value, label]) => '<option value="' + value + '"' + (theme === value ? ' selected' : '') + '>' + label + '</option>').join('') + '</select></div><fieldset class="accent-settings"><legend>Цвет акцента</legend><p>Цвет кнопок и выбранных элементов. «Исходный» возвращает прежний акцент оболочки.</p><div class="accent-choices">' + choices + '</div></fieldset><p class="subtle-note">Тема и акцент запоминаются в этом браузере.</p><div class="setting-row"><div><strong>Раскладка</strong><small>Открытые вкладки и группы запоминаются в браузере</small></div>' + B('Сохранить', 'data-command="save-layout"', 'check') + '</div><div class="callout">' + I('info') + '<p>Модель и Office не подключены. Это настройки визуального прототипа.</p></div></div>';
  }

  D.createView = function (id) {
    if (D.views[id].type === 'chat') return D.createChatView(id);
    const s = state(id), element = document.createElement('div');
    element.className = 'panel-content'; element.dataset.view = id;
    function capture() { const scroll = element.querySelector('.view-scroll'); if (scroll) s.scroll = scroll.scrollTop; }
    function render() {
      element.innerHTML = id === 'report' ? report(s) : id === 'chart' ? context('Материалы / Сравнение периодов', badge('Демо')) + '<div class="view-scroll padded"><h2>Динамика продаж</h2><p class="muted">Выручка · миллионы рублей</p>' + chart() + resource('report', 'Итоги сентября.md', 'Выводы по результатам сравнения') + '</div>' : ({ inspector, browser, files, library, diagnostics, settings }[id])();
      const scroll = element.querySelector('.view-scroll');
      if (scroll) { scroll.scrollTop = s.scroll; scroll.addEventListener('scroll', () => { s.scroll = scroll.scrollTop; }, { passive: true }); }
    }
    element.addEventListener('click', event => {
      const mode = event.target.closest('[data-report-mode]');
      if (mode) { capture(); s.source = mode.dataset.reportMode === 'source'; render(); }
    });
    return { element, init() { render(); }, onShow() { const scroll = element.querySelector('.view-scroll'); if (scroll) scroll.scrollTop = s.scroll; }, dispose: capture };
  };
}());
