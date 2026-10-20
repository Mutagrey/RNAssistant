using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace RNAssistant.Core.Storage
{
    public sealed class PluginIconRecord
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("formatVersion")] public int FormatVersion { get; set; } = 2;
        [JsonProperty("svg")] public string Svg { get; set; }
        [JsonProperty("sha256")] public string Sha256 { get; set; }
    }

    // User-owned icon assets live outside the runtime catalog. Publications copy their
    // bytes, so removing a library entry never changes an existing plugin.
    public sealed class PluginIconLibrary
    {
        private readonly string _directory;
        public PluginIconLibrary(AppDataPaths paths) { _directory = paths.PluginIconDirectory; }

        public IReadOnlyList<PluginIconRecord> List()
        {
            StorageFileSystem.EnsureRegularDirectory(_directory);
            return Directory.GetFiles(_directory, "icon-*.json")
                .Select(Read).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        public PluginIconRecord Get(string id)
        {
            var path = PathFor(id);
            if (!File.Exists(path)) throw new PluginException("plugin_icon_missing", "Saved icon is unavailable.");
            return Read(path);
        }

        public PluginIconRecord Save(string name, byte[] svg)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 120 || name.Any(char.IsControl))
                throw new PluginException("plugin_icon_invalid", "Icon name must be 1–120 characters.");
            var normalized = PluginPackageFiles.ReadIconSvg(svg);
            var digest = PluginPackageFiles.Hash(PluginPackageFiles.Utf8.GetBytes(normalized));
            var id = "icon-" + digest;
            var path = PathFor(id);
            if (File.Exists(path)) return Read(path);
            var record = new PluginIconRecord { Id = id, Name = name.Trim(), Svg = normalized, Sha256 = digest };
            var bytes = PluginPackageFiles.Utf8.GetBytes(JsonConvert.SerializeObject(record));
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    output.Write(bytes, 0, bytes.Length);
                File.Move(temporary, path);
            }
            catch (IOException) when (File.Exists(path)) { return Read(path); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return Read(path);
        }

        public void Delete(string id)
        {
            var path = PathFor(id);
            if (!File.Exists(path)) throw new PluginException("plugin_icon_missing", "Saved icon is unavailable.");
            Read(path);
            File.Delete(path);
            if (File.Exists(path)) throw new PluginException("plugin_icon_delete_unknown", "Icon deletion could not be verified.");
        }

        private string PathFor(string id)
        {
            if (id == null || !Regex.IsMatch(id, "^icon-[a-f0-9]{64}$", RegexOptions.CultureInvariant))
                throw new PluginException("plugin_icon_protected", "Built-in icons cannot be removed from the library.");
            StorageFileSystem.EnsureRegularDirectory(_directory);
            return Path.Combine(_directory, id + ".json");
        }

        private PluginIconRecord Read(string path)
        {
            if (StorageFileSystem.IsReparsePoint(path) || new FileInfo(path).Length > PluginIconSvg.MaximumBytes * 8)
                throw new PluginException("plugin_icon_invalid", "Saved icon file is invalid.");
            PluginIconRecord item;
            try
            {
                var json = File.ReadAllText(path, PluginPackageFiles.Utf8);
                var stored = Newtonsoft.Json.Linq.JObject.Parse(json);
                if (stored["formatVersion"] == null && stored["paths"] != null)
                {
                    var paths = stored["paths"].ToObject<string[]>();
                    if (paths == null || paths.Length == 0) throw new PluginException("plugin_icon_invalid", "Saved outline geometry is missing.");
                    var oldId = "icon-" + PluginPackageFiles.Hash(PluginPackageFiles.Utf8.GetBytes(string.Join("\n", paths)));
                    if ((string)stored["id"] != oldId || Path.GetFileName(path) != oldId + ".json")
                        throw new PluginException("plugin_icon_invalid", "Saved outline icon identity is invalid.");
                    var svg = PluginIconSvg.FromLegacyPaths(paths);
                    item = new PluginIconRecord { Id = oldId, Name = (string)stored["name"], Svg = svg,
                        Sha256 = PluginPackageFiles.Hash(PluginPackageFiles.Utf8.GetBytes(svg)) };
                    Validate(item, path);
                    return item;
                }
                item = stored.ToObject<PluginIconRecord>(JsonSerializer.Create(PluginPackageFiles.Json));
            }
            catch (JsonException error) { throw new PluginException("plugin_icon_invalid", error.Message); }
            Validate(item, path);
            return item;
        }
        private static void Validate(PluginIconRecord item, string path)
        {
            if (item == null || item.FormatVersion != 2 || item.Id == null || !Regex.IsMatch(item.Id, "^icon-[a-f0-9]{64}$") ||
                Path.GetFileName(path) != item.Id + ".json" || string.IsNullOrWhiteSpace(item.Name) ||
                item.Name.Length > 120 || item.Name.Any(char.IsControl) || item.Svg == null ||
                item.Sha256 != PluginPackageFiles.Hash(PluginPackageFiles.Utf8.GetBytes(item.Svg)) ||
                item.Svg != PluginPackageFiles.ReadIconSvg(PluginPackageFiles.Utf8.GetBytes(item.Svg)))
                throw new PluginException("plugin_icon_invalid", "Saved SVG contents do not match their digest.");
        }

    }
}
