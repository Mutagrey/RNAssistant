using System;
using System.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;

namespace RNAssistant.Application.Conversations
{
    // Common Office/Workspace projection of a kernel cancellation, not an executor.
    // The caller owns the conversation lease and the Inbox lock. Use the existing
    // CAS commit for call/activity/Run; a partial append is unknown, never replayed.
    internal static class ConversationWaitCancellation
    {
        internal static void Commit(ChatSession session, IConversationStore store, ChatBlobStore payloads)
        {
            var fact = AgentKernel.CancelWait(session.LastRun?.KernelState);
            if (fact.Execution == null)
            {
                if (session.LastRun.RunId != fact.Summary.RunId || session.LastRun.TurnId != fact.Summary.TurnId)
                    throw new InvalidOperationException("Saved pause belongs to another run or turn.");
                session.LastRun.KernelState = AgentRunState.Apply(session.LastRun.KernelState, fact);
                session.LastRun.Status = "cancelled";
                session.LastRun.Phase = "cancelled";
                session.LastRun.CurrentAction = fact.Summary.AssistantMessage;
                var pausedView = RunViewStateProjector.StampCurrentRun(session, includeProtocolResponses: true);
                new ConversationRunCommit(session, fact, new ChatMessage[0], updated: session.Messages.Where(message =>
                    pausedView != null && ReferenceEquals(message.RunViewState, pausedView))).Persist(store.CommitRun, session);
                return;
            }
            var context = fact.Execution.Context;
            if (session.LastRun.RunId != context.RunId || session.LastRun.TurnId != context.TurnId)
                throw new InvalidOperationException("Saved wait belongs to another run or turn.");
            var accepted = session.Messages.SingleOrDefault(message => message.RunId == context.RunId &&
                message.ProtocolMessage && message.Role == "assistant" && message.ToolCallId == context.Call.Id);
            if (accepted == null || session.Messages.Any(message => message.RunId == context.RunId &&
                message.ProtocolMessage && message.Role != "assistant" && message.ToolCallId == context.Call.Id))
                throw new InvalidOperationException("Saved wait has no unique unresolved accepted call.");
            var response = ConversationResponseHistoryReader.Read(AcceptedCallPayloadService.Hydrate(accepted, payloads));
            var call = response.Success ? response.Response.ToolCalls.SingleOrDefault() : null;
            if (call == null || call.Name != context.Call.Name || call.ArgumentsJson != context.Call.ArgumentsJson ||
                accepted.AcceptedCallOrigin?.StepId != context.StepId)
                throw new InvalidOperationException("Saved wait differs from the accepted call.");
            var activity = session.Messages.SingleOrDefault(message => message.RunId == context.RunId &&
                message.Activity?.ToolCallId == context.Call.Id);
            if (activity == null) throw new InvalidOperationException("Saved wait activity is missing.");
            var result = new ChatMessage { Role = accepted.ToolResultRole, ToolResultRole = accepted.ToolResultRole,
                ProtocolMessage = true, ToolCallId = context.Call.Id, ToolName = context.Call.Name,
                ResultStepId = context.StepId, RunId = context.RunId, ToolResultProtocolVersion = ToolResultWire.CurrentVersion,
                Content = ToolResultWire.Write(context.Call.Id, context.Call.Name, fact.Execution.Result),
                ExecutionProgress = ToolExecutionProgress.Capture(fact.Execution) };
            if (result.Role != ToolResultRoles.User && result.Role != ToolResultRoles.Tool && result.Role != ToolResultRoles.Developer)
                throw new InvalidOperationException("Saved wait result role is unsupported.");
            session.LastRun.KernelState = AgentRunState.Apply(session.LastRun.KernelState, fact);
            session.LastRun.Status = "cancelled";
            session.LastRun.Phase = "cancelled";
            session.LastRun.CurrentAction = fact.Summary.AssistantMessage;
            session.LastRun.IterationsUsed = fact.Summary.IterationsUsed;
            session.LastRun.ToolStepsUsed = fact.Summary.ToolStepsUsed;
            activity.Activity.Status = "cancelled";
            activity.Activity.ExecutionStatus = "notdispatched";
            activity.Activity.ResultMessage = fact.Execution.Message;
            activity.Activity.ErrorCode = "wait_cancelled";
            activity.Activity.ExecutionEvidence = fact.Execution.Evidence;
            activity.ExecutionProgress = result.ExecutionProgress;
            session.Messages.Add(result);
            var view = RunViewStateProjector.StampCurrentRun(session, includeProtocolResponses: true);
            new ConversationRunCommit(session, fact, new[] { result }, activity,
                updated: session.Messages.Where(message => view != null && ReferenceEquals(message.RunViewState, view))).Persist(store.CommitRun, session);
        }
    }
}
