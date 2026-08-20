using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    public sealed class ToolStore
    {
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
                var tool = _json.Load(file, (ToolDefinition)null);
                if (tool == null || string.IsNullOrWhiteSpace(tool.Id))
                {
                    continue;
                }

                var directory = Path.GetDirectoryName(file);
                tool.BuiltIn = false;
                tool.Executor = string.IsNullOrWhiteSpace(tool.Executor) ? "pipeline" : tool.Executor;
                tool.ArgumentSchemaJson = string.IsNullOrWhiteSpace(tool.ArgumentSchemaJson) ? "{}" : tool.ArgumentSchemaJson;
                tool.StoragePath = directory;
                tool.PipelineJson = ReadOptional(Path.Combine(directory, "pipeline.json"), tool.PipelineJson);
                LoadVbaSources(directory, tool);
                tool.Readme = ReadOptional(Path.Combine(directory, "README.md"), tool.Readme);
                JObject schema;
                string schemaError;
                if (!ToolSchemaSupport.TryNormalize(tool, out schema, out schemaError) ||
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
            Directory.CreateDirectory(directory);

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
            WriteOptional(Path.Combine(directory, "pipeline.json"), tool.PipelineJson);
            WriteVbaSources(directory, tool);
            WriteOptional(Path.Combine(directory, "README.md"), tool.Readme);
        }

        private string ToolDirectory(ToolDefinition tool)
        {
            return Path.Combine(_paths.ToolsDirectory, HostFolder(tool == null ? null : tool.Host), ToolFolder(tool == null ? null : tool.Id));
        }

        private static string ReadOptional(string path, string fallback)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : (fallback ?? string.Empty);
            }
            catch (IOException)
            {
                return fallback ?? string.Empty;
            }
            catch (UnauthorizedAccessException)
            {
                return fallback ?? string.Empty;
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

        private static void LoadVbaSources(string directory, ToolDefinition tool)
        {
            tool.Components = tool.Components ?? new List<VbaToolComponent>();
            var sourceDirectory = Path.Combine(directory, "src");
            foreach (var component in tool.Components)
            {
                if (component == null || !VbaToolManifestParser.ValidIdentifier(component.Name)) continue;
                component.FileName = SourceFileName(component);
                var path = Path.Combine(sourceDirectory, component.FileName);
                component.Code = ReadOptional(path, component.Code);
                component.CodeSha256 = VbaToolManifestParser.CodeSha256(component.Code);
            }
            var entry = tool.Components.FirstOrDefault(component => component != null &&
                string.Equals(component.Type, "StdModule", StringComparison.OrdinalIgnoreCase) &&
                (component.Code ?? string.Empty).IndexOf("<RNAssistantTool>", StringComparison.Ordinal) >= 0);
            if (entry != null)
            {
                tool.Code = entry.Code;
            }
            else
            {
                tool.Code = string.Empty;
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
                Directory.CreateDirectory(sourceDirectory);
                foreach (var component in tool.Components)
                {
                    if (component == null || string.IsNullOrWhiteSpace(component.Code) || !VbaToolManifestParser.ValidIdentifier(component.Name)) continue;
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
                         string.Equals(extension, ".cls", StringComparison.OrdinalIgnoreCase)) &&
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
            var extension = string.Equals(component == null ? null : component.Type, "ClassModule", StringComparison.OrdinalIgnoreCase) ? ".cls" : ".bas";
            return (component == null ? "Module1" : component.Name) + extension;
        }

        private static string HostFolder(string host)
        {
            return StorageFileSystem.SafeSegment(
                string.IsNullOrWhiteSpace(host) ? "common" : host.ToLowerInvariant(),
                "common");
        }

        private static string ToolFolder(string id)
        {
            return StorageFileSystem.SafeSegment((id ?? "tool").ToLowerInvariant(), "tool");
        }
    }
}
