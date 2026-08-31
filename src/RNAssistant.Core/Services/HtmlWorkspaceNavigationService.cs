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
        private const int MaxRecoveryCandidates = 100;

        public static List<HtmlWorkspaceRedoBranch> GetRedoBranches(ChatSession session)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.ActiveHtmlArtifactId))
            {
                return new List<HtmlWorkspaceRedoBranch>();
            }

            var artifacts = UniqueArtifacts(session);
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

        public static HtmlWorkspaceRecoveryState CreateRecoveryState(
            ChatSession session,
            string status,
            string issue,
            string message,
            string activeArtifactId,
            string problemArtifactId,
            bool canMutate)
        {
            var degraded = string.Equals(status, HtmlWorkspaceRecoveryStatuses.Degraded, StringComparison.OrdinalIgnoreCase);
            return new HtmlWorkspaceRecoveryState
            {
                Status = string.IsNullOrWhiteSpace(status) ? HtmlWorkspaceRecoveryStatuses.Empty : status,
                Issue = issue,
                Message = message,
                ActiveArtifactId = activeArtifactId,
                ProblemArtifactId = problemArtifactId,
                CanMutate = canMutate,
                Candidates = degraded
                    ? GetRecoveryCandidates(session, activeArtifactId)
                    : new List<HtmlWorkspaceRecoveryCandidate>()
            };
        }

        public static List<HtmlWorkspaceRecoveryCandidate> GetRecoveryCandidates(ChatSession session, string excludedArtifactId)
        {
            var artifacts = UniqueArtifacts(session);
            var active = artifacts.FirstOrDefault(item => item != null &&
                string.Equals(item.Id, excludedArtifactId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Kind, ChatArtifactKinds.HtmlWorkspace, StringComparison.OrdinalIgnoreCase));
            var preferredParentId = active == null ? null : active.ParentArtifactId;
            return artifacts
                .Where(item => item != null &&
                    !string.IsNullOrWhiteSpace(item.Id) &&
                    !string.Equals(item.Id, excludedArtifactId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.Kind, ChatArtifactKinds.HtmlWorkspace, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => string.Equals(item.Id, preferredParentId, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenByDescending(item => item.CreatedUtc)
                .ThenByDescending(item => item.Revision)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .Take(MaxRecoveryCandidates)
                .Select(ToRecoveryCandidate)
                .ToList();
        }

        private static List<ChatArtifact> UniqueArtifacts(ChatSession session)
        {
            return (session == null ? new List<ChatArtifact>() : session.Artifacts ?? new List<ChatArtifact>())
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Id))
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() == 1)
                .Select(group => group.Single())
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

        private static HtmlWorkspaceRecoveryCandidate ToRecoveryCandidate(ChatArtifact artifact)
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
                }
            }
            return new HtmlWorkspaceRecoveryCandidate
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
