using RNAssistant.Office.Contracts;
using RNAssistant.Office.Diagnostics;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
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
    }
}
