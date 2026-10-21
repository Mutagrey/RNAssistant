using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.ModelProtocol;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private sealed class ExcelReportWorkflowFixture
        {
            internal const string Task = "По исходным строкам Data!A1:C7 создай новый лист SalesReport. " +
                "В A1:B4 запиши сводку: сумма Revenue по Region в алфавитном порядке регионов, " +
                "с заголовками Region и Revenue; результаты должны быть числами без формул. " +
                "Оформи A1:B4 как таблицу SalesSummary, а B2:B4 — числовым форматом #,##0. " +
                "На этом же листе создай столбчатую диаграмму SalesByRegion с заголовком Revenue by region " +
                "по A1:B4. Исходные листы и данные сохрани. Проверь результат и кратко сообщи итог.";

            private readonly FakeOfficeAdapter _adapter;
            internal JObject Before { get; private set; }

            internal ExcelReportWorkflowFixture(FakeOfficeAdapter adapter)
            {
                _adapter = adapter;
                var source = new object[][] {
                    new object[] { "Region", "Product", "Revenue" },
                    new object[] { "North", "A", 120 }, new object[] { "South", "B", 80 },
                    new object[] { "North", "B", 30 }, new object[] { "West", "A", 70 },
                    new object[] { "South", "A", 20 }, new object[] { "West", "B", 40 }
                };
                for (var row = 0; row < source.Length; row++)
                    for (var column = 0; column < source[row].Length; column++)
                        adapter.SetExcelCellForTest("Data", ((char)('A' + column)).ToString() + (row + 1), source[row][column]);
                Before = adapter.ExcelWorkbookStateForTest();
            }

            internal Dictionary<string, bool> Checks()
            {
                return CheckWorkbook(Before, _adapter.ExcelWorkbookStateForTest());
            }

            internal static Dictionary<string, bool> CheckWorkbook(JObject before, JObject after)
            {
                var sheets = (JArray)after["sheets"];
                var original = (JArray)before["sheets"];
                var report = sheets.OfType<JObject>().SingleOrDefault(sheet => (string)sheet["name"] == "SalesReport");
                var cells = report?["cells"] as JObject;
                var expected = JObject.Parse("{ '1:1':'Region', '1:2':'Revenue', '2:1':'North', '2:2':150, '3:1':'South', '3:2':100, '4:1':'West', '4:2':110 }");
                var values = cells != null && cells.Count == expected.Count && expected.Properties().All(property =>
                    property.Value.Type == JTokenType.Integer
                        ? (cells[property.Name]?.Type == JTokenType.Integer || cells[property.Name]?.Type == JTokenType.Float) &&
                            (decimal)cells[property.Name] == (decimal)property.Value
                        : JToken.DeepEquals(property.Value, cells[property.Name]));
                var table = (report?["tables"] as JArray)?.OfType<JObject>().SingleOrDefault(item => (string)item["Name"] == "SalesSummary");
                var chart = (report?["charts"] as JArray)?.OfType<JObject>().SingleOrDefault(item => (string)item["Name"] == "SalesByRegion");
                var format = (string)after["formats"]?["SalesReport!B2:B4"];
                return new Dictionary<string, bool>
                {
                    { "sourcePreserved", original.All(sheet => sheets.Any(current => JToken.DeepEquals(sheet, current))) &&
                        new[] { "formats", "filters", "autoFits" }.All(section => JToken.DeepEquals(before[section],
                            new JObject(((JObject)after[section]).Properties().Where(property =>
                                !property.Name.StartsWith("SalesReport!", StringComparison.OrdinalIgnoreCase))))) },
                    { "onlyRequestedSheetAdded", report != null && sheets.Count == original.Count + 1 },
                    { "numericSummary", values && report["formulas"] is JArray formulas && formulas.Count == 0 },
                    { "table", table != null && (string)table["Range"] == "A1:B4" && (bool)table["HasHeaders"] &&
                        (int)table["Rows"] == 4 && (int)table["Columns"] == 2 },
                    { "numberFormat", format != null && (string)JObject.Parse(format)["numberFormat"] == "#,##0" },
                    { "chart", chart != null && (string)chart["SourceRange"] == "A1:B4" &&
                        (string)chart["ChartType"] == "column" && (bool)chart["HasTitle"] &&
                        (string)chart["Title"] == "Revenue by region" }
                };
            }

            internal static Queue<string> Script()
            {
                Func<string, JObject, string> call = (name, arguments) => ModelProtocolWire.Write("", new[] {
                    new ConversationToolCall { Name = name, Arguments = arguments } });
                return new Queue<string>(new[] {
                    LoadSkillResponse("excel.analysis_reporting"),
                    call("common.resources_read", new JObject { ["target"] = "Excel range: Data!A1:C7", ["representation"] = "table" }),
                    call("excel.add_sheet", new JObject { ["name"] = "SalesReport" }),
                    call("excel.write_range", new JObject { ["kind"] = "table", ["sheet"] = "SalesReport", ["address"] = "A1",
                        ["values"] = JArray.Parse("[['Region','Revenue'],['North',150],['South',100],['West',110]]") }),
                    call("excel.add_table", new JObject { ["sheet"] = "SalesReport", ["sourceRange"] = "A1:B4", ["name"] = "SalesSummary" }),
                    call("excel.format_range", new JObject { ["sheet"] = "SalesReport", ["address"] = "B2:B4", ["numberFormat"] = "#,##0" }),
                    call("excel.upsert_chart", new JObject { ["sheet"] = "SalesReport", ["sourceRange"] = "A1:B4", ["chartName"] = "SalesByRegion",
                        ["chartType"] = "column", ["title"] = "Revenue by region" }),
                    call("common.resources_read", new JObject { ["target"] = "Excel range: SalesReport!A1:B4", ["representation"] = "table" }),
                    ModelProtocolWire.Write("Создан и проверен отчёт. Реальный Excel и внешний вид диаграммы не проверялись.", new ConversationToolCall[0])
                });
            }
        }

        private static void ExcelReportAcceptanceRejectsIncompleteState()
        {
            var fixture = new ExcelReportWorkflowFixture(FakeOfficeAdapter.ForHost("Excel"));
            var after = (JObject)fixture.Before.DeepClone();
            ((JArray)after["sheets"]).Add(JObject.Parse(@"{
                'name':'SalesReport', 'cells':{'1:1':'Region','1:2':'Revenue','2:1':'North','2:2':150,'3:1':'South','3:2':100,'4:1':'West','4:2':110},
                'formulas':[], 'tables':[{'Name':'SalesSummary','Range':'A1:B4','HasHeaders':true,'Rows':4,'Columns':2}],
                'charts':[{'Name':'SalesByRegion','SourceRange':'A1:B4','ChartType':'column','HasTitle':true,'Title':'Revenue by region'}]
            }"));
            after["formats"]["SalesReport!B2:B4"] = "{\"numberFormat\":\"#,##0\"}";
            AssertTrue(ExcelReportWorkflowFixture.CheckWorkbook(fixture.Before, after).Values.All(value => value), "complete numeric report is accepted");
            var corruptions = new Dictionary<string, Action<JObject>> {
                { "numericSummary", state => state["sheets"].Last["cells"]["2:2"] = "150" },
                { "sourcePreserved", state => state["sheets"][0]["cells"]["2:3"] = 999 },
                { "table", state => ((JArray)state["sheets"].Last["tables"]).Clear() },
                { "numberFormat", state => state["formats"]["SalesReport!B2:B4"] = "{}" },
                { "chart", state => state["sheets"].Last["charts"][0]["SourceRange"] = "A1:B3" }
            };
            foreach (var corruption in corruptions)
            {
                var broken = (JObject)after.DeepClone(); corruption.Value(broken);
                AssertTrue(!ExcelReportWorkflowFixture.CheckWorkbook(fixture.Before, broken)[corruption.Key], "reject missing/incorrect report state: " + corruption.Key);
            }
        }
    }
}
