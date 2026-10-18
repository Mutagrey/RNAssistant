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
    public sealed class ResolvedVendorPackage
    {
        public VendorManifestV1 Manifest { get; internal set; }
        public IReadOnlyDictionary<string, byte[]> Files { get; internal set; }
        public string Digest { get; internal set; }
    }

    // Identity/content resolution only. Callers MUST separately admit their execution context.
    public sealed class VendorCatalog
    {
        private readonly string _directory, _webRoot;
        private readonly AppDataPaths _paths;
        private readonly ResourceAuthorityStore _authority;
        private readonly ChatBlobStore _blobs;
        private static readonly ResourceAuthorityScopeId Scope = new ResourceAuthorityScopeId("vendors", "local");
        private sealed class Supply
        {
            public VendorManifestV1 Manifest { get; set; }
            public SupplyFile[] Sources { get; set; }
        }
        private sealed class SupplyFile
        {
            public string Path { get; set; }
            public string SourcePath { get; set; }
            public string SourceSha256 { get; set; }
            public string CarrierField { get; set; }
        }
        private sealed class Carrier { public string Js { get; set; } public string Css { get; set; } }
        private sealed class Resolution
        {
            public readonly HashSet<string> Visiting = new HashSet<string>(StringComparer.Ordinal);
            public readonly Dictionary<string, ResolvedVendorPackage> Completed = new Dictionary<string, ResolvedVendorPackage>(StringComparer.Ordinal);
            public long Bytes;
        }

        public VendorCatalog(AppDataPaths paths, string webRoot)
        {
            _paths = paths;
            _directory = Path.Combine(paths.Root, "vendors"); _webRoot = webRoot;
            _authority = new ResourceAuthorityStore(paths); _blobs = new ChatBlobStore(paths);
        }

        public IReadOnlyList<VendorManifestV1> List()
        {
            using (PluginStore.AcquireRuntimeUse(_paths))
            {
            var result = Supplies().Select(item => item.Manifest).ToList();
            if (Directory.Exists(_directory))
            {
                PluginPackageFiles.Resolve(_directory, "placeholder", false);
                var directories = Directory.EnumerateDirectories(_directory).Take(301).ToArray();
                if (directories.Length > 300) throw new PluginException("vendor_limit", "Vendor directory limit exceeded.");
                foreach (var id in directories)
                {
                    if (!PluginPackageFiles.ValidId(Path.GetFileName(id))) throw new PluginException("vendor_invalid", "Invalid vendor directory.");
                    var versions = Directory.EnumerateDirectories(id).Take(301).ToArray();
                    if (versions.Length > 300) throw new PluginException("vendor_limit", "Vendor version limit exceeded.");
                    foreach (var version in versions)
                    {
                        var value = ReadManifest(version);
                        if (value.Id != Path.GetFileName(id) || value.Version != Path.GetFileName(version))
                            throw new PluginException("vendor_invalid", "Vendor directory and manifest identity differ.");
                        if (result.Any(item => item.Id == value.Id && item.Version == value.Version))
                            throw new PluginException("vendor_collision", "A local vendor shadows a shipped version.");
                        result.Add(value);
                        if (result.Count > 300) throw new PluginException("vendor_limit", "Vendor catalog limit exceeded.");
                    }
                }
            }
            if (result.Count > 300) throw new PluginException("vendor_limit", "Vendor catalog limit exceeded.");
            foreach (var item in result) Validate(item);
            return result.OrderBy(item => item.Id, StringComparer.Ordinal).ThenBy(item => item.Version, StringComparer.Ordinal).ToArray();
            }
        }

        public ResolvedVendorPackage Resolve(string id, string version)
        { using (PluginStore.AcquireRuntimeUse(_paths)) return Resolve(id, version, new Resolution()); }

        private ResolvedVendorPackage Resolve(string id, string version, Resolution resolution)
        {
            if (!PluginPackageFiles.ValidId(id) || !PluginPackageFiles.ValidVersion(version)) throw new PluginException("vendor_invalid", "An exact vendor version is required.");
            var key = id + "@" + version; ResolvedVendorPackage retained;
            if (resolution.Completed.TryGetValue(key, out retained)) return retained;
            if (resolution.Completed.Count + resolution.Visiting.Count >= 64) throw new PluginException("vendor_limit", "Vendor dependency graph exceeds 64 packages.");
            if (!resolution.Visiting.Add(key)) throw new PluginException("vendor_cycle", "Vendor dependency cycle.");
            var supply = Supplies().SingleOrDefault(item => item.Manifest.Id == id && item.Manifest.Version == version);
            var local = Path.Combine(_directory, id, version);
            if (supply != null && Directory.Exists(local)) throw new PluginException("vendor_collision", "Local and shipped vendor versions collide.");
            var manifest = supply?.Manifest ?? ReadManifest(local);
            Validate(manifest);
            if (manifest.Id != id || manifest.Version != version) throw new PluginException("vendor_invalid", "Vendor identity mismatch.");
            foreach (var dependency in manifest.Dependencies) Resolve(dependency.Id, dependency.Version, resolution);
            var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var file in manifest.Files)
            {
                byte[] bytes;
                if (supply == null) bytes = PluginPackageFiles.Read(local, file.Path);
                else
                {
                    var source = supply.Sources.Single(item => item.Path == file.Path);
                    bytes = PluginPackageFiles.Read(_webRoot, source.SourcePath);
                    if (PluginPackageFiles.Hash(bytes) != source.SourceSha256) throw new PluginException("vendor_changed", "Shipped vendor source changed: " + source.SourcePath);
                    if (source.CarrierField != null)
                    {
                        var text = PluginPackageFiles.Utf8.GetString(bytes);
                        var prefix = "window.RNAssistantHtmlVendorRuntime.register(\"" + id + "\", ";
                        var start = text.IndexOf(prefix, StringComparison.Ordinal);
                        var end = text.LastIndexOf(");", StringComparison.Ordinal);
                        if (start < 0 || end <= start) throw new PluginException("vendor_invalid", "Invalid inert source carrier.");
                        var carrier = JsonConvert.DeserializeObject<Carrier>(text.Substring(start + prefix.Length, end - start - prefix.Length), PluginPackageFiles.Json);
                        var content = source.CarrierField == "js" ? carrier.Js : source.CarrierField == "css" ? carrier.Css : null;
                        if (content == null) throw new PluginException("vendor_invalid", "Unknown source carrier member.");
                        bytes = PluginPackageFiles.Utf8.GetBytes(content);
                    }
                }
                if (PluginPackageFiles.Hash(bytes) != file.Sha256) throw new PluginException("vendor_changed", "Vendor bytes differ from their manifest: " + id + "/" + file.Path);
                if ((resolution.Bytes += bytes.Length) > PluginPackageFiles.MaximumPackageBytes) throw new PluginException("vendor_limit", "Resolved vendor graph exceeds its byte limit.");
                files.Add(file.Path, bytes);
            }
            if (files.Sum(item => (long)item.Value.Length) > PluginPackageFiles.MaximumPackageBytes) throw new PluginException("vendor_limit", "Vendor package exceeds its byte limit.");
            var digest = TextPatternEngine.Sha256(JsonConvert.SerializeObject(manifest));
            PinVersion(id, version, digest);
            resolution.Visiting.Remove(key);
            retained = new ResolvedVendorPackage { Manifest = manifest, Files = files, Digest = digest };
            resolution.Completed.Add(key, retained); return retained;
        }

        public VendorManifestV1 Import(string sourceRoot)
        {
            using (PluginStore.AcquireRuntimeUse(_paths))
            {
            var manifest = ReadManifest(sourceRoot); Validate(manifest);
            if (List().Any(item => item.Id == manifest.Id && item.Version == manifest.Version))
                throw new PluginException("vendor_exists", "Vendor version already exists; use a new version.");
            // Capture and validate all bytes before creating a destination.
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal); long total = 0;
            foreach (var file in manifest.Files)
            {
                var bytes = PluginPackageFiles.Read(sourceRoot, file.Path);
                if ((total += bytes.Length) > PluginPackageFiles.MaximumPackageBytes) throw new PluginException("vendor_limit", "Vendor package exceeds its byte limit.");
                files.Add(file.Path, bytes);
            }
            foreach (var file in manifest.Files)
                if (PluginPackageFiles.Hash(files[file.Path]) != file.Sha256) throw new PluginException("vendor_changed", "Imported vendor hash mismatch.");
            foreach (var dependency in manifest.Dependencies) Resolve(dependency.Id, dependency.Version);
            if (files.Sum(file => (long)file.Value.Length) > PluginPackageFiles.MaximumPackageBytes) throw new PluginException("vendor_limit", "Vendor package exceeds its byte limit.");
            StorageFileSystem.EnsureRegularDirectory(_directory);
            using (var importLock = new FileStream(PluginPackageFiles.Resolve(_directory, "import-" + manifest.Id + "-" + manifest.Version + ".lock", false), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
            var relative = manifest.Id + "/" + manifest.Version;
            var target = PluginPackageFiles.Resolve(_directory, relative, false);
            if (Directory.Exists(target)) throw new PluginException("vendor_exists", "Vendor destination already exists.");
            Directory.CreateDirectory(target);
            try
            {
                foreach (var file in files)
                {
                    var path = PluginPackageFiles.Resolve(target, file.Key, false);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Write(file.Value, 0, file.Value.Length);
                }
                using (var output = new FileStream(Path.Combine(target, "vendor.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(output, PluginPackageFiles.Utf8)) writer.Write(JsonConvert.SerializeObject(manifest, Formatting.Indented));
                return Resolve(manifest.Id, manifest.Version).Manifest;
            }
            catch (Exception error)
            { throw new PluginException("vendor_import_incomplete", "Inspect incomplete vendor import at " + target + ": " + error.Message); }
            }
            }
        }

        private void PinVersion(string id, string version, string digest)
        {
            var reference = new ResourceRef(ResourceUri.Create("vendor", id, "version", version), "1");
            var current = _authority.GetRevision(Scope, reference);
            if (current != null)
            {
                if (_blobs.ReadText(current.Payload.ToBlobReference()) != digest) throw new PluginException("vendor_version_changed", "An observed vendor version changed. Publish a new version.");
                return;
            }
            var payload = PayloadRef.FromBlob(_blobs.StoreText(digest, "text/plain"));
            _authority.RegisterRevision(Scope, new ResourceRevisionMetadata(reference, payload.Sha256, payload));
            _authority.RegisterView(Scope, new ResourceRevisionView(reference, "vendor-identity", payload.Sha256, payload, ResourceCoverage.Whole()));
            var frozen = _authority.Capture(Scope);
            _authority.Publish(ResourceAuthorityCommit.Create(Scope, frozen.Generation, null,
                new[] { new ResourceHeadChange(reference.Identity, frozen.GetHead(reference.Identity), ResourceHeadState.Known(reference, frozen.Generation + 1, "vendor-identity")) },
                AuthorityCommitReason.CatalogResourcePublication));
        }

        private Supply[] Supplies()
        {
            if (_webRoot == null) return new Supply[0];
            return JsonConvert.DeserializeObject<Supply[]>(PluginPackageFiles.Utf8.GetString(PluginPackageFiles.Read(_webRoot, "vendor-packages.json")), PluginPackageFiles.Json);
        }
        private static VendorManifestV1 ReadManifest(string root)
        { return JsonConvert.DeserializeObject<VendorManifestV1>(PluginPackageFiles.Utf8.GetString(PluginPackageFiles.Read(root, "vendor.json", 128 * 1024)), PluginPackageFiles.Json); }
        public static void Validate(VendorManifestV1 value)
        {
            if (value == null || value.FormatVersion != 1 || !PluginPackageFiles.ValidId(value.Id) || !PluginPackageFiles.ValidVersion(value.Version) ||
                string.IsNullOrWhiteSpace(value.Description) || string.IsNullOrWhiteSpace(value.Source) || string.IsNullOrWhiteSpace(value.License) ||
                value.Files == null || value.Files.Length == 0 || value.Files.Length > 200 || value.Licenses == null || value.Licenses.Length == 0 ||
                value.EntryPoints == null || value.EntryPoints.Length == 0 || value.Dependencies == null || value.Dependencies.Length > 32 ||
                value.Files.Any(file => file == null || !PluginPackageFiles.ValidPath(file.Path) || file.Path.Equals("vendor.json", StringComparison.OrdinalIgnoreCase) || !ChatBlobStore.ValidSha256(file.Sha256)) ||
                value.Files.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.Files.Length ||
                value.Licenses.Any(path => !value.Files.Any(file => file.Path == path)) ||
                value.EntryPoints.Any(entry => entry == null || !Enum.IsDefined(typeof(VendorEntryKind), entry.Kind) || !value.Files.Any(file => file.Path == entry.Path)) ||
                value.Dependencies.Any(item => item == null || !PluginPackageFiles.ValidId(item.Id) || !PluginPackageFiles.ValidVersion(item.Version)))
                throw new PluginException("vendor_invalid", "Invalid vendor manifest.");
        }
    }

    // Admission is deliberately separate from catalog resolution and has no shared
    // 'approved' bit. Export admission does not grant any native/runtime authority.
    public static class VendorAdmission
    {
        public static void RequireHtml(VendorManifestV1 manifest)
        {
            VendorCatalog.Validate(manifest);
            if (manifest.EntryPoints.Any(entry => entry.Kind == VendorEntryKind.Worker || entry.Kind == VendorEntryKind.Module))
                throw new PluginException("html_vendor_denied", "HTML materials admit classic scripts/styles/assets only.");
        }
        public static void RequirePlugin(VendorManifestV1 manifest) { VendorCatalog.Validate(manifest); }
        public static void RequireExport(VendorManifestV1 manifest) { VendorCatalog.Validate(manifest); }
    }
}
