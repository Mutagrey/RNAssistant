using System;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Tools
{
    internal static class ControllerToolDefinition
    {
        public static ToolDefinition CreateReadProjection(
            ToolDescriptor descriptor,
            ToolPolicy policy,
            string name = null)
        {
            if (policy == null || policy.Effect != ToolEffect.Read)
                throw new ArgumentException("This compatibility projection only supports read contracts.", nameof(policy));
            return new ToolDefinition
            {
                Id = descriptor.Id, Host = "Common", Name = name ?? descriptor.Id,
                Description = descriptor.Description, ArgumentSchemaJson = descriptor.ParametersJson,
                BuiltIn = true, Enabled = true, Scope = "session", AgentCanRun = true,
                RequiresConfirmation = policy.RequiresConfirmation, RiskLevel = policy.RiskLevel,
                RuntimePolicy = policy
            };
        }

        public static ToolDefinition CreateTypedProjection(
            ToolDescriptor descriptor,
            ToolPolicy policy,
            string host = "Common",
            string name = null,
            string scope = "global")
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            return new ToolDefinition
            {
                Id = descriptor.Id,
                Host = host,
                Name = name ?? descriptor.Id,
                Description = descriptor.Description,
                ArgumentSchemaJson = descriptor.ParametersJson,
                BuiltIn = true,
                Enabled = true,
                Scope = scope,
                AgentCanRun = true,
                MutatesDocument = policy.MayHaveSideEffects,
                RequiresConfirmation = policy.RequiresConfirmation,
                RiskLevel = policy.RiskLevel,
                RuntimePolicy = policy
            };
        }

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
            string name = null,
            string scope = "global",
            bool independentLocalRead = false)
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
                RiskLevel = riskLevel,
                Scope = scope,
                RuntimePolicy = independentLocalRead
                    ? new ToolPolicy(ToolEffect.Read, ToolVerification.None, requiresConfirmation,
                        !requiresConfirmation, new[] { "agent", "plan", "chat" }, riskLevel)
                    : null
            };
        }
    }
}
