namespace RNAssistant.Office.Vba
{
    internal sealed class VbaRenameGuardRequest
    {
        public string RequestedModuleName { get; set; }
        public string RequestedTargetModuleName { get; set; }
        public VbaMutationCorrelation Correlation { get; set; }
    }

    internal sealed class VbaRenameGuardPreparation
    {
        public VbaMutationGuard Guard { get; set; }
        public string ResolvedModuleName { get; set; }
        public string ResolvedTargetModuleName { get; set; }
        public VbaMutationOutcome Error { get; set; }
        public bool Success { get { return Error == null && Guard != null; } }
    }

    internal sealed class VbaRenameRequest
    {
        public string ModuleName { get; set; }
        public string NewModuleName { get; set; }
        public bool DryRun { get; set; }
        public VbaMutationGuard Guard { get; set; }
        public VbaMutationCorrelation Correlation { get; set; }
    }

    internal sealed class VbaRenameBackendRequest
    {
        public string ModuleName { get; set; }
        public string NewModuleName { get; set; }
        public string ExpectedCodeSha256 { get; set; }
        public string ExpectedComponentType { get; set; }
    }
}
