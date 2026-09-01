using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    // Catalog entries carry source-owned policy; this boundary only captures the
    // exact immutable descriptor, binding and package bytes admitted to a run.
    internal static class ToolPackSnapshotFactory
    {
        private const string RunPackId = "run-tool-pack";

        internal static ToolPackSnapshot Capture(string mode, string host,
            IEnumerable<ToolCatalogEntry> tools)
        {
            var registrations = (tools ?? new ToolCatalogEntry[0])
                .Where(tool => tool != null)
                .Select(tool => Capture(tool, mode))
                .ToArray();
            return new ToolPackSnapshot(RunPackId, mode, host, registrations);
        }

        internal static string ExecutionFingerprint(IEnumerable<ToolCatalogEntry> tools,
            string exactToolId, string mode = "agent")
        {
            var matches = (tools ?? new ToolCatalogEntry[0])
                .Where(tool => tool != null && !string.IsNullOrWhiteSpace(tool.Id))
                .Where(tool => string.Equals(tool.Id, exactToolId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var definition = matches.Length == 1 ? matches[0] : null;
            if (definition == null || string.Equals(definition.Executor, "pipeline", StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            return Capture(definition, mode).Revision;
        }

        private static ToolRegistration Capture(ToolCatalogEntry definition, string mode)
        {
            var binding = definition.Binding;
            if (binding == null)
                throw new InvalidOperationException(
                    "Tool has no direct runtime binding: " + definition.Id);
            var policy = definition.Policy;
            if (policy == null)
                throw new InvalidOperationException(
                    "Tool has no source-owned runtime policy: " +
                    definition.Id);
            var package = definition.BuiltIn ? null :
                new ToolPackageMetadata(
                    definition.PackageVersion,
                    definition.StoragePath,
                    definition.Code,
                    JsonConvert.SerializeObject(
                        definition.Components ??
                        new List<ToolPackageComponentDefinition>()),
                    definition.InstallationStatus,
                    definition.Readme);
            return ToolPackSnapshot.Capture(
                new ToolDescriptor(
                    definition.Id,
                    ConversationPromptComposer.BuildDescription(definition),
                    definition.ArgumentSchemaJson),
                policy,
                new ToolBinding(
                    binding.HandlerId,
                    binding.EntryPoint,
                    definition.Scope,
                    definition.Host),
                package);
        }

    }
}
