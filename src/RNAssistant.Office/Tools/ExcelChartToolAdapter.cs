using System;
using System.Collections.Generic;
using System.Threading;
using RNAssistant.Office.Domains.Excel;

namespace RNAssistant.Office.Tools
{
    internal sealed class ExcelChartToolAdapter
    {
        private readonly ExcelChartService _service;

        internal ExcelChartToolAdapter(IExcelChartBackend backend)
        {
            _service = new ExcelChartService(
                backend ?? throw new ArgumentNullException(nameof(backend)));
        }

        internal ExcelChartOutcome Execute(
            string toolId,
            IDictionary<string, object> arguments,
            Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            arguments = arguments ??
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (string.Equals(
                toolId, ExcelChartToolIds.CreateChatChart,
                StringComparison.Ordinal))
                return _service.CreateChatChart(new ExcelChatChartRequest
                {
                    Sheet = ToolArgumentReader.String(
                        arguments, "sheet", string.Empty),
                    Address = ToolArgumentReader.String(
                        arguments, "address", string.Empty),
                    ChartType = ToolArgumentReader.String(
                        arguments, "chartType", "auto"),
                    Title = ToolArgumentReader.String(
                        arguments, "title", "Excel chart")
                }, cancellationToken);
            return _service.Mutate(new ExcelChartMutationRequest
            {
                ToolId = toolId,
                Mode = ToolArgumentReader.String(arguments, "mode", "upsert"),
                Sheet = ToolArgumentReader.String(arguments, "sheet", string.Empty),
                ChartName = ToolArgumentReader.String(
                    arguments, "chartName", string.Empty),
                HasSourceRange = arguments.ContainsKey("sourceRange"),
                SourceRange = ToolArgumentReader.String(
                    arguments, "sourceRange", string.Empty),
                HasChartType = arguments.ContainsKey("chartType"),
                ChartType = ToolArgumentReader.String(
                    arguments, "chartType", string.Empty),
                HasTitle = arguments.ContainsKey("title"),
                Title = ToolArgumentReader.String(arguments, "title", string.Empty),
                HasCategoryLabelsRange =
                    arguments.ContainsKey("categoryLabelsRange"),
                CategoryLabelsRange = ToolArgumentReader.String(
                    arguments, "categoryLabelsRange", string.Empty),
                HasXAxisTitle = arguments.ContainsKey("xAxisTitle"),
                XAxisTitle = ToolArgumentReader.String(
                    arguments, "xAxisTitle", string.Empty),
                HasYAxisTitle = arguments.ContainsKey("yAxisTitle"),
                YAxisTitle = ToolArgumentReader.String(
                    arguments, "yAxisTitle", string.Empty),
                HasLeft = arguments.ContainsKey("left"),
                Left = ToolArgumentReader.Int32(arguments, "left", 0),
                HasTop = arguments.ContainsKey("top"),
                Top = ToolArgumentReader.Int32(arguments, "top", 0),
                HasWidth = arguments.ContainsKey("width"),
                Width = ToolArgumentReader.Int32(arguments, "width", 0),
                HasHeight = arguments.ContainsKey("height"),
                Height = ToolArgumentReader.Int32(arguments, "height", 0)
            }, markDispatchPossible, cancellationToken);
        }
    }
}
