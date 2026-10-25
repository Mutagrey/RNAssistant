/* Shared preview-only examples for the design prototype and the real product UI mock bridge. */
window.PrototypeFixtures = {
  document: "Продажи · сентябрь.xlsx",
  project: "Управленческая отчётность",
  chat: {
    title: "Что изменилось за сентябрь",
    user: "Сравни сентябрь с августом. Что изменилось в продажах и на что стоит обратить внимание?",
    assistant: "В сентябре выручка выросла на **18,4%**. Рост связан и с количеством заказов, и со средним чеком.\n\n| Показатель | Сентябрь | К августу |\n| --- | ---: | ---: |\n| Выручка | 12,8 млн ₽ | +18,4% |\n| Заказы | 1 248 | +13,0% |\n| Средний чек | 10 256 ₽ | +4,8% |\n\n### На что обратить внимание\n\nМосква и Казань дали основной прирост. По Самаре есть пропуски в исходных данных — проверьте их до итоговых выводов."
  },
  materials: [
    { id: "report", name: "Итоги сентября.md", type: "Markdown", icon: "file", owner: "Этот чат", time: "12:42" },
    { id: "html", name: "Динамика продаж.html", type: "HTML", icon: "chart", owner: "Этот чат", time: "12:40" },
    { id: "json", name: "Продажи по регионам.json", type: "JSON", icon: "braces", owner: "Этот чат", time: "12:39" },
    { id: "pdf", name: "Методика расчёта.pdf", type: "PDF", icon: "file", owner: "В документе", time: "Вчера" },
    { id: "image", name: "Диаграмма.png", type: "Изображение", icon: "image", owner: "В документе", time: "Вчера" },
    { id: "sequence", name: "Обзор регионов", type: "Коллекция", icon: "layers", owner: "Все документы", time: "14 окт." }
  ],
  sources: [
    { name: "Москва.xlsx", size: "2,4 МБ", byteLength: 2516582, rows: 128, status: "Готов" },
    { name: "Казань.xlsx", size: "1,8 МБ", byteLength: 1887437, rows: 96, status: "Готов" },
    { name: "Самара.xlsx", size: "840 КБ", byteLength: 860160, rows: 0, status: "Нужна проверка" }
  ],
  report: "# Итоги сентября\n\nВыручка выросла на 18,4% относительно августа.\n\n## Главное\n\n- Выручка: 12,8 млн ₽\n- Заказы: 1 248\n- Средний чек: 10 256 ₽\n\nРост обеспечили Москва и Казань. По Самаре нужно проверить исходные данные.\n",
  vba: "Option Explicit\n\nPublic Sub FormatSalesReport()\n    Dim report As Worksheet\n    Set report = ThisWorkbook.Worksheets(\"Продажи\")\n\n    With report.Range(\"A1:F1\")\n        .Font.Bold = True\n        .Interior.Color = RGB(232, 241, 233)\n    End With\n\n    report.Columns(\"A:F\").AutoFit\nEnd Sub\n",
  skill: "# Анализ продаж\n\nСравнивай период с предыдущим.\n\n1. Проверь наличие нужных колонок.\n2. Укажи период, валюту и источник.\n3. Раздели факты и предположения.\n4. Покажи исключения и пропуски.\n5. Сформируй краткий отчёт с таблицей.\n",
  template: '{\n  "name": "Ежемесячные продажи",\n  "sheet": "Продажи",\n  "headerRow": 1,\n  "fields": {\n    "region": "Регион",\n    "revenue": "Выручка",\n    "period": "Месяц"\n  }\n}'
};

