using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
namespace RNAssistant.Runtime
{
    public sealed class WorkspaceWebVerifierFactory : IWorkspaceWebVerifierFactory
    {
        public bool IsAvailable { get { return WorkspaceWebVerifier.FindBrowserExecutable() != null; } }
        public IWorkspaceWebVerifier Create(ResourceProviderRouter<WorkspaceFileResourceProvider> resources,
            WorkspaceWebSnapshotStore snapshots, WorkspaceDescriptor workspace)
        { return new WorkspaceWebVerifier(resources, snapshots, workspace); }
    }
}
