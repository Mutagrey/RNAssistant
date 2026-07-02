using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
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

            foreach (var file in SafeGetFiles(_paths.ToolsDirectory, "tool.json"))
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
                tool.Code = ReadOptional(Path.Combine(directory, "code.vba"), tool.Code);
                tool.Readme = ReadOptional(Path.Combine(directory, "README.md"), tool.Readme);
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
                TryDeleteDirectory(oldDirectory);
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
                TryDeleteDirectory(tool.StoragePath);
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
                    TryDeleteDirectory(existing.StoragePath);
                }
            }
        }

        private void SaveTool(ToolDefinition tool)
        {
            var directory = ToolDirectory(tool);
            Directory.CreateDirectory(directory);

            var metadata = new ToolDefinition
            {
                Id = tool.Id,
                Host = string.IsNullOrWhiteSpace(tool.Host) ? "Common" : tool.Host,
                Name = string.IsNullOrWhiteSpace(tool.Name) ? tool.Id : tool.Name,
                Description = tool.Description ?? string.Empty,
                ArgumentSchemaJson = string.IsNullOrWhiteSpace(tool.ArgumentSchemaJson) ? "{}" : tool.ArgumentSchemaJson,
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
                ExamplesJson = tool.ExamplesJson,
                PreconditionsJson = tool.PreconditionsJson,
                VerifyJson = tool.VerifyJson,
                CapabilityStatus = string.IsNullOrWhiteSpace(tool.CapabilityStatus) ? "available" : tool.CapabilityStatus,
                Limitations = tool.Limitations,
                ReplacementToolId = tool.ReplacementToolId
            };

            _json.Save(Path.Combine(directory, "tool.json"), metadata);
            WriteOptional(Path.Combine(directory, "pipeline.json"), tool.PipelineJson);
            WriteOptional(Path.Combine(directory, "code.vba"), tool.Code);
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

            var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tempPath, value);
                if (File.Exists(path))
                {
                    File.Replace(tempPath, path, null);
                }
                else
                {
                    File.Move(tempPath, path);
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return;
            }
            Directory.Delete(path, true);
        }

        private static string HostFolder(string host)
        {
            return SafeSegment(string.IsNullOrWhiteSpace(host) ? "common" : host.ToLowerInvariant());
        }

        private static string ToolFolder(string id)
        {
            return SafeSegment((id ?? "tool").ToLowerInvariant());
        }

        private static string SafeSegment(string value)
        {
            var chars = (value ?? "tool").Select(c =>
                char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray();
            var result = new string(chars).Trim('_');
            return string.IsNullOrWhiteSpace(result) ? "tool" : result;
        }

        private static IEnumerable<string> SafeGetFiles(string directory, string pattern)
        {
            var files = new List<string>();
            AddFiles(directory, pattern, files);
            return files;
        }

        private static void AddFiles(string directory, string pattern, List<string> files)
        {
            string[] localFiles;
            string[] childDirectories;
            try
            {
                localFiles = Directory.GetFiles(directory, pattern);
                childDirectories = Directory.GetDirectories(directory);
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            files.AddRange(localFiles);
            foreach (var childDirectory in childDirectories)
            {
                AddFiles(childDirectory, pattern, files);
            }
        }
    }
}
