using RNAssistant.Core.Models;

namespace RNAssistant.Office.Tools
{
    internal static class ToolSafetyPolicy
    {
        public static bool RequiresConfirmation(ToolDefinition tool, AppSettings settings, bool dryRun, bool manualRun)
        {
            if (dryRun || manualRun || tool == null)
            {
                return false;
            }

            var effectiveSettings = settings ?? new AppSettings();
            if (effectiveSettings.AutoConfirmToolActions)
            {
                return false;
            }

            if (tool.RequiresConfirmation)
            {
                return true;
            }

            if (!tool.MutatesDocument)
            {
                return false;
            }

            return !CanAgentRunMutation(tool, effectiveSettings);
        }

        public static bool CanAgentRunMutation(ToolDefinition tool, AppSettings settings)
        {
            return settings != null &&
                settings.AgentModeEnabled != false &&
                tool != null &&
                tool.BuiltIn &&
                tool.AgentCanRun;
        }
    }
}
