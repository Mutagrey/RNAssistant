using System;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Tools
{
    internal static class ControllerToolCatalogEntry
    {
        public static ToolCatalogEntry CreateReadProjection(
            ToolDescriptor descriptor,
            ToolPolicy policy,
            ToolBinding binding,
            string name = null)
        {
            if (policy == null || policy.Effect != ToolEffect.Read)
                throw new ArgumentException("This catalog entry only supports read contracts.", nameof(policy));
            return CreateTypedProjection(descriptor, policy, binding, name: name, scope: "session");
        }

        public static ToolCatalogEntry CreateTypedProjection(
            ToolDescriptor descriptor,
            ToolPolicy policy,
            ToolBinding binding,
            string host = "Common",
            string name = null,
            string scope = "global",
            bool mutatesDocument = false,
            bool mutatesLocalState = false)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            if (binding == null) throw new ArgumentNullException(nameof(binding));
            return new ToolCatalogEntry
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
                MutatesDocument = mutatesDocument,
                MutatesLocalState = mutatesLocalState,
                RequiresConfirmation = policy.RequiresConfirmation,
                RiskLevel = policy.RiskLevel,
                Policy = policy,
                Binding = binding
            };
        }

    }
}
