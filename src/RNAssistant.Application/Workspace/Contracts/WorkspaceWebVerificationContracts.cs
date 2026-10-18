using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Runtime.Contracts;

namespace RNAssistant.Runtime
{
    public enum WebVerificationStatus { Passed, Failed, NotRun }

    public sealed class WebVerificationResult
    {
        public WebVerificationStatus Status { get; internal set; }
        public string EntryPath { get; internal set; }
        public IReadOnlyList<string> CheckedFiles { get; internal set; }
        public IReadOnlyList<string> Errors { get; internal set; }
        public IReadOnlyList<string> Hints { get; internal set; }
        public string SnapshotSha256 { get; internal set; }
        public string Browser { get; internal set; }
        public IReadOnlyList<ResourceEvidence> Evidence { get; internal set; }
        public string SnapshotId { get; internal set; }
        public string VerificationId { get; internal set; }
        public ResourceRef VerificationReference { get; internal set; }
        public bool Historical { get; internal set; }
        public IReadOnlyList<WebCheckResult> CheckResults { get; internal set; }
        public IReadOnlyList<WebDiagnosticDescriptor> Diagnostics { get; internal set; }
    }

    public interface IWorkspaceWebVerifierFactory
    {
        bool IsAvailable { get; }
        IWorkspaceWebVerifier Create(ResourceProviderRouter<WorkspaceFileResourceProvider> resources,
            WorkspaceWebSnapshotStore snapshots, WorkspaceDescriptor workspace);
    }
    public interface IWorkspaceWebVerifier
    {
        Task<WebVerificationResult> VerifyAsync(string entryPath, CancellationToken cancellationToken = default(CancellationToken),
            string snapshotId = null, WebVerificationOrigin origin = null, WebFunctionalChecks checks = null);
        Task<WorkspacePreviewReport> PreviewAsync(WorkspacePreviewRequest request,
            Action<WorkspacePreviewReport> opened, CancellationToken cancellationToken = default(CancellationToken));
    }
    public static class WorkspaceWebVerificationStatus
    {
        public static string Code(WebVerificationStatus status)
        { return status == WebVerificationStatus.NotRun ? "not-run" : status.ToString().ToLowerInvariant(); }
    }
}
