using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    public static class ChatSessionHeaderFactory
    {
        internal static DateTime LastActivityUtc(DateTime createdUtc, ChatRunRecord run, IEnumerable<DateTime> messageTimes)
        {
            var latest = messageTimes.DefaultIfEmpty(createdUtc).Max();
            if (latest < createdUtc) latest = createdUtc;
            return run != null && run.StartedUtc > latest ? run.StartedUtc : latest;
        }

        // Omit storageUsage for identity/group metadata only; storage fields are
        // zero unless the caller supplies the addressed Store usage projection.
        public static ChatSessionHeader Create(ChatSession session, ChatSessionHeader storageUsage = null)
        {
            if (session == null) return null;

            var workspace = session.HtmlWorkspace;
            var fileCount = workspace == null || workspace.Files == null ? 0 : workspace.Files.Count;
            var dataSourceCount = workspace == null || workspace.DataSources == null ? 0 : workspace.DataSources.Count;
            return Create(session, fileCount, dataSourceCount, storageUsage);
        }

        public static ChatSessionHeader Create(ChatSession session, int htmlFileCount, int htmlDataSourceCount,
            ChatSessionHeader storageUsage = null)
        {
            if (session == null) return null;
            if (storageUsage != null && !string.Equals(storageUsage.Id, session.Id, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Storage usage belongs to another chat.", nameof(storageUsage));

            var run = session.LastRun;
            return new ChatSessionHeader
            {
                Id = session.Id,
                Revision = session.Revision,
                Host = session.Host,
                WorkspaceId = session.WorkspaceId,
                DocumentKey = session.DocumentKey,
                DocumentTitle = session.DocumentTitle,
                DocumentPath = session.DocumentPath,
                DocumentAuthorityId = session.DocumentAuthorityId,
                CreatorChatId = session.AgentCreation?.Creator?.ChatId,
                Title = session.Title,
                Model = session.Model,
                Mode = ChatModes.Normalize(session.Mode),
                ReasoningEnabled = session.ReasoningEnabled,
                HasHtmlWorkspace = htmlFileCount > 0 || htmlDataSourceCount > 0,
                HtmlFileCount = htmlFileCount,
                HtmlDataSourceCount = htmlDataSourceCount,
                CreatedUtc = session.CreatedUtc,
                UpdatedUtc = session.UpdatedUtc,
                LastActivityUtc = LastActivityUtc(session.CreatedUtc, run,
                    (session.Messages ?? new List<ChatMessage>()).Where(message => message != null)
                        .Select(message => message.CreatedUtc)),
                MessageCount = session.Messages == null
                    ? 0
                    : session.Messages.Count(message => message != null && !message.ProtocolMessage),
                RunId = run == null ? null : run.RunId,
                RunRuntimeId = run == null ? null : run.RuntimeId,
                RunStatus = run == null ? null : run.Status,
                RunPhase = run == null ? null : run.Phase,
                RunStartedUtc = run == null ? (System.DateTime?)null : run.StartedUtc,
                RunViewState = RunViewStateProjector.Create(session),
                // Storage is an advisory snapshot from its existing owner, not
                // the session's physical cursor or authority to advance Revision.
                JsonlByteLength = storageUsage?.JsonlByteLength ?? 0,
                CasBlobCount = storageUsage?.CasBlobCount ?? 0,
                CasLogicalByteLength = storageUsage?.CasLogicalByteLength ?? 0,
                CasStoredByteLength = storageUsage?.CasStoredByteLength ?? 0,
                CasMissingBlobCount = storageUsage?.CasMissingBlobCount ?? 0,
                CasReferenceIssueCount = storageUsage?.CasReferenceIssueCount ?? 0,
                StorageWarningLevel = storageUsage?.StorageWarningLevel ?? ChatStorageWarningLevels.None
            };
        }
    }
}
