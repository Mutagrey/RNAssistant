using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Persistence
{
    // A transient write command for the existing session stream, not another store.
    // Runtime facts select semantics; protocol roles and UI labels do not.
    public sealed class ConversationRunCommit
    {
        public string RunId { get; private set; }
        public IReadOnlyList<ConversationMessageWrite> Messages { get; private set; }

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
