using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Runtime
{
    // Phase 4 boundary for current catalog/authoring consumers. A missing legacy
    // mutation flag is not evidence that a tool has only local read effects.
    internal static class LegacyToolDefinitionAdapter
    {
        internal static ToolPolicy PolicyFor(ToolDefinition definition, string mode = "agent")
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            var safety = ToolSafetyPolicy.Resolve(definition, null);
            var trusted = definition.BuiltIn && string.Equals(definition.Executor, "builtin", StringComparison.OrdinalIgnoreCase);
            var declared = trusted ? definition.RuntimePolicy : null;
            var effect = safety.MutatesDocument || safety.MutatesLocalState
                ? ToolEffect.Write : declared == null ? ToolEffect.Unclassified : declared.Effect;
            var confirmation = safety.RequiresConfirmation;
            var independent = safety.Valid && definition.Enabled && definition.AgentCanRun &&
                declared != null && declared.IndependentLocalRead && effect == ToolEffect.Read && !confirmation;
            return new ToolPolicy(effect, declared == null ? ToolVerification.None : declared.Verification,
                confirmation, independent, declared == null ? new[] { mode } : declared.AllowedModes,
                Math.Max(safety.RiskLevel, declared == null ? 0 : declared.RiskLevel));
        }

        internal static ToolBinding BindingFor(ToolDefinition definition)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            return new ToolBinding(
                "legacy.office." + (definition.Executor ?? "unknown") +
                    (definition.BuiltIn ? ".builtin" : ".custom"),
                definition.EntryPoint,
                definition.Scope,
                definition.Host);
        }

        internal static ToolRegistration Adapt(ToolDefinition definition,
            ToolBinding binding = null, string mode = "agent", string descriptorDescription = null)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            var selectedBinding = binding ?? BindingFor(definition);
            var capturedBinding = new ToolBinding(
                selectedBinding.HandlerId,
                selectedBinding.EntryPoint,
                definition.Scope,
                definition.Host);
            return ToolPackSnapshot.Capture(
                new ToolDescriptor(definition.Id,
                    descriptorDescription ?? definition.Description,
                    definition.ArgumentSchemaJson),
                PolicyFor(definition, mode),
                capturedBinding,
                new ToolPackageMetadata(definition.PackageVersion, definition.StoragePath, definition.Code,
                    JsonConvert.SerializeObject(definition.Components ?? new List<VbaToolComponent>()), definition.InstallationStatus));
        }

        internal static ToolDefinition ProjectRead(ToolDescriptor descriptor, ToolPolicy policy, string name = null)
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
    }
}
