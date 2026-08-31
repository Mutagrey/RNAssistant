using System;
using System.Collections.Generic;
using System.Threading;
using RNAssistant.Office.Domains.Excel;

namespace RNAssistant.Office.Tools
{
    internal sealed class ExcelSheetToolAdapter
    {
        private readonly ExcelSheetService _service;

        internal ExcelSheetToolAdapter(IExcelSheetBackend backend)
        {
            _service = new ExcelSheetService(
                backend ?? throw new ArgumentNullException(nameof(backend)));
        }

        internal ExcelSheetOutcome Add(
            IDictionary<string, object> arguments,
            Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            arguments = arguments ??
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            return _service.Add(new ExcelAddSheetRequest
            {
                Name = ToolArgumentReader.String(arguments, "name", "AI Sheet")
            }, markDispatchPossible, cancellationToken);
        }

        internal ExcelSheetOutcome Rename(
            IDictionary<string, object> arguments,
            Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            arguments = arguments ??
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            return _service.Rename(new ExcelRenameSheetRequest
            {
                Sheet = ToolArgumentReader.String(arguments, "sheet", string.Empty),
                NewName = ToolArgumentReader.String(arguments, "newName", string.Empty)
            }, markDispatchPossible, cancellationToken);
        }
    }
}
