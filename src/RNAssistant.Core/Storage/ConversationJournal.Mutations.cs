using System;
using System.IO;
using System.Linq;
using System.Threading;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Storage
{
    internal sealed partial class ConversationJournal
    {
        internal ResourceMutationOrigin MutationOrigin(ChatSession session, ToolExecutionContext context)
        {
            if (session?.LastRun == null || context == null || session.LastRun.RunId != context.RunId || session.LastRun.TurnId != context.TurnId)
                throw new InvalidOperationException("Mutation belongs to another conversation run.");
            return new ResourceMutationOrigin(_scope, session.Id, context.RunId, context.Call.Id, context.StepId);
        }

        internal ResourceCallLink BindMutation(ResourceMutationOrigin origin)
        {
            if (origin?.ConversationScope != _scope) throw new InvalidOperationException("Foreign conversation scope.");
            return Access((db, cursor) => {
                var current = TextChat(db, origin.ChatId);
                var run = CurrentRun(db, origin.ChatId);
                var call = FindCall(db, origin.ChatId, origin.RunId, origin.CallId);
                var operation = StartedOperation(db, origin.ChatId, origin.RunId, origin.CallId);
                var started = operation == null ? null : FindChatWrite(db, operation);
                if (current?.Fact.Run?.RunId != origin.RunId || run?.Fact.Lifecycle != RunLifecycle.Running ||
                    call?.Call.AcceptedCallOrigin.StepId != origin.StepId || started?.Fact.Tools?.MayHaveSideEffects != true ||
                    started.Fact.Tools.Kind != AgentRunEventKind.ToolStarted || PendingTool(db, origin.ChatId, origin.RunId, origin.CallId) != null ||
                    TerminalOperation(db, origin.ChatId, origin.RunId, origin.CallId) != null)
                    throw new InvalidOperationException("Mutation requires its active accepted call and runtime entry.");
                return new ResourceCallLink(origin, call.Position, started.Position);
            }, CancellationToken.None);
        }

        // Immutable direct reads need neither conversation catch-up nor its append
        // lock. Resource replay can validate this proof without a lock-order cycle.
        internal void VerifyMutation(ResourceCallLink link)
        {
            if (link?.Origin.ConversationScope != _scope) throw new InvalidDataException("Foreign mutation origin.");
            var origin = link.Origin;
            var accepted = DecodeChatCommit(Journal.Read(link.Accepted));
            var started = DecodeChatCommit(Journal.Read(link.Started));
            var call = accepted.Messages.SingleOrDefault(item => item.Header.RunId == origin.RunId &&
                item.Header.ToolCallId == origin.CallId && item.Header.AcceptedCallOrigin != null)?.Header;
            if (accepted.Card.ChatId != origin.ChatId || accepted.Tools?.Kind != AgentRunEventKind.ResponseAccepted ||
                call?.AcceptedCallOrigin.StepId != origin.StepId || started.Card.ChatId != origin.ChatId ||
                started.Run?.RunId != origin.RunId || started.Run.TurnId != accepted.Run.TurnId ||
                started.Tools?.Kind != AgentRunEventKind.ToolStarted || started.Tools.CallId != origin.CallId ||
                started.Tools.ToolName != call.ToolName || started.Tools.StepId != origin.StepId || !started.Tools.MayHaveSideEffects)
                throw new InvalidDataException("Resource preparation does not identify its accepted runtime entry.");
        }
    }
}
