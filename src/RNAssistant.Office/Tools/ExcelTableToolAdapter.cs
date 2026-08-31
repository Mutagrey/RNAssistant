using System;
using System.Collections.Generic;
using System.Threading;
using RNAssistant.Office.Domains.Excel;

namespace RNAssistant.Office.Tools
{
    internal sealed class ExcelTableToolAdapter
    {
        private readonly ExcelTableService _service;

        internal ExcelTableToolAdapter(IExcelTableBackend backend)
        {
            _service = new ExcelTableService(
                backend ?? throw new ArgumentNullException(nameof(backend)));
        }

        internal ExcelTableOutcome Add(
            IDictionary<string, object> arguments,
            Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            arguments = arguments ??
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            return _service.Add(new ExcelAddTableRequest
            {
                Sheet = ToolArgumentReader.String(
                    arguments, "sheet", string.Empty),
                SourceRange = ToolArgumentReader.String(
                    arguments, "sourceRange", "A1:B2"),
                Name = ToolArgumentReader.String(
                    arguments, "name", string.Empty),
                HasHeaders = ToolArgumentReader.Boolean(
                    arguments, "hasHeaders", true),
                Style = ToolArgumentReader.String(
                    arguments, "style", string.Empty)
            }, markDispatchPossible, cancellationToken);
        }
    }
}
