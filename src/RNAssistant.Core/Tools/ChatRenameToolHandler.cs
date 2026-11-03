using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Core.Tools
{
    public sealed class ChatRenameToolHandler : IManagedMutationToolHandler
    {
        public const string ToolId = "chat.rename";
        public static readonly ToolBinding Binding = new ToolBinding("conversation.chat.rename.v1");
        private readonly ChatSession _session;

        public ChatRenameToolHandler(ChatSession session) { _session = session; }

        public static ToolCatalogEntry Entry()
        {
            return new ToolCatalogEntry
            {
                Id = ToolId, Host = "Common", Name = "chat_rename", BuiltIn = true,
                Enabled = true, Scope = "session", AgentCanRun = true, MutatesLocalState = true,
                Description = "Rename your current chat once the main task is clear if the title is generic, after a substantial topic/task change makes the title inaccurate, or when the user asks. Use a concise, specific title in the user's language. Preserve an explicitly user-chosen title unless the user requests a rename. Do not rename after every step or for cosmetic rewording. Only this chat changes; no document changes or confirmation are needed.",
                ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{\"title\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":200,\"description\":\"Concise single-line chat title.\"}},\"required\":[\"title\"],\"additionalProperties\":false}",
                Binding = Binding,
                Policy = new ToolPolicy(ToolEffect.Write, ToolVerification.Tool, false, false,
                    new[] { ChatModes.Agent, ChatModes.Plan })
            };
        }

        public Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (_session == null || !_session.UsesCurrentProtocol)
                return Rejected("An editable current chat is required.");
            object value;
            var title = context.Arguments.TryGetValue("title", out value) ? (value as string)?.Trim() : null;
            if (string.IsNullOrWhiteSpace(title) || title.Length > 200 || title.Any(char.IsControl))
                return Rejected("Use a nonempty single-line title of at most 200 characters.");

            var changed = !string.Equals(_session.Title, title, StringComparison.Ordinal) || !_session.TitleAssignedByAgent;
            if (changed)
            {
                context.MarkDispatchPossible();
                _session.Title = title;
                _session.TitleAssignedByAgent = true;
            }
            // The existing kernel commit persists these metadata with the tool result.
            return Task.FromResult(context.Complete(new ToolHandlerResult(
                RuntimeResult.Ok("Название чата: " + _session.Title,
                    JsonConvert.SerializeObject(new RenameResult { Title = _session.Title })),
                changed ? ToolEffectEvidence.VerifiedChange : ToolEffectEvidence.VerifiedNoChange)));
        }

        private static Task<ToolHandlerResult> Rejected(string message)
        {
            return Task.FromResult(new ToolHandlerResult(RuntimeResult.Error(message), ToolEffectEvidence.None,
                recovery: new ToolRecoveryContract(ToolFailureKind.RejectedNoEffect, ToolRetryPolicy.Replan)));
        }

        private sealed class RenameResult
        {
            [JsonProperty("title")] public string Title { get; set; }
        }
    }
}
