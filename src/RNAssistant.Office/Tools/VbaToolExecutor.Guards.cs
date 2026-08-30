using System;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Office.Vba;

namespace RNAssistant.Office.Tools
{
    internal sealed partial class VbaToolExecutor
    {
        private bool IsPublicMutation(string toolId)
        {
            return string.Equals(toolId, ToolId("vba_write_module"), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, ToolId("vba_apply_patch"), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, ToolId("vba_delete_module"), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, ToolId("vba_restore_backup"), StringComparison.OrdinalIgnoreCase);
        }

        private bool IsPreflightMutation(string toolId)
        {
            return IsPublicMutation(toolId) ||
                string.Equals(toolId, ToolId("office_run_macro"), StringComparison.OrdinalIgnoreCase);
        }

        private static VbaMutationGuard ReadGuard(ToolCommand command)
        {
            if (command == null || string.IsNullOrWhiteSpace(command.RuntimeGuardJson)) return null;
            try
            {
                return JsonConvert.DeserializeObject<VbaMutationGuard>(command.RuntimeGuardJson);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static VbaRestoreGuard ReadRestoreGuard(ToolCommand command)
        {
            if (command == null ||
                string.IsNullOrWhiteSpace(command.RuntimeGuardJson)) return null;
            try
            {
                return JsonConvert.DeserializeObject<VbaRestoreGuard>(
                    command.RuntimeGuardJson);
            }
            catch (JsonException)
            {
                return null;
            }
        }

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
