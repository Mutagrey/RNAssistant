using System;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Office.Domains.Excel;

namespace RNAssistant.Office.Tools
{
    // Temporary 7B seam. The typed owner is host-neutral; the legacy host
    // adapter exposes only internal backend commands until the bound 7D backend.
    internal sealed class ExcelReadCompatibilityBackend : IExcelReadBackend
    {
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly string _toolCallId;
        private readonly string _runtimeStepId;

        internal ExcelReadCompatibilityBackend(
            IOfficeApplicationAdapter adapter,
            string toolCallId = null,
            string runtimeStepId = null)
        {
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            _toolCallId = toolCallId;
            _runtimeStepId = runtimeStepId;
        }

        public ExcelInspectSnapshot Inspect(ExcelInspectRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var command = BackendCommand(ExcelReadToolIds.InspectBackend);
            command.Arguments["kind"] = request.Kind;
            command.Arguments["sheet"] = request.Sheet;
            command.Arguments["chartName"] = request.ChartName;
            command.Arguments["maxItems"] = request.MaxItems;
            command.Arguments["maxSeries"] = request.MaxSeries;
            return Read<ExcelInspectSnapshot>(command, "inspection");
        }

        public ExcelRangeSnapshot ReadRange(ExcelRangeReadRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var command = BackendCommand(ExcelReadToolIds.ReadRangeBackend);
            command.Arguments["sheet"] = request.Sheet;
            command.Arguments["address"] = request.Address;
            command.Arguments["content"] = request.Content;
            command.Arguments["maxCells"] = request.MaxCells;
            return Read<ExcelRangeSnapshot>(command, "range read");
        }

        private ToolCommand BackendCommand(string toolId)
        {
            return new ToolCommand
            {
                ToolId = toolId,
                ToolCallId = _toolCallId,
                RuntimeStepId = _runtimeStepId
            };
        }

        private T Read<T>(ToolCommand command, string operation) where T : class
        {
            var result = _adapter.ExecuteTool(command);
            if (result == null)
                throw new ExcelReadBackendException("Excel " + operation + " returned no result.", "excel_read_backend_missing", false);
            if (!result.Success)
                throw new ExcelReadBackendException(result.Message, result.ErrorCode, result.Retryable == true, result.DataJson);
            if (string.IsNullOrWhiteSpace(result.DataJson))
                throw new ExcelReadBackendException("Excel " + operation + " returned no data.", "excel_read_snapshot_invalid", false);
            try
            {
                var snapshot = JsonConvert.DeserializeObject<T>(result.DataJson,
                    new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });
                if (snapshot == null) throw new JsonException("Snapshot is null.");
                return snapshot;
            }
            catch (JsonException ex)
            {
                throw new ExcelReadBackendException("Excel " + operation + " returned malformed data: " + ex.Message,
                    "excel_read_snapshot_invalid", false);
            }
        }
    }
}
