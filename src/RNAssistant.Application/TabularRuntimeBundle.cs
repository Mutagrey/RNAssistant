using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace RNAssistant.Application
{
    // Capture the approved shipped package once. Neither workspace files nor request
    // URLs select executable content. Every response later uses these exact bytes.
    public sealed class TabularRuntimeBundle
    {
        public const int WorkerProtocol = 5;
        public const string EngineRevision = "tabular-core-2";
        public const string EntryPath = "tabular/runtime.html";
        public const int MaximumBytes = 512 * 1024;
        private readonly Dictionary<string, byte[]> _files;
        public string Sha256 { get; private set; }

        internal static readonly string[] ApprovedPaths = new[] {
            "js/vendor/sax.js", "tabular/byte-source.js", "tabular/decimal.js", "tabular/engine.js",
            "tabular/executor.js", "tabular/expression.js", "tabular/json.js", "tabular/native-runtime.js",
            EntryPath, "tabular/selectors.js", "tabular/snapshot.js", "tabular/sources.js", "tabular/template.js",
            "tabular/transform.js", "tabular/worker.js", "tabular/xlsx-reader.js", "tabular/xlsx-zip.js"
        };

        private sealed class Manifest
        {
            [JsonProperty("formatVersion", Required = Required.Always)] public int FormatVersion { get; set; }
            [JsonProperty("workerProtocol", Required = Required.Always)] public int WorkerProtocol { get; set; }
            [JsonProperty("engineRevision", Required = Required.Always)] public string EngineRevision { get; set; }
            [JsonProperty("entry", Required = Required.Always)] public string Entry { get; set; }
            [JsonProperty("files", Required = Required.Always)] public FilePin[] Files { get; set; }
        }
        private sealed class FilePin
        {
            [JsonProperty("path", Required = Required.Always)] public string Path { get; set; }
            [JsonProperty("bytes", Required = Required.Always)] public int Bytes { get; set; }
            [JsonProperty("sha256", Required = Required.Always)] public string Sha256 { get; set; }
        }

        private TabularRuntimeBundle(Dictionary<string, byte[]> files, string sha256)
        { _files = files; Sha256 = sha256; }

        // webRoot is application installation configuration, never workspace/UI intent.
        public static TabularRuntimeBundle Capture(string webRoot)
        {
            try
            {
                var root = Path.GetFullPath(webRoot);
                var lockBytes = ReadBounded(root, "tabular/runtime-lock.json", 8192);
                if (Hash(lockBytes) != TabularRuntimePin.Sha256) throw Invalid();
                var text = new UTF8Encoding(false, true).GetString(lockBytes);
                var manifest = JsonConvert.DeserializeObject<Manifest>(text, new JsonSerializerSettings {
                    MissingMemberHandling = MissingMemberHandling.Error, MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
                    CheckAdditionalContent = true, MaxDepth = 4 });
                // This is a generated, canonical package lock. Refusing alternate
                // encodings also rejects duplicate fields, comments and coercions.
                if (manifest == null || text != JsonConvert.SerializeObject(manifest, Formatting.Indented).Replace("\r\n", "\n") + "\n" ||
                    manifest.FormatVersion != 1 || manifest.WorkerProtocol != WorkerProtocol ||
                    manifest.EngineRevision != EngineRevision || manifest.Entry != EntryPath ||
                    manifest.Files == null || manifest.Files.Length != ApprovedPaths.Length)
                    throw Invalid();
                var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                var total = 0;
                for (var index = 0; index < ApprovedPaths.Length; index++)
                {
                    var pin = manifest.Files[index];
                    if (pin == null || pin.Path != ApprovedPaths[index] || pin.Bytes < 1 || pin.Bytes > MaximumBytes - total ||
                        pin.Sha256 == null || pin.Sha256.Length != 64 || pin.Sha256.Any(c => !(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')))
                        throw Invalid();
                    var bytes = ReadBounded(root, pin.Path, pin.Bytes);
                    if (bytes.Length != pin.Bytes || Hash(bytes) != pin.Sha256) throw Invalid();
                    files.Add(pin.Path, bytes); total += bytes.Length;
                }
                return new TabularRuntimeBundle(files, Hash(lockBytes));
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException ||
                error is JsonException || error is ArgumentException || error is NotSupportedException)
            { throw Invalid(); }
        }

        internal Stream OpenAsset(string path)
        {
            byte[] bytes;
            return path != null && _files.TryGetValue(path, out bytes) ? new MemoryStream(bytes, false) : null;
        }

        private static byte[] ReadBounded(string root, string relative, int maximumBytes)
        {
            // Reject linked package components. Approved relative names are fixed
            // above; no user-controlled path is ever passed to the filesystem.
            var current = root;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Invalid();
            foreach (var part in relative.Split('/'))
            {
                current = Path.Combine(current, part);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Invalid();
            }
            using (var input = new FileStream(current, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length < 1 || input.Length > maximumBytes) throw Invalid();
                var bytes = new byte[(int)input.Length];
                var offset = 0;
                while (offset < bytes.Length)
                {
                    var count = input.Read(bytes, offset, bytes.Length - offset);
                    if (count == 0) throw Invalid();
                    offset += count;
                }
                if (input.ReadByte() != -1) throw Invalid();
                return bytes;
            }
        }

        private static string Hash(byte[] bytes)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        private static WorkspaceApplicationException Invalid()
        { return new WorkspaceApplicationException("tabular_bundle_invalid", "Пакет исполнения Data Templates отсутствует или не соответствует закреплённым файлам."); }
    }
}
