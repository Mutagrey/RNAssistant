namespace RNAssistant.Office.Vba
{
    internal enum VbaWholeModuleWriteMode
    {
        Unknown,
        Upsert,
        CreateOnly,
        UpdateOnly
    }

    internal sealed class VbaWholeModuleWriteGuardRequest
    {
        public string RequestedModuleName { get; set; }
        public VbaMutationCorrelation Correlation { get; set; }
    }

    internal sealed class VbaWholeModuleWriteGuardPreparation
    {
        public VbaMutationGuard Guard { get; set; }
        public string ResolvedModuleName { get; set; }
        public VbaMutationOutcome Error { get; set; }
        public bool Success { get { return Error == null && Guard != null; } }
    }

    internal sealed class VbaWholeModuleWriteRequest
    {
        public string ModuleName { get; set; }
        public string Code { get; set; }
        public string ComponentType { get; set; }
        public VbaWholeModuleWriteMode Mode { get; set; }
        public bool DryRun { get; set; }
        public VbaMutationGuard Guard { get; set; }
        public VbaMutationCorrelation Correlation { get; set; }
    }

    internal sealed class VbaModuleCreateRequest
    {
        public string ModuleName { get; set; }
        public string ComponentType { get; set; }
        public string Code { get; set; }
    }
}
