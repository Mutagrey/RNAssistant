using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    // One conversion boundary while current catalogs are still ToolDefinition.
    // The snapshot itself is typed and independent of AgentKernel/model context.
    internal static class ToolPackSnapshotFactory
    {
        private const string RunPackId = "run-tool-pack";

        internal static ToolPackSnapshot Capture(string mode, string host,
            IEnumerable<ToolDefinition> tools)
        {
            var registrations = (tools ?? new ToolDefinition[0])
                .Where(tool => tool != null)
                .Select(tool => Capture(tool, mode))
                .ToArray();
            return new ToolPackSnapshot(RunPackId, mode, host, registrations);
        }

        internal static string ExecutionFingerprint(IEnumerable<ToolDefinition> tools,
            string exactToolId, string mode = "agent")
        {
            var matches = (tools ?? new ToolDefinition[0])
                .Where(tool => tool != null && !string.IsNullOrWhiteSpace(tool.Id))
                .Where(tool => string.Equals(tool.Id, exactToolId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var definition = matches.Length == 1 ? matches[0] : null;
            if (definition == null || string.Equals(definition.Executor, "pipeline", StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            return Capture(definition, mode).Revision;
        }

        private static ToolRegistration Capture(ToolDefinition definition, string mode)
        {
            var binding = NativeToolRuntimeAdapter.BindingFor(definition.Id) ??
                VbaPackageToolHandler.BindingFor(definition) ??
                LegacyToolDefinitionAdapter.BindingFor(definition);
            return LegacyToolDefinitionAdapter.Adapt(definition, binding, mode,
                ConversationPromptComposer.BuildDescription(definition));
        }
    }
}
