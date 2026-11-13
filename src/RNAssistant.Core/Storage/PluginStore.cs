using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Storage
{
    // One authority journal owns registrations and immutable publications. CAS retains
    // package bytes; the registry never becomes a second workspace/file store.
    public sealed partial class PluginStore
    {
        private static readonly ResourceAuthorityScopeId Scope = new ResourceAuthorityScopeId("plugins", "local");
        private readonly ResourceAuthorityStore _authority;
        private readonly ChatBlobStore _blobs;
        private readonly WorkspaceFileService _files;
        private readonly AppDataPaths _paths;
        private readonly ResourceMutationJournal _journal;
        public PluginStore(AppDataPaths paths)
        { _paths = paths; _authority = new ResourceAuthorityStore(paths); _blobs = new ChatBlobStore(paths);
            _files = new WorkspaceFileService(paths); _journal = new ResourceMutationJournal(paths); }

        // A transient OS lease, not another registry. It protects retained plugin
        // bytes across panels/processes while the existing runtime-clear owner runs.
        public static IDisposable AcquireRuntimeUse(AppDataPaths paths, bool exclusive = false)
        {
            var path = Path.Combine(paths.Root, "plugins.use");
            StorageFileSystem.EnsureRegularDirectory(paths.Root);
            try
            {
                if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                    throw new PluginException("plugin_lease_invalid", "Plugin lifetime lease must be a regular file.");
            }
            catch (FileNotFoundException) { /* First lease creates this empty coordination file. */ }
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, exclusive ? FileShare.None : FileShare.ReadWrite); }
            catch (IOException) { throw new PluginException("plugins_busy", "Close plugin views and finish their commands/source chats before clearing runtime data."); }
        }

        public IReadOnlyList<PluginRegistration> List()
        {
            return _authority.Capture(Scope).Heads.Values.Where(head => head.Knowledge == HeadKnowledge.Known &&
                head.Revision.Uri.IndexOf("/registration/", StringComparison.Ordinal) >= 0)
                .Select(head => Read<PluginRegistration>(head.Revision, "plugin-registration"))
                .OrderBy(item => item.PluginId, StringComparer.Ordinal).ToArray();
        }

        public PluginRegistration Get(string id)
        {
            RequireId(id);
            var head = _authority.GetHead(Scope, RegistrationRef(id, "0").Identity);
            return head?.Knowledge == HeadKnowledge.Known ? Read<PluginRegistration>(head.Revision, "plugin-registration") : null;
        }

        public PluginRegistration Connect(PluginSourceWorkspace source)
        {
            var manifest = PluginPackageFiles.ParseManifest(_files.ReadText(source.Workspace, "plugin.json").Text);
            if (PluginPackageFiles.IconIsSvgPath(manifest.Icon))
                PluginPackageFiles.ReadIconSvg(PluginPackageFiles.Read(source.Workspace.RootPath, manifest.Icon, PluginIconSvg.MaximumBytes));
            var current = Get(manifest.Id);
            if (current != null)
            {
                if (current.SourceWorkspaceId != source.SourceWorkspaceId || current.SourceRoot != source.Workspace.RootPath)
                    throw new PluginException("plugin_already_connected", "This plugin ID is connected to another source workspace.");
                return current.Hidden ? SetHidden(manifest.Id, false) : current;
            }
            var registration = new PluginRegistration { PluginId = manifest.Id, Name = manifest.Name, Icon = manifest.Icon,
                SourceWorkspaceId = source.SourceWorkspaceId, SourceRoot = source.Workspace.RootPath, Enabled = false };
            SaveRegistration(registration, null);
            return Get(manifest.Id);
        }

        public PluginReleaseRecord Capture(PluginSourceWorkspace source, VendorCatalog vendors,
            IEnumerable<PluginReleaseRecord> reusable = null, bool retain = true,
            Action<PluginReleaseRecord> onCaptured = null, System.Threading.CancellationToken token = default(System.Threading.CancellationToken))
        {
            using (var capture = _files.CaptureSetFromDefinition(source.Workspace, "plugin.json",
                json => PluginPackageFiles.ParseManifest(json).Files, token))
            {
                var manifest = PluginPackageFiles.ParseManifest(capture.DefinitionText);
                var members = new SortedDictionary<string, PayloadRef>(StringComparer.Ordinal);
                foreach (var pin in capture.Files)
                {
                    var payload = pin.Evidence.Payload;
                    members.Add(pin.RelativePath, new PayloadRef(payload.Sha256, payload.ByteLength,
                        Mime(pin.RelativePath), payload.Encryption, payload.ProtectionKeyId));
                }
                if (PluginPackageFiles.IconIsSvgPath(manifest.Icon))
                    PluginPackageFiles.ReadIconSvg(_blobs.ReadBytes(members[manifest.Icon].ToBlobReference()));
                long totalBytes = members.Values.Sum(payload => payload.ByteLength);
                foreach (var dependency in manifest.Vendors)
                {
                    token.ThrowIfCancellationRequested();
                    var package = vendors.Resolve(dependency.Id, dependency.Version);
                    VendorAdmission.RequirePlugin(package.Manifest);
                    if (package.Manifest.Dependencies.Any(required => !manifest.Vendors.Any(value => value.Id == required.Id && value.Version == required.Version)))
                        throw new PluginException("vendor_dependency_missing", "Declare mounts for every exact transitive dependency.");
                    foreach (var member in package.Files)
                    {
                        token.ThrowIfCancellationRequested();
                        var path = dependency.Mount + "/" + member.Key;
                        var payload = PayloadRef.FromBlob(_blobs.StoreBytes(member.Value, Mime(path)));
                        PayloadRef existing;
                        if (members.TryGetValue(path, out existing) && existing.Sha256 != payload.Sha256)
                            throw new PluginException("vendor_collision", "Source and resolved vendor differ: " + path);
                        if (!members.ContainsKey(path))
                        {
                            if (!PluginPackageFiles.ValidPath(path) || members.Count >= PluginPackageFiles.MaximumFiles ||
                                payload.ByteLength > WorkspaceFileService.MaximumCaptureSetBytes - totalBytes)
                                throw new PluginException("package_limit", "Resolved package exceeds its path/file/byte limits.");
                            totalBytes += payload.ByteLength; members.Add(path, payload);
                        }
                    }
                }
                if (members.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != members.Count)
                    throw new PluginException("package_limit", "Plugin package has colliding paths.");
                var record = new PluginReleaseRecord { PluginId = manifest.Id, ReleaseId = "rel_" + Guid.NewGuid().ToString("N"),
                    SourceWorkspaceId = source.SourceWorkspaceId, ManifestJson = capture.DefinitionText,
                    Files = members.Select(file => new PluginPackageFile { Path = file.Key, Payload = file.Value }).ToList() };
                record.PackageSha256 = Digest(record.Files);
                var retainedMatch = (reusable ?? Enumerable.Empty<PluginReleaseRecord>()).FirstOrDefault(item =>
                    item.PluginId == record.PluginId && item.SourceWorkspaceId == record.SourceWorkspaceId &&
                    item.PackageSha256 == record.PackageSha256 && item.ManifestJson == record.ManifestJson);
                capture.Validate();
                if (retainedMatch != null) record = retainedMatch;
                else if (retain) Publish(ReleaseRef(record.PluginId, record.ReleaseId), "plugin-release", record,
                    record.Files.Select(file => file.Payload), null);
                // Save may publish its catalog while these exact definition/content handles remain held.
                onCaptured?.Invoke(record);
                return record;
            }
        }

        public PluginRegistration Disable(string id)
        {
            if (VersionCatalog(id).State() != null)
            {
                using (_journal.AcquireScope(Scope, true))
                {
                    var active = Get(id) ?? throw new PluginException("plugin_missing", "Plugin is not connected.");
                    if (!active.Enabled) return active;
                    var expectedActive = active.ActiveRevision;
                    active.Enabled = false;
                    active.ActiveRevision = Guid.NewGuid().ToString("N");
                    VersionCatalog(id).UpdateOwner(frozen => new[] {
                        StageVersionRegistration(frozen, active, active.ActiveSavedVersionId, active.ActiveReleaseId, expectedActive) });
                    return Get(id);
                }
            }
            var registration = Get(id) ?? throw new PluginException("plugin_missing", "Plugin is not connected.");
            registration.Enabled = false;
            SaveRegistration(registration, registration.ActiveReleaseId);
            return Get(id);
        }

        public PluginRegistration SetHidden(string id, bool hidden)
        {
            var registration = Get(id) ?? throw new PluginException("plugin_missing", "Plugin is not connected.");
            if (registration.Hidden == hidden) return registration;
            var previousActiveRevision = registration.ActiveRevision;
            registration.Hidden = hidden;
            registration.ActiveRevision = Guid.NewGuid().ToString("N");
            PluginRegistration observed;
            try { SaveRegistration(registration, registration.ActiveReleaseId, previousActiveRevision, true); }
            catch (Exception error)
            {
                try { observed = Get(id); }
                catch { throw new PluginException("effect_unknown", "Plugin panel change could not be read back. Inspect the catalog before retrying. " + error.Message); }
                if (observed?.Hidden == hidden && observed.ActiveReleaseId == registration.ActiveReleaseId) return observed;
                if (observed?.Hidden == !hidden && observed.ActiveReleaseId == registration.ActiveReleaseId) throw;
                throw new PluginException("effect_unknown", "Plugin panel state changed without a verified receipt. Inspect before retrying. " + error.Message);
            }
            try { observed = Get(id); }
            catch (Exception error) { throw new PluginException("effect_unknown", "Plugin panel read-back failed. Inspect before retrying. " + error.Message); }
            if (observed?.Hidden != hidden || observed.ActiveReleaseId != registration.ActiveReleaseId)
                throw new PluginException("effect_unknown", "Plugin panel visibility could not be verified. Refresh the catalog.");
            return observed;
        }

        public PluginRegistration SetAppearance(string id, PluginAppearance appearance)
        {
            RequireId(id);
            var head = _authority.GetHead(Scope, RegistrationRef(id, "0").Identity);
            if (head?.Knowledge != HeadKnowledge.Known) throw new PluginException("plugin_missing", "Plugin is not connected.");
            var registration = Read<PluginRegistration>(head.Revision, "plugin-registration");
            registration.Appearance = appearance;
            var next = RegistrationRef(id, "reg_" + Guid.NewGuid().ToString("N"));
            var expected = JsonConvert.SerializeObject(registration);
            try { Publish(next, "plugin-registration", registration, Enumerable.Empty<PayloadRef>(), head.Revision); }
            catch (PluginException error) when (error.Code == "plugin_selection_changed") { throw; }
            catch (Exception error)
            {
                try { var recovered = Get(id); if (JsonConvert.SerializeObject(recovered) == expected) return recovered; }
                catch { /* Uncertain publication must not be silently retried. */ }
                throw new PluginException("effect_unknown", "Plugin appearance could not be verified. Refresh the catalog before retrying. " + error.Message);
            }
            PluginRegistration observed;
            try { observed = Get(id); }
            catch (Exception error) { throw new PluginException("effect_unknown", "Plugin appearance read-back failed. " + error.Message); }
            if (JsonConvert.SerializeObject(observed) != expected)
                throw new PluginException("effect_unknown", "Plugin appearance changed before read-back. Refresh the catalog.");
            return observed;
        }

        public PluginReleaseRecord ReadRelease(PluginReleaseIdentity identity)
        {
            var record = Read<PluginReleaseRecord>(ReleaseRef(identity.PluginId, identity.ReleaseId), "plugin-release");
            ValidateRelease(record);
            if (record.PluginId != identity.PluginId || record.ReleaseId != identity.ReleaseId)
                throw new PluginException("plugin_release_invalid", "Publication identity mismatch.");
            return record;
        }

        private IEnumerable<string> PublishedReleaseIds(string id, ResourceRef cutoff = null)
        {
            // Follow the registration's immutable parent chain. Checked candidates
            // have release heads too, but no registration revision points to them.
            var head = _authority.GetHead(Scope, RegistrationRef(id, "0").Identity);
            if (head?.Knowledge != HeadKnowledge.Known) yield break;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var reference = cutoff ?? head.Revision;
            while (reference != null)
            {
                var revision = _authority.GetRevision(Scope, reference);
                if (revision == null) throw new PluginException("plugin_history_invalid", "История регистрации неполная.");
                var historic = Read<PluginRegistration>(reference, "plugin-registration", false);
                if (!string.IsNullOrEmpty(historic.ActiveReleaseId) && seen.Add(historic.ActiveReleaseId))
                    yield return historic.ActiveReleaseId;
                reference = revision.Parent;
            }
        }

        public byte[] ReadFile(PluginReleaseRecord release, string path)
        {
            var file = release.Files.SingleOrDefault(item => item.Path == path);
            if (file == null) throw new PluginException("package_file_missing", "File is outside the captured package.");
            return _blobs.ReadBytes(file.Payload.ToBlobReference()) ?? throw new PluginException("plugin_release_invalid", "Captured package file is unavailable.");
        }

        private void SaveRegistration(PluginRegistration registration, string expectedRelease,
            string expectedActiveRevision = null, bool activeRevisionChanged = false)
        {
            var identity = RegistrationRef(registration.PluginId, "0").Identity;
            var before = _authority.GetHead(Scope, identity);
            var saved = before?.Knowledge == HeadKnowledge.Known ? Read<PluginRegistration>(before.Revision, "plugin-registration") : null;
            if (saved?.ActiveReleaseId != expectedRelease || saved != null && (saved.SourceWorkspaceId != registration.SourceWorkspaceId ||
                saved.ActiveSavedVersionId != registration.ActiveSavedVersionId ||
                saved.ActiveRevision != (activeRevisionChanged ? expectedActiveRevision : registration.ActiveRevision)))
                throw new PluginException("plugin_selection_changed", "Plugin registration changed.");
            var next = RegistrationRef(registration.PluginId, "reg_" + Guid.NewGuid().ToString("N"));
            Publish(next, "plugin-registration", registration, Enumerable.Empty<PayloadRef>(), before?.Revision);
        }

        private void Publish(ResourceRef reference, string view, object record, IEnumerable<PayloadRef> parts, ResourceRef expected)
        {
            var payload = PayloadRef.FromBlob(_blobs.StoreText(JsonConvert.SerializeObject(record), "application/json"));
            _authority.RegisterRevision(Scope, new ResourceRevisionMetadata(reference, payload.Sha256, payload, parent: expected));
            _authority.RegisterView(Scope, new ResourceRevisionView(reference, view, payload.Sha256, payload, ResourceCoverage.Whole(), parts));
            var frozen = _authority.Capture(Scope);
            var before = frozen.GetHead(reference.Identity);
            if (before?.Revision?.Revision != expected?.Revision)
                throw new PluginException("plugin_selection_changed", "Plugin publication changed concurrently.");
            _authority.Publish(ResourceAuthorityCommit.Create(Scope, frozen.Generation, null,
                new[] { new ResourceHeadChange(reference.Identity, before, ResourceHeadState.Known(reference, frozen.Generation + 1, view)) },
                AuthorityCommitReason.CatalogResourcePublication));
        }

        private T Read<T>(ResourceRef reference, string view, bool requireHead = true)
        {
            if (requireHead)
            {
                var published = _authority.GetHead(Scope, reference.Identity);
                if (published?.Knowledge != HeadKnowledge.Known || published.Revision.Revision != reference.Revision)
                    throw new PluginException("plugin_release_missing", "The exact publication is not available.");
            }
            else if (_authority.GetRevision(Scope, reference) == null)
                throw new PluginException("plugin_release_missing", "The exact publication is not available.");
            var data = _authority.GetView(Scope, reference, view);
            if (data?.Payload == null || data.Payload.ByteLength > 1024 * 1024)
                throw new PluginException("plugin_release_invalid", "Invalid package metadata.");
            var json = _blobs.ReadText(data.Payload.ToBlobReference());
            if (typeof(T) == typeof(PluginRegistration))
            {
                // Decode the previous path-array appearance into the only live SVG representation.
                var document = Newtonsoft.Json.Linq.JObject.Parse(json);
                var appearance = document["Appearance"] as Newtonsoft.Json.Linq.JObject;
                var paths = appearance?["IconPaths"];
                if (paths != null)
                {
                    if (paths.Type != Newtonsoft.Json.Linq.JTokenType.Null)
                        appearance["IconSvg"] = PluginIconSvg.FromLegacyPaths(paths.ToObject<string[]>());
                    appearance.Remove("IconPaths"); json = document.ToString(Formatting.None);
                }
            }
            return JsonConvert.DeserializeObject<T>(json, PluginPackageFiles.Json);
        }

        private static void ValidateRelease(PluginReleaseRecord record)
        {
            if (record == null || record.FormatVersion != 1 || record.ReleaseId == null || !record.ReleaseId.StartsWith("rel_", StringComparison.Ordinal) ||
                record.Files == null || record.Files.Count == 0 || record.Files.Count > PluginPackageFiles.MaximumFiles ||
                record.Files.Any(file => file?.Payload == null || !PluginPackageFiles.ValidPath(file.Path) || file.Payload.ByteLength > PluginPackageFiles.MaximumFileBytes) ||
                record.Files.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != record.Files.Count ||
                record.Files.Sum(file => file.Payload.ByteLength) > PluginPackageFiles.MaximumPackageBytes ||
                record.PackageSha256 != Digest(record.Files) || PluginPackageFiles.ParseManifest(record.ManifestJson).Id != record.PluginId)
                throw new PluginException("plugin_release_invalid", "Invalid immutable plugin package.");
        }
        private static string Digest(IEnumerable<PluginPackageFile> files)
        { return TextPatternEngine.Sha256(string.Join("\n", files.OrderBy(file => file.Path, StringComparer.Ordinal).Select(file => file.Path + " " + file.Payload.Sha256))); }
        private static void RequireId(string id)
        { if (!PluginPackageFiles.ValidId(id)) throw new PluginException("plugin_id_invalid", "Invalid plugin ID."); }
        private static ResourceRef RegistrationRef(string id, string revision)
        { RequireId(id); return new ResourceRef(ResourceUri.Create("plugin", id, "registration", "current"), revision); }
        public static ResourceRef ReleaseRef(string id, string release)
        { RequireId(id); return new ResourceRef(ResourceUri.Create("plugin", id, "release", release), "1"); }
        public static string Mime(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".html": return "text/html; charset=utf-8";
                case ".js": return "text/javascript; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".json": return "application/json; charset=utf-8";
                case ".svg": return "image/svg+xml";
                case ".png": return "image/png";
                case ".woff2": return "font/woff2";
                default: return "application/octet-stream";
            }
        }
    }
}
