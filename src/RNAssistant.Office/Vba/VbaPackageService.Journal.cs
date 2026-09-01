using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.Vba
{
    internal sealed partial class VbaPackageService
    {
        private VbaMutationOutcome InstallPackage(
            VbaPackageDefinition package,
            bool sessionOnly,
            string lifecycleId,
            VbaMutationCorrelation correlation,
            Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            var marker = sessionOnly
                ? SessionMarker(package, lifecycleId)
                : PersistentMarker(package);
            var prepared = PrepareJournaledPackageMutation(
                package,
                "package_install",
                sessionOnly,
                true,
                lifecycleId,
                marker,
                correlation);
            if (!prepared.Success) return prepared.Error;
            return ExecuteJournaledPackageMutation(
                prepared.Preparation,
                () => _backend.InstallPackage(new VbaPackageInstallActionRequest
                {
                    Components = package.Components,
                    ExpectedBefore = prepared.Preparation.Components.Select(component =>
                        new VbaPackageExpectedComponentState
                        {
                            Name = component.ModuleName,
                            Exists = component.BeforeExists,
                            ComponentType = component.BeforeComponentType,
                            ComparableCodeSha256 = component.BeforeComparableCodeSha256,
                            OwnershipMarkerPresent = component.BeforeOwnershipMarkerPresent == true,
                            OwnershipMarker = component.BeforeOwnershipMarker
                        }).ToList(),
                    Marker = marker
                }),
                markDispatchPossible,
                cancellationToken);
        }

        private VbaMutationOutcome RemovePackage(
            VbaPackageDefinition package,
            bool sessionOnly,
            string lifecycleId,
            string expectedMarker,
            VbaMutationCorrelation correlation,
            Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(expectedMarker))
            {
                return VbaMutationOutcome.Error(
                    "VBA package removal requires an exact ownership marker.",
                    null,
                    "vba_component_not_owned",
                    false);
            }
            var prepared = PrepareJournaledPackageMutation(
                package,
                "package_remove",
                sessionOnly,
                false,
                lifecycleId,
                expectedMarker,
                correlation);
            if (!prepared.Success) return prepared.Error;
            var expected = package.Components.ToDictionary(
                component => component.Name,
                component => VbaTextCanonicalizer.PackageComparableCodeSha256(component.Code),
                StringComparer.OrdinalIgnoreCase);
            return ExecuteJournaledPackageMutation(
                prepared.Preparation,
                () => _backend.RemovePackage(new VbaPackageRemoveActionRequest
                {
                    ExpectedComparableHashes = expected,
                    ExpectedMarker = expectedMarker
                }),
                markDispatchPossible,
                cancellationToken);
        }

        private PackagePreparationResult PrepareJournaledPackageMutation(
            VbaPackageDefinition package,
            string operation,
            bool sessionOnly,
            bool intendedAfterExists,
            string lifecycleId,
            string ownershipMarker,
            VbaMutationCorrelation correlation)
        {
            var components = new List<VbaPackageMutationComponent>();
            foreach (var component in package.Components)
            {
                var before = ReadPackageComponent(component.Name);
                var beforeExists = before != null && before.Success;
                if (!beforeExists && (before == null || !before.IsNotFound))
                {
                    return PackagePreparationResult.Failure(VbaMutationOutcome.Error(
                        "VBA package mutation was blocked because component state could not be read: " + component.Name + ".",
                        before == null ? null : before.Data,
                        "vba_package_probe_failed",
                        false));
                }
                if (sessionOnly && intendedAfterExists && beforeExists)
                {
                    return PackagePreparationResult.Failure(VbaMutationOutcome.Error(
                        "VBA package state changed after the session probe; temporary installation was blocked before journal/dispatch.",
                        null,
                        "vba_package_state_changed",
                        false));
                }
                if (beforeExists &&
                    (string.Equals(before.Module.ComponentType, "MSForm", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(component.Type, "MSForm", StringComparison.OrdinalIgnoreCase)) &&
                    (!string.Equals(before.Module.ComponentType, component.Type, StringComparison.OrdinalIgnoreCase) ||
                     before.Module.CodeOnlyUserForm != true))
                {
                    return PackagePreparationResult.Failure(VbaMutationOutcome.Error(
                        "VBA package cannot replace or remove UserForm state unless the existing component is a blank code-only MSForm: " + component.Name + ".",
                        null,
                        "vba_userform_designer_unsupported",
                        false));
                }
                var beforeMarker = beforeExists ? VbaPackageOwnershipMarker.Parse(before.Module.Code) : null;
                components.Add(new VbaPackageMutationComponent
                {
                    ModuleName = component.Name,
                    BeforeExists = beforeExists,
                    BeforeComponentType = beforeExists ? before.Module.ComponentType : null,
                    BeforeCode = beforeExists ? before.Module.Code : null,
                    BeforeOwnershipMarkerPresent = beforeExists
                        ? (bool?)beforeMarker.Found
                        : false,
                    BeforeOwnershipMarker = beforeExists
                        ? VbaPackageOwnershipMarker.Evidence(before.Module.Code)
                        : null,
                    IntendedAfterExists = intendedAfterExists,
                    IntendedAfterComponentType = intendedAfterExists ? component.Type : null,
                    IntendedAfterCode = intendedAfterExists ? component.Code : null
                });
            }

            try
            {
                correlation = correlation ?? new VbaMutationCorrelation();
                return PackagePreparationResult.Prepared(_journal.PreparePackageMutation(
                    new VbaPackageMutationPreparation
                    {
                        Operation = operation,
                        PackageId = package.Id ?? string.Empty,
                        PackageVersion = package.Version ?? string.Empty,
                        SessionOnly = sessionOnly,
                        RetainBackups = !sessionOnly,
                        LifecycleId = sessionOnly ? lifecycleId : null,
                        OwnershipMarker = ownershipMarker,
                        Host = _document.HostName ?? string.Empty,
                        DocumentKey = _document.DocumentKey ?? string.Empty,
                        RuntimeDocumentKey = _document.RuntimeDocumentKey ?? string.Empty,
                        DocumentTitle = _document.DocumentTitle ?? string.Empty,
                        Components = components,
                        SessionId = correlation.SessionId ?? string.Empty,
                        RunId = correlation.RunId,
                        TurnId = correlation.TurnId,
                        StepId = correlation.StepId,
                        ToolCallId = correlation.ToolCallId
                    }));
            }
            catch (Exception ex)
            {
                return PackagePreparationResult.Failure(VbaMutationOutcome.Error(
                    "VBA package " + operation + " was blocked because its prepared journal record could not be saved. " + ex.Message,
                    null,
                    "vba_package_journal_prepare_failed",
                    false));
            }
        }

        private VbaMutationOutcome ExecuteJournaledPackageMutation(
            VbaPackageMutationPreparation prepared,
            Func<VbaMutationActionResult> action,
            Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            TracePackageMutation(prepared, SessionEventKind.DomainEffectPrepared, null);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                CompletePackageCancellationBeforeDispatch(prepared);
                throw;
            }

            VbaMutationActionResult actionResult;
            try
            {
                if (markDispatchPossible != null) markDispatchPossible();
                TracePackageMutation(prepared, SessionEventKind.DomainEffectDispatched, null);
                actionResult = action == null ? null : action();
            }
            catch (OperationCanceledException ex)
            {
                actionResult = VbaMutationActionResult.Unknown(
                    "VBA package mutation was cancelled after dispatch. " + ex.Message,
                    null,
                    "vba_package_cancelled_after_dispatch");
            }
            catch (Exception ex)
            {
                actionResult = VbaMutationActionResult.Error(
                    "VBA package mutation threw after its prepared record was persisted. " + ex.Message,
                    null,
                    "vba_package_mutation_exception",
                    false);
            }
            if (actionResult == null)
            {
                actionResult = VbaMutationActionResult.Error(
                    "VBA package mutation returned no result.",
                    null,
                    "vba_package_mutation_missing_result",
                    false);
            }

            var assessment = InspectPackageMutation(prepared);
            TracePackageMutation(prepared, SessionEventKind.DomainEffectVerified, assessment.Status);
            try
            {
                _journal.CompletePackageMutation(
                    prepared.Host,
                    prepared.DocumentKey,
                    prepared.MutationId,
                    assessment.Status,
                    assessment.Components,
                    actionResult.ErrorCode ?? assessment.ErrorCode,
                    assessment.Message);
            }
            catch (Exception ex)
            {
                var terminalData = PackageJournalData(actionResult.Data, prepared, assessment);
                terminalData["terminalRecorded"] = false;
                return VbaMutationOutcome.Unknown(
                    "The VBA package effect was inspected, but its terminal journal record could not be saved. Inspect package ownership before retrying. " + ex.Message,
                    terminalData,
                    "vba_package_journal_terminal_failed");
            }

            var data = PackageJournalData(actionResult.Data, prepared, assessment);
            if (string.Equals(assessment.Status, VbaMutationStatuses.Committed, StringComparison.Ordinal))
            {
                if (actionResult.Status == VbaMutationActionStatus.Error ||
                    actionResult.Status == VbaMutationActionStatus.Unknown)
                {
                    data["backendReportedError"] = true;
                    data["backendErrorCode"] = actionResult.ErrorCode;
                }
                return VbaMutationOutcome.Ok(
                    actionResult.Message +
                    (actionResult.Status == VbaMutationActionStatus.Succeeded ||
                     actionResult.Status == VbaMutationActionStatus.Verified
                        ? string.Empty
                        : " Live components match the intended result and terminal evidence was recorded."),
                    data);
            }
            if (string.Equals(assessment.Status, VbaMutationStatuses.Unknown, StringComparison.Ordinal))
            {
                return VbaMutationOutcome.Unknown(
                    actionResult.Message + " Final package component or ownership state is mixed or unknown; inspect it before retrying.",
                    data,
                    "vba_package_mutation_unknown");
            }
            return VbaMutationOutcome.Error(
                actionResult.Message,
                data,
                string.IsNullOrWhiteSpace(actionResult.ErrorCode)
                    ? "vba_package_mutation_not_applied"
                    : actionResult.ErrorCode,
                actionResult.Retryable);
        }

        private void CompletePackageCancellationBeforeDispatch(VbaPackageMutationPreparation prepared)
        {
            var assessment = InspectPackageMutation(prepared);
            TracePackageMutation(prepared, SessionEventKind.DomainEffectVerified, assessment.Status);
            try
            {
                _journal.CompletePackageMutation(
                    prepared.Host,
                    prepared.DocumentKey,
                    prepared.MutationId,
                    assessment.Status,
                    assessment.Components,
                    "vba_package_cancelled_before_dispatch",
                    "Cancellation was observed after preparation and before dispatch. " + assessment.Message);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "VBA package mutation was cancelled before dispatch, but its terminal journal record could not be saved.",
                    ex);
            }
        }

        private PackageMutationAssessment InspectPackageMutation(VbaPackageMutationPreparation prepared)
        {
            var components = new List<VbaPackageMutationComponentAssessment>();
            foreach (var expected in prepared.Components ?? new List<VbaPackageMutationComponent>())
            {
                components.Add(InspectPackageComponent(expected, prepared));
            }
            var allIntended = components.Count > 0 && components.All(item => item.MatchesIntendedAfter);
            var allBefore = components.Count > 0 && components.All(item => item.MatchesBefore);
            var failed = components.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.ErrorCode));
            return new PackageMutationAssessment
            {
                Status = allIntended
                    ? VbaMutationStatuses.Committed
                    : allBefore ? VbaMutationStatuses.NotApplied : VbaMutationStatuses.Unknown,
                Components = components,
                ErrorCode = failed == null ? null : failed.ErrorCode,
                Message = allIntended
                    ? "Every live package component and required ownership marker match the recorded intended state."
                    : allBefore
                        ? "Every live package component matches the recorded before state."
                        : "Package components do not collectively match either the complete before or intended state."
            };
        }

        private VbaPackageMutationComponentAssessment InspectPackageComponent(
            VbaPackageMutationComponent expected,
            VbaPackageMutationPreparation prepared)
        {
            var read = ReadPackageComponent(expected.ModuleName);
            if (read != null && read.Success)
            {
                var actual = read.Module;
                var hash = VbaTextCanonicalizer.PackageCodeSha256(actual.Code);
                var comparableHash = VbaTextCanonicalizer.PackageComparableCodeSha256(actual.Code);
                var marker = VbaPackageOwnershipMarker.Parse(actual.Code);
                var beforeMarkerMatches = BeforeMarkerMatches(expected, marker);
                var intendedMarkerMatches = RequiredIntendedMarkerMatches(
                    expected,
                    prepared,
                    marker);
                var matchesBefore = expected.BeforeExists && beforeMarkerMatches &&
                    VbaVerifier.MatchesRecordedState(
                        hash,
                        comparableHash,
                        expected.BeforeCodeSha256,
                        expected.BeforeComparableCodeSha256) &&
                    string.Equals(actual.ComponentType, expected.BeforeComponentType, StringComparison.OrdinalIgnoreCase) &&
                    (!string.Equals(expected.BeforeComponentType, "MSForm", StringComparison.OrdinalIgnoreCase) || actual.CodeOnlyUserForm == true);
                var matchesIntended = expected.IntendedAfterExists && intendedMarkerMatches &&
                    VbaVerifier.MatchesRecordedState(
                        hash,
                        comparableHash,
                        expected.IntendedAfterCodeSha256,
                        expected.IntendedAfterComparableCodeSha256) &&
                    string.Equals(actual.ComponentType, expected.IntendedAfterComponentType, StringComparison.OrdinalIgnoreCase) &&
                    (!string.Equals(expected.IntendedAfterComponentType, "MSForm", StringComparison.OrdinalIgnoreCase) || actual.CodeOnlyUserForm == true);
                return new VbaPackageMutationComponentAssessment
                {
                    ModuleName = expected.ModuleName,
                    ActualExists = true,
                    ActualComponentType = actual.ComponentType,
                    ActualCodeSha256 = hash,
                    ActualComparableCodeSha256 = comparableHash,
                    MatchesBefore = matchesBefore,
                    MatchesIntendedAfter = matchesIntended,
                    ErrorCode = matchesBefore || matchesIntended ? null : "vba_package_component_diverged",
                    Message = matchesIntended
                        ? "Live component matches intended source, type, and ownership."
                        : matchesBefore
                            ? "Live component matches before source, type, and ownership."
                            : "Live component matches neither recorded state."
                };
            }
            if (read != null && read.IsNotFound)
            {
                return new VbaPackageMutationComponentAssessment
                {
                    ModuleName = expected.ModuleName,
                    ActualExists = false,
                    MatchesBefore = !expected.BeforeExists,
                    MatchesIntendedAfter = !expected.IntendedAfterExists,
                    ErrorCode = expected.BeforeExists && expected.IntendedAfterExists
                        ? "vba_package_component_diverged"
                        : null,
                    Message = !expected.IntendedAfterExists
                        ? "Live component absence matches intended state."
                        : !expected.BeforeExists
                            ? "Live component absence matches before state."
                            : "Live component is unexpectedly absent."
                };
            }
            return new VbaPackageMutationComponentAssessment
            {
                ModuleName = expected.ModuleName,
                ActualExists = null,
                ErrorCode = read == null ? "vba_package_component_read_failed" : read.ErrorCode,
                Message = "Live component could not be inspected. " + (read == null ? string.Empty : read.Message)
            };
        }

        private static bool BeforeMarkerMatches(
            VbaPackageMutationComponent expected,
            VbaPackageOwnershipMarker marker)
        {
            if (expected.BeforeOwnershipMarkerPresent == false) return marker != null && !marker.Found;
            if (expected.BeforeOwnershipMarkerPresent == true)
            {
                return marker != null && marker.Found && string.Equals(
                    marker.Raw,
                    expected.BeforeOwnershipMarker,
                    StringComparison.OrdinalIgnoreCase);
            }
            return string.IsNullOrWhiteSpace(expected.BeforeOwnershipMarker) ||
                marker != null && marker.Found && string.Equals(
                    marker.Raw,
                    expected.BeforeOwnershipMarker,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static JObject PackageJournalData(
            JObject actionData,
            VbaPackageMutationPreparation prepared,
            PackageMutationAssessment assessment)
        {
            var data = actionData == null ? new JObject() : (JObject)actionData.DeepClone();
            data.Remove("journalStatus");
            data.Remove("packageJournalStatus");
            data.Remove("terminalRecorded");
            data.Remove("componentAssessments");
            data["packageJournaled"] = true;
            data["packageMutationId"] = prepared == null ? null : prepared.MutationId;
            data["packageLifecycleId"] = prepared == null ? null : prepared.LifecycleId;
            data["componentAssessments"] = assessment == null
                ? new JArray()
                : JArray.FromObject(assessment.Components ?? new List<VbaPackageMutationComponentAssessment>());
            return data;
        }

        private static bool RequiredIntendedMarkerMatches(
            VbaPackageMutationComponent expected,
            VbaPackageMutationPreparation prepared,
            VbaPackageOwnershipMarker marker)
        {
            if (!expected.IntendedAfterExists) return true;
            if (!string.IsNullOrWhiteSpace(prepared.OwnershipMarker))
            {
                return marker.Valid && string.Equals(
                    marker.Raw,
                    prepared.OwnershipMarker,
                    StringComparison.OrdinalIgnoreCase);
            }
            if (!string.Equals(prepared.Operation, "package_install", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            var expectedHash = TextPatternEngine.Sha256(string.Join(
                "\n",
                (prepared.Components ?? new List<VbaPackageMutationComponent>())
                    .OrderBy(component => component.ModuleName, StringComparer.OrdinalIgnoreCase)
                    .Select(component => component.ModuleName + ":" + component.IntendedAfterCodeSha256)
                    .ToArray()));
            return marker.Valid &&
                string.Equals(marker.Kind, prepared.SessionOnly ? "session" : "persistent", StringComparison.Ordinal) &&
                string.Equals(marker.PackageId, prepared.PackageId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(marker.PackageVersion, prepared.PackageVersion, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(marker.PackageHash, expectedHash, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPackageOperation(string operation)
        {
            return string.Equals(operation, "package_install", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(operation, "package_remove", StringComparison.OrdinalIgnoreCase);
        }

        private static void TracePackageMutation(
            VbaPackageMutationPreparation prepared,
            SessionEventKind kind,
            string status)
        {
            if (prepared == null) return;
            RunCausalTrace.Record(new CausalTraceRecord(kind)
            {
                StepId = prepared.StepId,
                ToolCallId = prepared.ToolCallId,
                DocumentRuntimeId = prepared.RuntimeDocumentKey,
                MutationId = prepared.MutationId,
                JournalRunId = prepared.RunId,
                Status = status,
                Boundary = "vba_package_mutation"
            });
        }

        private sealed class PackagePreparationResult
        {
            public VbaPackageMutationPreparation Preparation { get; private set; }
            public VbaMutationOutcome Error { get; private set; }
            public bool Success { get { return Preparation != null && Error == null; } }

            public static PackagePreparationResult Prepared(VbaPackageMutationPreparation preparation)
            {
                return new PackagePreparationResult { Preparation = preparation };
            }

            public static PackagePreparationResult Failure(VbaMutationOutcome error)
            {
                return new PackagePreparationResult { Error = error };
            }
        }

        private sealed class PackageMutationAssessment
        {
            public string Status { get; set; }
            public List<VbaPackageMutationComponentAssessment> Components { get; set; }
            public string ErrorCode { get; set; }
            public string Message { get; set; }
        }
    }
}
