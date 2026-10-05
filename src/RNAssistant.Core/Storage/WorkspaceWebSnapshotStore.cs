using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Storage
{
    // Publication owner only: manifests/results use the existing authority journal
    // and CAS. Source bytes and revisions stay owned by WorkspaceFileService.
    public sealed class WorkspaceWebSnapshotStore
    {
        public const int MaximumFiles = 32;
        public const int MaximumSnapshotBytes = 4 * 1024 * 1024;
        private const int MaximumRecordBytes = 128 * 1024;
        private const string ManifestView = "web-project-manifest";
        private const string VerificationView = "web-verification";
        private readonly ResourceAuthorityStore _authority;
        private readonly ChatBlobStore _payloads;
        private readonly WorkspaceFileService _files;
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
            { MissingMemberHandling = MissingMemberHandling.Error };

        private sealed class Manifest
        {
            [JsonProperty(Required = Required.Always)] public int SchemaVersion { get; set; } = 1;
            [JsonProperty(Required = Required.Always)] public string Id { get; set; }
            [JsonProperty(Required = Required.Always)] public string WorkspaceId { get; set; }
            [JsonProperty(Required = Required.Always)] public string EntryPath { get; set; }
            [JsonProperty(Required = Required.Always)] public string SnapshotSha256 { get; set; }
            [JsonProperty(Required = Required.Always)] public IReadOnlyList<WebSnapshotFile> Files { get; set; }
        }

        public WorkspaceWebSnapshotStore(AppDataPaths paths, WorkspaceFileService files)
        {
            _authority = new ResourceAuthorityStore(paths);
            _payloads = new ChatBlobStore(paths);
            _files = files ?? throw new ArgumentNullException(nameof(files));
        }

        public RetainedWebSnapshot PublishSnapshot(WorkspaceDescriptor workspace, string entryPath,
            IEnumerable<WebSnapshotFile> files)
        {
            var manifest = new Manifest { Id = Guid.NewGuid().ToString("N"), WorkspaceId = workspace.WorkspaceId,
                EntryPath = entryPath, Files = files?.Take(MaximumFiles + 1).ToArray() };
            ValidateManifest(manifest, workspace, manifest.Id, false);
            manifest.Files = manifest.Files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
            manifest.SnapshotSha256 = Digest(manifest.Files);
            foreach (var file in manifest.Files)
            {
                var observed = _files.ReadExactText(workspace, file.Path, file.Reference);
                if (!file.Matches(observed.Evidence.Payload))
                    throw Unavailable("Snapshot file does not match its published exact payload.");
            }
            var reference = Reference(workspace, "snapshot", manifest.Id, "1");
            Publish(workspace, reference, ManifestView, manifest, manifest.Files.Select(file => file.Payload),
                dependencies: manifest.Files.Select(file => new ResourceDependency(file.Reference, "text", kind: "snapshot-source")));
            return ReadSnapshot(workspace, manifest.Id);
        }

        public RetainedWebSnapshot ReadSnapshot(WorkspaceDescriptor workspace, string id)
        {
            var reference = Reference(workspace, "snapshot", id, "1");
            var view = PublishedView(workspace, reference, ManifestView);
            var manifest = Read<Manifest>(view);
            ValidateManifest(manifest, workspace, id);
            if (view.Parts.Count != manifest.Files.Count || manifest.Files.Where((file, index) =>
                !file.Matches(view.Parts[index])).Any())
                throw Unavailable("Manifest payload roots differ from its files.");
            return new RetainedWebSnapshot(id, workspace.WorkspaceId, manifest.EntryPath,
                manifest.SnapshotSha256, reference, view.Payload, manifest.Files);
        }

        public string BeginVerification(WorkspaceDescriptor workspace, string entryPath, bool historical,
            WebVerificationOrigin origin = null, string requestedSnapshotId = null)
        {
            var record = new WebVerificationRecord { Id = Guid.NewGuid().ToString("N"),
                WorkspaceId = workspace.WorkspaceId, EntryPath = entryPath ?? string.Empty,
                RequestedSnapshotId = requestedSnapshotId,
                Historical = historical, State = WebVerificationState.Pending,
                StartedUtc = DateTime.UtcNow, Origin = origin };
            SaveVerification(workspace, record, null);
            return record.Id;
        }

        public void AttachSnapshot(WorkspaceDescriptor workspace, string verificationId, RetainedWebSnapshot snapshot)
        {
            if (snapshot.WorkspaceId != workspace.WorkspaceId) throw Unavailable("Snapshot belongs to another workspace.");
            ResourceRef previous;
            var record = ReadVerification(workspace, verificationId, out previous);
            if (record.State != WebVerificationState.Pending || record.SnapshotId != null)
                throw Unavailable("Verification already has a snapshot or terminal result.");
            record.SnapshotId = snapshot.Id;
            record.EntryPath = snapshot.EntryPath;
            record.SnapshotSha256 = snapshot.SnapshotSha256;
            record.CheckedFiles = snapshot.Files.Select(file => file.Path).ToArray();
            SaveVerification(workspace, record, previous);
        }

        public void CompleteVerification(WorkspaceDescriptor workspace, string id, WebVerificationState state,
            string browser, IReadOnlyList<string> errors, IReadOnlyList<string> hints)
        {
            ResourceRef previous;
            var record = ReadVerification(workspace, id, out previous);
            if (record.State != WebVerificationState.Pending || state == WebVerificationState.Pending ||
                !Enum.IsDefined(typeof(WebVerificationState), state) || errors == null || hints == null ||
                state == WebVerificationState.Passed && (record.SnapshotId == null ||
                    string.IsNullOrWhiteSpace(browser) || errors == null || errors.Count != 0))
                throw Unavailable("Verification cannot accept this terminal result.");
            record.State = state; record.CompletedUtc = DateTime.UtcNow;
            record.Browser = browser; record.Errors = errors; record.Hints = hints;
            SaveVerification(workspace, record, previous);
        }

        public WebVerificationRecord ReadVerification(WorkspaceDescriptor workspace, string id)
        { ResourceRef reference; return ReadVerification(workspace, id, out reference); }

        public WebVerificationPage ListVerifications(WorkspaceDescriptor workspace, int offset = 0)
        {
            var prefix = ResourceUri.Create("workspace-web", workspace.WorkspaceId, "verification") + "/";
            var page = _authority.ReadHeads(Scope(workspace),
                new[] { new ResourceHeadRange(prefix, prefix + "\uffff") }, offset, 20);
            return new WebVerificationPage { Generation = page.Generation, Total = page.Total,
                NextOffset = page.NextOffset, Items = page.Items.Select(head =>
                    ReadVerificationRevision(workspace, ResourceUri.Parse(head.Identity.Uri).Segments.Last(), head.Revision)).ToArray() };
        }

        private WebVerificationRecord ReadVerification(WorkspaceDescriptor workspace, string id, out ResourceRef reference)
        {
            var identity = Reference(workspace, "verification", id, "1").Identity;
            var head = _authority.GetHead(Scope(workspace), identity);
            if (head?.Knowledge != HeadKnowledge.Known) throw Unavailable("Verification record is unavailable in this workspace.");
            reference = head.Revision;
            return ReadVerificationRevision(workspace, id, reference);
        }

        private WebVerificationRecord ReadVerificationRevision(WorkspaceDescriptor workspace, string id, ResourceRef reference)
        {
            if (reference == null) throw Unavailable("Verification record is unavailable in this workspace.");
            var record = Read<WebVerificationRecord>(PublishedView(workspace, reference, VerificationView));
            if (record == null || record.SchemaVersion != 1 || record.Id != id || record.WorkspaceId != workspace.WorkspaceId ||
                !Enum.IsDefined(typeof(WebVerificationState), record.State) ||
                (record.State == WebVerificationState.Pending) == record.CompletedUtc.HasValue ||
                record.CheckedFiles == null || record.Errors == null || record.Hints == null ||
                record.State == WebVerificationState.Passed && (record.SnapshotId == null ||
                    string.IsNullOrWhiteSpace(record.Browser) || record.Errors.Count != 0 || record.CheckedFiles.Count == 0))
                throw Unavailable("Verification record is invalid or unsupported.");
            record.Reference = reference;
            return record;
        }

        private void SaveVerification(WorkspaceDescriptor workspace, WebVerificationRecord record, ResourceRef previous)
        {
            var snapshot = record.SnapshotId == null ? null : ReadSnapshot(workspace, record.SnapshotId);
            Publish(workspace, Reference(workspace, "verification", record.Id, Guid.NewGuid().ToString("N")),
                VerificationView, record, snapshot == null ? new PayloadRef[0] : new[] { snapshot.Payload }, previous,
                snapshot == null ? null : new[] { new ResourceDependency(snapshot.Reference, ManifestView, kind: "verified-snapshot") });
        }

        private void Publish(WorkspaceDescriptor workspace, ResourceRef reference, string viewName,
            object record, IEnumerable<PayloadRef> parts, ResourceRef expected = null,
            IEnumerable<ResourceDependency> dependencies = null)
        {
            var json = JsonConvert.SerializeObject(record);
            if (Encoding.UTF8.GetByteCount(json) > MaximumRecordBytes)
                throw Unavailable("Snapshot metadata exceeds its bound.");
            var payload = PayloadRef.FromBlob(_payloads.StoreText(json, "application/json"));
            var scope = Scope(workspace);
            _authority.RegisterRevision(scope, new ResourceRevisionMetadata(reference, payload.Sha256, payload,
                parent: expected, dependencies: dependencies));
            _authority.RegisterView(scope, new ResourceRevisionView(reference, viewName, payload.Sha256,
                payload, ResourceCoverage.Whole(), parts));
            for (var retry = 0; retry < 8; retry++)
            {
                var frozen = _authority.Capture(scope);
                var before = frozen.GetHead(reference.Identity);
                if (before?.Revision?.Revision != expected?.Revision)
                    throw new ResourceAuthorityConflictException("Snapshot publication head changed.");
                try
                {
                    _authority.Publish(ResourceAuthorityCommit.Create(scope, frozen.Generation, null,
                        new[] { new ResourceHeadChange(reference.Identity, before,
                            ResourceHeadState.Known(reference, frozen.Generation + 1, viewName)) },
                        AuthorityCommitReason.DerivedPublication));
                    return;
                }
                catch (ResourceAuthorityConflictException) when (retry < 7) { }
            }
        }

        private ResourceRevisionView PublishedView(WorkspaceDescriptor workspace, ResourceRef reference, string viewName)
        {
            var scope = Scope(workspace);
            var frozen = _authority.Capture(scope);
            if (!frozen.Commits.Any(commit => commit.HeadChanges.Any(change =>
                change.After.Knowledge == HeadKnowledge.Known && change.After.Revision.Uri == reference.Uri &&
                change.After.Revision.Revision == reference.Revision)))
                throw Unavailable("Exact web resource has not been published in this workspace.");
            var view = _authority.GetView(scope, reference, viewName);
            if (view?.Payload == null || view.Coverage?.Kind != ResourceCoverageKinds.Whole ||
                view.Payload.ByteLength > MaximumRecordBytes || view.ContentSha256 != view.Payload.Sha256 ||
                _authority.GetRevision(scope, reference)?.ContentSha256 != view.ContentSha256)
                throw Unavailable("Exact web resource view is unavailable.");
            return view;
        }

        private T Read<T>(ResourceRevisionView view)
        {
            var bytes = _payloads.ReadBytes(view.Payload.ToBlobReference());
            if (bytes == null) throw Unavailable("Retained web metadata is missing or corrupt.");
            try { return JsonConvert.DeserializeObject<T>(new UTF8Encoding(false, true).GetString(bytes), JsonSettings); }
            catch (Exception ex) when (ex is JsonException || ex is DecoderFallbackException)
            { throw Unavailable("Retained web metadata is invalid: " + ex.Message); }
        }

        private static void ValidateManifest(Manifest manifest, WorkspaceDescriptor workspace, string id, bool checkDigest = true)
        {
            if (manifest == null || manifest.SchemaVersion != 1 || manifest.Id != id ||
                manifest.WorkspaceId != workspace.WorkspaceId || !ValidPath(manifest.EntryPath) ||
                manifest.Files == null || manifest.Files.Count == 0 || manifest.Files.Count > MaximumFiles ||
                manifest.Files.Any(file => file == null || !ValidPath(file.Path) || file.Reference?.IsExact != true ||
                    file.Payload == null || file.Payload.ByteLength > WorkspaceFileService.MaximumTextBytes) ||
                manifest.Files.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Count ||
                manifest.Files.Sum(file => file.Payload.ByteLength) > MaximumSnapshotBytes ||
                !manifest.Files.Any(file => file.Path == manifest.EntryPath) || checkDigest && manifest.SnapshotSha256 != Digest(manifest.Files))
                throw Unavailable("Web snapshot manifest is invalid, unsupported or exceeds its bound.");
        }

        private static bool ValidPath(string path)
        { return !string.IsNullOrEmpty(path) && path.Length <= 1024 && !path.Any(char.IsControl) &&
            path.IndexOfAny(new[] { '\\', ':', '?', '#' }) < 0 &&
            path.Split('/').All(part => part.Length > 0 && part != "." && part != ".."); }

        public static string Digest(IEnumerable<WebSnapshotFile> files)
        { return TextPatternEngine.Sha256(string.Join("\n", files.OrderBy(file => file.Path, StringComparer.Ordinal)
            .Select(file => file.Path + " " + file.Payload.Sha256))); }

        private static ResourceAuthorityScopeId Scope(WorkspaceDescriptor workspace)
        { return new ResourceAuthorityScopeId("workspace-web", workspace.WorkspaceId); }

        private static ResourceRef Reference(WorkspaceDescriptor workspace, string kind, string id, string revision)
        {
            Guid parsed;
            if (!Guid.TryParseExact(id, "N", out parsed) || parsed.ToString("N") != id)
                throw new ArgumentException("Web snapshot/verification id must be a canonical ID.");
            return new ResourceRef(ResourceUri.Create("workspace-web", workspace.WorkspaceId, kind, id), revision);
        }

        private static WorkspaceFileException Unavailable(string message)
        { return new WorkspaceFileException("snapshot_unavailable", message); }
    }
}
