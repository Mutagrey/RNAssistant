using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using RNAssistant.Office.Domains.Excel;

namespace RNAssistant.Office.Tools
{
    internal sealed class ExcelWriteToolAdapter
    {
        private readonly IExcelWriteBackend _backend;

        internal ExcelWriteToolAdapter(IExcelWriteBackend backend)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        internal ExcelWriteOutcome Execute(IDictionary<string, object> arguments,
            Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            arguments = arguments ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            object value;
            var hasValue = arguments.TryGetValue("value", out value);
            return new ExcelWriteService(_backend).Write(new ExcelWriteRequest
                {
                    Kind = ToolArgumentReader.String(arguments, "kind", string.Empty),
                    Sheet = ToolArgumentReader.String(arguments, "sheet", string.Empty),
                    Address = ToolArgumentReader.String(arguments, "address", "A1"),
                    HasValue = hasValue,
                    Value = value,
                    Formula = arguments.ContainsKey("formula")
                        ? ToolArgumentReader.String(arguments, "formula", null) : null,
                    Values = arguments.ContainsKey("values") ? ReadTable(arguments["values"]) : null
                }, markDispatchPossible, cancellationToken);
        }

        private static IReadOnlyList<IReadOnlyList<object>> ReadTable(object raw)
        {
            var token = raw as JToken;
            if (token == null)
            {
                try { token = raw == null ? null : JToken.FromObject(raw); }
                catch (Exception) { return null; }
            }
            var rows = token as JArray;
            if (rows == null) return null;
            var result = new List<IReadOnlyList<object>>(rows.Count);
            foreach (var rowToken in rows)
            {
                var row = rowToken as JArray;
                if (row == null) return null;
                result.Add(row.Select(CellValue).ToList());
            }
            return result;
        }

        private static object CellValue(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined) return null;
            var value = token as JValue;
            return value == null ? token : value.Value;
        }
    }
}
