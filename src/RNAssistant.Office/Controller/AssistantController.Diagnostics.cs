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
            if (update != null && update.TotalMs.GetValueOrDefault() >= 1000)
            {
                var headerWaitMs = update.ResponseHeadersMs.HasValue && update.PreparationMs.HasValue
                    ? update.ResponseHeadersMs.Value - update.PreparationMs.Value : -1;
                var firstDataWaitMs = update.FirstChunkMs.HasValue && update.ResponseHeadersMs.HasValue
                    ? update.FirstChunkMs.Value - update.ResponseHeadersMs.Value : -1;
                RuntimeLog.Info("Model request timing: id=" + update.RequestId + ", prepare=" +
                    (update.PreparationMs.HasValue ? update.PreparationMs.Value : -1) +
                    "ms, headersWait=" + headerWaitMs + "ms, firstDataWait=" +
                    firstDataWaitMs + "ms, total=" + update.TotalMs.Value +
                    "ms, requestBytes=" + (update.RequestBytes.HasValue ? update.RequestBytes.Value : -1) +
                    ", http=" + (update.StatusCode.HasValue ? update.StatusCode.Value : -1) +
                    ", phase=" + update.Phase + ".");
            }
            var handler = ModelRequestDiagnostics;
            if (handler != null) handler(update);
        }
    }
}
