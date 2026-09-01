using System;
using System.Threading;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Vba;

namespace RNAssistant.Office.Tools
{
    internal sealed partial class VbaToolExecutor
    {
        internal VbaPackageResult ExecuteCustomPackage(
            ToolPackageSource source,
            JObject arguments,
            bool dryRun,
            ToolExecutionContext execution,
            ChatSession session,
            Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            if (!dryRun)
            {
                var reconciliation = ReconcilePendingMutationOutcome();
                if (reconciliation != null)
                    return VbaPackageResult.Execution(
                        source, reconciliation, false);
            }
            var dispatched = false;
            Action dispatch = delegate
            {
                dispatched = true;
                if (markDispatchPossible != null) markDispatchPossible();
            };
            var outcome = _packageService.Execute(
                new VbaPackageExecutionRequest
                {
                    Source = source,
                    Arguments = arguments == null
                        ? new JObject() : (JObject)arguments.DeepClone(),
                    DryRun = dryRun,
                    Correlation = MutationCorrelation(execution, session),
                    MarkDispatchPossible = dispatch
                },
                cancellationToken);
            return VbaPackageResult.Execution(source, outcome, dispatched);
        }

        internal VbaPackageResult InstallCustomPackage(
            ToolPackageSource source,
            bool dryRun,
            ChatSession session = null,
            bool reconcile = true,
            Action markDispatchPossible = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (reconcile && !dryRun)
            {
                var reconciliation = ReconcilePendingMutationOutcome();
                if (reconciliation != null)
                    return VbaPackageResult.Lifecycle(
                        source, reconciliation, false);
            }
            var dispatched = false;
            Action dispatch = delegate
            {
                dispatched = true;
                if (markDispatchPossible != null) markDispatchPossible();
            };
            var outcome = _packageService.InstallPersistent(
                new VbaPackageInstallRequest
                {
                    Source = source,
                    DryRun = dryRun,
                    Correlation = MutationCorrelation(
                        (ToolExecutionContext)null, session),
                    MarkDispatchPossible = dispatch
                },
                cancellationToken);
            return VbaPackageResult.Lifecycle(source, outcome, dispatched);
        }

        internal VbaPackageResult RemoveCustomPackage(
            ToolPackageSource source,
            ChatSession session = null,
            bool reconcile = true,
            Action markDispatchPossible = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (reconcile)
            {
                var reconciliation = ReconcilePendingMutationOutcome();
                if (reconciliation != null)
                    return VbaPackageResult.Lifecycle(
                        source, reconciliation, false);
            }
            var dispatched = false;
            Action dispatch = delegate
            {
                dispatched = true;
                if (markDispatchPossible != null) markDispatchPossible();
            };
            var outcome = _packageService.RemoveOwned(
                new VbaPackageRemoveRequest
                {
                    Source = source,
                    Correlation = MutationCorrelation(
                        (ToolExecutionContext)null, session),
                    MarkDispatchPossible = dispatch
                },
                cancellationToken);
            return VbaPackageResult.Lifecycle(source, outcome, dispatched);
        }

        internal VbaPackageStatusResult GetInstallationStatus(
            ToolPackageSource source)
        {
            return _packageService.GetInstallationStatus(source);
        }

        internal VbaPackageStatusResult GetInstallationStatus(
            ToolPackageSource globalSource,
            ToolPackageSource documentSource)
        {
            return new VbaPackageStatusResult(globalSource,
                _packageService.ClassifyDocumentSnapshot(
                    globalSource,
                    documentSource == null
                        ? null : documentSource.Components));
        }

        private static VbaMutationCorrelation MutationCorrelation(
            ToolExecutionContext execution,
            ChatSession session)
        {
            return new VbaMutationCorrelation
            {
                SessionId = SessionId(session),
                RunId = execution == null
                    ? session == null || session.LastRun == null
                        ? null : session.LastRun.RunId
                    : execution.RunId,
                TurnId = execution == null
                    ? session == null || session.LastRun == null
                        ? null : session.LastRun.TurnId
                    : execution.TurnId,
                StepId = execution == null ? null : execution.StepId,
                ToolCallId = execution == null || execution.Call == null
                    ? null : execution.Call.Id
            };
        }
    }
}
