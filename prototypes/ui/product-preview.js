/* Local visual fixture for the existing product UI. This file is never loaded by web/index.html. */
(function () {
  var frame = document.getElementById("product");
  var sampleCode = [
    "Option Explicit", "", "Public Sub FormatSalesReport()", "    Dim report As Worksheet",
    "    Set report = ThisWorkbook.Worksheets(\"Продажи\")", "", "    With report.Range(\"A1:F1\")",
    "        .Font.Bold = True", "        .Interior.Color = RGB(232, 241, 233)", "    End With",
    "", "    report.Columns(\"A:F\").AutoFit", "End Sub", ""
  ].join("\n");
  var skillBody = [
    "# Анализ продаж", "", "Сравнивай период с предыдущим.", "",
    "1. Проверь наличие нужных колонок.", "2. Укажи период, валюту и источник.",
    "3. Раздели факты и предположения.", "4. Покажи исключения и пропуски.",
    "5. Сформируй краткий отчёт с таблицей.", ""
  ].join("\n");

  frame.addEventListener("load", function () {
    var app = frame.contentWindow;
    if (!app || !app.state || typeof app.switchTab !== "function") return;
    Promise.resolve(app.state.initializePromise).catch(function () {}).then(function () {
      var state = app.state;
      state.bridgeUnavailable = false;
      state.host = "Excel";
      state.title = "Продажи · сентябрь.xlsx";
      state.activeChatId = "visual-preview";
      state.skills = [{
        Id: "excel.sales_analysis", Name: "Анализ продаж", Host: "Excel",
        Description: "Сравнение периодов и анализ продаж по регионам", Enabled: true,
        Revision: "preview", References: [{ Path: "references/metrics.md", Revision: "preview", ByteLength: 97 }],
        _sourceDrafts: { "": skillBody, "references/metrics.md": "# Метрики\n\nВыручка, заказы и средний чек.\n" },
        _sourceLoaded: { "": true, "references/metrics.md": true }
      }];
      state.tools = [{
        Id: "excel.format_sales", Name: "excel.format_sales", Host: "Excel", Executor: "vba",
        Description: "Применяет оформление к заголовкам таблицы в связанном документе.",
        Enabled: true, RequiresConfirmation: true, Revision: "preview", Scope: "global",
        ArgumentSchemaJson: '{"type":"object","properties":{"sheet":{"type":"string"}},"additionalProperties":false}',
        Components: [{ Name: "FormatSales", Type: "StdModule", FileName: "FormatSales.bas", Code: sampleCode }],
        _sourceLoaded: true
      }];
      state.vba = {
        modules: [
          { name: "FormatSales", type: "StdModule", code: sampleCode, lineCount: 14 },
          { name: "ThisWorkbook", type: "Document", code: "Option Explicit\n", lineCount: 1 },
          { name: "Sheet1", type: "Document", code: "Option Explicit\n", lineCount: 1 }
        ],
        backups: [], selectedModule: "FormatSales"
      };
      state.selectedInstructionKind = "skill";
      state.selectedSkillIndex = 0;
      state.selectedToolIndex = 0;
      if (state.syncTimer) { app.clearInterval(state.syncTimer); state.syncTimer = null; }
      app.switchTab("instructions");
    });
  });
})();
