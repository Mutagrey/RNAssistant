using System;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Storage
{
    public sealed partial class WorkspaceStore
    {
        public sealed class HtmlDefinitionPin : IDisposable
        {
            internal IDisposable Lease;
            internal FileStream Stream;
            internal Action Check;
            public HtmlProjectDefinitionState State { get; internal set; }
            public void Validate()
            { if (Stream == null) throw new ObjectDisposedException(nameof(HtmlDefinitionPin)); Check(); }
            public void Dispose() { Stream?.Dispose(); Stream = null; Lease?.Dispose(); Lease = null; }
        }

        public HtmlProjectDefinitionState GetHtmlProjectDefinition(WorkspaceDescriptor workspace)
        { using (var pin = PinHtmlProjectDefinition(workspace)) return pin.State; }

        // Definition lease precedes content scopes and aggregate publication scope.
        // workspace.json never passes through public file-tool path admission.
        public HtmlDefinitionPin PinHtmlProjectDefinition(WorkspaceDescriptor workspace,
            CancellationToken token = default(CancellationToken))
        {
            ValidateDescriptor(workspace);
            var pin = new HtmlDefinitionPin();
            try
            {
                pin.Lease = DefinitionLease(workspace, token);
                var path = DefinitionPath(workspace);
                pin.Stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var manifest = ReadDefinitionManifest(pin.Stream, workspace);
                pin.State = DefinitionState(manifest.HtmlProject);
                var expected = pin.State.Fingerprint;
                pin.Check = () => {
                    token.ThrowIfCancellationRequested();
                    if (DefinitionPath(workspace) != path || DefinitionState(pin.State.Definition).Fingerprint != expected ||
                        DefinitionState(ReadDefinitionManifest(pin.Stream, workspace).HtmlProject).Fingerprint != expected)
                        throw new WorkspaceFileException("project_definition_changed", "HTML project definition changed.");
                };
                return pin;
            }
            catch { pin.Dispose(); throw; }
        }

        public HtmlProjectDefinitionState SetHtmlProjectDefinition(WorkspaceDescriptor workspace,
            string expectedFingerprint, HtmlProjectDefinition definition, CancellationToken token = default(CancellationToken))
        {
            ValidateDescriptor(workspace);
            if (workspace.ReadOnly) throw new WorkspaceFileException("workspace_read_only", "Project configuration is read-only.");
            var canonical = CanonicalDefinition(definition);
            using (DefinitionLease(workspace, token))
            {
                var path = DefinitionPath(workspace);
                WorkspaceManifest manifest;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    manifest = ReadDefinitionManifest(stream, workspace);
                if (DefinitionState(manifest.HtmlProject).Fingerprint != expectedFingerprint)
                    throw new WorkspaceFileException("project_definition_changed", "Refresh the HTML project definition before saving it.");
                foreach (var member in canonical.Members) WorkspaceFileService.ValidateCapturePath(workspace, member);
                token.ThrowIfCancellationRequested();
                if (DefinitionState(canonical).Fingerprint == expectedFingerprint) return DefinitionState(canonical);
                manifest.HtmlProject = canonical; manifest.HtmlEntryPath = null;
                StorageFileSystem.WriteAllTextAtomic(path, JsonConvert.SerializeObject(manifest, Formatting.Indented), Utf8);
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var verified = DefinitionState(ReadDefinitionManifest(stream, workspace).HtmlProject);
                    if (verified.Fingerprint != DefinitionState(canonical).Fingerprint)
                        throw new WorkspaceFileException("project_definition_unknown", "Project definition publication was not confirmed; read it before retrying.");
                    workspace.HtmlEntryPath = canonical.EntryPath;
                    return verified;
                }
            }
        }

        private IDisposable DefinitionLease(WorkspaceDescriptor workspace, CancellationToken token)
        { return StorageFileSystem.AcquireWriteLock(Path.Combine(_paths.WorkspaceDirectory, workspace.WorkspaceId + ".definition.lck"), token); }

        private static string DefinitionPath(WorkspaceDescriptor workspace)
        {
            var control = Path.Combine(workspace.RootPath, ".rnassistant");
            var path = Path.Combine(control, "workspace.json");
            if (!Directory.Exists(workspace.RootPath) || StorageFileSystem.IsReparsePoint(workspace.RootPath) ||
                !Directory.Exists(control) || StorageFileSystem.IsReparsePoint(control) || StorageFileSystem.IsReparsePoint(path))
                throw new WorkspaceFileException("workspace_unavailable", "Workspace definition is unavailable or linked.");
            return path;
        }

        private static WorkspaceManifest ReadDefinitionManifest(FileStream stream, WorkspaceDescriptor workspace)
        {
            if (stream.Length > WorkspaceFileService.MaximumDefinitionBytes)
                throw new WorkspaceFileException("project_definition_too_large", "Workspace definition exceeds one MiB.");
            stream.Position = 0;
            WorkspaceManifest manifest;
            using (var reader = new StreamReader(stream, Utf8, false, 4096, true))
                manifest = JsonConvert.DeserializeObject<WorkspaceManifest>(reader.ReadToEnd());
            Validate(manifest);
            if (manifest.WorkspaceId != workspace.WorkspaceId)
                throw new WorkspaceFileException("workspace_changed", "Workspace identity changed.");
            return manifest;
        }

        private static HtmlProjectDefinitionState DefinitionState(HtmlProjectDefinition value)
        {
            if (value == null) return new HtmlProjectDefinitionState();
            var definition = CanonicalDefinition(value);
            return new HtmlProjectDefinitionState { Definition = definition,
                Fingerprint = TextPatternEngine.Sha256(JsonConvert.SerializeObject(definition, Formatting.None)) };
        }

        private static HtmlProjectDefinition CanonicalDefinition(HtmlProjectDefinition value)
        {
            if (value == null || !ArtifactEntryPath(value.EntryPath) || value.Members == null || value.Members.Length == 0 ||
                value.Members.Length > WorkspaceFileService.MaximumCaptureSetMembers ||
                !value.Members.Contains(value.EntryPath, StringComparer.Ordinal) ||
                value.Members.Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.Members.Length)
                throw new WorkspaceFileException("project_members_invalid", "Declare the HTML entry and 1–512 unique project members.");
            foreach (var path in value.Members)
            {
                if (path == null || path.Length > 1024 || path.Contains("\\"))
                    throw new WorkspaceFileException("project_members_invalid", "Project members require canonical relative paths.");
                WorkspaceFileService.ValidateWritePath(path);
            }
            return new HtmlProjectDefinition { EntryPath = value.EntryPath,
                Members = value.Members.OrderBy(path => path, StringComparer.Ordinal).ToArray() };
        }
    }
}
