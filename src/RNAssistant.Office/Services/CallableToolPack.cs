using System;
using System.Collections.Generic;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    internal static class CallableToolPack
    {
        private static readonly HashSet<string> BootstrapToolIds = ExactIds(
            SkillLoadToolHandler.ToolId,
            ResourceToolCatalog.FindToolId,
            ResourceToolCatalog.ReadToolId,
            CapabilityToolCatalog.SearchToolId,
            CapabilityToolCatalog.ReadToolId);

        private static readonly HashSet<string> ExcelCoreToolIds = ExactIds(
            "excel.inspect");

        private static readonly HashSet<string> VbaCoreToolIds = ExactIds(
            "common.vba_write_module",
            "common.vba_apply_patch");

        public static CallableToolSet Create(string mode, string host, string runId,
            IReadOnlyList<ToolCatalogEntry> catalog,
            IReadOnlyList<ToolPackExtensionEventData> restoredAdmissions = null)
        {
            mode = ConversationRunPolicy.For(mode).Mode;
            return CallableToolSet.Create(mode, host, runId, catalog, CreateProfile(mode, host),
                tools => ToolPackSnapshotFactory.Capture(mode, host, tools).Revision, restoredAdmissions);
        }

        private static string ProfileId(string mode, string host)
        {
            if (string.Equals(mode, ChatModes.Agent, StringComparison.Ordinal) &&
                string.Equals(host, "Excel", StringComparison.OrdinalIgnoreCase)) return "excel-inspect-core";
            if (string.Equals(mode, ChatModes.Agent, StringComparison.Ordinal) &&
                (string.Equals(host, "Word", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(host, "PowerPoint", StringComparison.OrdinalIgnoreCase))) return "vba-core";
            return "bootstrap-core";
        }

        private static CallableToolProfile CreateProfile(string mode, string host)
        {
            var selected = new HashSet<string>(BootstrapToolIds, StringComparer.Ordinal);
            if (string.Equals(mode, ChatModes.Agent, StringComparison.Ordinal) &&
                string.Equals(host, "Excel", StringComparison.OrdinalIgnoreCase)) selected.UnionWith(ExcelCoreToolIds);
            if (string.Equals(mode, ChatModes.Agent, StringComparison.Ordinal) &&
                (string.Equals(host, "Excel", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(host, "Word", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(host, "PowerPoint", StringComparison.OrdinalIgnoreCase))) selected.UnionWith(VbaCoreToolIds);
            return CallableToolProfile.Explicit(ProfileId(mode, host), selected);
        }

        private static HashSet<string> ExactIds(params string[] ids)
        { return new HashSet<string>(ids, StringComparer.Ordinal); }
    }
}
