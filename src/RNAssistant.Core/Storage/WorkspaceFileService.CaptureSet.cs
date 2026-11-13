using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    public sealed partial class WorkspaceFileService
    {
        public const int MaximumCaptureSetMembers = 512;
        public const long MaximumCaptureSetBytes = 256L * 1024 * 1024;
        internal const int MaximumDefinitionBytes = 1024 * 1024;

        // A bounded lifetime, not a second store. CAS/file authority remain owned
        // by CaptureOpened. The caller holds this through its aggregate commit.
        public sealed class CaptureSetLease : IDisposable
        {
            internal readonly List<IDisposable> Held = new List<IDisposable>();
            internal Action Check;
            internal bool Disposed;
            public IReadOnlyList<WorkspaceFileSnapshot> Files { get; internal set; }
            public string DefinitionText { get; internal set; }
            public void Validate()
            { if (Disposed) throw new ObjectDisposedException(nameof(CaptureSetLease)); Check(); }
            public void Dispose()
            {
                if (Disposed) return; Disposed = true;
                for (var index = Held.Count - 1; index >= 0; index--) Held[index].Dispose();
                Held.Clear();
            }
        }

        private sealed class CaptureMember
        {
            internal string Relative, Path, Id;
            internal ResourceAuthorityScopeId Scope;
            internal FileStream Stream;
            internal WorkspaceFileSnapshot Snapshot;
        }

        internal static void ValidateCapturePath(WorkspaceDescriptor workspace, string relativePath)
        {
            if (!File.Exists(ResolvePath(workspace, relativePath, false)))
                throw new WorkspaceFileException("project_member_missing", "A declared project member is missing: " + relativePath);
        }

        public CaptureSetLease CaptureSet(WorkspaceDescriptor workspace, IEnumerable<string> members,
            long maximumBytes = MaximumCaptureSetBytes, CancellationToken token = default(CancellationToken))
        { return CaptureSetCore(workspace, members, maximumBytes, token, new CaptureSetLease(), null, null); }

        internal CaptureSetLease CaptureSetFromDefinition(WorkspaceDescriptor workspace, string definitionPath,
            Func<string, IEnumerable<string>> members, CancellationToken token = default(CancellationToken))
        {
            var lease = new CaptureSetLease();
            try
            {
                var path = ResolvePath(workspace, definitionPath, false);
                var definition = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                lease.Held.Add(definition);
                if (definition.Length > MaximumDefinitionBytes)
                    throw new WorkspaceFileException("project_definition_too_large", "Project definition exceeds one MiB.");
                using (var reader = new StreamReader(definition, new UTF8Encoding(false, true), false, 4096, true))
                    lease.DefinitionText = reader.ReadToEnd();
                definition.Position = 0;
                return CaptureSetCore(workspace, members(lease.DefinitionText), MaximumCaptureSetBytes,
                    token, lease, definitionPath, definition);
            }
            catch { lease.Dispose(); throw; }
        }

        private CaptureSetLease CaptureSetCore(WorkspaceDescriptor workspace, IEnumerable<string> members,
            long maximumBytes, CancellationToken token, CaptureSetLease lease, string definitionPath, FileStream definition)
        {
            try
            {
                if (maximumBytes < 1 || maximumBytes > MaximumCaptureSetBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
                token.ThrowIfCancellationRequested();
                var paths = (members ?? Enumerable.Empty<string>()).Take(MaximumCaptureSetMembers + 1).ToArray();
                if (paths.Length < 1 || paths.Length > MaximumCaptureSetMembers ||
                    paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length ||
                    paths.Any(path => path == null || path.Contains("\\")) ||
                    definitionPath != null && !paths.Contains(definitionPath, StringComparer.Ordinal))
                    throw new WorkspaceFileException("project_members_invalid", "Provide 1–512 unique canonical relative members including the entry/definition.");
                var items = paths.OrderBy(path => path, StringComparer.Ordinal).Select(path => new CaptureMember {
                    Relative = path, Path = ResolvePath(workspace, path, false) }).ToArray();
                foreach (var item in items)
                {
                    token.ThrowIfCancellationRequested();
                    lease.Held.Add(AcquireFileScope(item.Path, out item.Id, out item.Scope, token));
                    EnsureNoUnresolved(item.Scope);
                }
                long total = 0;
                foreach (var item in items)
                {
                    ResolvePath(workspace, item.Relative, false);
                    item.Stream = item.Relative == definitionPath ? definition :
                        new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
                    if (item.Stream != definition) lease.Held.Add(item.Stream);
                    if (item.Stream.Length > maximumBytes - total)
                        throw new WorkspaceFileException("project_too_large", "Project capture exceeds its byte budget.");
                    total += item.Stream.Length;
                }
                FaultPoint?.Invoke("after-capture-set-open");
                foreach (var item in items)
                {
                    token.ThrowIfCancellationRequested(); item.Stream.Position = 0;
                    item.Snapshot = CaptureOpened(item.Relative, item.Stream, item.Id, item.Scope, maximumBytes, token);
                    if (item.Relative == definitionPath && item.Snapshot.ContentSha256 !=
                        RNAssistant.Core.Tools.TextPatternEngine.Sha256(lease.DefinitionText))
                        throw new WorkspaceFileException("project_definition_changed", "Pinned definition bytes changed or use unsupported encoding.");
                    FaultPoint?.Invoke("after-capture-set-member");
                }
                lease.Files = Array.AsReadOnly(items.Select(item => item.Snapshot).ToArray());
                lease.Check = () => {
                    token.ThrowIfCancellationRequested(); FaultPoint?.Invoke("before-capture-set-validate");
                    foreach (var item in items)
                    {
                        token.ThrowIfCancellationRequested();
                        if (ResolvePath(workspace, item.Relative, false) != item.Path || ExistingLocatorId(item.Path) != item.Id ||
                            item.Stream.Length != item.Snapshot.ByteLength)
                            throw new WorkspaceFileException("project_source_changed", "A pinned project member changed.");
                        long length;
                        if (ChatBlobStore.HashFile(item.Stream, out length, token, null) != item.Snapshot.ContentSha256)
                            throw new WorkspaceFileException("project_source_changed", "Pinned project bytes changed during capture.");
                    }
                };
                lease.Validate(); return lease;
            }
            catch { lease.Dispose(); throw; }
        }
    }
}
