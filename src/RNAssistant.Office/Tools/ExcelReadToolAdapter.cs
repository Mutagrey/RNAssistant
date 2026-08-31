using System;
using System.Collections.Generic;
using System.Threading;
using RNAssistant.Core.Models;
using RNAssistant.Office.Domains.Excel;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    internal sealed class ExcelReadToolAdapter
    {
        private readonly IExcelReadBackend _backend;

        internal ExcelReadToolAdapter(IExcelReadBackend backend)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        internal RuntimeResult Execute(
            string toolId,
            IDictionary<string, object> arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = ExecuteOutcome(toolId, arguments);
            return outcome.Success
                ? RuntimeResult.Ok(outcome.Message, outcome.DataJson)
                : RuntimeResult.Error(outcome.Message, outcome.DataJson);
        }

        internal ToolResult ExecuteDataSource(ToolCommand command, CancellationToken cancellationToken)
        {
            if (command == null) return ToolResult.Fail("Excel read command is empty.", null, "excel_read_command_missing", false);
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = ExecuteOutcome(command.ToolId, command.Arguments);
            return outcome.Success
                ? ToolResult.Ok(outcome.Message, outcome.DataJson)
                : ToolResult.Fail(outcome.Message, outcome.DataJson, outcome.ErrorCode, outcome.Retryable);
        }

        private ExcelReadOutcome ExecuteOutcome(
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
            if (string.Equals(toolId, ExcelReadToolIds.ReadRange, StringComparison.Ordinal))
            {
                return service.ReadRange(
                    ToolArgumentReader.String(arguments, "sheet", string.Empty),
                    ToolArgumentReader.String(arguments, "address", string.Empty),
                    ToolArgumentReader.String(arguments, "content", "values"));
            }
            return ExcelReadOutcome.Fail("Unsupported Excel read tool: " + toolId,
                "{\"code\":\"unknown_tool\",\"retryable\":false}", "unknown_tool", false);
        }
    }
}
