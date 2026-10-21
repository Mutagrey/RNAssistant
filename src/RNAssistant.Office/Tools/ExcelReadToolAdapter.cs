using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
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
            IDictionary<string, object> arguments,
            CancellationToken cancellationToken = default(CancellationToken))
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
            if (string.Equals(toolId, ExcelReadToolIds.SummarizeColumns, StringComparison.Ordinal))
            {
                object raw;
                arguments.TryGetValue("columns", out raw);
                var values = raw as JArray ?? (raw == null ? null : JToken.FromObject(raw) as JArray);
                var columns = values == null ? null : values.Select(value =>
                    value.Type == JTokenType.String ? value.Value<string>() : null).ToList();
                return new ExcelColumnAnalysisService(_backend).Analyze(
                    ToolArgumentReader.String(arguments, "sheet", string.Empty),
                    ToolArgumentReader.String(arguments, "address", string.Empty),
                    columns, cancellationToken);
            }
            return ExcelReadOutcome.Fail("Unsupported Excel read tool: " + toolId,
                "{\"code\":\"unknown_tool\",\"retryable\":false}", "unknown_tool", false);
        }
    }
}
