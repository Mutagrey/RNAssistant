using System;
using System.Collections.Generic;
using RNAssistant.Office.Domains.Excel;

namespace RNAssistant.Office.Tools
{
    internal sealed class ExcelReadToolAdapter
    {
        private readonly IExcelReadBackend _backend;

        internal ExcelReadToolAdapter(IExcelReadBackend backend)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        internal ExcelReadOutcome ExecuteOutcome(
            string toolId,
            IDictionary<string, object> arguments)
        {
            arguments = arguments ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var service = new ExcelReadService(_backend);
            if (string.Equals(toolId, ExcelReadToolIds.Inspect, StringComparison.Ordinal))
            {
                return service.Inspect(
                    ToolArgumentReader.String(arguments, "kind", string.Empty),
                    ToolArgumentReader.String(arguments, "sheet", string.Empty),
                    ToolArgumentReader.String(arguments, "chartName", string.Empty));
            }
            return ExcelReadOutcome.Fail("Unsupported Excel read tool: " + toolId,
                "{\"code\":\"unknown_tool\",\"retryable\":false}", "unknown_tool", false);
        }
    }
}
