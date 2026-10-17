(function () {
  "use strict";
  var P = window.Prototype, I = P.icon, B = P.button, timer;
  function work(id, title, summary, steps) { return { type: "work", id: id, title: title, summary: summary, state: "running", steps: steps }; }
  function step(id, title, detail, state, result, target) { return { id: id, title: title, detail: detail, state: state || "complete", result: result, target: target }; }
  function text(value, phase) { return { type: "text", phase: phase || "commentary", text: value }; }
  function note(title, value) { return { type: "notice", title: title, text: value, tone: "warning" }; }
  function agent(id, blocks) { return { role: "assistant", kind: "agent", id: id, text: "", blocks: blocks }; }
  function message(c, id) { return c.messages.find(function (m) { return m.id === id; }); }
  function task(c, messageId, id) { return message(c, messageId).blocks.find(function (b) { return b.id === id; }); }
  var stages = [
    function (c) {
      c.messages.push({ role: "user", text: "Проверь продажи за сентябрь. Сравни с августом, найди исключения и подготовь краткий отчёт." }, agent("review", [
        text("Проверю источники и сравню одинаковые периоды."),
        work("read", "Проверяю источники", "Источники прочитаны", [step("september", "Прочитать сентябрь", "Читаю строки на листе «Продажи».", "running", "1 248 строк", "Продажи · сентябрь.xlsx → Продажи!A1:F1249")])
      ]));
    },
    function (c) {
      var w = task(c, "review", "read");
      w.steps[0].state = "complete";
      w.steps[0].detail = "Прочитаны 1 248 строк и 6 колонок. Заголовки распознаны.";
      w.steps.push(step("august", "Прочитать август", "Проверяю даты и валюту.", "running", "Сверка периода", "Продажи · август.xlsx → Продажи!A1:F1105"));
    },
    function (c) {
      var w = task(c, "review", "read"); w.state = "warning";
      w.steps[1].state = "complete"; w.steps[1].result = "1 104 строки"; w.steps[1].detail = "Август прочитан. Оба периода указаны в рублях.";
      w.steps.push(step("samara", "Прочитать Самару", "Источник временно занят. Данные не получены; документ не изменялся.", "error", "Источник занят", "Самара.xlsx → Продажи!A:F"));
      message(c, "review").blocks.push(text("Самара временно не отвечает. Повторю только чтение файла."));
    },
    function (c) {
      message(c, "review").blocks.push(work("retry", "Повторно читаю Самару", "Проверены 3 источника", [step("samara-retry", "Повторить чтение Самары", "Повторно запрашиваю исходные строки.", "running", "Повтор 1 из 1", "Самара.xlsx → Продажи!A:F")]));
    },
    function (c) {
      var w = task(c, "review", "retry"); w.state = "complete"; w.steps[0].state = "complete"; w.steps[0].result = "Прочитано"; w.steps[0].detail = "Повтор помог: файл прочитан. В 12 строках не указан регион.";
      task(c, "review", "read").steps[2].resolved = true;
      message(c, "review").blocks.push(
        note("12 строк без региона · включены в общий итог", "В разбивке эти строки будут отмечены как «Не указан». Перед итоговыми выводами заполните регион в исходнике."),
        text("Источники доступны. Сравню выручку, заказы и средний чек."),
        work("compare", "Сравниваю периоды", "Сравнение проверено", [step("totals", "Сверить показатели", "Проверяю суммы, количество заказов и средний чек.", "running", "3 показателя")])
      );
    },
    function (c) {
      var m = message(c, "review"), w = task(c, "review", "compare"); w.state = "complete";
      w.steps[0].state = "complete"; w.steps[0].result = "Итоги сходятся"; w.steps[0].detail = "Выручка: 12,8 млн ₽; заказов: 1 248; средний чек: 10 256 ₽.";
      w.steps.push(step("currency", "Проверить период и валюту", "Сопоставлены полные месяцы — сентябрь и август. Все суммы в рублях.", "complete", "Без расхождений"));
      m.blocks.push(text("Выручка выросла на 18,4%: с 10,8 до 12,8 млн ₽. Заказов стало больше на 13%. Динамику по регионам проверю отдельно.", "final"));
      m.text = "Выручка выросла на 18,4%. 12 строк без региона отмечены отдельно."; m.done = true;
    },
    function (c) {
      c.messages.push({ role: "user", text: "Покажи разбивку по регионам. Для отсутствующих данных не делай предположений." }, agent("regions", [
        text("Проверю, для каких регионов есть оба периода."),
        work("regions-read", "Сверяю регионы", "Данные регионов проверены", [step("moscow-samara", "Сопоставить Москву и Самару", "Оба периода доступны, даты и валюта совпадают.", "complete", "2 региона"), step("kazan", "Найти август для Казани", "Проверяю строки исходной таблицы.", "running", "Поиск периода")])
      ]));
    },
    function (c) {
      var m = message(c, "regions"), w = task(c, "regions", "regions-read"); w.state = "warning";
      w.steps[1].state = "warning"; w.steps[1].result = "Август отсутствует"; w.steps[1].detail = "Строк за август нет. Это ограничение источника: динамику вычислить нельзя.";
      m.blocks.push(note("Казань · нет августа, динамика недоступна", "Покажу только выручку сентября. Отсутствующие данные не заменяются нулём."), work("regions-table", "Готовлю разбивку", "Разбивка подготовлена", [step("table", "Собрать таблицу", "Добавляю показатели и отметки качества данных.", "running", "4 группы")]));
    },
    function (c) {
      var m = message(c, "regions"), w = task(c, "regions", "regions-table"); w.state = "complete"; w.steps[0].state = "complete"; w.steps[0].detail = "Четыре группы, включая 12 строк без региона. Недоступная динамика отмечена явно.";
      m.blocks.push(text("Москва дала основную часть выручки. По Казани есть только сентябрь, поэтому процент изменения не рассчитан.", "final"), { type: "table", headers: ["Регион", "Сентябрь", "К августу"], rows: [["Москва", "7,2 млн ₽", "+12,5%"], ["Казань", "4,9 млн ₽", "Нет данных"], ["Самара", "0,5 млн ₽", "+4,2%"], ["Не указан", "0,2 млн ₽", "12 строк"]] });
      m.text = "Разбивка готова. Динамика Казани недоступна: нет данных за август."; m.done = true;
    },
    function (c) {
      c.messages.push({ role: "user", text: "Собери это в отчёт. Добавь ограничения и список того, что нужно проверить." }, agent("report", [text("Соберу выводы и ограничения в материал этого чата."), work("report-draft", "Собираю отчёт", "Черновик собран", [step("draft", "Подготовить текст", "Добавляю выводы, таблицу и список проверок.", "running", "Markdown")])]));
    },
    function (c) {
      var m = message(c, "report"), w = task(c, "report", "report-draft"); w.state = "complete"; w.steps[0].state = "complete"; w.steps[0].result = "3 раздела"; w.steps[0].detail = "Выводы, ограничения и следующие проверки добавлены в черновик.";
      m.blocks.push(text("В отчёте отдельно отмечены 12 строк без региона и отсутствие данных Казани за август.", "answer"), work("report-check", "Проверяю материал", "Материал проверен", [step("verify", "Проверить содержимое", "Сверяю итоговые числа и ограничения с исходным анализом.", "running", "Проверка отчёта")]));
    },
    function (c) {
      var m = message(c, "report"), w = task(c, "report", "report-check"); w.state = "complete"; w.steps[0].state = "complete"; w.steps[0].result = "Доступен для просмотра"; w.steps[0].detail = "Числа и ограничения совпадают с анализом. Материал создан в демочате.";
      m.blocks.push(text("Отчёт готов. Можно открыть материал и проверить исходные данные.", "final"), { type: "artifact", name: "Итоги проверки продаж.md", description: "Отчёт · Markdown" });
      m.text = "Отчёт готов. Проверьте строки без региона и данные Казани за август."; m.done = true;
    }
  ];
  function current() { return P.state.demo && P.state.chats[P.state.demo.chatId]; }
  function finish() {
    var d = P.state.demo, c = current();
    var source = "# Итоги проверки продаж\n\nВыручка: 12,8 млн ₽. Рост к августу — 18,4%.\n\n## По регионам\n\n| Регион | Сентябрь | К августу |\n| --- | --- | --- |\n| Москва | 7,2 млн ₽ | +12,5% |\n| Казань | 4,9 млн ₽ | Нет данных |\n| Самара | 0,5 млн ₽ | +4,2% |\n| Не указан | 0,2 млн ₽ | 12 строк |\n\n## Ограничения\n\n- В 12 строках не указан регион. Эти строки входят в общий итог.\n- Для Казани нет данных за август. Динамика не рассчитывалась.\n\n## Следующие шаги\n\n- Заполнить регионы у 12 строк.\n- Добавить данные Казани за август.\n- Пересчитать сравнение после проверки исходников.\n";
    P.state.materials.push({ id: d.materialId, name: "Итоги проверки продаж.md", type: "Markdown", icon: "file", owner: "Этот чат", time: "Сейчас", page: 1, source: source, versions: [{ body: source, note: "Создан в демосценарии" }] });
    c.messages[5].blocks[c.messages[5].blocks.length - 1].materialId = d.materialId;
    c.messages[5].changes = { complete: true, items: [{ title: "Итоги проверки продаж.md", scope: "Материал чата", availability: "available", beforeExists: false, afterExists: true, before: "", after: source }] };
    d.playing = false; c.status = "complete"; c.unread = !(P.isChatVisible(d.chatId) && P.chatScroll.follows(d.chatId)); }
  function schedule() { clearTimeout(timer); if (P.state.demo && P.state.demo.playing) timer = setTimeout(advance, 4200); }
  function advance() {
    var d = P.state.demo, c = current(); if (!c || d.step >= stages.length - 1) return;
    c.status = "running"; d.step++; stages[d.step](c);
    if (d.step === stages.length - 1) finish();
    P.refreshDemo(d.chatId); schedule();
  }
  P.demo = {
    start: function () {
      clearTimeout(timer);
      var s = P.state, id = "demo-realistic", parent = s.parents.find(function (p) { return p.id === "office-sales"; });
      if (!parent) { parent = { id: "office-demo", scope: "office", name: "Продажи · демо.xlsx", host: "Excel", open: true }; s.parents.push(parent); }
      s.chats[id] = { title: "Анализ продаж · живой пример", order: ++s.chatSequence, parent: parent.id, scope: "office", document: parent.name, messages: [], attachments: ["Продажи · сентябрь.xlsx", "Продажи · август.xlsx"], draft: "", demo: true, status: "running", usage: { input: 24600, limit: 128000 } };
      s.scenario = "ready"; s.demo = { chatId: id, step: -1, playing: true, materialId: "demo-report-" + Date.now() }; P.chatScroll.reset(id); P.openChat(id); advance();
    },
    toggle: function () { if (!P.state.demo) return P.demo.start(); if (P.state.demo.step === stages.length - 1) { P.demo.start(); return; } P.state.demo.playing = !P.state.demo.playing; if (P.state.demo.playing) schedule(); else clearTimeout(timer); P.refreshDemoControls(); },
    next: function () { if (!P.state.demo) return; clearTimeout(timer); P.state.demo.playing = false; advance(); },
    stop: function (cancel) {
      clearTimeout(timer); if (P.state.demo) P.state.demo.playing = false;
      if (cancel && current()) current().messages.forEach(function (message) { message.done = true; (message.blocks || []).forEach(function (block) { if (block.state === "running") { block.state = "stopped"; block.steps.forEach(function (item) { if (item.state === "running") item.state = "stopped"; }); } }); });
      P.refreshDemoControls();
    },
    renderControls: function () {
      var d = P.state.demo;
      return !d ? B("Живой пример чата", "start-chat-demo", "play", "ghost") : '<span class="demo-step-count">' + (d.step + 1) + ' / ' + stages.length + '</span>' + P.iconButton(d.playing ? "Пауза демо" : d.step === stages.length - 1 ? "Повторить демо" : "Продолжить демо", "toggle-chat-demo", d.playing ? "pause" : "play") + '<button class="icon-btn" data-action="next-chat-demo" aria-label="Следующий шаг демо" title="Следующий шаг демо"' + (d.step === stages.length - 1 ? ' disabled' : '') + '>' + I("right") + '</button>' + P.iconButton("Начать демо заново", "start-chat-demo", "refresh");
    }
  };
}());
