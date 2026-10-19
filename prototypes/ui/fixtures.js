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
