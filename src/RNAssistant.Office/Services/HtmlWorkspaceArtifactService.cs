using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    internal static class HtmlWorkspaceArtifactService
    {
        public static string CaptureCurrent(ChatSession session, string title)
        {
            if (session == null) return string.Empty;
            session.HtmlWorkspace = HtmlArtifactToolExecutor.NormalizeWorkspace(session.HtmlWorkspace);
            session.Artifacts = session.Artifacts ?? new List<ChatArtifact>();
            var snapshot = HtmlWorkspaceCopyService.CaptureSnapshot(
                session.HtmlWorkspace,
                string.IsNullOrWhiteSpace(title) ? "HTML workspace" : title);
            var stateJson = SerializeState(snapshot);
            var current = session.Artifacts.FirstOrDefault(item => item != null &&
                string.Equals(item.Id, session.ActiveHtmlArtifactId, StringComparison.OrdinalIgnoreCase));
            if (current != null && SameState(current.InlineText, snapshot))
            {
                RebuildNavigation(session);
                return current.Id;
            }
            if (current == null && snapshot.Files.Count == 0 && snapshot.DataSources.Count == 0)
            {
                session.ActiveHtmlArtifactId = null;
                session.HtmlWorkspace.History = new List<HtmlWorkspaceSnapshot>();
                session.HtmlWorkspace.RedoBranches = new List<HtmlWorkspaceRedoBranch>();
                return string.Empty;
            }
            var artifact = new ChatArtifact
            {
                Kind = ChatArtifactKinds.HtmlWorkspace,
                Title = snapshot.Label,
                MimeType = "application/vnd.rnassistant.html-workspace+json",
                ParentArtifactId = current == null ? null : current.Id,
                Revision = current == null ? 1 : Math.Max(1, current.Revision + 1),
                InlineText = stateJson,
                ModelContextPolicy = "reference",
                MetadataJson = JsonConvert.SerializeObject(new
                {
                    activeFileId = snapshot.ActiveFileId,
                    fileCount = snapshot.Files.Count,
                    dataSourceCount = snapshot.DataSources.Count
                })
            };
            session.Artifacts.Add(artifact);
            session.ActiveHtmlArtifactId = artifact.Id;
            RebuildNavigation(session);
            return artifact.Id;
        }

        public static bool Restore(ChatSession session, string artifactId)
        {
            if (session == null || string.IsNullOrWhiteSpace(artifactId) || session.Artifacts == null) return false;
            var artifact = session.Artifacts.FirstOrDefault(item => item != null &&
                string.Equals(item.Id, artifactId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Kind, ChatArtifactKinds.HtmlWorkspace, StringComparison.OrdinalIgnoreCase));
            if (artifact == null || string.IsNullOrWhiteSpace(artifact.InlineText)) return false;
            HtmlWorkspaceSnapshot snapshot;
            try
            {
                snapshot = JsonConvert.DeserializeObject<HtmlWorkspaceSnapshot>(artifact.InlineText);
            }
            catch (JsonException)
            {
                return false;
            }
            if (snapshot == null) return false;
            session.HtmlWorkspace = HtmlArtifactToolExecutor.NormalizeWorkspace(
                HtmlWorkspaceCopyService.CreateWorkspaceFromSnapshot(snapshot));
            session.ActiveHtmlArtifactId = artifact.Id;
            RebuildNavigation(session);
            return true;
        }

        public static void RebuildNavigation(ChatSession session)
        {
            if (session == null || session.HtmlWorkspace == null) return;
            session.HtmlWorkspace.History = new List<HtmlWorkspaceSnapshot>();
            session.HtmlWorkspace.RedoBranches = new List<HtmlWorkspaceRedoBranch>();
            var active = FindArtifact(session, session.ActiveHtmlArtifactId);
            if (active == null) return;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { active.Id };
            var current = active;
            while (!string.IsNullOrWhiteSpace(current.ParentArtifactId) && visited.Add(current.ParentArtifactId))
            {
                current = FindArtifact(session, current.ParentArtifactId);
                var snapshot = ParseSnapshot(current);
                if (snapshot == null) break;
                session.HtmlWorkspace.History.Add(snapshot);
            }
            session.HtmlWorkspace.History = HtmlWorkspaceHistoryPolicy.Trim(session.HtmlWorkspace.History);

            session.HtmlWorkspace.RedoBranches = HtmlWorkspaceNavigationService.GetRedoBranches(session);
        }

        public static string CheckpointAtOrBefore(IReadOnlyList<ChatMessage> messages, int index)
        {
            if (messages == null) return string.Empty;
            for (var current = Math.Min(index, messages.Count - 1); current >= 0; current--)
            {
                var id = messages[current] == null ? null : messages[current].HtmlWorkspaceCheckpointId;
                if (!string.IsNullOrWhiteSpace(id)) return id;
            }
            return string.Empty;
        }

        public static void StampUncheckpointed(ChatSession session, int startIndex, string checkpointId)
        {
            if (session == null || session.Messages == null || string.IsNullOrWhiteSpace(checkpointId)) return;
            for (var index = Math.Max(0, startIndex); index < session.Messages.Count; index++)
            {
                var message = session.Messages[index];
                if (message != null && string.IsNullOrWhiteSpace(message.HtmlWorkspaceCheckpointId))
                {
                    message.HtmlWorkspaceCheckpointId = checkpointId;
                }
            }
        }

        private static bool SameState(string existingJson, HtmlWorkspaceSnapshot candidate)
        {
            if (string.IsNullOrWhiteSpace(existingJson) || candidate == null) return false;
            try
            {
                var existing = JsonConvert.DeserializeObject<HtmlWorkspaceSnapshot>(existingJson);
                return existing != null &&
                    string.Equals(existing.ActiveFileId, candidate.ActiveFileId, StringComparison.OrdinalIgnoreCase) &&
                    JsonConvert.SerializeObject(existing.Files ?? new List<HtmlWorkspaceFile>()) == JsonConvert.SerializeObject(candidate.Files ?? new List<HtmlWorkspaceFile>()) &&
                    JsonConvert.SerializeObject(existing.DataSources ?? new List<HtmlWorkspaceDataSource>()) == JsonConvert.SerializeObject(candidate.DataSources ?? new List<HtmlWorkspaceDataSource>());
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static ChatArtifact FindArtifact(ChatSession session, string artifactId)
        {
            if (session == null || string.IsNullOrWhiteSpace(artifactId)) return null;
            return (session.Artifacts ?? new List<ChatArtifact>()).FirstOrDefault(item => item != null &&
                string.Equals(item.Id, artifactId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Kind, ChatArtifactKinds.HtmlWorkspace, StringComparison.OrdinalIgnoreCase));
        }

        private static HtmlWorkspaceSnapshot ParseSnapshot(ChatArtifact artifact)
        {
            if (artifact == null || string.IsNullOrWhiteSpace(artifact.InlineText)) return null;
            try
            {
                var snapshot = JsonConvert.DeserializeObject<HtmlWorkspaceSnapshot>(artifact.InlineText);
                if (snapshot == null) return null;
                snapshot.Id = artifact.Id;
                snapshot.Label = string.IsNullOrWhiteSpace(artifact.Title) ? "HTML workspace" : artifact.Title;
                snapshot.CreatedUtc = artifact.CreatedUtc;
                return snapshot;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string SerializeState(HtmlWorkspaceSnapshot snapshot)
        {
            snapshot = snapshot ?? new HtmlWorkspaceSnapshot();
            return JsonConvert.SerializeObject(new
            {
                snapshot.ActiveFileId,
                Files = snapshot.Files ?? new List<HtmlWorkspaceFile>(),
                DataSources = snapshot.DataSources ?? new List<HtmlWorkspaceDataSource>()
            }, Formatting.None);
        }

    }
}
