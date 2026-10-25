using System.Collections.Generic;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
namespace RNAssistant.Runtime.Contracts
{
    public sealed class WorkspaceToolContribution
    {
        public ToolCatalogEntry Entry { get; set; }
        public IToolHandler Handler { get; set; }
    }

    // Optional host module. The CLI has no Office dependency and supplies none.
    public interface IWorkspaceOfficeCapabilities
    {
        WorkspaceOfficeRunTarget CaptureCurrentTarget();
        WorkspaceOfficeCapabilitySet ForTarget(WorkspaceOfficeRunTarget target,
            ChatSession workspaceSession, AppSettings settings, string runId);
    }

    public sealed class WorkspaceOfficeCapabilitySet
    {
        public WorkspaceOfficeRunTarget Target { get; set; }
        public bool Available { get; set; }
        public IReadOnlyList<WorkspaceToolContribution> Tools { get; set; }
        public IReadOnlyToolHandler ResourceFind { get; set; }
        public IReadOnlyToolHandler ResourceRead { get; set; }
        public IReadOnlyToolHandler ResourceSearch { get; set; }
        public IToolMutationObserver MutationObserver { get; set; }
        // Pure model projection for the bound host's tool results; no live host reads.
        public ModelResultProjection ModelProjection { get; set; }
    }
}
