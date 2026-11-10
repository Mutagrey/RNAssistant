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
        public static readonly ToolBinding Binding = new ToolBinding("conversation.chat.rename.v2");
        private readonly ChatSession _session;
        private readonly Func<string, string, Action, ChatRenameResult> _renameOther;

        public ChatRenameToolHandler(ChatSession session, Func<string, string, Action, ChatRenameResult> renameOther = null)
        { _session = session; _renameOther = renameOther; }

        public static ToolCatalogEntry Entry()
        {
            return new ToolCatalogEntry
            {
                Id = ToolId, Host = "Common", Name = "chat_rename", BuiltIn = true,
                Enabled = true, Scope = "session", AgentCanRun = true, MutatesLocalState = true, ContractVersion = "2",
                Description = "Rename your current chat once its main task is clear or changes substantially, or when the user asks. Use a concise title in the user's language. Preserve a user-chosen title unless asked to rename. Do not rename after every step. Optional agent_id addresses another chat in the same document/project: first read its Chat overview through core.read in this run. A busy, incompatible or changed target is rejected; no other chat is started. Only title metadata changes.",
                ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{\"title\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":200,\"description\":\"Concise single-line chat title.\"},\"agent_id\":{\"type\":\"string\",\"minLength\":1,\"description\":\"Optional exact agent address discovered in this document/project. Omit for this chat.\"}},\"required\":[\"title\"],\"additionalProperties\":false}",
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

            var agentId = context.Arguments.TryGetValue("agent_id", out value) ? value as string : null;
            if (context.Arguments.ContainsKey("agent_id") && string.IsNullOrWhiteSpace(agentId))
                return Rejected("Use an exact nonempty agent address or omit agent_id.");
            if (agentId != null && agentId != AgentChatAddress.IdFor(_session.Id))
            {
                if (_renameOther == null) return Rejected("Addressed chat rename is unavailable.");
                var dispatched = false;
                try
                {
                    var result = _renameOther(agentId, title, () => { dispatched = true; context.MarkDispatchPossible(); });
                    return Task.FromResult(context.Complete(new ToolHandlerResult(
                        RuntimeResult.Ok("Название чата: " + result.Title, JsonConvert.SerializeObject(result)),
                        result.Changed ? ToolEffectEvidence.VerifiedChange : ToolEffectEvidence.VerifiedNoChange)));
                }
                catch (InvalidOperationException error) when (!dispatched)
                { return Rejected(error.Message); }
            }

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
                    JsonConvert.SerializeObject(new ChatRenameResult { Title = _session.Title })),
                changed ? ToolEffectEvidence.VerifiedChange : ToolEffectEvidence.VerifiedNoChange)));
        }

        private static Task<ToolHandlerResult> Rejected(string message)
        {
            return Task.FromResult(new ToolHandlerResult(RuntimeResult.Error(message), ToolEffectEvidence.None,
                recovery: new ToolRecoveryContract(ToolFailureKind.RejectedNoEffect, ToolRetryPolicy.Replan)));
        }

    }

    public sealed class ChatRenameResult
    {
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("agent_id", NullValueHandling = NullValueHandling.Ignore)] public string AgentId { get; set; }
        [JsonProperty("warning", NullValueHandling = NullValueHandling.Ignore)] public string Warning { get; set; }
        [JsonIgnore] public bool Changed { get; set; }
    }
}
