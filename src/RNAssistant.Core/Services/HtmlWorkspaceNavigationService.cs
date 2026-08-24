using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    public static class HtmlWorkspaceNavigationService
    {
        public static List<HtmlWorkspaceRedoBranch> GetRedoBranches(ChatSession session)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.ActiveHtmlArtifactId))
            {
                return new List<HtmlWorkspaceRedoBranch>();
            }

            var artifacts = session.Artifacts ?? new List<ChatArtifact>();
            var active = artifacts.FirstOrDefault(item => item != null &&
                string.Equals(item.Id, session.ActiveHtmlArtifactId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Kind, ChatArtifactKinds.HtmlWorkspace, StringComparison.OrdinalIgnoreCase));
            if (active == null) return new List<HtmlWorkspaceRedoBranch>();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { active.Id };
            return artifacts
                .Where(item => item != null &&
                    !string.IsNullOrWhiteSpace(item.Id) &&
                    string.Equals(item.Kind, ChatArtifactKinds.HtmlWorkspace, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.ParentArtifactId, session.ActiveHtmlArtifactId, StringComparison.OrdinalIgnoreCase) &&
                    seen.Add(item.Id))
                .OrderByDescending(item => item.CreatedUtc)
                .ThenByDescending(item => item.Revision)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .Select(ToBranch)
                .ToList();
        }

        private static HtmlWorkspaceRedoBranch ToBranch(ChatArtifact artifact)
        {
            int? fileCount = null;
            int? dataSourceCount = null;
            if (!string.IsNullOrWhiteSpace(artifact.MetadataJson))
            {
                try
                {
                    var metadata = JObject.Parse(artifact.MetadataJson);
                    fileCount = ReadCount(metadata, "fileCount", "FileCount");
                    dataSourceCount = ReadCount(metadata, "dataSourceCount", "DataSourceCount");
                }
                catch (JsonException)
                {
                    // Metadata is advisory; the artifact graph remains authoritative.
                }
            }
            return new HtmlWorkspaceRedoBranch
            {
                Id = artifact.Id,
                ParentArtifactId = artifact.ParentArtifactId,
                Label = string.IsNullOrWhiteSpace(artifact.Title) ? "HTML workspace" : artifact.Title,
                Revision = Math.Max(1, artifact.Revision),
                FileCount = fileCount,
                DataSourceCount = dataSourceCount,
                CreatedUtc = artifact.CreatedUtc
            };
        }

        private static int? ReadCount(JObject metadata, string camelName, string pascalName)
        {
            var value = metadata[camelName] ?? metadata[pascalName];
            if (value == null) return null;
            int count;
            return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out count) && count >= 0
                ? (int?)count
                : null;
        }
    }
}
