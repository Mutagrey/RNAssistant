using System;
using RNAssistant.Core.Models;
using RNAssistant.Office.Vba;

namespace RNAssistant.Office.Tools
{
    internal sealed partial class VbaToolExecutor
    {
        private void RecordObservation(ChatSession session, string moduleName, string hash)
        {
            _mutationService.RecordObservation(SessionId(session), moduleName, hash);
        }
    }
}
