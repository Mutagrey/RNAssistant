using RNAssistant.Core.Models;

namespace RNAssistant.Office.Tools
{
    internal static class ControllerToolDefinition
    {
        public static ToolDefinition Create(
            string id,
            string host,
            string description,
            string schema,
            bool mutatesDocument = false,
            bool mutatesLocalState = false,
            bool requiresConfirmation = false,
            bool agentCanRun = true,
            int riskLevel = 0,
            string name = null)
        {
            return new ToolDefinition
            {
                Id = id,
                Host = host,
                Name = name ?? id,
                Description = description,
                ArgumentSchemaJson = schema,
                BuiltIn = true,
                Enabled = true,
                MutatesDocument = mutatesDocument,
                MutatesLocalState = mutatesLocalState,
                RequiresConfirmation = requiresConfirmation,
                AgentCanRun = agentCanRun,
                RiskLevel = riskLevel
            };
        }
    }
}
