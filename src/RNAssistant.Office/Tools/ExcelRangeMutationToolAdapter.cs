using System;
using System.Collections.Generic;
using System.Threading;
using RNAssistant.Office.Domains.Excel;

namespace RNAssistant.Office.Tools
{
    internal sealed class ExcelRangeMutationToolAdapter
    {
        private readonly ExcelRangeMutationService _service;

        internal ExcelRangeMutationToolAdapter(IExcelRangeMutationBackend backend)
        {
            _service = new ExcelRangeMutationService(
                backend ?? throw new ArgumentNullException(nameof(backend)));
        }

        internal ExcelRangeMutationOutcome Execute(
            string toolId,
            IDictionary<string, object> arguments,
            Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            arguments = arguments ??
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            switch (toolId)
            {
                case ExcelRangeMutationToolIds.ClearRange:
                    return _service.Clear(new ExcelClearRangeRequest
                    {
                        Sheet = ToolArgumentReader.String(
                            arguments, "sheet", string.Empty),
                        Address = ToolArgumentReader.String(
                            arguments, "address", string.Empty),
                        ClearWhat = ToolArgumentReader.String(
                            arguments, "clearWhat", "values")
                    }, markDispatchPossible, cancellationToken);
                case ExcelRangeMutationToolIds.SortRange:
                    return _service.Sort(new ExcelSortRangeRequest
                    {
                        Sheet = ToolArgumentReader.String(
                            arguments, "sheet", string.Empty),
                        Address = ToolArgumentReader.String(
                            arguments, "address", string.Empty),
                        KeyColumn = Math.Max(1, ToolArgumentReader.Int32(
                            arguments, "keyColumn", 1)),
                        Descending = ToolArgumentReader.Boolean(
                            arguments, "descending", false),
                        HasHeaders = ToolArgumentReader.Boolean(
                            arguments, "hasHeaders", true)
                    }, markDispatchPossible, cancellationToken);
                case ExcelRangeMutationToolIds.FilterRange:
                    return _service.Filter(new ExcelFilterRangeRequest
                    {
                        Sheet = ToolArgumentReader.String(
                            arguments, "sheet", string.Empty),
                        Address = ToolArgumentReader.String(
                            arguments, "address", string.Empty),
                        Field = Math.Max(1, ToolArgumentReader.Int32(
                            arguments, "field", 1)),
                        Criteria = ToolArgumentReader.String(
                            arguments, "criteria", string.Empty)
                    }, markDispatchPossible, cancellationToken);
                case ExcelRangeMutationToolIds.FormatRange:
                    return _service.Format(new ExcelFormatRangeRequest
                    {
                        Sheet = ToolArgumentReader.String(
                            arguments, "sheet", string.Empty),
                        Address = ToolArgumentReader.String(
                            arguments, "address", string.Empty),
                        HasNumberFormat = arguments.ContainsKey("numberFormat"),
                        NumberFormat = ToolArgumentReader.String(
                            arguments, "numberFormat", string.Empty),
                        HasBold = arguments.ContainsKey("bold"),
                        Bold = ToolArgumentReader.Boolean(arguments, "bold", false),
                        HasItalic = arguments.ContainsKey("italic"),
                        Italic = ToolArgumentReader.Boolean(arguments, "italic", false),
                        HasFillColor = arguments.ContainsKey("fillColor"),
                        FillColor = ToolArgumentReader.String(
                            arguments, "fillColor", string.Empty),
                        HasFontColor = arguments.ContainsKey("fontColor"),
                        FontColor = ToolArgumentReader.String(
                            arguments, "fontColor", string.Empty),
                        HasHorizontalAlignment =
                            arguments.ContainsKey("horizontalAlignment"),
                        HorizontalAlignment = ToolArgumentReader.String(
                            arguments, "horizontalAlignment", string.Empty),
                        AutoFit = ToolArgumentReader.String(
                            arguments, "autoFit", string.Empty)
                    }, markDispatchPossible, cancellationToken);
                default:
                    throw new ArgumentException(
                        "Unsupported Excel range mutation tool: " + toolId,
                        nameof(toolId));
            }
        }
    }
}
