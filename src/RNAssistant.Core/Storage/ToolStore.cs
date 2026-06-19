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

            foreach (var file in Directory.GetFiles(_paths.ToolsDirectory, "tool.json", SearchOption.AllDirectories))
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
            SaveAll(tools);
        }

        public void Save(IEnumerable<ToolDefinition> tools, string host)
        {
            var incoming = new List<ToolDefinition>((tools ?? new ToolDefinition[0])
                .Where(t => t != null && !t.BuiltIn && !string.IsNullOrWhiteSpace(t.Id)));
            var keep = Load().Where(t =>
                !string.Equals(t.Host, "Common", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(t.Host, host, StringComparison.OrdinalIgnoreCase));

            SaveAll(keep.Concat(incoming));
        }

        private void SaveAll(IEnumerable<ToolDefinition> tools)
        {
            if (Directory.Exists(_paths.ToolsDirectory))
            {
                Directory.Delete(_paths.ToolsDirectory, true);
            }

            Directory.CreateDirectory(_paths.ToolsDirectory);
            foreach (var tool in tools ?? new ToolDefinition[0])
            {
                if (tool == null || tool.BuiltIn || string.IsNullOrWhiteSpace(tool.Id))
                {
                    continue;
                }

                SaveTool(tool);
            }
        }

        private void SaveTool(ToolDefinition tool)
        {
            var directory = Path.Combine(_paths.ToolsDirectory, HostFolder(tool.Host), ToolFolder(tool.Id));
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
                AgentCanRun = tool.AgentCanRun,
                Enabled = tool.Enabled,
                BuiltIn = false
            };

            _json.Save(Path.Combine(directory, "tool.json"), metadata);
            WriteOptional(Path.Combine(directory, "pipeline.json"), tool.PipelineJson);
            WriteOptional(Path.Combine(directory, "code.vba"), tool.Code);
            WriteOptional(Path.Combine(directory, "README.md"), tool.Readme);
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
        }

        private static void WriteOptional(string path, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            File.WriteAllText(path, value);
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
    }
}
