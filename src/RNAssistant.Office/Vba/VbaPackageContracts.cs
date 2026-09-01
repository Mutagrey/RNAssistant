using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Vba
{
    internal enum VbaPackageInstallationState
    {
        NotInstalled,
        DocumentLocal,
        Persistent,
        SessionOwned,
        Partial,
        ModifiedLocal,
        RecoveryRequired,
        Unavailable
    }

    internal enum VbaPackageEffectEvidence
    {
        None,
        VerifiedNoChange,
        VerifiedChange,
        Unknown
    }

    internal sealed class VbaPackageResult
    {
        internal const int CurrentContractVersion = 1;

        private readonly JObject _data;

        internal int ContractVersion { get { return CurrentContractVersion; } }
        internal string SourceRevision { get; private set; }
        internal VbaMutationOutcomeStatus Status { get; private set; }
        internal bool Success
        {
            get { return Status == VbaMutationOutcomeStatus.Ok; }
        }
        internal string Message { get; private set; }
        internal string ErrorCode { get; private set; }
        internal bool? Retryable { get; private set; }
        internal bool MayHaveDispatched { get; private set; }
        internal VbaPackageEffectEvidence Effect { get; private set; }
        internal JObject Data
        {
            get { return _data == null ? null : (JObject)_data.DeepClone(); }
        }
        internal string DataJson
        {
            get
            {
                var data = DataWithContract();
                return data == null || !data.HasValues
                    ? null : data.ToString(Formatting.None);
            }
        }

        private VbaPackageResult(ToolPackageSource source,
            VbaMutationOutcomeStatus status, string message, JObject data,
            string errorCode, bool? retryable, bool dispatched,
            VbaPackageEffectEvidence effect)
        {
            SourceRevision = source == null ? string.Empty : source.Revision;
            Status = status;
            Message = message ?? string.Empty;
            ErrorCode = errorCode;
            Retryable = status == VbaMutationOutcomeStatus.Unknown
                ? false : retryable;
            MayHaveDispatched = dispatched;
            Effect = effect;
            _data = data == null ? null : (JObject)data.DeepClone();
        }

        internal static VbaPackageResult Lifecycle(
            ToolPackageSource source, VbaMutationOutcome outcome,
            bool dispatched)
        {
            if (outcome == null)
                return Error(source,
                    "VBA package mutation returned no typed outcome.",
                    "vba_package_missing_outcome", false, dispatched);
            var effect = outcome.Status == VbaMutationOutcomeStatus.Unknown
                ? VbaPackageEffectEvidence.Unknown
                : dispatched && outcome.Status == VbaMutationOutcomeStatus.Ok
                    ? VbaPackageEffectEvidence.VerifiedChange
                    : outcome.Status == VbaMutationOutcomeStatus.Ok || dispatched
                        ? VbaPackageEffectEvidence.VerifiedNoChange
                        : VbaPackageEffectEvidence.None;
            return new VbaPackageResult(source, outcome.Status,
                outcome.Message, outcome.Data, outcome.ErrorCode,
                outcome.Retryable, dispatched, effect);
        }

        internal static VbaPackageResult Execution(
            ToolPackageSource source, VbaMutationOutcome outcome,
            bool dispatched)
        {
            if (outcome == null)
                return Error(source,
                    "VBA package execution returned no typed outcome.",
                    "vba_package_missing_outcome", false, dispatched);
            var status = dispatched
                ? VbaMutationOutcomeStatus.Unknown : outcome.Status;
            var code = dispatched && string.IsNullOrWhiteSpace(outcome.ErrorCode)
                ? "vba_package_effect_unknown" : outcome.ErrorCode;
            return new VbaPackageResult(source, status, outcome.Message,
                outcome.Data, code, outcome.Retryable, dispatched,
                dispatched || outcome.Status == VbaMutationOutcomeStatus.Unknown
                    ? VbaPackageEffectEvidence.Unknown
                    : outcome.Status == VbaMutationOutcomeStatus.Ok
                        ? VbaPackageEffectEvidence.VerifiedNoChange
                        : VbaPackageEffectEvidence.None);
        }

        internal static VbaPackageResult Error(ToolPackageSource source,
            string message, string code, bool? retryable,
            bool dispatched = false)
        {
            return new VbaPackageResult(source,
                dispatched ? VbaMutationOutcomeStatus.Unknown
                    : VbaMutationOutcomeStatus.Error,
                message, null, code, retryable, dispatched,
                dispatched ? VbaPackageEffectEvidence.Unknown
                    : VbaPackageEffectEvidence.None);
        }

        internal JObject DataWithContract()
        {
            var data = _data == null ? new JObject() : (JObject)_data.DeepClone();
            data["packageContractVersion"] = CurrentContractVersion;
            data["packageSourceRevision"] = SourceRevision;
            data["effect"] = EffectText(Effect);
            if (!string.IsNullOrWhiteSpace(ErrorCode)) data["code"] = ErrorCode;
            if (Retryable.HasValue) data["retryable"] = Retryable.Value;
            return data;
        }

        internal static string StatusText(VbaMutationOutcomeStatus status)
        {
            return status == VbaMutationOutcomeStatus.Ok ? "ok" :
                status == VbaMutationOutcomeStatus.Unknown ? "unknown" : "error";
        }

        internal static string EffectText(VbaPackageEffectEvidence effect)
        {
            return effect == VbaPackageEffectEvidence.VerifiedChange
                ? "verified_change"
                : effect == VbaPackageEffectEvidence.VerifiedNoChange
                    ? "verified_no_change"
                    : effect == VbaPackageEffectEvidence.Unknown
                        ? "unknown" : "none";
        }
    }

    internal sealed class VbaPackageStatusResult
    {
        internal const int CurrentContractVersion = 1;

        internal int ContractVersion { get { return CurrentContractVersion; } }
        internal string SourceRevision { get; private set; }
        internal string Status { get; private set; }

        internal VbaPackageStatusResult(ToolPackageSource source,
            string status)
        {
            SourceRevision = source == null ? string.Empty : source.Revision;
            Status = string.IsNullOrWhiteSpace(status) ? "invalid" : status;
        }
    }

    internal sealed class VbaPackageDefinition
    {
        public string Id { get; set; }
        public string Host { get; set; }
        public string Version { get; set; }
        public string EntryPoint { get; set; }
        public JObject ArgumentSchema { get; set; }
        public IReadOnlyList<string> ArgumentOrder { get; set; }
        public IReadOnlyList<VbaPackageComponent> Components { get; set; }
        public string StoragePath { get; set; }
        public string Readme { get; set; }
    }

    internal sealed class VbaPackageComponent
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public string FileName { get; set; }
        public string Code { get; set; }
        public string CodeSha256 { get; set; }
    }

    internal sealed class VbaPackagePreparationResult
    {
        public VbaPackageDefinition Package { get; set; }
        public VbaMutationOutcome Error { get; set; }
        public bool Success { get { return Package != null && Error == null; } }
    }

    internal sealed class VbaPackageProbeResult
    {
        public VbaPackageInstallationState State { get; set; }
        public JObject Data { get; set; }
        public string OwnershipMarker { get; set; }
        public string LifecycleId { get; set; }
        public bool CanCleanupSession { get; set; }
    }

    internal sealed class VbaPackageExecutionRequest
    {
        public ToolPackageSource Source { get; set; }
        public JObject Arguments { get; set; }
        public bool DryRun { get; set; }
        public VbaMutationCorrelation Correlation { get; set; }
        public Action MarkDispatchPossible { get; set; }
    }

    internal sealed class VbaPackageInstallRequest
    {
        public ToolPackageSource Source { get; set; }
        public bool DryRun { get; set; }
        public VbaMutationCorrelation Correlation { get; set; }
        public Action MarkDispatchPossible { get; set; }
    }

    internal sealed class VbaPackageRemoveRequest
    {
        public ToolPackageSource Source { get; set; }
        public VbaMutationCorrelation Correlation { get; set; }
        public Action MarkDispatchPossible { get; set; }
    }

    internal sealed class VbaPackageInstallActionRequest
    {
        public IReadOnlyList<VbaPackageComponent> Components { get; set; }
        public IReadOnlyList<VbaPackageExpectedComponentState> ExpectedBefore { get; set; }
        public string Marker { get; set; }
    }

    internal sealed class VbaPackageExpectedComponentState
    {
        public string Name { get; set; }
        public bool Exists { get; set; }
        public string ComponentType { get; set; }
        public string ComparableCodeSha256 { get; set; }
        public bool OwnershipMarkerPresent { get; set; }
        public string OwnershipMarker { get; set; }
    }

    internal sealed class VbaPackageRemoveActionRequest
    {
        public IReadOnlyDictionary<string, string> ExpectedComparableHashes { get; set; }
        public string ExpectedMarker { get; set; }
    }

    internal sealed class VbaPackageRunActionRequest
    {
        public string MacroName { get; set; }
        public JArray Arguments { get; set; }
    }

    internal interface IVbaPackageBackend
    {
        VbaMutationActionResult InstallPackage(VbaPackageInstallActionRequest request);
        VbaMutationActionResult RemovePackage(VbaPackageRemoveActionRequest request);
        VbaMutationActionResult RunMacro(VbaPackageRunActionRequest request);
    }
}
