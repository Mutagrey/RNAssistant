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

        public static ChatSessionHeader Create(ChatSession session)
        {
            if (session == null) return null;

            var workspace = session.HtmlWorkspace;
            var fileCount = workspace == null || workspace.Files == null ? 0 : workspace.Files.Count;
            var dataSourceCount = workspace == null || workspace.DataSources == null ? 0 : workspace.DataSources.Count;
            return Create(session, fileCount, dataSourceCount);
        }

        public static ChatSessionHeader Create(ChatSession session, int htmlFileCount, int htmlDataSourceCount)
        {
            if (session == null) return null;

            var run = session.LastRun;
            var jsonlByteLength = System.Math.Max(0, session.StorageByteLength);
            return new ChatSessionHeader
            {
                Id = session.Id,
                Revision = session.Revision,
                Host = session.Host,
                DocumentKey = session.DocumentKey,
                DocumentTitle = session.DocumentTitle,
                DocumentPath = session.DocumentPath,
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
                JsonlByteLength = jsonlByteLength,
                StorageWarningLevel = ChatStorageUsagePolicy.GetWarningLevel(
                    jsonlByteLength, 0, 0, 0, 0)
            };
        }
    }
}
