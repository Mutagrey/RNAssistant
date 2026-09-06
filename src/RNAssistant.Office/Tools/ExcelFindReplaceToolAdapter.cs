using System;
using System.Collections.Generic;
using System.Threading;
using RNAssistant.Office.Domains.Excel;

namespace RNAssistant.Office.Tools
{
    internal sealed class ExcelFindReplaceToolAdapter
    {
        private readonly ExcelFindReplaceService _service;

        internal ExcelFindReplaceToolAdapter(IExcelFindReplaceBackend backend)
        {
            _service = new ExcelFindReplaceService(
                backend ?? throw new ArgumentNullException(nameof(backend)));
        }

        internal ExcelReplaceOutcome Replace(
            IDictionary<string, object> arguments,
            Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            arguments = arguments ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            return _service.Replace(new ExcelReplaceRequest
            {
                Sheet = ToolArgumentReader.String(arguments, "sheet", string.Empty),
                Address = ToolArgumentReader.String(arguments, "address", string.Empty),
                Scope = ToolArgumentReader.String(arguments, "scope", string.Empty),
                Find = ToolArgumentReader.String(arguments, "find", string.Empty),
                Replacement = ToolArgumentReader.String(arguments, "replace", string.Empty),
                Mode = ToolArgumentReader.String(arguments, "mode", "literal"),
                MatchCase = ToolArgumentReader.Boolean(arguments, "matchCase", false),
                WholeWord = ToolArgumentReader.Boolean(arguments, "wholeWord", false),
                LookIn = ToolArgumentReader.String(arguments, "lookIn", "values"),
                ReplaceAll = ToolArgumentReader.Boolean(arguments, "replaceAll", true),
                MaxReplacements = ToolArgumentReader.Int32(arguments, "maxReplacements", 500)
            }, markDispatchPossible, cancellationToken);
        }
    }
}
