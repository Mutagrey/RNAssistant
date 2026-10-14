using System;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Domains.PowerPoint;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.Tools
{
    internal sealed class PowerPointToolHandler : IReadOnlyToolHandler, IManagedMutationToolHandler
    {
        private readonly string _toolId;
        private readonly PowerPointToolAdapter _adapter;
        private readonly HostRuntime _runtime;
        private readonly ChatSession _session;
        private readonly PowerPointSearchResourceService _search;

        internal PowerPointToolHandler(
            string toolId,
            PowerPointToolAdapter adapter,
            HostRuntime runtime,
            ChatSession session,
            ResourceGatewayService gateway)
        {
            if (!PowerPointToolIds.Owns(toolId))
                throw new ArgumentException(
                    "An exact PowerPoint tool id is required.", nameof(toolId));
            _toolId = toolId;
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _session = session;
            _search = new PowerPointSearchResourceService(gateway);
        }

        internal static ToolBinding BindingFor(string toolId)
        {
            if (!PowerPointToolIds.Owns(toolId)) return null;
            if (toolId == PowerPointToolIds.SearchText) return new ToolBinding("powerpoint.search.text.resource.v1");
            return new ToolBinding(
                "powerpoint." +
                toolId.Substring("powerpoint.".Length).Replace('_', '.') +
                ".v1");
        }

        public Task<ToolHandlerResult> ExecuteAsync(
            ToolHandlerContext context,
            CancellationToken cancellationToken)
        {
            if (_session == null)
                return OfficeToolFailure.Rejected(
                    "PowerPoint operations require an active chat session.",
                    "powerpoint_session_required");
            try
            {
                if (_toolId == PowerPointToolIds.SearchText)
                    return Task.FromResult(_runtime.ReadDocument(Target(_session), cancellationToken, delegate {
                        context.MarkDispatchPossible();
                        return _search.Search(_session, context.Arguments, cancellationToken);
                    }));
                var outcome = PowerPointToolIds.IsRead(_toolId)
                    ? _runtime.ReadDocument(
                        Target(_session), cancellationToken, delegate
                        {
                            context.MarkDispatchPossible();
                            return _adapter.Execute(
                                _toolId, context.Arguments, null,
                                cancellationToken);
                        })
                    : _runtime.ExecuteDocumentMutation(
                        Target(_session), cancellationToken, delegate
                        {
                            return _adapter.Execute(
                                _toolId, context.Arguments,
                                context.MarkDispatchPossible,
                                cancellationToken);
                        }, terminalOutcome => context.Complete(OfficeDomainResult.Project(terminalOutcome)), context.CompleteFailure);
                return Task.FromResult(OfficeDomainResult.Project(outcome));
            }
            catch (OfficeDocumentGuardException ex)
                when (!context.MayHaveDispatched)
            {
                return OfficeToolFailure.Guard(ex);
            }
            catch (HostRuntime.MutationLockException ex)
                when (!context.MayHaveDispatched)
            {
                return OfficeToolFailure.Lock(ex);
            }
        }

        private static OfficeDocumentExecutionExpectation Target(
            ChatSession session)
        {
            return new OfficeDocumentExecutionExpectation
            {
                Host = session.Host,
                DocumentKey = session.DocumentKey,
                RuntimeDocumentKey = session.LastRun == null
                    ? string.Empty : session.LastRun.DocumentRuntimeKey
            };
        }

    }
}
