using System;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Llm;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Diagnostics;
using RNAssistant.Office.Services;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        internal event Action<LlmRequestDiagnosticUpdate> ModelRequestDiagnostics;

        public Task<ModelConnectionTestResponse> TestModelConnectionAsync(CancellationToken cancellationToken)
        {
            return new ModelConnectionTestService(_llmCompletion).TestAsync(
                _settingsService.Load(),
                cancellationToken);
        }

        public RuntimeLogResponse GetRuntimeLog()
        {
            return new RuntimeLogResponse
            {
                Content = RuntimeLog.ReadTail(1000000),
                Path = RuntimeLog.FilePath
            };
        }

        public RuntimeLogResponse ClearRuntimeLog()
        {
            RuntimeLog.Clear();
            return GetRuntimeLog();
        }

        public CasHealthResponse GetCasHealth()
        {
            return CasHealthResponse.From(_casMaintenanceService.Audit());
        }

        public CasGarbageCollectionResponse CollectCasGarbage()
        {
            var result = _casMaintenanceService.Collect();
            RuntimeLog.Info("CAS GC: deleted " + result.DeletedBlobCount + " blob(s), " +
                result.DeletedStoredByteLength + " stored byte(s); completed=" + result.Completed + ".");
            return CasGarbageCollectionResponse.From(result);
        }

        private void ReportModelRequestDiagnostics(LlmRequestDiagnosticUpdate update)
        {
            var handler = ModelRequestDiagnostics;
            if (handler != null) handler(update);
        }
    }
}
