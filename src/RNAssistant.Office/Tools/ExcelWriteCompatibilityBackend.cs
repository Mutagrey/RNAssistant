using System;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Domains.Excel;

namespace RNAssistant.Office.Tools
{
    // Temporary 7C seam over the current host adapter. 7D replaces these
    // internal commands with a backend bound to ExcelDocumentSession.
    internal sealed class ExcelWriteCompatibilityBackend : IExcelWriteBackend
    {
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly string _toolCallId;
        private readonly string _runtimeStepId;

        internal ExcelWriteCompatibilityBackend(IOfficeApplicationAdapter adapter,
            string toolCallId, string runtimeStepId)
        {
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            _toolCallId = toolCallId;
            _runtimeStepId = runtimeStepId;
        }

        public ExcelWriteSnapshot Read(ExcelWriteReadRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var command = Command(ExcelWriteToolIds.ReadBackend);
            AddTarget(command, request.Kind, request.Sheet, request.Address,
                request.Rows, request.Columns, request.MaxCells);
            var result = _adapter.ExecuteTool(command);
            EnsureSuccess(result, "state read");
            if (string.IsNullOrWhiteSpace(result.DataJson))
                throw new ExcelWriteBackendException("Excel write state read returned no data.",
                    "excel_write_snapshot_invalid", false);
            try
            {
                var snapshot = JsonConvert.DeserializeObject<ExcelWriteSnapshot>(result.DataJson,
                    new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });
                if (snapshot == null) throw new JsonException("Snapshot is null.");
                return snapshot;
            }
            catch (JsonException ex)
            {
                throw new ExcelWriteBackendException("Excel write state read returned malformed data: " + ex.Message,
                    "excel_write_snapshot_invalid", false);
            }
        }

        public void Apply(ExcelWriteApplyRequest request, Action markDispatchPossible)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (markDispatchPossible == null) throw new ArgumentNullException(nameof(markDispatchPossible));
            var command = Command(ExcelWriteToolIds.ApplyBackend);
            AddTarget(command, request.Kind, request.Sheet, request.Address,
                request.Rows, request.Columns, request.MaxCells);
            if (request.Kind == "value") command.Arguments["value"] = request.Value;
            else if (request.Kind == "formula") command.Arguments["formula"] = request.Formula;
            else command.Arguments["values"] = JArray.FromObject(request.Values);
            command.Arguments["dispatchBoundary"] = new DispatchBoundary(markDispatchPossible);
            EnsureSuccess(_adapter.ExecuteTool(command), "apply");
        }

        private ToolCommand Command(string toolId)
        {
            return new ToolCommand
            {
                ToolId = toolId,
                ToolCallId = _toolCallId,
                RuntimeStepId = _runtimeStepId
            };
        }

        private static void AddTarget(ToolCommand command, string kind, string sheet, string address,
            int rows, int columns, int maxCells)
        {
            command.Arguments["kind"] = kind;
            command.Arguments["sheet"] = sheet;
            command.Arguments["address"] = address;
            command.Arguments["rows"] = rows;
            command.Arguments["columns"] = columns;
            command.Arguments["maxCells"] = maxCells;
        }

        private static void EnsureSuccess(ToolResult result, string operation)
        {
            if (result == null)
                throw new ExcelWriteBackendException("Excel write " + operation + " returned no result.",
                    "excel_write_backend_missing", false);
            if (!result.Success)
                throw new ExcelWriteBackendException(result.Message, result.ErrorCode,
                    result.Retryable == true, result.DataJson);
        }

        private sealed class DispatchBoundary : IExcelWriteDispatchBoundary
        {
            private readonly Action _mark;
            private int _marked;

            internal DispatchBoundary(Action mark)
            {
                _mark = mark;
            }

            public void Mark()
            {
                if (Interlocked.Exchange(ref _marked, 1) == 0) _mark();
            }
        }
    }
}
