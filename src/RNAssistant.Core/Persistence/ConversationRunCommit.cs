using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Persistence
{
    // State changes never infer message/tool events. History owners identify the
    // exact writes and removals; kernel facts alone select tool lifecycle semantics.
    public class ConversationCommit
    {
        public IReadOnlyList<ConversationMessageWrite> Messages { get; protected set; }
        public IReadOnlyList<string> RemovedMessageIds { get; private set; }

        public ConversationCommit(IEnumerable<ChatMessage> updated = null, IEnumerable<string> removed = null)
        {
            Messages = (updated ?? new ChatMessage[0]).GroupBy(message => message.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => new ConversationMessageWrite(SessionOperationTypes.MessageUpdated, group.Last())).ToArray();
            RemovedMessageIds = (removed ?? new string[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (RemovedMessageIds.Any(string.IsNullOrWhiteSpace) || Messages.Any(write => RemovedMessageIds.Contains(write.Message.Id, StringComparer.OrdinalIgnoreCase)))
                throw new ArgumentException("Message writes and removals require distinct exact identities.");
        }

        public static ConversationCommit UserInput(ChatMessage message, IEnumerable<ChatMessage> updated = null)
        { return Append(SessionOperationTypes.UserMessageAppended, message, updated); }

        // A recorded cancellation/result message is distinct from an execution
        // outcome: only ConversationRunCommit may finish a tool execution.
        public static ConversationCommit ToolResult(ChatMessage message, IEnumerable<ChatMessage> updated = null)
        { return Append(SessionOperationTypes.ToolResultRecorded, message, updated); }

        private static ConversationCommit Append(string operation, ChatMessage message, IEnumerable<ChatMessage> updated)
        {
            var commit = new ConversationCommit(updated);
            commit.Messages = new[] { new ConversationMessageWrite(operation, message) }
                .Concat(commit.Messages.Where(write => !string.Equals(write.Message.Id, message.Id, StringComparison.OrdinalIgnoreCase))).ToArray();
            return commit;
        }

        // Reserved for explicit history rewrites (edit/delete/clear/fork), never
        // a routine checkpoint. Only identities are compared, not message bodies.
        public static ConversationCommit RewriteHistory(IEnumerable<string> previousIds, IEnumerable<ChatMessage> remaining)
        {
            var messages = remaining.ToArray();
            var retained = new HashSet<string>(messages.Select(message => message.Id), StringComparer.OrdinalIgnoreCase);
            return new ConversationCommit(messages, previousIds.Where(id => !retained.Contains(id)));
        }
    }

    // A transient write command for the existing session stream, not another store.
    // Runtime facts select semantics; protocol roles and UI labels do not.
    public sealed class ConversationRunCommit : ConversationCommit
    {
        public string RunId { get; private set; }

        public ConversationRunCommit(AgentRunEvent fact, IEnumerable<ChatMessage> appended,
            ChatMessage activity = null, bool resultProjection = false, IEnumerable<ChatMessage> updated = null)
        {
            if (fact?.Summary == null) throw new ArgumentNullException(nameof(fact));
            RunId = fact.Summary.RunId;
            var writes = new List<ConversationMessageWrite>();
            foreach (var message in appended ?? new ChatMessage[0])
            {
                if (ReferenceEquals(message, activity)) continue;
                string operation;
                switch (fact.Kind)
                {
                    case AgentRunEventKind.ResponseAccepted:
                        operation = fact.Response.ToolCalls.Count == 0
                            ? SessionOperationTypes.AssistantMessageAppended : SessionOperationTypes.ToolCallRecorded;
                        break;
                    case AgentRunEventKind.ToolCompleted:
                        // Media helpers may also be protocol messages. Only the exact
                        // runtime call identity establishes a terminal result.
                        operation = message.ToolCallId == fact.Execution.Context.Call.Id
                            ? SessionOperationTypes.ToolResultRecorded : SessionOperationTypes.MessageUpdated;
                        break;
                    case AgentRunEventKind.SummaryChanged:
                        operation = SessionOperationTypes.AssistantMessageAppended;
                        break;
                    default: throw new InvalidOperationException("This runtime fact cannot append a message.");
                }
                writes.Add(new ConversationMessageWrite(operation, message));
            }
            if (activity != null)
            {
                if (fact.Kind != AgentRunEventKind.ToolStarted && fact.Kind != AgentRunEventKind.ToolCompleted)
                    throw new InvalidOperationException("Only a tool fact can publish activity.");
                if (fact.Kind == AgentRunEventKind.ToolCompleted)
                    activity.ExecutionProgress = RNAssistant.Core.Tools.ToolExecutionProgress.Capture(fact.Execution);
                writes.Add(new ConversationMessageWrite(resultProjection ? SessionOperationTypes.MessageUpdated :
                    fact.Kind == AgentRunEventKind.ToolStarted ? SessionOperationTypes.ToolExecutionStarted :
                    SessionOperationTypes.ToolExecutionFinished, activity));
            }
            foreach (var message in updated ?? new ChatMessage[0])
                if (!writes.Any(write => string.Equals(write.Message.Id, message.Id, StringComparison.OrdinalIgnoreCase)))
                    writes.Add(new ConversationMessageWrite(SessionOperationTypes.MessageUpdated, message));
            if (writes.Select(write => write.Message.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != writes.Count)
                throw new InvalidOperationException("A runtime commit contains duplicate message writes.");
            Messages = writes.AsReadOnly();
        }
    }

    public sealed class ConversationMessageWrite
    {
        public string Operation { get; private set; }
        public ChatMessage Message { get; private set; }

        internal ConversationMessageWrite(string operation, ChatMessage message)
        {
            if (message == null || string.IsNullOrWhiteSpace(message.Id))
                throw new ArgumentException("A runtime message requires its exact identity.", nameof(message));
            Operation = operation;
            Message = message;
        }
    }
}
