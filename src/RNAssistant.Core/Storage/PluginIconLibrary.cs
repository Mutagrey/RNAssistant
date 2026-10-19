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
        [JsonProperty("paths")] public string[] Paths { get; set; }
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
            var paths = PluginPackageFiles.ReadIconPaths(svg);
            var id = "icon-" + PluginPackageFiles.Hash(PluginPackageFiles.Utf8.GetBytes(string.Join("\n", paths)));
            var path = PathFor(id);
            if (File.Exists(path)) return Read(path);
            var record = new PluginIconRecord { Id = id, Name = name.Trim(), Paths = paths };
            var normalized = PluginPackageFiles.Utf8.GetBytes(Svg(record));
            PluginPackageFiles.ReadIconPaths(normalized);
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

        public static string Svg(PluginIconRecord record)
        {
            return "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\">" +
                string.Concat(record.Paths.Select(path => "<path d=\"" + path + "\"/>")) + "</svg>";
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
            if (StorageFileSystem.IsReparsePoint(path) || new FileInfo(path).Length > 8192)
                throw new PluginException("plugin_icon_invalid", "Saved icon file is invalid.");
            PluginIconRecord item;
            try { item = JsonConvert.DeserializeObject<PluginIconRecord>(File.ReadAllText(path, PluginPackageFiles.Utf8)); }
            catch (JsonException error) { throw new PluginException("plugin_icon_invalid", error.Message); }
            if (item == null || Path.GetFileName(path) != item.Id + ".json" || string.IsNullOrWhiteSpace(item.Name) ||
                item.Name.Length > 120 || item.Name.Any(char.IsControl) || item.Paths == null ||
                item.Id != "icon-" + PluginPackageFiles.Hash(PluginPackageFiles.Utf8.GetBytes(string.Join("\n", item.Paths))) ||
                !item.Paths.SequenceEqual(PluginPackageFiles.ReadIconPaths(PluginPackageFiles.Utf8.GetBytes(Svg(item)))))
                throw new PluginException("plugin_icon_invalid", "Saved icon contents do not match their identity.");
            return item;
        }
    }
}
