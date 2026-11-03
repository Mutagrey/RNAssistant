using System;
using System.Collections.Generic;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Tools
{
    public static class CapabilityToolCatalog
    {
        public const string SearchToolId = "core.tools.search";
        public const string ReadToolId = "core.tools.read";

        public static bool Owns(string toolId)
        {
            return string.Equals(toolId, SearchToolId, StringComparison.Ordinal) ||
                string.Equals(toolId, ReadToolId, StringComparison.Ordinal);
        }

        public static IEnumerable<ToolCatalogEntry> GetTools()
        {
            yield return Projection(
                SearchToolId,
                "Read-only: Filter the complete compact RUNTIME_CONTEXT.capabilities catalog by id or metadata. Results identify tools and skills but load neither. Paging and limits are runtime-owned; refine the semantic query when complete=false.",
                CapabilityCatalogService.SearchSchema(),
                "capabilities_search");
            yield return Projection(
                ReadToolId,
                "Read-only: Load one exact tool schema from RUNTIME_CONTEXT.capabilities or core.tools.search. Select a skill directly with core.skills.load; it supplies instructions and dependent tools without a preliminary read. Skill source inspection belongs to core.read. Admission state is runtime-owned.",
                CapabilityCatalogService.ReadSchema(null),
                "capabilities_read");
        }

        public static ToolBinding BindingFor(string id)
        {
            if (id == SearchToolId) return new ToolBinding("capabilities.search.intent.v1");
            if (id == ReadToolId) return new ToolBinding("capabilities.read.intent.v1");
            return null;
        }

        private static ToolCatalogEntry Projection(
            string id, string description, string schema, string name)
        {
            return new ToolCatalogEntry {
                Id = id, Host = "Common", Name = name, Description = description,
                ArgumentSchemaJson = schema, BuiltIn = true, Enabled = true,
                Scope = "session", AgentCanRun = true,
                Policy = new ToolPolicy(ToolEffect.Read, ToolVerification.None, false, true, new[] { "agent", "plan" }, evidenceEligibility: EvidenceEligibility.None),
                Binding = BindingFor(id)
            };
        }
    }
}
