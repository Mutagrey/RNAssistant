using RNAssistant.Core.Persistence;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Llm;
using RNAssistant.Runtime.Contracts;

namespace RNAssistant.Runtime
{
    public sealed partial class WorkspaceConversationService
    {
        public async Task<WorkspaceCancellationReceipt> CancelRunAsync(WorkspaceDescriptor workspace,
            WorkspaceRunControlRequest request, CancellationToken cancellationToken = default(CancellationToken))
        {
            ValidateControl(request);
            var receipt = new WorkspaceCancellationReceipt { SessionId = request.SessionId, RunId = request.RunId };
            var session = GetSession(workspace, request.SessionId);
            if (session?.LastRun?.RunId != request.RunId || ConversationEventFeed.Cursor(session) != request.Cursor)
                receipt.Status = WorkspaceCancellationStatus.StaleState;
            else if (session.LastRun.KernelState == null)
                receipt.Status = WorkspaceCancellationStatus.Unconfirmed;
            else if (session.LastRun.KernelState.Summary.Lifecycle != RunLifecycle.Running)
                receipt.Status = WorkspaceCancellationStatus.AlreadyStopped;
            else receipt.Status = await WorkspaceRunControl.SendAsync(_paths.WorkspaceDirectory, request, cancellationToken).ConfigureAwait(false);
            return receipt;
        }

        public async Task<WorkspaceRunResult> ContinueAsync(WorkspaceDescriptor workspace, WorkspaceRunControlRequest request,
            AppSettings settings, Func<string> apiKeyProvider, Action<WorkspaceRunEvent> progress = null,
            CancellationToken cancellationToken = default(CancellationToken), string modelDigest = null)
        {
            ValidateControl(request);
            using (AcquireSessionLease(request.SessionId))
            {
                var session = GetSession(workspace, request.SessionId);
                if (session?.LastRun?.RunId != request.RunId || ConversationEventFeed.Cursor(session) != request.Cursor)
                    throw new InvalidOperationException("stale_run_checkpoint: inspect the session before requesting continuation.");
                ConversationRunPolicy.ForAcceptedRun(session);
                ValidateContinuationSettings(session, settings, modelDigest);
                if (session.LastRun.InterruptedToolId != null || session.LastRun.InterruptedEffectPossible ||
                    session.Messages.Any(item => item.RunId == request.RunId && item.ProtocolMessage && item.ExcludeFromModelContext))
                    throw new InvalidOperationException("An interrupted tool exchange requires explicit inspection/new input, not same-run continuation.");
                var continuation = AgentRunContinuation.RestoreCheckpoint(session.LastRun.KernelState, 0, RestoreAcceptedHistory(session));
                var acceptance = session.LastRun.WorkspaceAcceptance ?? new WorkspaceRunAcceptance();
                var ports = new WorkspacePorts(_paths, _chats, _files, _payloads, _webSnapshots, workspace,
                    session, settings, apiKeyProvider, progress, acceptance, webHost: _webHost,
                    extraTools: _extraTools?.Invoke(workspace),
                    office: OfficeFor(workspace, session.LastRun.OfficeTarget, session, settings, session.LastRun.RunId),
                    pluginSource: IsPluginSource(workspace));
                _chats.CloseOpenSteps(session, request.RunId, "interrupted", "Explicit continuation after the previous invocation stopped.");
                // The kernel's first mandatory append claims this cursor. No new
                // input, run ID, budget or accepted tool call is manufactured.
                acceptance.State = acceptance.Requested ? WorkspaceAcceptanceState.Pending : WorkspaceAcceptanceState.NotRequested;
                acceptance.Error = null;
                session.LastRun.Phase = null;
                session.LastRun.CurrentAction = null;
                using (var control = OpenRunControl(session, cancellationToken))
                {
                    var result = await ConversationRunCoordinator.ContinueAsync(continuation,
                        ports, ports.Tools, ports, control.Token).ConfigureAwait(false);
                    if (acceptance.Requested)
                    {
                        AssessAcceptance(workspace, session, result.Summary, acceptance);
                        session.LastRun.WorkspaceAcceptance = acceptance;
                        _chats.Commit(session, new ConversationCommit());
                    }
                    StartTitleGeneration(workspace, session, settings, apiKeyProvider, result.Summary);
                    return new WorkspaceRunResult(session.Id, result.Summary, acceptance, GetPendingQuestions(session),
                        cursor: ConversationEventFeed.Cursor(session), presentation: Presentation(workspace, session));
                }
            }
        }

        private WorkspaceRunControl OpenRunControl(ChatSession session, CancellationToken cancellationToken)
        {
            var initialRevision = session.Revision;
            return new WorkspaceRunControl(_paths.WorkspaceDirectory, session.Id, session.LastRun.RunId,
                cursor => session.LastRun.KernelState?.Summary.Lifecycle == RunLifecycle.Running &&
                    session.Revision > initialRevision && ConversationEventFeed.Cursor(session) == cursor,
                cancellationToken);
        }

        private static void ValidateControl(WorkspaceRunControlRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.SessionId) || string.IsNullOrWhiteSpace(request.RunId) ||
                string.IsNullOrWhiteSpace(request.Cursor) || request.Cursor.Length > 512 || !request.Cursor.StartsWith("events1.", StringComparison.Ordinal))
                throw new ArgumentException("Session, exact run ID and inspect cursor are required.");
        }

        private static void ValidateContinuationSettings(ChatSession session, AppSettings settings, string modelDigest)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            ValidateMessageRoles(settings);
            var accepted = session.LastRun.ModelConfiguration;
            if (accepted == null || session.ReasoningEnabled != accepted.ReasoningEnabled || settings.Model != accepted.Model ||
                ModelContextBudget.ContextWindowTokens(settings) != accepted.ContextWindowTokens ||
                EndpointSha256(settings.BaseUrl) != accepted.EndpointSha256 || modelDigest != accepted.Digest ||
                settings.AgentResponseMode != accepted.AgentResponseMode || settings.SystemPromptRole != accepted.SystemPromptRole ||
                settings.ToolResultRole != accepted.ToolResultRole || settings.ReasoningRequestMode != accepted.ReasoningRequestMode)
                throw new InvalidOperationException("Continuation must keep the accepted endpoint, model, digest, context and protocol roles/settings.");
        }
    }
}
