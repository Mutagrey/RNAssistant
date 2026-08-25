using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    public sealed class ToolStore
    {
        private const long MaxToolMetadataFileBytes = 2100000;
        private const long MaxPipelineFileBytes = 1100000;
        private const long MaxReadmeFileBytes = 2100000;
        private const long MaxComponentFileBytes = 4100000;
        private const long MaxComponentPackageBytes = 8100000;
        private const int MaxComponents = 50;
        private readonly AppDataPaths _paths;
        private readonly JsonFileStore _json;

        public ToolStore(AppDataPaths paths)
        {
            _paths = paths;
            _json = new JsonFileStore();
        }

        public List<ToolDefinition> Load()
        {
            var result = new List<ToolDefinition>();
            if (!Directory.Exists(_paths.ToolsDirectory))
            {
                return result;
            }

            foreach (var file in StorageFileSystem.GetFilesRecursive(_paths.ToolsDirectory, "tool.json"))
            {
                ToolDefinition tool;
                if (!TryLoadMetadata(file, out tool)) continue;
                if (tool == null || string.IsNullOrWhiteSpace(tool.Id))
                {
                    continue;
                }

                var directory = Path.GetDirectoryName(file);
                tool.BuiltIn = false;
                tool.Executor = string.IsNullOrWhiteSpace(tool.Executor) ? "pipeline" : tool.Executor;
                tool.ArgumentSchemaJson = string.IsNullOrWhiteSpace(tool.ArgumentSchemaJson) ? "{}" : tool.ArgumentSchemaJson;
                tool.StoragePath = directory;
                string sidecar;
                if (string.Equals(tool.Executor, "vba", StringComparison.OrdinalIgnoreCase))
                {
                    tool.PipelineJson = string.Empty;
                    if (!LoadVbaSources(directory, tool) || !TryApplyVbaManifest(tool)) continue;
                }
                else
                {
                    if (!TryReadOptional(Path.Combine(directory, "pipeline.json"), tool.PipelineJson, MaxPipelineFileBytes, out sidecar)) continue;
                    tool.PipelineJson = sidecar;
                    tool.Code = string.Empty;
                    tool.Components = new List<VbaToolComponent>();
                }
                if (!TryReadOptional(Path.Combine(directory, "README.md"), tool.Readme, MaxReadmeFileBytes, out sidecar)) continue;
                tool.Readme = sidecar;
                if (!HasSupportedMetadata(tool)) continue;
                JObject schema;
                string schemaError;
                if (!ToolSchemaSupport.TryParse(tool, out schema, out schemaError) ||
                    string.Equals(tool.Executor, "vba", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(tool.Code))
                {
                    continue;
                }
                tool.ArgumentSchemaJson = schema.ToString(Formatting.None);
                result.Add(tool);
            }

            return result.OrderBy(t => t.Host).ThenBy(t => t.Id).ToList();
        }

        public void Save(IEnumerable<ToolDefinition> tools)
        {
            Reconcile(tools, null);
        }

        public void Save(IEnumerable<ToolDefinition> tools, string host)
        {
            var incoming = new List<ToolDefinition>((tools ?? new ToolDefinition[0])
                .Where(t => t != null && !t.BuiltIn && !string.IsNullOrWhiteSpace(t.Id)));
            Reconcile(incoming, host);
        }

        public ToolDefinition SaveOne(ToolDefinition tool)
        {
            if (tool == null || tool.BuiltIn || string.IsNullOrWhiteSpace(tool.Id))
            {
                return null;
            }

            var targetDirectory = ToolDirectory(tool);
            var oldDirectories = Load()
                .Where(t => string.Equals(t.Id, tool.Id, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.StoragePath)
                .Where(path => !string.Equals(path, targetDirectory, StringComparison.OrdinalIgnoreCase))
                .ToList();
            SaveTool(tool);
            foreach (var oldDirectory in oldDirectories)
            {
                StorageFileSystem.TryDeleteDirectory(oldDirectory);
            }
            return Load().FirstOrDefault(t => string.Equals(t.Id, tool.Id, StringComparison.OrdinalIgnoreCase));
        }

        public bool Delete(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            var matches = Load()
                .Where(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0)
            {
                return false;
            }

            foreach (var tool in matches)
            {
                StorageFileSystem.TryDeleteDirectory(tool.StoragePath);
            }
            return true;
        }

        private void Reconcile(IEnumerable<ToolDefinition> tools, string host)
        {
            var incoming = (tools ?? new ToolDefinition[0])
                .Where(t => t != null && !t.BuiltIn && !string.IsNullOrWhiteSpace(t.Id))
                .ToList();
            var incomingDirectories = new HashSet<string>(incoming.Select(ToolDirectory), StringComparer.OrdinalIgnoreCase);
            var existingTools = Load();
            foreach (var tool in incoming)
            {
                SaveTool(tool);
            }

            foreach (var existing in existingTools)
            {
                var inScope = string.IsNullOrWhiteSpace(host) ||
                    string.Equals(existing.Host, host, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(existing.Host, "Common", StringComparison.OrdinalIgnoreCase);
                if (inScope && !incomingDirectories.Contains(existing.StoragePath ?? string.Empty))
                {
                    StorageFileSystem.TryDeleteDirectory(existing.StoragePath);
                }
            }
        }

        private void SaveTool(ToolDefinition tool)
        {
            var directory = ToolDirectory(tool);
            StorageFileSystem.EnsureRegularDirectory(_paths.ToolsDirectory);
            StorageFileSystem.EnsureRegularDirectory(Path.GetDirectoryName(directory));
            StorageFileSystem.EnsureRegularDirectory(directory);

            if (string.Equals(tool.Executor, "vba", StringComparison.OrdinalIgnoreCase) &&
                (tool.Components == null || tool.Components.Count == 0) &&
                !string.IsNullOrWhiteSpace(tool.Code))
            {
                var parsedManifest = new VbaToolManifestParser().Parse(tool.Code);
                if (parsedManifest.Success)
                {
                    tool.EntryPoint = parsedManifest.Tool.EntryPoint;
                    tool.PackageVersion = parsedManifest.Tool.PackageVersion;
                    tool.ArgumentOrder = parsedManifest.Tool.ArgumentOrder;
                    tool.Components = parsedManifest.Tool.Components;
                }
            }

            var metadata = new ToolDefinition
            {
                Id = tool.Id,
                Host = string.IsNullOrWhiteSpace(tool.Host) ? "Common" : tool.Host,
                Name = string.IsNullOrWhiteSpace(tool.Name) ? tool.Id : tool.Name,
                Description = tool.Description ?? string.Empty,
                ArgumentSchemaJson = tool.ArgumentSchemaJson,
                Executor = string.IsNullOrWhiteSpace(tool.Executor) ? "pipeline" : tool.Executor,
                RequiresConfirmation = tool.RequiresConfirmation,
                MutatesDocument = tool.MutatesDocument,
                MutatesLocalState = tool.MutatesLocalState,
                AgentCanRun = tool.AgentCanRun,
                Enabled = tool.Enabled,
                BuiltIn = false,
                RiskLevel = tool.RiskLevel,
                UseWhen = tool.UseWhen,
                DoNotUseWhen = tool.DoNotUseWhen,
                CapabilityStatus = string.IsNullOrWhiteSpace(tool.CapabilityStatus) ? "available" : tool.CapabilityStatus,
                Limitations = tool.Limitations,
                PackageVersion = tool.PackageVersion,
                EntryPoint = tool.EntryPoint,
                ArgumentOrder = new List<string>(tool.ArgumentOrder ?? new List<string>()),
                Components = (tool.Components ?? new List<VbaToolComponent>()).Select(component => new VbaToolComponent
                {
                    Name = component.Name,
                    Type = component.Type,
                    FileName = SourceFileName(component),
                    CodeSha256 = string.IsNullOrWhiteSpace(component.CodeSha256) ? VbaToolManifestParser.CodeSha256(component.Code) : component.CodeSha256
                }).ToList(),
                Scope = "global"
            };

            _json.Save(Path.Combine(directory, "tool.json"), metadata);
            WriteOptional(
                Path.Combine(directory, "pipeline.json"),
                string.Equals(metadata.Executor, "pipeline", StringComparison.OrdinalIgnoreCase) ? tool.PipelineJson : string.Empty);
            WriteVbaSources(directory, tool);
            WriteOptional(Path.Combine(directory, "README.md"), tool.Readme);
        }

        private string ToolDirectory(ToolDefinition tool)
        {
            return Path.Combine(_paths.ToolsDirectory, HostFolder(tool == null ? null : tool.Host), ToolFolder(tool == null ? null : tool.Id));
        }

        private static bool TryReadOptional(string path, string fallback, long maxBytes, out string value)
        {
            value = fallback ?? string.Empty;
            try
            {
                if (!File.Exists(path)) return true;
                return TryReadUtf8(path, maxBytes, out value);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (SecurityException)
            {
                return false;
            }
        }

        private static void WriteOptional(string path, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                return;
            }

            StorageFileSystem.WriteAllTextAtomic(path, value);
        }

        private static bool LoadVbaSources(string directory, ToolDefinition tool)
        {
            try
            {
                tool.Components = tool.Components ?? new List<VbaToolComponent>();
                if (tool.Components.Count == 0 || tool.Components.Count > MaxComponents) return false;
                var sourceDirectory = Path.Combine(directory, "src");
                if (!Directory.Exists(sourceDirectory) ||
                    (File.GetAttributes(sourceDirectory) & FileAttributes.ReparsePoint) != 0) return false;
                var expectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var component in tool.Components)
                {
                    if (component == null ||
                        !VbaToolManifestParser.ValidComponentName(component.Name) ||
                        (!string.Equals(component.Type, "StdModule", StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(component.Type, "ClassModule", StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(component.Type, "MSForm", StringComparison.OrdinalIgnoreCase))) return false;
                    component.FileName = SourceFileName(component);
                    if (!expectedFiles.Add(component.FileName)) return false;
                }
                var sourceFiles = Directory.GetFiles(sourceDirectory);
                if (sourceFiles.Any(path => path.EndsWith(".frm", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".frx", StringComparison.OrdinalIgnoreCase))) return false;
                var storedSources = sourceFiles
                    .Where(IsComponentSourceFile)
                    .ToArray();
                if (storedSources.Length != expectedFiles.Count || storedSources
                    .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                    .Any(group => group.Count() > 1) || storedSources
                    .Any(path => !expectedFiles.Contains(Path.GetFileName(path)) ||
                        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)) return false;

                long totalBytes = 0;
                long totalCharacters = 0;
                foreach (var component in tool.Components)
                {
                    var path = Path.Combine(sourceDirectory, component.FileName);
                    string code;
                    if (!TryReadUtf8(path, MaxComponentFileBytes, out code)) return false;
                    totalBytes += Encoding.UTF8.GetByteCount(code);
                    if (totalBytes > MaxComponentPackageBytes) return false;
                    component.Code = code;
                    if (component.Code.Length > 1000000) return false;
                    totalCharacters += component.Code.Length;
                    if (totalCharacters > 2000000) return false;
                    component.CodeSha256 = VbaToolManifestParser.CodeSha256(component.Code);
                }
                var manifestEntries = tool.Components.Where(component => component != null &&
                    string.Equals(component.Type, "StdModule", StringComparison.OrdinalIgnoreCase) &&
                    (component.Code ?? string.Empty).IndexOf("<RNAssistantTool>", StringComparison.Ordinal) >= 0).ToList();
                if (manifestEntries.Count != 1)
                {
                    tool.Code = string.Empty;
                    return false;
                }
                tool.Code = manifestEntries[0].Code;
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                ex is SecurityException || ex is ArgumentException || ex is NotSupportedException)
            {
                return false;
            }
        }

        private static bool TryApplyVbaManifest(ToolDefinition tool)
        {
            var parsed = new VbaToolManifestParser().Parse(tool == null ? null : tool.Code);
            if (!parsed.Success ||
                !string.Equals(parsed.Tool.Id, tool.Id, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(parsed.Tool.Host, tool.Host, StringComparison.OrdinalIgnoreCase)) return false;

            var supplied = (tool.Components ?? new List<VbaToolComponent>())
                .Where(component => component != null)
                .ToList();
            var declared = parsed.Tool.Components ?? new List<VbaToolComponent>();
            if (declared.Count == 0 || supplied.Count != declared.Count ||
                supplied.GroupBy(component => component.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1) ||
                declared.Any(component => !supplied.Any(candidate =>
                    string.Equals(candidate.Name, component.Name, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(candidate.Code)))) return false;
            var entryName = declared[0].Name;
            var entry = supplied.FirstOrDefault(component =>
                string.Equals(component.Name, entryName, StringComparison.OrdinalIgnoreCase));
            if (entry == null || !string.Equals(supplied[0].Name, entryName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(entry.Type, "StdModule", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(entry.Code, tool.Code, StringComparison.Ordinal)) return false;

            tool.Name = parsed.Tool.Name;
            tool.Description = parsed.Tool.Description;
            tool.ArgumentSchemaJson = parsed.Tool.ArgumentSchemaJson;
            tool.EntryPoint = parsed.Tool.EntryPoint;
            tool.PackageVersion = parsed.Tool.PackageVersion;
            tool.ArgumentOrder = parsed.Tool.ArgumentOrder;
            tool.MutatesDocument = parsed.Tool.MutatesDocument;
            tool.AgentCanRun = parsed.Tool.AgentCanRun;
            tool.RequiresConfirmation = parsed.Tool.RequiresConfirmation;
            tool.RiskLevel = parsed.Tool.RiskLevel;
            return true;
        }

        private static bool HasSupportedMetadata(ToolDefinition tool)
        {
            if (tool == null || string.IsNullOrWhiteSpace(tool.Id) || tool.Id.Length > 128 ||
                tool.Id.Any(char.IsWhiteSpace)) return false;
            if (!new[] { "Common", "Excel", "Word", "PowerPoint", "Outlook" }
                .Any(host => string.Equals(host, tool.Host, StringComparison.OrdinalIgnoreCase))) return false;
            if (!string.Equals(tool.Executor, "pipeline", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(tool.Executor, "vba", StringComparison.OrdinalIgnoreCase)) return false;
            if (tool.RiskLevel < 0 || tool.RiskLevel > 3 || tool.MutatesDocument && tool.RiskLevel == 0) return false;
            return (tool.Name ?? string.Empty).Length <= 200 &&
                (tool.Description ?? string.Empty).Length <= 8000 &&
                (tool.ArgumentSchemaJson ?? string.Empty).Length <= 64000 &&
                (tool.PipelineJson ?? string.Empty).Length <= 250000 &&
                (tool.Readme ?? string.Empty).Length <= 500000 &&
                (tool.UseWhen ?? string.Empty).Length <= 4000 &&
                (tool.DoNotUseWhen ?? string.Empty).Length <= 4000 &&
                (tool.Limitations ?? string.Empty).Length <= 4000;
        }

        private static bool IsReadableRegularFile(string path, long maxBytes)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists && info.Length >= 0 && info.Length <= maxBytes &&
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (SecurityException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }
        }

        private static bool TryLoadMetadata(string path, out ToolDefinition tool)
        {
            tool = null;
            string json;
            if (!TryReadUtf8(path, MaxToolMetadataFileBytes, out json)) return false;
            try
            {
                var root = JObject.Parse(json, new JsonLoadSettings
                {
                    DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
                });
                tool = root.ToObject<ToolDefinition>();
                return tool != null;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool TryReadUtf8(string path, long maxBytes, out string value)
        {
            value = string.Empty;
            try
            {
                if (!IsReadableRegularFile(path, maxBytes)) return false;
                var bytes = File.ReadAllBytes(path);
                if (bytes.LongLength > maxBytes) return false;
                var offset = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
                value = new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
                return true;
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (SecurityException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }
        }

        private static void WriteVbaSources(string directory, ToolDefinition tool)
        {
            if (!string.Equals(tool.Executor, "vba", StringComparison.OrdinalIgnoreCase))
            {
                StorageFileSystem.TryDeleteDirectory(Path.Combine(directory, "src"));
                return;
            }

            if ((tool.Components == null || tool.Components.Count == 0) && !string.IsNullOrWhiteSpace(tool.Code))
            {
                var parsed = new VbaToolManifestParser().Parse(tool.Code);
                if (parsed.Success)
                {
                    tool.EntryPoint = parsed.Tool.EntryPoint;
                    tool.PackageVersion = parsed.Tool.PackageVersion;
                    tool.ArgumentOrder = parsed.Tool.ArgumentOrder;
                    tool.Components = parsed.Tool.Components;
                }
            }
            var sourceDirectory = Path.Combine(directory, "src");
            if (tool.Components != null && tool.Components.Any(component => component != null && !string.IsNullOrWhiteSpace(component.Code)))
            {
                StorageFileSystem.EnsureRegularDirectory(sourceDirectory);
                foreach (var component in tool.Components)
                {
                    if (component == null || string.IsNullOrWhiteSpace(component.Code) || !VbaToolManifestParser.ValidComponentName(component.Name)) continue;
                    component.FileName = SourceFileName(component);
                    StorageFileSystem.WriteAllTextAtomic(Path.Combine(sourceDirectory, component.FileName), component.Code);
                }
                var expectedFiles = new HashSet<string>(tool.Components
                    .Where(component => component != null && !string.IsNullOrWhiteSpace(component.Code))
                    .Select(SourceFileName), StringComparer.OrdinalIgnoreCase);
                foreach (var existing in Directory.GetFiles(sourceDirectory))
                {
                    var extension = Path.GetExtension(existing);
                    if ((string.Equals(extension, ".bas", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(extension, ".cls", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(extension, ".frm", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(extension, ".frx", StringComparison.OrdinalIgnoreCase) ||
                         existing.EndsWith(".form.vba", StringComparison.OrdinalIgnoreCase)) &&
                        !expectedFiles.Contains(Path.GetFileName(existing)))
                    {
                        File.Delete(existing);
                    }
                }
                return;
            }
            StorageFileSystem.TryDeleteDirectory(sourceDirectory);
        }

        private static string SourceFileName(VbaToolComponent component)
        {
            var type = component == null ? null : component.Type;
            var extension = string.Equals(type, "ClassModule", StringComparison.OrdinalIgnoreCase)
                ? ".cls"
                : string.Equals(type, "MSForm", StringComparison.OrdinalIgnoreCase) ? ".form.vba" : ".bas";
            return (component == null ? "Module1" : component.Name) + extension;
        }

        private static bool IsComponentSourceFile(string path)
        {
            return path != null && (path.EndsWith(".bas", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".cls", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".form.vba", StringComparison.OrdinalIgnoreCase));
        }

        private static string HostFolder(string host)
        {
            return StorageFileSystem.SafeSegment(
                string.IsNullOrWhiteSpace(host) ? "common" : host.ToLowerInvariant(),
                "common");
        }

        private static string ToolFolder(string id)
        {
            var normalized = string.IsNullOrWhiteSpace(id) ? "tool" : id.Trim().ToLowerInvariant();
            var readable = StorageFileSystem.SafeSegment(normalized, "tool");
            if (readable.Length > 40)
            {
                readable = readable.Substring(0, 40).TrimEnd('_');
            }
            using (var sha = SHA256.Create())
            {
                var hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(normalized)))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
                return readable + "_" + hash;
            }
        }
    }
}
