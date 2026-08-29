namespace RNAssistant.Office.Vba
{
    internal sealed class VbaDeleteModuleGuardRequest
    {
        public string RequestedModuleName { get; set; }
        public VbaMutationCorrelation Correlation { get; set; }
    }

    internal sealed class VbaDeleteModuleGuardPreparation
    {
        public VbaMutationGuard Guard { get; set; }
        public string ResolvedModuleName { get; set; }
        public VbaMutationOutcome Error { get; set; }
        public bool Success { get { return Error == null && Guard != null; } }
    }

    internal sealed class VbaDeleteModuleRequest
    {
        public string ModuleName { get; set; }
        public bool DryRun { get; set; }
        public VbaMutationGuard Guard { get; set; }
        public VbaMutationCorrelation Correlation { get; set; }
    }

    internal sealed class VbaModuleDeleteRequest
    {
        public string ModuleName { get; set; }
        public string ExpectedCodeSha256 { get; set; }
    }
}
