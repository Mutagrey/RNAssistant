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

        private void ReportModelRequestDiagnostics(LlmRequestDiagnosticUpdate update)
        {
            var handler = ModelRequestDiagnostics;
            if (handler != null) handler(update);
        }
    }
}
