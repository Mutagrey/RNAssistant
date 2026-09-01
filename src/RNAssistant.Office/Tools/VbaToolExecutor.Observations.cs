using System;
using RNAssistant.Core.Models;
using RNAssistant.Office.Vba;

namespace RNAssistant.Office.Tools
{
    internal sealed partial class VbaToolExecutor
    {
        private void RecordObservationFromModule(
            ChatSession session,
            string moduleName,
            VbaModuleState module)
        {
            if (module == null || string.IsNullOrWhiteSpace(module.CodeSha256)) return;
            RecordObservation(
                session,
                string.IsNullOrWhiteSpace(module.Name) ? moduleName : module.Name,
                module.CodeSha256);
        }

        private void RecordObservation(ChatSession session, string moduleName, string hash)
        {
            _mutationService.RecordObservation(SessionId(session), moduleName, hash);
        }
    }
}