window.PrototypeFixtures.materialSources = {
  report: window.PrototypeFixtures.report,
  html: '<!doctype html>\n<html lang="ru"><meta charset="utf-8">\n<style>body{font:16px system-ui;color:#21392a;padding:32px}h1{font-size:28px}.chart{display:flex;align-items:end;gap:24px;height:180px;margin:40px 0}.bar{flex:1;background:#d7e7dc;text-align:center;border-radius:6px 6px 0 0}p{line-height:1.7}</style>\n<h1>Динамика продаж</h1>\n<p>Выручка выросла на 18,4%. Москва и Казань дали основной прирост.</p>\n<div class="chart"><div class="bar" style="height:60%">Июнь · 9,6</div><div class="bar" style="height:68%">Июль · 10,2</div><div class="bar" style="height:76%">Август · 10,8</div><div class="bar" style="height:94%">Сентябрь · 12,8</div></div>\n<p>Миллионы рублей · демонстрационные данные</p></html>',
  json: JSON.stringify({ period: "2026-09", regions: [{ name: "Москва", revenue: 7420000 }, { name: "Казань", revenue: 5380000 }, { name: "Самара", revenue: null }] }, null, 2),
  plan: "# План подготовки отчёта\n\n1. Сверить источники и периоды.\n2. Объяснить изменения показателей.\n3. Подготовить отчёт и проверить выводы.\n",
  tasks: JSON.stringify({ goal: "Подготовить отчёт за сентябрь", status: "active", steps: [{ id: "read", text: "Проверить источники", status: "completed" }, { id: "compare", text: "Сравнить периоды", status: "in_progress", note: "Москва и Казань включены; Самара требует уточнения." }, { id: "report", text: "Собрать итоговый отчёт", status: "pending" }] }, null, 2),
  sequence: JSON.stringify({ title: "Обзор регионов", items: [
    { region: "Москва", revenue: 7420000, changePercent: 20.1 },
    { region: "Казань", revenue: 5380000, changePercent: 16.2 },
    { region: "Самара", status: "Нужна проверка источника" }
  ] }, null, 2),
  text: "Заметки к отчёту\n\nСравнить сентябрь с августом.\nУказать состав источников и пропуски.\nНе включать Самару без поля «Выручка»."
};

window.PrototypeFixtures.officeExamples = [
  { id: "office-sales", documentKey: "preview-document", name: window.PrototypeFixtures.document,
    host: "Excel", open: true, chats: [
      { id: "sales", previewId: "preview-chat", title: window.PrototypeFixtures.chat.title },
      { id: "summary", previewId: "preview-summary", title: "Сводка по регионам" }
    ] },
  { id: "office-budget", documentKey: "preview-budget-document", name: "Бюджет · IV квартал.xlsx",
    host: "Excel", open: false, chats: [
      { id: "budget", previewId: "preview-budget", title: "План бюджета" }
    ] },
  { id: "office-notes", documentKey: "preview-notes-document", name: "Заметки к отчёту.docx",
    host: "Word", open: true, chats: [
      { id: "notes", previewId: "preview-notes", title: "Обсуждение отчёта" }
    ] }
];

