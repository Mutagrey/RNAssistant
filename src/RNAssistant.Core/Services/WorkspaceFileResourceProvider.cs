using System;
using System.Collections.Generic;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Core.Services
{
    // A workspace-bound filesystem provider. WorkspaceFileService remains the only
    // filesystem reader, writer, authority observer and mutation journal owner.
    public sealed class WorkspaceFileResourceProvider : IResourceProviderIdentity
    {
        private readonly WorkspaceFileService _files;
        private readonly WorkspaceDescriptor _workspace;

        public string Id { get { return "file"; } }

        public WorkspaceFileResourceProvider(WorkspaceFileService files, WorkspaceDescriptor workspace)
        {
            _files = files ?? throw new ArgumentNullException(nameof(files));
            _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        }

        public WorkspaceDirectoryPage Find(string directory, string query, int limit = 200)
        {
            return _files.ListPage(_workspace, directory, query, limit);
        }

        public WorkspaceFileObservation Read(string relativePath)
        {
            return _files.ReadText(_workspace, relativePath);
        }

        public ResourceAuthoritySnapshotSet CaptureAuthority(IEnumerable<ResourceEvidence> evidence)
        {
            return _files.CaptureAuthority(evidence);
        }
    }
}
