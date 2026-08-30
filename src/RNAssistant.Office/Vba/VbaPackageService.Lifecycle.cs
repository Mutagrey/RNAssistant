using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.Vba
{
    internal sealed partial class VbaPackageService
    {
        public VbaMutationOutcome Execute(
            VbaPackageExecutionRequest request,
            CancellationToken cancellationToken)
        {
            var preparation = PreparePackage(request == null ? null : request.Source);
            if (!preparation.Success) return preparation.Error;
            var package = preparation.Package;
            var arguments = request.Arguments == null
                ? new JObject()
                : (JObject)request.Arguments.DeepClone();
            string argumentError;
            if (!ToolSchemaSupport.ValidateArguments(arguments, package.ArgumentSchema, true, out argumentError))
            {
                return VbaMutationOutcome.Error(
                    argumentError,
                    null,
                    "vba_arguments_invalid",
                    true);
            }
            var positional = new JArray((package.ArgumentOrder ?? new string[0])
                .Select(name => arguments[name] == null
                    ? JValue.CreateNull()
                    : arguments[name].DeepClone()));
            var probe = Probe(package);
            var blocked = ExecutionProbeError(probe);
            if (blocked != null) return blocked;
            if (request.DryRun)
            {
                return VbaMutationOutcome.Ok(
                    "Dry run: VBA tool is valid and would run " + package.EntryPoint + ".",
                    new JObject
                    {
                        ["toolId"] = package.Id,
                        ["entryPoint"] = package.EntryPoint,
                        ["arguments"] = positional,
                        ["installationStatus"] = StatusText(probe.State),
                        ["sessionInstall"] = probe.State == VbaPackageInstallationState.NotInstalled
                    });
            }

            cancellationToken.ThrowIfCancellationRequested();
            var sessionInstalled = false;
            var lifecycleId = string.Empty;
            VbaMutationOutcome install = null;
            if (probe.State == VbaPackageInstallationState.NotInstalled)
            {
                lifecycleId = "session_lifecycle_" + Guid.NewGuid().ToString("N");
                install = InstallPackage(
                    package,
                    true,
                    lifecycleId,
                    request.Correlation,
                    cancellationToken);
                if (install.Status != VbaMutationOutcomeStatus.Ok) return install;
                sessionInstalled = true;
            }

            VbaMutationActionResult run = null;
            VbaMutationOutcome cleanup = null;
            OperationCanceledException cancellation = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var runProbe = Probe(package);
                var runProbeError = RunProbeError(runProbe, sessionInstalled, lifecycleId);
                if (runProbeError != null)
                {
                    run = VbaMutationActionResult.Error(
                        runProbeError.Message,
                        runProbeError.Data,
                        runProbeError.ErrorCode,
                        runProbeError.Retryable);
                }
                else
                {
                    var entry = package.Components.First(component =>
                        string.Equals(component.Type, "StdModule", StringComparison.OrdinalIgnoreCase) &&
                        (component.Code ?? string.Empty).IndexOf("<RNAssistantTool>", StringComparison.Ordinal) >= 0);
                    run = _backend.RunMacro(new VbaPackageRunActionRequest
                    {
                        MacroName = entry.Name + "." + package.EntryPoint,
                        Arguments = positional
                    });
                    if (run == null)
                    {
                        run = VbaMutationActionResult.Error(
                            "VBA function returned no result.",
                            null,
                            "vba_function_failed",
                            true);
                    }
                }
            }
            catch (OperationCanceledException ex)
            {
                cancellation = ex;
            }
            catch (Exception ex)
            {
                run = VbaMutationActionResult.Error(
                    "VBA function failed: " + ex.Message,
                    null,
                    "vba_function_failed",
                    true);
            }
            finally
            {
                if (sessionInstalled)
                {
                    cleanup = RemovePackage(
                        package,
                        true,
                        lifecycleId,
                        SessionMarker(package, lifecycleId),
                        request.Correlation,
                        CancellationToken.None);
                }
            }

            var data = ExecutionData(run, install, cleanup, sessionInstalled, lifecycleId);
            if (cleanup != null && cleanup.Status != VbaMutationOutcomeStatus.Ok)
            {
                return VbaMutationOutcome.Unknown(
                    "VBA tool finished, but temporary components were not durably verified as removed: " + cleanup.Message,
                    data,
                    "vba_session_cleanup_failed");
            }
            if (cancellation != null) throw cancellation;
            if (run == null || run.Status == VbaMutationActionStatus.Unknown)
            {
                return VbaMutationOutcome.Unknown(
                    run == null ? "VBA function result is unknown." : run.Message,
                    data,
                    run == null || string.IsNullOrWhiteSpace(run.ErrorCode)
                        ? "vba_function_unknown"
                        : run.ErrorCode);
            }
            if (run.Status == VbaMutationActionStatus.Error)
            {
                return VbaMutationOutcome.Error(
                    "VBA function failed: " + run.Message,
                    data,
                    string.IsNullOrWhiteSpace(run.ErrorCode) ? "vba_function_failed" : run.ErrorCode,
                    run.Retryable);
            }
            return VbaMutationOutcome.Ok(ExtractMacroOutput(run), data);
        }

        public VbaMutationOutcome InstallPersistent(
            VbaPackageInstallRequest request,
            CancellationToken cancellationToken)
        {
            var preparation = PreparePackage(request == null ? null : request.Source);
            if (!preparation.Success) return preparation.Error;
            if (request.DryRun)
            {
                return VbaMutationOutcome.Ok(
                    "Dry run: would install VBA package " + preparation.Package.Id,
                    PackageData(preparation.Package));
            }
            if (!SupportsPersistentVbaDocument())
            {
                return VbaMutationOutcome.Error(
                    "Persistent VBA installation requires a macro-enabled document (.xlsm/.xlam/.xlsb, .docm/.dotm, or .pptm/.ppam/.potm/.ppsm). Use normal tool execution for temporary session injection.",
                    null,
                    "vba_macro_enabled_document_required",
                    false);
            }
            var probe = Probe(preparation.Package);
            if (probe.State == VbaPackageInstallationState.Unavailable)
            {
                return VbaMutationOutcome.Error(
                    "VBA package state or lifecycle history could not be read. Installation was blocked.",
                    probe.Data,
                    "vba_package_probe_failed",
                    true);
            }
            if (probe.State == VbaPackageInstallationState.SessionOwned ||
                probe.Data != null && probe.Data.Value<bool?>("durableLifecycleIncomplete") == true)
            {
                return VbaMutationOutcome.Error(
                    "A previous temporary VBA lifecycle is incomplete. Run explicit Uninstall cleanup before persistent installation.",
                    probe.Data,
                    "vba_session_cleanup_required",
                    false);
            }
            return InstallPackage(
                preparation.Package,
                false,
                null,
                request.Correlation,
                cancellationToken);
        }

        public VbaMutationOutcome RemoveOwned(
            VbaPackageRemoveRequest request,
            CancellationToken cancellationToken)
        {
            var preparation = PreparePackage(request == null ? null : request.Source);
            if (!preparation.Success) return preparation.Error;
            var package = preparation.Package;
            var probe = Probe(package);
            if (probe.State == VbaPackageInstallationState.Unavailable)
            {
                return VbaMutationOutcome.Error(
                    "VBA package state could not be read. Removal was blocked.",
                    probe.Data,
                    "vba_package_probe_failed",
                    true);
            }
            if (probe.State == VbaPackageInstallationState.NotInstalled)
            {
                return VbaMutationOutcome.Ok(
                    "VBA package is not installed.",
                    PackageData(package));
            }
            if (probe.State == VbaPackageInstallationState.DocumentLocal)
            {
                return VbaMutationOutcome.Error(
                    "Matching document-local VBA components are not owned by RNAssistant and were preserved.",
                    probe.Data,
                    "vba_component_not_owned",
                    false);
            }
            if (probe.State == VbaPackageInstallationState.Persistent)
            {
                return RemovePackage(
                    package,
                    false,
                    null,
                    probe.OwnershipMarker,
                    request.Correlation,
                    cancellationToken);
            }
            if ((probe.State == VbaPackageInstallationState.SessionOwned ||
                 probe.State == VbaPackageInstallationState.RecoveryRequired) &&
                probe.CanCleanupSession)
            {
                return RemovePackage(
                    package,
                    true,
                    probe.LifecycleId,
                    probe.OwnershipMarker,
                    request.Correlation,
                    cancellationToken);
            }
            return VbaMutationOutcome.Error(
                "VBA package ownership or component state is mixed. Explicit reinstall/review is required; no component was removed.",
                probe.Data,
                "vba_package_drift",
                false);
        }

        public string GetInstallationStatus(VbaPackageSourceDefinition source)
        {
            var preparation = PreparePackage(source);
            return preparation.Success ? StatusText(Probe(preparation.Package).State) : "invalid";
        }

        public VbaMutationOutcome ReconcilePendingMutations()
        {
            try
            {
                foreach (var record in _journal.ListOpenPackageMutations(
                    _document.HostName,
                    _document.DocumentKey))
                {
                    if (record == null || record.Prepared == null || !IsPackageOperation(record.Prepared.Operation)) continue;
                    var assessment = InspectPackageMutation(record.Prepared);
                    _journal.CompletePackageMutation(
                        _document.HostName,
                        _document.DocumentKey,
                        record.Prepared.MutationId,
                        assessment.Status,
                        assessment.Components,
                        assessment.ErrorCode,
                        "Recovered on the next safe VBA access. " + assessment.Message);
                }
                return null;
            }
            catch (Exception ex)
            {
                return VbaMutationOutcome.Error(
                    "VBA package history could not be validated; the operation was blocked. " + ex.Message,
                    null,
                    "vba_journal_unavailable",
                    false);
            }
        }

        private static VbaMutationOutcome ExecutionProbeError(VbaPackageProbeResult probe)
        {
            if (probe.State == VbaPackageInstallationState.Unavailable)
            {
                return VbaMutationOutcome.Error(
                    "VBA package state could not be read. Execution was blocked.",
                    probe.Data,
                    "vba_package_probe_failed",
                    true);
            }
            if (probe.State == VbaPackageInstallationState.SessionOwned ||
                probe.State == VbaPackageInstallationState.RecoveryRequired)
            {
                return VbaMutationOutcome.Error(
                    "A session-owned or ambiguously owned VBA package remains in the document. Macro execution is blocked until an explicit Tools > Uninstall cleanup succeeds.",
                    probe.Data,
                    "vba_session_cleanup_required",
                    false);
            }
            if (probe.State == VbaPackageInstallationState.ModifiedLocal ||
                probe.State == VbaPackageInstallationState.Partial)
            {
                return VbaMutationOutcome.Error(
                    "VBA package components collide with modified or partial document code. Review and explicitly reinstall the package.",
                    probe.Data,
                    "vba_package_drift",
                    false);
            }
            return null;
        }

        private static VbaMutationOutcome RunProbeError(
            VbaPackageProbeResult probe,
            bool sessionInstalled,
            string lifecycleId)
        {
            if (sessionInstalled)
            {
                if (probe.State == VbaPackageInstallationState.SessionOwned &&
                    probe.CanCleanupSession &&
                    string.Equals(probe.LifecycleId, lifecycleId, StringComparison.Ordinal))
                {
                    return null;
                }
                return VbaMutationOutcome.Error(
                    "Temporary VBA package state changed before macro dispatch. Execution was blocked and cleanup will be attempted.",
                    probe.Data,
                    "vba_package_state_changed",
                    false);
            }
            if (probe.State == VbaPackageInstallationState.DocumentLocal ||
                probe.State == VbaPackageInstallationState.Persistent)
            {
                return null;
            }
            var blocked = ExecutionProbeError(probe);
            return blocked ?? VbaMutationOutcome.Error(
                "VBA package state changed before macro dispatch. Execution was blocked.",
                probe.Data,
                "vba_package_state_changed",
                false);
        }

        private bool SupportsPersistentVbaDocument()
        {
            var extension = Path.GetExtension(_document.DocumentTitle ?? string.Empty);
            if (string.Equals(_document.HostName, "Excel", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(extension, ".xlsm", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".xlam", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".xlsb", StringComparison.OrdinalIgnoreCase);
            }
            if (string.Equals(_document.HostName, "Word", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(extension, ".docm", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".dotm", StringComparison.OrdinalIgnoreCase);
            }
            if (string.Equals(_document.HostName, "PowerPoint", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(extension, ".pptm", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".ppam", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".potm", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".ppsm", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        private static string PersistentMarker(VbaPackageDefinition package)
        {
            return "RNAssistantPackage: id=" + package.Id + "; version=" + package.Version +
                "; hash=" + PackageHash(package) + ";";
        }

        private static string SessionMarker(VbaPackageDefinition package, string lifecycleId)
        {
            return "RNAssistantSession: id=" + package.Id + "; version=" + package.Version +
                "; hash=" + PackageHash(package) +
                (string.IsNullOrWhiteSpace(lifecycleId) ? string.Empty : "; lifecycle=" + lifecycleId) + ";";
        }

        private static JObject PackageData(VbaPackageDefinition package)
        {
            return new JObject
            {
                ["id"] = package.Id,
                ["version"] = package.Version,
                ["entryPoint"] = package.EntryPoint,
                ["components"] = new JArray(package.Components.Select(component => new JObject
                {
                    ["name"] = component.Name,
                    ["type"] = component.Type,
                    ["codeSha256"] = component.CodeSha256
                }))
            };
        }

        private static JObject ExecutionData(
            VbaMutationActionResult run,
            VbaMutationOutcome install,
            VbaMutationOutcome cleanup,
            bool sessionInstalled,
            string lifecycleId)
        {
            return new JObject
            {
                ["protocolVersion"] = 1,
                ["output"] = ExtractMacroOutput(run),
                ["sessionInstalled"] = sessionInstalled,
                ["sessionLifecycleId"] = string.IsNullOrWhiteSpace(lifecycleId) ? null : lifecycleId,
                ["install"] = OutcomeData(install),
                ["cleanup"] = OutcomeData(cleanup)
            };
        }

        private static JObject OutcomeData(VbaMutationOutcome outcome)
        {
            if (outcome == null) return null;
            return new JObject
            {
                ["status"] = outcome.Status == VbaMutationOutcomeStatus.Ok
                    ? "ok"
                    : outcome.Status == VbaMutationOutcomeStatus.Unknown ? "unknown" : "error",
                ["message"] = outcome.Message,
                ["errorCode"] = outcome.ErrorCode,
                ["data"] = outcome.Data
            };
        }

        private static string ExtractMacroOutput(VbaMutationActionResult result)
        {
            if (result == null) return string.Empty;
            var data = result.Data;
            var output = data == null ? null : data["output"];
            return output == null || output.Type == JTokenType.Null
                ? result.Message ?? string.Empty
                : Convert.ToString(((JValue)output).Value);
        }

    }
}
