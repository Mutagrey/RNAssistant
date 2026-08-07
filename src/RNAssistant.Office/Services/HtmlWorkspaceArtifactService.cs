using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
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
            var snapshot = new HtmlWorkspaceSnapshot
            {
                Label = string.IsNullOrWhiteSpace(title) ? "HTML workspace" : title,
                ActiveFileId = session.HtmlWorkspace.ActiveFileId,
                Files = CloneFiles(session.HtmlWorkspace.Files),
                DataSources = CloneDataSources(session.HtmlWorkspace.DataSources),
                CreatedUtc = DateTime.UtcNow
            };
            var stateJson = JsonConvert.SerializeObject(snapshot);
            var current = session.Artifacts.FirstOrDefault(item => item != null &&
                string.Equals(item.Id, session.ActiveHtmlArtifactId, StringComparison.OrdinalIgnoreCase));
            if (current != null && SameState(current.InlineText, snapshot))
            {
                return current.Id;
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
            session.HtmlWorkspace = HtmlArtifactToolExecutor.NormalizeWorkspace(new HtmlWorkspace
            {
                ActiveFileId = snapshot.ActiveFileId,
                Files = CloneFiles(snapshot.Files),
                DataSources = CloneDataSources(snapshot.DataSources),
                History = new List<HtmlWorkspaceSnapshot>(),
                RedoHistory = new List<HtmlWorkspaceSnapshot>(),
                UpdatedUtc = DateTime.UtcNow
            });
            session.ActiveHtmlArtifactId = artifact.Id;
            return true;
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

        private static List<HtmlWorkspaceFile> CloneFiles(IEnumerable<HtmlWorkspaceFile> files)
        {
            return (files ?? new HtmlWorkspaceFile[0]).Where(file => file != null).Select(file => new HtmlWorkspaceFile
            {
                Id = file.Id,
                Path = file.Path,
                Kind = file.Kind,
                Content = file.Content,
                CreatedUtc = file.CreatedUtc,
                UpdatedUtc = file.UpdatedUtc
            }).ToList();
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

        private static List<HtmlWorkspaceDataSource> CloneDataSources(IEnumerable<HtmlWorkspaceDataSource> values)
        {
            return (values ?? new HtmlWorkspaceDataSource[0]).Where(value => value != null).Select(value => new HtmlWorkspaceDataSource
            {
                Id = value.Id,
                Name = value.Name,
                Json = value.Json,
                CreatedUtc = value.CreatedUtc,
                UpdatedUtc = value.UpdatedUtc
            }).ToList();
        }
    }
}