// Preview-only message bodies; delivered through the existing mock bridge.
window.PrototypeFixtures.visualChat = {
  "user": "Покажи все варианты оформления ответа: схемы, таблицу, HTML-карточки, интерактивный график, код, JSON и формулы.",
  "assistant": "Вот как может выглядеть ответ с визуализациями. **Данные демонстрационные.**\n\n### Как собирается отчёт\n```mermaid\nflowchart LR\n A[\"Данные\"] --> B[\"Проверка\"] --> C[\"Отчёт\"]\n```\n\n### Сравнение регионов\n| Регион | Выручка, млн ₽ | Изменение |\n|:---|---:|---:|\n| Москва | 12,4 | +18% |\n| Казань | 8,1 | +12% |\n| Пермь | 5,6 | +9% |\n\n### Интерактивный график\nНажмите ▶ в панели блока. Подсказки на столбцах работают без сети.\n```html\n<!doctype html><html lang=\"ru\"><head><style>body{font:14px system-ui;padding:8px}h2{margin:0 0 4px;font-size:18px}p{opacity:.65;margin:0 0 12px}#chart{height:220px}</style></head><body><h2>Выручка по регионам</h2><p>Демонстрационные данные · млн ₽</p><div id=\"chart\"></div><script>const chart=echarts.init(document.getElementById('chart'));chart.setOption({color:['#6d927f'],grid:{left:40,right:16,top:15,bottom:28},tooltip:{trigger:'axis'},xAxis:{type:'category',data:['Москва','Казань','Пермь']},yAxis:{type:'value',splitLine:{lineStyle:{color:'#ccd4ce',opacity:.3}}},series:[{type:'bar',data:[12.4,8.1,5.6],barWidth:38,itemStyle:{borderRadius:[5,5,0,0]}}]});addEventListener('resize',()=>chart.resize());</script></body></html>\n```\n\n### Карточная схема · HTML\nЗдесь можно свободно сочетать карточки, подзаголовки и иконки.\n```html\n<!doctype html><html lang=\"ru\"><head><style>\nbody{margin:0;padding:14px;font:14px system-ui;color:#27332c}*{box-sizing:border-box}.card{padding:15px 18px;border-radius:16px;background:#fff;border:1px solid #e1e6e2;text-align:center}.card strong{display:block;font-size:16px;margin-bottom:5px}.card small{color:#788179}.arrow{text-align:center;color:#94a298;margin:7px}.row{display:grid;grid-template-columns:repeat(3,1fr);gap:10px}.row .card{background:#eaf0ec;border-color:transparent;padding:12px 6px}.row b{display:block;margin-bottom:4px}.row small{font-size:12px}.card.accent{background:#e5eee8;border-color:#ccdbd1}@media(prefers-color-scheme:dark){body{color:#e5e7e8}.card{background:#292d32;border-color:#3b4145}.card small{color:#a8b2ac}.row .card,.card.accent{background:#283a30;border-color:#3b5043}}\n</style></head><body><section class=\"card accent\"><strong>LLM Agent</strong><small>Понимает задачу и выбирает следующий шаг</small></section><div class=\"arrow\">↓</div><section class=\"row\"><div class=\"card\"><b>⌕ Поиск</b><small>Находит контекст</small></div><div class=\"card\"><b>◇ Действие</b><small>Изменяет документ</small></div><div class=\"card\"><b>✓ Проверка</b><small>Подтверждает результат</small></div></section><div class=\"arrow\">↓</div><section class=\"card\"><strong>Журнал и доказательства</strong><small>Результаты · версии · диагностика</small></section></body></html>\n```\n\n### Пример кода\n```javascript\nconst total = rows.reduce((sum, row) => sum + row.revenue, 0);\n```\n\n### Структурированные данные · JSON\n```json\n{\n  \"report\": \"Продажи по регионам\",\n  \"currency\": \"RUB\",\n  \"regions\": [\n    { \"name\": \"Москва\", \"revenue\": 12400000, \"growth\": 0.18 },\n    { \"name\": \"Казань\", \"revenue\": 8100000, \"growth\": 0.12 }\n  ],\n  \"verified\": true\n}\n```\n\n### Формулы\nПрирост считается относительно прошлого периода:\n\n$$g = \\frac{R_{new} - R_{old}}{R_{old}} \\times 100\\%$$\n\n### Текст и пояснения\n**Что важно в отчёте**\n\n- **Источники** — видны рядом с выводами.\n- **Сравнение** — числа представлены в одинаковых единицах.\n- **Проверка** — результат отделён от предположения.\n\n> Рост выручки сам по себе не означает рост прибыли. Для вывода нужны данные о расходах.\n\n### Схема взаимодействия\n```mermaid\nsequenceDiagram\n  participant U as Пользователь\n  participant A as Агент\n  participant D as Документ\n  U->>A: Подготовь отчёт\n  A->>D: Прочитать данные\n  D-->>A: Проверенные значения\n  A-->>U: Таблица и график\n```\n"
};
