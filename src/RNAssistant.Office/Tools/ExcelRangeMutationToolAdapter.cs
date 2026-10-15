using RNAssistant.Office.Domains;
using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Office.Domains.Excel;

namespace RNAssistant.Office.Tools
{
    internal sealed class ExcelRangeMutationToolAdapter
    {
        private readonly ExcelRangeMutationService _service;
        private readonly ExcelDeleteRowsColumnsService _deleteService;

        internal ExcelRangeMutationToolAdapter(IExcelRangeMutationBackend backend)
        {
            _service = new ExcelRangeMutationService(
                backend ?? throw new ArgumentNullException(nameof(backend)));
            _deleteService = new ExcelDeleteRowsColumnsService(backend);
        }

        internal DomainOutcome Execute(
            string toolId,
            IDictionary<string, object> arguments,
            Action markDispatchPossible,
            CancellationToken cancellationToken, string preparedStateJson = null)
        {
            arguments = arguments ??
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            switch (toolId)
            {
                case ExcelRangeMutationToolIds.DeleteRowsColumns:
                    return _deleteService.Delete(DeletionRequest(arguments), markDispatchPossible, cancellationToken,
                        string.IsNullOrWhiteSpace(preparedStateJson) ? null : JsonConvert.DeserializeObject<ExcelDeletionPreparedState>(preparedStateJson));
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

        internal ExcelDeletionPreparedState PrepareDeletion(IDictionary<string, object> arguments, CancellationToken cancellationToken)
        { return _deleteService.Prepare(DeletionRequest(arguments), cancellationToken); }

        private static ExcelDeleteRowsColumnsRequest DeletionRequest(IDictionary<string, object> arguments)
        {
            var scope = ToolArgumentReader.String(arguments, "scope", string.Empty);
            var axis = ToolArgumentReader.String(arguments, "axis", string.Empty);
            return new ExcelDeleteRowsColumnsRequest
            {
                Sheet = ToolArgumentReader.String(arguments, "sheet", string.Empty),
                Table = ToolArgumentReader.String(arguments, "table", null),
                Scope = scope == "table" ? ExcelDeletionScope.Table : scope == "worksheet" ? ExcelDeletionScope.Worksheet : (ExcelDeletionScope)(-1),
                Axis = axis == "rows" ? ExcelDeletionAxis.Rows : axis == "columns" ? ExcelDeletionAxis.Columns : (ExcelDeletionAxis)(-1),
                Start = ToolArgumentReader.Int32(arguments, "start", 0),
                Count = ToolArgumentReader.Int32(arguments, "count", 0)
            };
        }
    }
}
