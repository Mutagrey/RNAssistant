using System.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    public static class ChatSessionHeaderFactory
    {
        public static ChatSessionHeader Create(ChatSession session)
        {
            if (session == null) return null;

            var workspace = session.HtmlWorkspace;
            var run = session.LastRun;
            var fileCount = workspace == null || workspace.Files == null ? 0 : workspace.Files.Count;
            var dataSourceCount = workspace == null || workspace.DataSources == null ? 0 : workspace.DataSources.Count;
            return new ChatSessionHeader
            {
                Id = session.Id,
                Host = session.Host,
                DocumentKey = session.DocumentKey,
                DocumentTitle = session.DocumentTitle,
                DocumentPath = session.DocumentPath,
                Title = session.Title,
                Model = session.Model,
                Mode = ChatModes.Normalize(session.Mode),
                HtmlModeEnabled = session.HtmlModeEnabled,
                ReasoningEnabled = session.ReasoningEnabled,
                HasHtmlWorkspace = fileCount > 0 || dataSourceCount > 0,
                HtmlFileCount = fileCount,
                HtmlDataSourceCount = dataSourceCount,
                CreatedUtc = session.CreatedUtc,
                UpdatedUtc = session.UpdatedUtc,
                MessageCount = session.Messages == null
                    ? 0
                    : session.Messages.Count(message => message != null && !message.ProtocolMessage),
                RunId = run == null ? null : run.RunId,
                RunRuntimeId = run == null ? null : run.RuntimeId,
                RunStatus = run == null ? null : run.Status,
                RunPhase = run == null ? null : run.Phase,
                RunStartedUtc = run == null ? (System.DateTime?)null : run.StartedUtc
            };
        }
    }
}
