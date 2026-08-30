using System.Collections.Generic;
using Newtonsoft.Json.Linq;

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

    internal sealed class VbaPackageSourceDefinition
    {
        public string Id { get; set; }
        public string Host { get; set; }
        public string Code { get; set; }
        public string StoragePath { get; set; }
        public string Readme { get; set; }
        public IReadOnlyList<VbaPackageSourceComponent> Components { get; set; }
    }

    internal sealed class VbaPackageSourceComponent
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public string FileName { get; set; }
        public string Code { get; set; }
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
        public VbaPackageSourceDefinition Source { get; set; }
        public JObject Arguments { get; set; }
        public bool DryRun { get; set; }
        public VbaMutationCorrelation Correlation { get; set; }
    }

    internal sealed class VbaPackageInstallRequest
    {
        public VbaPackageSourceDefinition Source { get; set; }
        public bool DryRun { get; set; }
        public VbaMutationCorrelation Correlation { get; set; }
    }

    internal sealed class VbaPackageRemoveRequest
    {
        public VbaPackageSourceDefinition Source { get; set; }
        public VbaMutationCorrelation Correlation { get; set; }
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
