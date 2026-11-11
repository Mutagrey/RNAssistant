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
        public string OperationId { get; private set; } = Guid.NewGuid().ToString("N");
        private string _journalChat;
        internal long JournalRevision { get; private set; }
        internal int JournalMessageCount { get; private set; }

        internal void BindJournal(ChatSession session)
        {
            if (_journalChat != null && _journalChat != session.Id)
                throw new InvalidOperationException("The commit belongs to another chat.");
            if (_journalChat != null) return;
            _journalChat = session.Id;
            JournalRevision = session.Revision;
            JournalMessageCount = this is ConversationRunCommit run ? run.ExpectedMessageCount : session.Messages.Count;
        }

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
        internal AgentRunEventKind FactKind { get; private set; }
        internal RNAssistant.Core.Tools.ToolExecutionContext ToolContext { get; private set; }
        internal RNAssistant.Core.Tools.ToolExecutionRecord Execution { get; private set; }
        internal bool ResultProjection { get; private set; }
        internal string ActivityId { get; private set; }
        public string SessionId { get { return _state.Id; } }
        public string Host { get { return _state.Host; } }
        public string DocumentKey { get { return _state.DocumentKey; } }
        public long ExpectedRevision { get { return _state.Revision; } }
        public int ExpectedMessageCount { get; private set; }
        internal IReadOnlyList<string> AppendedOrder { get; private set; }
        public SessionEventPosition Position { get; private set; }
        private readonly ChatSession _state;
        private ChatSession _applied;
        private bool _projectionApplied;
        private bool _submitted;
        private bool _referenceDelta;

        public ConversationRunCommit(ChatSession session, AgentRunEvent fact, IEnumerable<ChatMessage> appended,
            ChatMessage activity = null, bool resultProjection = false, IEnumerable<ChatMessage> updated = null,
            int? expectedMessageCount = null)
        {
            if (fact?.Summary == null) throw new ArgumentNullException(nameof(fact));
            if (session?.LastRun?.RunId != fact.Summary.RunId || string.IsNullOrWhiteSpace(session.Id) ||
                string.IsNullOrWhiteSpace(session.Host) || string.IsNullOrWhiteSpace(session.DocumentKey) || session.Revision <= 0)
                throw new InvalidOperationException("A runtime commit requires its persisted conversation and current run.");
            _state = session.CopyForRunCommit();
            ExpectedMessageCount = expectedMessageCount ?? session.Messages.Count;
            if (ExpectedMessageCount < 0) throw new ArgumentOutOfRangeException(nameof(expectedMessageCount));
            RunId = fact.Summary.RunId; FactKind = fact.Kind;
            ToolContext = fact.ToolContext ?? fact.Execution?.Context; Execution = fact.Execution;
            ResultProjection = resultProjection; ActivityId = activity?.Id;
            var writes = new List<ConversationMessageWrite>();
            var additions = (appended ?? new ChatMessage[0]).ToArray();
            AppendedOrder = Array.AsReadOnly(additions.Select(message => message.Id).ToArray());
            foreach (var message in additions)
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
                if (fact.Kind != AgentRunEventKind.ToolStarted && fact.Kind != AgentRunEventKind.ToolCompleted && fact.Kind != AgentRunEventKind.ToolWaiting)
                    throw new InvalidOperationException("Only a tool fact can publish activity.");
                if (fact.Kind == AgentRunEventKind.ToolCompleted)
                    activity.ExecutionProgress = RNAssistant.Core.Tools.ToolExecutionProgress.Capture(fact.Execution);
                writes.Add(new ConversationMessageWrite(resultProjection || fact.Kind == AgentRunEventKind.ToolWaiting ? SessionOperationTypes.MessageUpdated :
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

        // A synchronous submission, not a retained session/snapshot delegate.
        // Preserve the old own-append recovery cursor even when the call throws.
        public void Persist(Action<ConversationRunCommit> write, ChatSession projection)
        {
            if (write == null) throw new ArgumentNullException(nameof(write));
            if (_submitted || projection == null || projection.Id != SessionId || projection.Host != Host ||
                projection.DocumentKey != DocumentKey || projection.Revision != ExpectedRevision || projection.LastRun?.RunId != RunId)
                throw new InvalidOperationException("The runtime commit belongs to another projection, revision or submission.");
            _submitted = true;
            try
            {
                write(this);
                if (Position == null) throw new InvalidOperationException("Runtime storage did not confirm the commit.");
            }
            finally
            {
                if (_applied != null)
                {
                    projection.Revision = _applied.Revision;
                    projection.UpdatedUtc = _applied.UpdatedUtc;
                    projection.StorageHeadHash = _applied.StorageHeadHash;
                    projection.StorageByteLength = _applied.StorageByteLength;
                    projection.StorageLastWriteUtcTicks = _applied.StorageLastWriteUtcTicks;
                    projection.StorageTailByteOffset = _applied.StorageTailByteOffset;
                    projection.ToolCallReferenceSequence = _applied.ToolCallReferenceSequence;
                    projection.EvidenceReferenceSequence = _applied.EvidenceReferenceSequence;
                    if (_referenceDelta) projection.ResourceReferenceIndex.AddRange(_applied.ResourceReferenceIndex);
                    else projection.ResourceReferenceIndex = _applied.ResourceReferenceIndex;
                    if (_projectionApplied)
                    {
                        projection.Artifacts = _applied.Artifacts;
                        projection.HtmlWorkspace = _applied.HtmlWorkspace;
                        projection.HtmlWorkspaceRecovery = _applied.HtmlWorkspaceRecovery;
                        projection.ContextCheckpoints = _applied.ContextCheckpoints;
                        projection.ActiveContextCheckpointId = _applied.ActiveContextCheckpointId;
                        projection.ActiveHtmlArtifactId = _applied.ActiveHtmlArtifactId;
                        projection.ActiveTaskListArtifactId = _applied.ActiveTaskListArtifactId;
                        projection.ActivePlanDocumentArtifactId = _applied.ActivePlanDocumentArtifactId;
                    }
                    _applied = null;
                }
            }
        }

        // Temporary conversion inside the existing aggregate backend. Never expose
        // this state as a partial ChatSession to callers or generic Commit.
        internal ChatSession CreateStateProjection() { return _state.CopyForRunCommit(); }
        internal void RecordStorageState(ChatSession state, bool projectionApplied, bool referenceDelta = false)
        {
            if (state.Revision <= ExpectedRevision) return;
            _applied = state.CopyForRunCommit();
            _projectionApplied = projectionApplied;
            _referenceDelta = referenceDelta;
            _applied.ResourceReferenceIndex = state.ResourceReferenceIndex;
            _applied.HtmlWorkspace = state.HtmlWorkspace;
            _applied.ContextCheckpoints = state.ContextCheckpoints;
            Position = new SessionEventPosition(state.Id, state.Revision, state.StorageHeadHash);
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
