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

        // The caller validates the active body. Navigation needs only metadata;
        // a selected restore source is read and validated at the mutation boundary.
        public static void RebuildHistory(ChatSession session, ChatArtifact active)
        {
            var artifacts = UniqueArtifacts(session).Where(item =>
                string.Equals(item.Kind, ChatArtifactKinds.HtmlWorkspace, StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrEmpty(item.AvailabilityIssue))
                .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
            var history = new List<HtmlWorkspaceSnapshot>();
            var navigationId = (string)JObject.Parse(active.MetadataJson ?? "{}")["navigationBaseArtifactId"];
            ChatArtifact current = active;
            if (!string.IsNullOrEmpty(navigationId)) artifacts.TryGetValue(navigationId, out current);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { current?.Id ?? active.Id };
            string issue = current == null ? HtmlWorkspaceRecoveryIssues.ParentArtifactMissing : null;
            string message = current == null ? "The restored HTML navigation source is unavailable. The active revision is readable, but undo history is incomplete." : null;
            string problemArtifactId = current == null ? navigationId : null;
            while (current != null && !string.IsNullOrWhiteSpace(current.ParentArtifactId) &&
                history.Count < HtmlWorkspaceHistoryPolicy.MaxItems)
            {
                problemArtifactId = current.ParentArtifactId;
                if (!visited.Add(problemArtifactId))
                {
                    issue = HtmlWorkspaceRecoveryIssues.LineageCycle;
                    message = "The HTML workspace revision lineage contains a cycle. The active revision is readable, but older undo history is incomplete.";
                    break;
                }
                if (!artifacts.TryGetValue(problemArtifactId, out current))
                {
                    issue = HtmlWorkspaceRecoveryIssues.ParentArtifactMissing;
                    message = "An older HTML workspace revision is missing. The active revision is readable, but undo history is incomplete.";
                    break;
                }
                history.Add(new HtmlWorkspaceSnapshot
                {
                    Id = current.Id,
                    Label = string.IsNullOrWhiteSpace(current.Title) ? "HTML workspace" : current.Title,
                    CreatedUtc = current.CreatedUtc
                });
            }
            session.HtmlWorkspace.History = history;
            session.HtmlWorkspace.RedoBranches = GetRedoBranches(session);
            session.HtmlWorkspaceRecovery = CreateRecoveryState(session,
                issue == null ? HtmlWorkspaceRecoveryStatuses.Healthy : HtmlWorkspaceRecoveryStatuses.Degraded,
                issue, message, active.Id, issue == null ? null : problemArtifactId, true);
        }

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

            if (HtmlWorkspaceIdentity.LogicalId(active.Id) != null && !string.IsNullOrWhiteSpace(active.DocumentAuthorityId))
            {
                var redoIds = JObject.Parse(active.MetadataJson ?? "{}")["redoArtifactIds"] as JArray;
                if (redoIds != null)
                {
                    var next = (string)redoIds.FirstOrDefault();
                    return artifacts.Where(item => item.Id == next && HtmlWorkspaceIdentity.LogicalId(item.Id) == HtmlWorkspaceIdentity.LogicalId(active.Id))
                        .Select(ToBranch).ToList();
                }
            }

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
                .Where(item => HtmlWorkspaceIdentity.LogicalId(excludedArtifactId) == null ||
                    HtmlWorkspaceIdentity.LogicalId(item.Id) == HtmlWorkspaceIdentity.LogicalId(excludedArtifactId))
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
