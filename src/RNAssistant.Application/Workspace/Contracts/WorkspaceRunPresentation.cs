using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Runtime.Contracts
{
    // Bounded, disposable presentation of accepted runtime facts. No file bodies,
    // model assertions, persistence or execution authority live in these DTOs.
    public sealed class WorkspaceFileActivity
    {
        public string ToolId { get; set; }
        public string Path { get; set; }
        public string TargetPath { get; set; }
        public bool PathsTruncated { get; set; }
        public IReadOnlyList<PatchTargetResult> Targets { get; set; }
        [JsonConverter(typeof(StringEnumConverter))]
        public ResourceEffectOutcome Effect { get; set; }
    }

    public sealed class WorkspaceVerificationSummary
    {
        public string VerificationId { get; set; }
        public string SnapshotId { get; set; }
        public string EntryPath { get; set; }
        public bool EntryPathTruncated { get; set; }
        [JsonConverter(typeof(StringEnumConverter))]
        public WebVerificationState? State { get; set; }
        public bool Unavailable { get; set; }
        public bool Historical { get; set; }
        public int CheckedFiles { get; set; }
        public int PassedChecks { get; set; }
        public int FailedChecks { get; set; }
        public int NotRunChecks { get; set; }
    }

    public sealed class WorkspaceToolActivity
    {
        [JsonConverter(typeof(StringEnumConverter))]
        public ToolExecutionOutcome Outcome { get; set; }
        public string Message { get; set; }
        public WorkspaceFileActivity File { get; set; }
        public WorkspaceVerificationSummary Verification { get; set; }
    }

    public sealed class WorkspaceRunPresentation
    {
        public IReadOnlyList<WorkspaceFileActivity> Files { get; set; } = new WorkspaceFileActivity[0];
        public bool FilesTruncated { get; set; }
        public WorkspaceVerificationSummary LatestVerification { get; set; }
    }
}
