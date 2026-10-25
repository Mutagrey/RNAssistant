using System;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office.Services
{
    internal static class ChatDiagnosticsScope
    {
        internal static ChatSession ReadWorkspaceSession(AppDataPaths paths, ChatStore chats, string workspaceId, string chatId)
        {
            if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(chatId))
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: an exact workspace and chat are required.");
            var workspaces = new WorkspaceStore(paths);
            var workspace = workspaces.OpenConnectedHistory(workspaceId);
            var session = workspaces.LoadSession(chats, workspace, chatId) ??
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: the chat is absent from this workspace.");
            Require(session, chatId, workspaceId);
            return session;
        }

        internal static void Require(ChatSession session, string chatId, string workspaceId)
        {
            if (!string.IsNullOrEmpty(session.WorkspaceId) || workspaceId != null)
            {
                if (session.Host != "Workspace" || string.IsNullOrWhiteSpace(workspaceId) ||
                    session.WorkspaceId != workspaceId || session.DocumentKey != workspaceId || session.Id != chatId)
                    throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: diagnostics require the exact workspace and chat.");
            }
        }

        internal static string DownloadOwner(string owner, string workspaceId)
        { return string.IsNullOrEmpty(workspaceId) ? owner : owner + ":" + workspaceId; }
    }
}
