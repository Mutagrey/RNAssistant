using RNAssistant.Core.Models;

namespace RNAssistant.Office.Tools
{
    internal static class ToolSafetyPolicy
    {
        public static bool RequiresConfirmation(SkillDefinition tool, AppSettings settings, bool dryRun, bool manualRun)
        {
            if (dryRun || manualRun || tool == null || !tool.MutatesDocument)
            {
                return false;
            }

            var effectiveSettings = settings ?? new AppSettings();
            if (effectiveSettings.AutoConfirmToolActions)
            {
                return false;
            }

            return !CanAgentRunMutation(tool, effectiveSettings);
        }

        public static bool CanAgentRunMutation(SkillDefinition tool, AppSettings settings)
        {
            return settings != null &&
                settings.AgentModeEnabled != false &&
                tool != null &&
                tool.BuiltIn &&
                tool.AgentCanRun;
        }
    }
}
