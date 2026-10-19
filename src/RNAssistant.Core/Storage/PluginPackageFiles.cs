using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Storage
{
    public sealed class PluginException : InvalidOperationException
    {
        public string Code { get; private set; }
        public PluginException(string code, string message) : base(message) { Code = code; }
    }

    public static class PluginPackageFiles
    {
        public const int MaximumFiles = 512, MaximumFileBytes = 8 * 1024 * 1024, MaximumPackageBytes = 32 * 1024 * 1024;
        public static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        public static readonly JsonSerializerSettings Json = new JsonSerializerSettings
        { MissingMemberHandling = MissingMemberHandling.Error, MaxDepth = 32 };

        public static bool ValidId(string value)
        { return value != null && Regex.IsMatch(value, "^[a-z][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant); }
        public static bool ValidVersion(string value)
        { return value != null && Regex.IsMatch(value, "^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[a-zA-Z0-9.-]+)?$", RegexOptions.CultureInvariant); }
        public static bool ValidPath(string value)
        { return !string.IsNullOrEmpty(value) && value.Length <= 512 && value.IndexOfAny(new[] { '\\', ':', '?', '#', '%', '\0' }) < 0 &&
            !value.Any(char.IsControl) && value.Split('/').All(part => part.Length > 0 && part != "." && part != ".." &&
                !part.EndsWith(".", StringComparison.Ordinal) && !part.EndsWith(" ", StringComparison.Ordinal) &&
                !new[] { ".rnassistant", ".host", ".data", ".result" }.Contains(part, StringComparer.OrdinalIgnoreCase) &&
                !Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase)); }

        public static string Resolve(string root, string relative, bool existing = true)
        {
            if (!ValidPath(relative)) throw new PluginException("package_path_invalid", "Invalid package member: " + relative);
            root = Path.GetFullPath(root);
            for (var ancestor = new DirectoryInfo(root); ancestor != null; ancestor = ancestor.Parent)
                if (ancestor.Exists && StorageFileSystem.IsReparsePoint(ancestor.FullName))
                    throw new PluginException("package_link", "Package paths cannot contain links.");
            var current = root;
            foreach (var part in relative.Split('/'))
            {
                current = Path.Combine(current, part);
                if ((File.Exists(current) || Directory.Exists(current)) && StorageFileSystem.IsReparsePoint(current))
                    throw new PluginException("package_link", "Package paths cannot contain links.");
            }
            if (existing && !File.Exists(current)) throw new PluginException("package_file_missing", "Missing package member: " + relative);
            return current;
        }

        public static byte[] Read(string root, string relative, int maximum = MaximumFileBytes)
        {
            var path = Resolve(root, relative);
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (file.Length > maximum) throw new PluginException("package_limit", "Package member exceeds its limit: " + relative);
                var bytes = new byte[(int)file.Length]; var read = 0;
                while (read < bytes.Length) { var count = file.Read(bytes, read, bytes.Length - read); if (count == 0) throw new EndOfStreamException(); read += count; }
                return bytes;
            }
        }

        public static bool IconIsSvgPath(string icon)
        { return icon != null && icon.EndsWith(".svg", StringComparison.OrdinalIgnoreCase); }

        public static string[] ReadIconPaths(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > 4096)
                throw new PluginException("plugin_icon_invalid", "The SVG icon must contain 1–4096 UTF-8 bytes.");
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                    MaxCharactersInDocument = 4096 };
                using (var reader = XmlReader.Create(new StringReader(Utf8.GetString(bytes)), settings))
                {
                    var root = XElement.Load(reader);
                    if (root.Name.LocalName != "svg" ||
                        root.Name.NamespaceName != "" && root.Name.NamespaceName != "http://www.w3.org/2000/svg" ||
                        (string)root.Attribute("viewBox") != "0 0 24 24" ||
                        root.Attributes().Any(attribute => attribute.Name.LocalName != "viewBox" &&
                            !(attribute.IsNamespaceDeclaration && attribute.Value == "http://www.w3.org/2000/svg")) ||
                        root.Nodes().Any(node => !(node is XElement) && !(node is XText && string.IsNullOrWhiteSpace(((XText)node).Value))))
                        throw new PluginException("plugin_icon_invalid", "Use a 24×24 SVG with path elements only.");
                    var paths = root.Elements().ToArray();
                    if (paths.Length == 0 || paths.Length > 6 || paths.Any(path =>
                        path.Name.LocalName != "path" || path.HasElements || path.Attributes().Count() != 1 ||
                        path.Attributes().First().Name.LocalName != "d" || path.Attributes().First().Name.NamespaceName != "" ||
                        string.IsNullOrWhiteSpace((string)path.Attribute("d")) || ((string)path.Attribute("d")).Length > 1024 ||
                        !Regex.IsMatch((string)path.Attribute("d"), @"^[MmLlHhVvCcSsQqTtAaZz0-9\s,.+\-]+$", RegexOptions.CultureInvariant)))
                        throw new PluginException("plugin_icon_invalid", "The SVG icon supports 1–6 safe path elements only.");
                    return paths.Select(path => (string)path.Attribute("d")).ToArray();
                }
            }
            catch (Exception error) when (error is XmlException || error is DecoderFallbackException)
            { throw new PluginException("plugin_icon_invalid", "The SVG icon is not valid UTF-8 XML."); }
        }

        public static PluginManifestV1 ParseManifest(string json)
        {
            PluginManifestV1 manifest;
            try { manifest = JsonConvert.DeserializeObject<PluginManifestV1>(json, Json); }
            catch (JsonException error) { throw new PluginException("plugin_manifest_invalid", error.Message); }
            if (manifest == null || manifest.FormatVersion != 1 || manifest.HostProtocol != 1 || !ValidId(manifest.Id) ||
                !ValidVersion(manifest.Version) || string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 120 ||
                manifest.Files == null || manifest.Files.Length == 0 || manifest.Files.Length > MaximumFiles ||
                manifest.Files.Any(file => !ValidPath(file)) || manifest.Files.Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Length ||
                !manifest.Files.Contains("plugin.json") || !manifest.Files.Contains(manifest.Entry) || !manifest.Entry.EndsWith(".html", StringComparison.Ordinal) ||
                manifest.Icon != null && !(IconIsSvgPath(manifest.Icon)
                    ? ValidPath(manifest.Icon) && manifest.Files.Contains(manifest.Icon)
                    : Regex.IsMatch(manifest.Icon, "^[A-Za-z][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)) ||
                manifest.Capabilities == null || manifest.Capabilities.Any(value => value != "project.read" && value != "project.write") ||
                manifest.Commands == null || manifest.Commands.Length > 32 || manifest.Vendors == null || manifest.Vendors.Length > 32)
                throw new PluginException("plugin_manifest_invalid", "Invalid or unsupported plugin manifest/protocol.");
            if (manifest.Commands.Select(command => command?.Id).Distinct(StringComparer.Ordinal).Count() != manifest.Commands.Length)
                throw new PluginException("plugin_command_invalid", "Duplicate command IDs.");
            foreach (var command in manifest.Commands)
            {
                Newtonsoft.Json.Linq.JObject schema; string error;
                if (command == null || !ValidId(command.Id) || string.IsNullOrWhiteSpace(command.Description) || command.Description.Length > 500 ||
                    !manifest.Files.Contains(command.Entry) || !command.Entry.EndsWith(".js", StringComparison.Ordinal) ||
                    command.ArgumentsSchema == null || command.ResultSchema == null ||
                    command.WritesData && !manifest.Capabilities.Contains("project.write"))
                    throw new PluginException("plugin_command_invalid", "Invalid command contract.");
                if (!ToolSchemaSupport.TryParse(new ToolCatalogEntry { ArgumentSchemaJson = command.ArgumentsSchema.ToString() }, out schema, out error) ||
                    !ToolSchemaSupport.TryParse(new ToolCatalogEntry { ArgumentSchemaJson = command.ResultSchema.ToString() }, out schema, out error))
                    throw new PluginException("plugin_command_invalid", command.Id + ": " + error);
            }
            foreach (var vendor in manifest.Vendors)
                if (vendor == null || !ValidId(vendor.Id) || !ValidVersion(vendor.Version) || !ValidPath(vendor.Mount))
                    throw new PluginException("plugin_vendor_invalid", "Dependencies require an exact version and package-relative mount.");
            if (manifest.Vendors.Select(vendor => vendor.Id).Distinct(StringComparer.Ordinal).Count() != manifest.Vendors.Length)
                throw new PluginException("plugin_vendor_invalid", "Duplicate vendor dependencies.");
            return manifest;
        }

        public static string Hash(byte[] value)
        { using (var hash = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(value)).Replace("-", "").ToLowerInvariant(); }
    }
}
