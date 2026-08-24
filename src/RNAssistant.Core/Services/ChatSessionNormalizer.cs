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

        private static void NormalizeMessages(ChatSession session)
        {
            session.Messages = (session.Messages ?? new List<ChatMessage>())
                .Where(message => message != null)
                .ToList();
            foreach (var message in session.Messages)
            {
                if (string.IsNullOrWhiteSpace(message.Id)) message.Id = Guid.NewGuid().ToString("N");
                if (message.CreatedUtc == default(DateTime)) message.CreatedUtc = session.CreatedUtc;
                message.ArtifactIds = (message.ArtifactIds ?? new List<string>())
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
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
            foreach (var dataSource in session.HtmlWorkspace.DataSources.Where(item => item != null && item.Binding != null))
            {
                var binding = dataSource.Binding;
                binding.ArgumentsJson = string.IsNullOrWhiteSpace(binding.ArgumentsJson) ? "{}" : binding.ArgumentsJson;
                binding.Transform = string.Equals(binding.Transform, "table", StringComparison.OrdinalIgnoreCase) ? "table" : "raw";
                binding.Headers = string.Equals(binding.Headers, "none", StringComparison.OrdinalIgnoreCase) ? "none" : "firstRow";
                binding.RefreshPolicy = string.Equals(binding.RefreshPolicy, "manual", StringComparison.OrdinalIgnoreCase) ? "manual" : "on_preview";
                binding.Status = string.IsNullOrWhiteSpace(binding.Status) ? "ready" : binding.Status.Trim().ToLowerInvariant();
                if (binding.CreatedUtc == default(DateTime)) binding.CreatedUtc = dataSource.CreatedUtc == default(DateTime) ? DateTime.UtcNow : dataSource.CreatedUtc;
                if (binding.UpdatedUtc == default(DateTime)) binding.UpdatedUtc = binding.CreatedUtc;
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
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(item => item.CreatedUtc).First())
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

            if (!session.Artifacts.Any(item =>
                string.Equals(item.Id, session.ActivePlanArtifactId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Kind, ChatArtifactKinds.Plan, StringComparison.OrdinalIgnoreCase)))
            {
                session.ActivePlanArtifactId = null;
            }
        }
    }
}
