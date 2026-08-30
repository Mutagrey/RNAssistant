using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Vba;

namespace RNAssistant.Office.Tools
{
    internal sealed partial class VbaToolExecutor
    {
        public ToolResult ExecuteCustomTool(
            ToolDefinition tool,
            ToolCommand command,
            AppSettings settings,
            bool dryRun,
            bool manualRun,
            ChatSession session,
            CancellationToken cancellationToken)
        {
            if (!dryRun)
            {
                var reconciliationError = ReconcilePendingMutations();
                if (reconciliationError != null) return reconciliationError;
            }
            JObject arguments;
            try
            {
                arguments = JObject.FromObject(command == null
                    ? new Dictionary<string, object>()
                    : command.Arguments ?? new Dictionary<string, object>());
            }
            catch (JsonException ex)
            {
                return ToolResult.Fail(
                    "VBA tool arguments are invalid: " + ex.Message,
                    null,
                    "vba_arguments_invalid",
                    true);
            }
            var outcome = _packageService.Execute(
                new VbaPackageExecutionRequest
                {
                    Source = VbaPackageToolAdapter.ToSource(tool),
                    Arguments = arguments,
                    DryRun = dryRun,
                    Correlation = MutationCorrelation(command, session)
                },
                cancellationToken);
            return VbaMutationToolResultMapper.ToToolResult(outcome);
        }

        public ToolResult InstallCustomTool(
            ToolDefinition tool,
            bool dryRun,
            ChatSession session = null,
            ToolCommand command = null,
            bool reconcile = true,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (reconcile && !dryRun)
            {
                var reconciliationError = ReconcilePendingMutations();
                if (reconciliationError != null) return reconciliationError;
            }
            var outcome = _packageService.InstallPersistent(
                new VbaPackageInstallRequest
                {
                    Source = VbaPackageToolAdapter.ToSource(tool),
                    DryRun = dryRun,
                    Correlation = MutationCorrelation(command, session)
                },
                cancellationToken);
            return VbaMutationToolResultMapper.ToToolResult(outcome);
        }

        public ToolResult RemoveCustomTool(
            ToolDefinition tool,
            ChatSession session = null,
            ToolCommand command = null,
            bool reconcile = true,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (reconcile)
            {
                var reconciliationError = ReconcilePendingMutations();
                if (reconciliationError != null) return reconciliationError;
            }
            var outcome = _packageService.RemoveOwned(
                new VbaPackageRemoveRequest
                {
                    Source = VbaPackageToolAdapter.ToSource(tool),
                    Correlation = MutationCorrelation(command, session)
                },
                cancellationToken);
            return VbaMutationToolResultMapper.ToToolResult(outcome);
        }

        public string GetInstallationStatus(ToolDefinition tool)
        {
            return _packageService.GetInstallationStatus(VbaPackageToolAdapter.ToSource(tool));
        }

        public string GetInstallationStatus(ToolDefinition globalTool, ToolDefinition documentTool)
        {
            var live = VbaPackageToolAdapter.ToSource(documentTool);
            return _packageService.ClassifyDocumentSnapshot(
                VbaPackageToolAdapter.ToSource(globalTool),
                live == null ? null : live.Components);
        }
    }
}
