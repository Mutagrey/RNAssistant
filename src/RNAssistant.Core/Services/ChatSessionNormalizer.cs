using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    public static class ChatSessionNormalizer
    {
        public static void Normalize(ChatSession session, string host, string documentKey, string documentTitle)
        {
            if (session == null) return;

            session.FormatVersion = ChatSession.CurrentFormatVersion;
            if (string.IsNullOrWhiteSpace(session.Id)) session.Id = Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(session.Host)) session.Host = host ?? string.Empty;
            if (string.IsNullOrWhiteSpace(session.DocumentKey)) session.DocumentKey = documentKey ?? string.Empty;
            NormalizePreviousDocumentKeys(session);
            if (string.IsNullOrWhiteSpace(session.DocumentTitle)) session.DocumentTitle = documentTitle ?? session.Title ?? string.Empty;
            if (string.IsNullOrWhiteSpace(session.Title)) session.Title = "New chat";
            if (session.CreatedUtc == default(DateTime))
            {
                session.CreatedUtc = session.UpdatedUtc == default(DateTime) ? DateTime.UtcNow : session.UpdatedUtc;
            }
            if (session.UpdatedUtc == default(DateTime)) session.UpdatedUtc = session.CreatedUtc;
            if (session.LastRun != null && string.IsNullOrWhiteSpace(session.LastRun.TurnId))
            {
                session.LastRun.TurnId = session.LastRun.RunId;
            }

            NormalizeMessages(session);
            NormalizeContext(session);
            NormalizeWorkspace(session);
            NormalizeCheckpoints(session);
            NormalizeArtifacts(session);
            NormalizeActiveReferences(session);
        }

        public static void RecordDocumentKeyMigration(
            ChatSession session,
            string previousDocumentKey,
            string currentDocumentKey)
        {
            if (session == null) return;
            var keys = session.PreviousDocumentKeys ?? new List<string>();
            if (!string.IsNullOrWhiteSpace(previousDocumentKey) &&
                !string.Equals(previousDocumentKey, currentDocumentKey, StringComparison.OrdinalIgnoreCase))
            {
                keys.Add(previousDocumentKey.Trim());
            }
            session.PreviousDocumentKeys = keys
                .Where(value => !string.IsNullOrWhiteSpace(value) &&
                    !string.Equals(value, currentDocumentKey, StringComparison.OrdinalIgnoreCase))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void NormalizePreviousDocumentKeys(ChatSession session)
        {
            RecordDocumentKeyMigration(session, null, session.DocumentKey);
        }

        private static void NormalizeMessages(ChatSession session)
        {
            session.Messages = (session.Messages ?? new List<ChatMessage>())
                .Where(message => message != null)
                .ToList();
            foreach (var message in session.Messages)
            {
                if (string.IsNullOrWhiteSpace(message.Id)) message.Id = Guid.NewGuid().ToString("N");
                if (message.CreatedUtc == default(DateTime)) message.CreatedUtc = session.CreatedUtc;
                message.ResourceRefs = (message.ResourceRefs ?? new List<ResourceRef>())
                    .Where(reference => reference != null && ResourceUri.TryParse(reference.Uri, out _))
                    .GroupBy(reference => (reference.Uri ?? string.Empty) + "\n" + (reference.Revision ?? string.Empty), StringComparer.Ordinal)
                    .Select(group => group.First())
                    .ToList();
                if (message.HtmlWorkspaceCheckpoint != null &&
                    !ResourceUri.TryParse(message.HtmlWorkspaceCheckpoint.Uri, out _))
                {
                    message.HtmlWorkspaceCheckpoint = null;
                }
                message.Attachments = (message.Attachments ?? new List<ChatAttachment>())
                    .Where(attachment => attachment != null)
                    .ToList();
                foreach (var attachment in message.Attachments)
                {
                    if (attachment.PageTextLengths == null) attachment.PageTextLengths = new List<int>();
                }
            }
        }

        private static void NormalizeContext(ChatSession session)
        {
            if (session.Context == null) session.Context = new DocumentContext();
            if (string.IsNullOrWhiteSpace(session.Context.Host)) session.Context.Host = session.Host;
            if (string.IsNullOrWhiteSpace(session.Context.DocumentKey)) session.Context.DocumentKey = session.DocumentKey;
            if (string.IsNullOrWhiteSpace(session.Context.Title)) session.Context.Title = session.Title;
            if (session.Context.Notes == null) session.Context.Notes = new List<ContextNote>();
        }

        private static void NormalizeWorkspace(ChatSession session)
        {
            if (session.HtmlWorkspace == null) session.HtmlWorkspace = new HtmlWorkspace();
            if (session.HtmlWorkspaceRecovery == null) session.HtmlWorkspaceRecovery = new HtmlWorkspaceRecoveryState();
            if (session.HtmlWorkspace.Files == null) session.HtmlWorkspace.Files = new List<HtmlWorkspaceFile>();
            if (session.HtmlWorkspace.DataSources == null) session.HtmlWorkspace.DataSources = new List<HtmlWorkspaceDataSource>();
            foreach (var dataSource in session.HtmlWorkspace.DataSources.Where(item => item != null))
            {
                if (dataSource.Binding?.Resource == null ||
                    dataSource.Binding.Policy != "head" && dataSource.Binding.Policy != "exact" ||
                    dataSource.Binding.Policy == "exact" && !dataSource.Binding.Resource.IsExact)
                    throw new InvalidOperationException("HTML_RESOURCE_BINDING_INVALID: incompatible workspace binding.");
            }
            if (session.HtmlWorkspace.History == null) session.HtmlWorkspace.History = new List<HtmlWorkspaceSnapshot>();
            if (session.HtmlWorkspace.RedoBranches == null) session.HtmlWorkspace.RedoBranches = new List<HtmlWorkspaceRedoBranch>();
            if (session.HtmlWorkspace.UpdatedUtc == default(DateTime))
            {
                session.HtmlWorkspace.UpdatedUtc = session.UpdatedUtc == default(DateTime) ? DateTime.UtcNow : session.UpdatedUtc;
            }
        }

        private static void NormalizeCheckpoints(ChatSession session)
        {
            var checkpoints = session.ContextCheckpoints ?? new List<ContextCheckpoint>();
            foreach (var checkpoint in checkpoints.Where(item => item != null))
            {
                if (string.IsNullOrWhiteSpace(checkpoint.Id)) checkpoint.Id = Guid.NewGuid().ToString("N");
                if (checkpoint.CreatedUtc == default(DateTime)) checkpoint.CreatedUtc = session.UpdatedUtc;
            }
            session.ContextCheckpoints = checkpoints
                .Where(item => item != null)
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(item => item.CreatedUtc).First())
                .ToList();
        }

        private static void NormalizeArtifacts(ChatSession session)
        {
            var artifacts = session.Artifacts ?? new List<ChatArtifact>();
            foreach (var artifact in artifacts.Where(item => item != null))
            {
                if (string.IsNullOrWhiteSpace(artifact.Id)) artifact.Id = Guid.NewGuid().ToString("N");
                if (artifact.Revision <= 0) artifact.Revision = 1;
                if (artifact.CreatedUtc == default(DateTime)) artifact.CreatedUtc = session.UpdatedUtc;
                if (artifact.RelatedArtifactIds == null) artifact.RelatedArtifactIds = new List<string>();
            }
            session.Artifacts = artifacts
                .Where(item => item != null)
                .ToList();
        }

        private static void NormalizeActiveReferences(ChatSession session)
        {
            var messageIds = new HashSet<string>(session.Messages.Select(item => item.Id), StringComparer.OrdinalIgnoreCase);
            var activeCheckpoint = session.ContextCheckpoints.FirstOrDefault(item =>
                string.Equals(item.Id, session.ActiveContextCheckpointId, StringComparison.OrdinalIgnoreCase));
            if (activeCheckpoint == null || !messageIds.Contains(activeCheckpoint.ThroughMessageId))
            {
                session.ActiveContextCheckpointId = null;
            }

            if (!HasUniqueArtifact(session, session.ActiveTaskListArtifactId, ChatArtifactKinds.TaskList))
            {
                session.ActiveTaskListArtifactId = null;
            }
            if (!HasUniqueArtifact(session, session.ActivePlanDocumentArtifactId, ChatArtifactKinds.PlanDocument))
            {
                session.ActivePlanDocumentArtifactId = null;
            }
        }

        private static bool HasUniqueArtifact(ChatSession session, string artifactId, string kind)
        {
            if (session == null || string.IsNullOrWhiteSpace(artifactId)) return false;
            var matches = (session.Artifacts ?? new List<ChatArtifact>())
                .Where(item => item != null && string.Equals(
                    item.Id,
                    artifactId,
                    StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToList();
            return matches.Count == 1 && string.Equals(
                matches[0].Kind,
                kind,
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
