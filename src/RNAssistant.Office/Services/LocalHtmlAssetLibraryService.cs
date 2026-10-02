using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office.Services
{
    // User-maintained source packages. Import copies verified text into the existing
    // document-owned HTML workspace; this directory is never a workspace store.
    internal sealed class LocalHtmlAssetLibraryService
    {
        private const int MaxPackages = 300;
        private const int MaxFiles = 20;
        private const int MaxManifestBytes = 16384;
        private const int MaxFileBytes = 1000000;
        private const int MaxPackageBytes = 3000000;
        private static readonly Regex IdPattern = new Regex("^[a-z][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);
        private static readonly Regex VersionPattern = new Regex("^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[a-zA-Z0-9.-]+)?$", RegexOptions.CultureInvariant);
        private static readonly Regex SegmentPattern = new Regex("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant);
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private readonly string _directory;
        private readonly ChatBlobStore _payloads;

        internal LocalHtmlAssetLibraryService(AppDataPaths paths, ChatBlobStore payloads)
        {
            if (paths == null) throw new ArgumentNullException(nameof(paths));
            _payloads = payloads ?? throw new ArgumentNullException(nameof(payloads));
            _directory = Path.Combine(paths.Root, "assets");
        }

        internal HtmlAssetCatalogPage List(string query, int limit)
        {
            EnsureLibraryRoot();
            if (limit < 1 || limit > 50)
                throw new InvalidOperationException("Asset catalog limit is out of range.");
            query = (query ?? string.Empty).Trim();
            if (query.Length > 100)
                throw new InvalidOperationException("Asset catalog query is too long.");
            var matches = new List<Tuple<string, string>>();
            var items = new List<HtmlAssetCatalogItem>();
            var errors = new List<string>();
            var skipped = 0;
            var candidates = new List<Tuple<string, string>>();
            foreach (var idDirectory in Directory.GetDirectories(_directory).OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
            {
                if (Path.GetFileName(idDirectory).StartsWith(".staging-", StringComparison.Ordinal)) continue;
                if (!IsRegularDirectory(idDirectory)) continue;
                foreach (var versionDirectory in Directory.GetDirectories(idDirectory).OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
                {
                    if (!IsRegularDirectory(versionDirectory)) continue;
                    candidates.Add(Tuple.Create(Path.GetFileName(idDirectory), Path.GetFileName(versionDirectory)));
                    if (candidates.Count > MaxPackages)
                        throw new InvalidOperationException("Asset library exceeds 300 packages.");
                }
            }
            foreach (var candidate in candidates)
            {
                try
                {
                    byte[] ignoredBytes;
                    var manifest = ReadManifest(candidate.Item1, candidate.Item2, out ignoredBytes);
                    if (query.Length == 0 ||
                        (candidate.Item1 + " " + candidate.Item2 + " " + manifest.Title + " " +
                         manifest.Description + " " + manifest.Usage)
                            .IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                        matches.Add(candidate);
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is IOException ||
                    ex is UnauthorizedAccessException || ex is JsonException || ex is DecoderFallbackException)
                {
                    skipped++;
                    if (errors.Count < 8)
                        errors.Add(candidate.Item1 + "/" + candidate.Item2 + ": " +
                            (ex is InvalidOperationException ? ex.Message : ex.GetType().Name));
                }
            }
            var examined = 0;
            foreach (var candidate in matches)
            {
                if (items.Count > limit) break;
                examined++;
                try
                {
                    var package = Load(candidate.Item1, candidate.Item2);
                    items.Add(new HtmlAssetCatalogItem
                    {
                        Id = package.Id, Version = package.Version, Kind = package.Kind,
                        Title = package.Title, Description = package.Description,
                        Usage = package.Usage, License = package.License,
                        Provenance = package.Provenance, Revision = package.Revision,
                        FileCount = package.Files.Count,
                        Files = package.Files.Take(query.Length == 0 ? 8 : MaxFiles)
                            .Select(file => file.Path).ToArray()
                    });
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is IOException ||
                    ex is UnauthorizedAccessException || ex is JsonException || ex is DecoderFallbackException)
                {
                    skipped++;
                    if (errors.Count < 8)
                        errors.Add(candidate.Item1 + "/" + candidate.Item2 + ": " +
                            (ex is InvalidOperationException ? ex.Message : ex.GetType().Name));
                }
            }
            return new HtmlAssetCatalogPage
            {
                HasMore = items.Count > limit || examined < matches.Count,
                Items = items.Take(limit).ToArray(),
                Skipped = skipped,
                Errors = errors.ToArray()
            };
        }

        internal HtmlAssetPackage Load(string id, string version)
        {
            byte[] manifestBytes;
            var manifest = ReadManifest(id, version, out manifestBytes);
            var packageDirectory = Path.Combine(_directory, id, version);
            var files = new List<HtmlAssetFile>();
            var totalBytes = manifestBytes.Length;
            using (var revisionBytes = new MemoryStream())
            {
                WriteHashPart(revisionBytes, manifestBytes);
                foreach (var relativePath in manifest.Files)
                {
                    var filePath = packageDirectory;
                    foreach (var segment in relativePath.Split('/'))
                    {
                        filePath = Path.Combine(filePath, segment);
                        if (Directory.Exists(filePath) && !IsRegularDirectory(filePath))
                            throw new InvalidOperationException("Linked asset directory is unsupported: " + relativePath);
                    }
                    var bytes = ReadRegularFile(filePath, MaxFileBytes);
                    totalBytes += bytes.Length;
                    if (totalBytes > MaxPackageBytes)
                        throw new InvalidOperationException("HTML asset package is too large.");
                    var content = StrictUtf8.GetString(bytes);
                    if (content.Length > 300000)
                        throw new InvalidOperationException("HTML asset file exceeds the workspace limit: " + relativePath);
                    files.Add(new HtmlAssetFile { Path = relativePath, Content = content,
                        Kind = relativePath.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ? "script" :
                            relativePath.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ? "css" : "html" });
                    WriteHashPart(revisionBytes, StrictUtf8.GetBytes(relativePath));
                    WriteHashPart(revisionBytes, bytes);
                }
                using (var sha = SHA256.Create())
                {
                    return new HtmlAssetPackage
                    {
                        Id = id, Version = version, Kind = manifest.Kind,
                        Title = manifest.Title.Trim(),
                        Description = manifest.Description.Trim(), Usage = manifest.Usage,
                        License = manifest.License, Provenance = manifest.Provenance,
                        Revision = BitConverter.ToString(sha.ComputeHash(revisionBytes.ToArray()))
                            .Replace("-", string.Empty).ToLowerInvariant(),
                        Files = files
                    };
                }
            }
        }

        private HtmlAssetManifest ReadManifest(string id, string version, out byte[] manifestBytes)
        {
            EnsureLibraryRoot();
            if (!IdPattern.IsMatch(id ?? string.Empty) || !VersionPattern.IsMatch(version ?? string.Empty))
                throw new InvalidOperationException("Invalid asset package id/version.");
            var idDirectory = Path.Combine(_directory, id);
            var packageDirectory = Path.Combine(idDirectory, version);
            if (!IsRegularDirectory(idDirectory) ||
                !IsRegularDirectory(packageDirectory))
                throw new InvalidOperationException("Asset package was not found or uses a linked directory.");
            var manifestPath = Path.Combine(packageDirectory, "manifest.json");
            manifestBytes = ReadRegularFile(manifestPath, MaxManifestBytes);
            var manifest = JsonConvert.DeserializeObject<HtmlAssetManifest>(StrictUtf8.GetString(manifestBytes));
            if (manifest == null || manifest.SchemaVersion != 1 || manifest.Kind != "html-workspace" ||
                manifest.Offline != true || manifest.Id != id || manifest.Version != version ||
                string.IsNullOrWhiteSpace(manifest.Title) || manifest.Title.Length > 100 ||
                string.IsNullOrWhiteSpace(manifest.Description) || manifest.Description.Length > 240 ||
                manifest.Files == null || manifest.Files.Count < 1 || manifest.Files.Count > MaxFiles ||
                (manifest.License ?? string.Empty).Length > 100 ||
                (manifest.Usage ?? string.Empty).Length > 600 ||
                (manifest.Provenance ?? string.Empty).Length > 200)
                throw new InvalidOperationException("Invalid HTML asset manifest contract.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var relativePath in manifest.Files)
            {
                ValidateRelativePath(relativePath);
                if (!names.Add(relativePath))
                    throw new InvalidOperationException("Duplicate asset file path: " + relativePath);
            }
            return manifest;
        }

        internal HtmlAssetPackage Publish(string id, string version, string title,
            string description, string usage, string license, string provenance,
            IReadOnlyList<HtmlAssetFile> files,
            Action markDispatchPossible)
        {
            EnsureLibraryRoot();
            if (!IdPattern.IsMatch(id ?? string.Empty) || !VersionPattern.IsMatch(version ?? string.Empty) ||
                string.IsNullOrWhiteSpace(title) || title.Length > 100 ||
                string.IsNullOrWhiteSpace(description) || description.Length > 240 ||
                (usage ?? string.Empty).Length > 600 ||
                (license ?? string.Empty).Length > 100 || files == null ||
                (provenance ?? string.Empty).Length > 200 ||
                files.Count < 1 || files.Count > MaxFiles)
                throw new InvalidOperationException("Invalid HTML asset package metadata.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var totalBytes = 0;
            foreach (var file in files)
            {
                ValidateRelativePath(file?.Path);
                if (!names.Add(file.Path))
                    throw new InvalidOperationException("Duplicate HTML asset path: " + file.Path);
                var bytes = StrictUtf8.GetBytes(file.Content ?? string.Empty);
                totalBytes += bytes.Length;
                if (bytes.Length > MaxFileBytes || (file.Content ?? string.Empty).Length > 300000 ||
                    totalBytes > MaxPackageBytes)
                    throw new InvalidOperationException("HTML asset package exceeds its size limit.");
            }
            var idDirectory = Path.Combine(_directory, id);
            var destination = Path.Combine(idDirectory, version);
            if (Directory.Exists(destination) || File.Exists(destination))
                throw new InvalidOperationException("Asset package version already exists; choose a new version.");
            if (Directory.Exists(idDirectory) && !IsRegularDirectory(idDirectory))
                throw new InvalidOperationException("Asset package directory is linked.");
            var staging = Path.Combine(_directory, ".staging-" + Guid.NewGuid().ToString("N"));
            EnsureRegularDirectory(staging);
            try
            {
                foreach (var file in files)
                {
                    var path = staging;
                    var segments = file.Path.Split('/');
                    for (var index = 0; index < segments.Length - 1; index++)
                    {
                        path = Path.Combine(path, segments[index]);
                        EnsureRegularDirectory(path);
                    }
                    File.WriteAllText(Path.Combine(path, segments[segments.Length - 1]),
                        file.Content ?? string.Empty, StrictUtf8);
                }
                var manifest = new HtmlAssetManifest
                {
                    SchemaVersion = 1, Id = id, Version = version,
                    Kind = "html-workspace", Title = title.Trim(),
                    Description = description.Trim(), Usage = usage,
                    License = license, Provenance = provenance,
                    Offline = true, Files = files.Select(file => file.Path).ToList()
                };
                File.WriteAllText(Path.Combine(staging, "manifest.json"),
                    JsonConvert.SerializeObject(manifest, Formatting.Indented), StrictUtf8);
                if (new FileInfo(Path.Combine(staging, "manifest.json")).Length > MaxManifestBytes)
                    throw new InvalidOperationException("HTML asset manifest is too large.");
                if (markDispatchPossible != null) markDispatchPossible();
                EnsureRegularDirectory(idDirectory);
                Directory.Move(staging, destination);
                return Load(id, version);
            }
            finally
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
            }
        }

        internal ResourceMutationReadBack CapturePublishedPackage(HtmlAssetPackage expected)
        {
            if (expected == null) throw new ArgumentNullException(nameof(expected));
            var current = Load(expected.Id, expected.Version);
            if (!string.Equals(current.Revision, expected.Revision, StringComparison.Ordinal))
                throw new InvalidOperationException("Published asset package changed before read-back.");
            var summary = new HtmlAssetCatalogItem
            {
                Id = current.Id, Version = current.Version, Kind = current.Kind,
                Title = current.Title,
                Description = current.Description, Usage = current.Usage,
                License = current.License, Provenance = current.Provenance,
                Revision = current.Revision,
                FileCount = current.Files.Count,
                Files = current.Files.Select(file => file.Path).ToArray()
            };
            var payload = PayloadRef.FromBlob(_payloads.StoreText(
                JsonConvert.SerializeObject(new HtmlAssetPublicationRecord
                { Package = summary, ContentRevision = current.Revision }),
                "application/json"));
            return new ResourceMutationReadBack(
                new ResourceIdentity(ResourceUri.Create("catalog", "html-assets",
                    current.Id, current.Version)), true, "catalog-state",
                payload.Sha256, payload);
        }

        private static void ValidateRelativePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > 200 || path.IndexOf('\\') >= 0 ||
                !new[] { ".js", ".css", ".html", ".htm" }.Any(extension =>
                    path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) ||
                path.Split('/').Any(segment => segment == "." || segment == ".." ||
                    !SegmentPattern.IsMatch(segment)))
                throw new InvalidOperationException("Invalid HTML asset file path: " + path);
        }

        private static byte[] ReadRegularFile(string path, int maxBytes)
        {
            if (!IsRegularFile(path))
                throw new InvalidOperationException("Asset file is missing or linked: " + Path.GetFileName(path));
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > maxBytes)
                    throw new InvalidOperationException("Asset file is too large: " + Path.GetFileName(path));
                var bytes = new byte[(int)stream.Length];
                var offset = 0;
                while (offset < bytes.Length)
                {
                    var count = stream.Read(bytes, offset, bytes.Length - offset);
                    if (count == 0) throw new IOException("Asset file changed during read.");
                    offset += count;
                }
                if (stream.ReadByte() != -1)
                    throw new IOException("Asset file changed during read.");
                return bytes;
            }
        }

        private static void EnsureRegularDirectory(string path)
        {
            Directory.CreateDirectory(path);
            if (!IsRegularDirectory(path))
                throw new IOException("Asset directory is not a regular directory: " + path);
        }

        private void EnsureLibraryRoot()
        {
            if (!Directory.Exists(_directory)) EnsureRegularDirectory(_directory);
            if (!IsRegularDirectory(_directory))
                throw new InvalidOperationException("Local asset library root is unavailable or linked.");
        }

        private static bool IsRegularDirectory(string path)
        {
            try
            {
                var attributes = File.GetAttributes(path);
                return (attributes & FileAttributes.Directory) != 0 &&
                    (attributes & FileAttributes.ReparsePoint) == 0;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private static bool IsRegularFile(string path)
        {
            try
            {
                var attributes = File.GetAttributes(path);
                return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private static void WriteHashPart(Stream stream, byte[] bytes)
        {
            var length = BitConverter.GetBytes(bytes.Length);
            stream.Write(length, 0, length.Length);
            stream.Write(bytes, 0, bytes.Length);
        }

        private sealed class HtmlAssetManifest
        {
            [JsonProperty("schemaVersion")] public int SchemaVersion { get; set; }
            [JsonProperty("id")] public string Id { get; set; }
            [JsonProperty("version")] public string Version { get; set; }
            [JsonProperty("kind")] public string Kind { get; set; }
            [JsonProperty("title")] public string Title { get; set; }
            [JsonProperty("description")] public string Description { get; set; }
            [JsonProperty("usage")] public string Usage { get; set; }
            [JsonProperty("license")] public string License { get; set; }
            [JsonProperty("provenance")] public string Provenance { get; set; }
            [JsonProperty("offline")] public bool? Offline { get; set; }
            [JsonProperty("files")] public List<string> Files { get; set; }
        }
    }

    internal sealed class HtmlAssetFile
    {
        public string Path { get; set; }
        public string Kind { get; set; }
        public string Content { get; set; }
    }

    internal sealed class HtmlAssetPackage
    {
        public string Id { get; set; }
        public string Version { get; set; }
        public string Kind { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string Usage { get; set; }
        public string License { get; set; }
        public string Provenance { get; set; }
        public string Revision { get; set; }
        public List<HtmlAssetFile> Files { get; set; }
    }

    internal sealed class HtmlAssetCatalogItem
    {
        public string Id { get; set; }
        public string Version { get; set; }
        public string Kind { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string Usage { get; set; }
        public string License { get; set; }
        public string Provenance { get; set; }
        [JsonIgnore] public string Revision { get; set; }
        public int FileCount { get; set; }
        public string[] Files { get; set; }
    }

    internal sealed class HtmlAssetCatalogPage
    {
        public bool HasMore { get; set; }
        public HtmlAssetCatalogItem[] Items { get; set; }
        public int Skipped { get; set; }
        public string[] Errors { get; set; }
    }

    internal sealed class HtmlAssetPublicationRecord
    {
        public HtmlAssetCatalogItem Package { get; set; }
        public string ContentRevision { get; set; }
    }
}
