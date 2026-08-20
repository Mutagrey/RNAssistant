using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    public sealed class ToolCatalogService
    {
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly OfficeToolExecutor _toolExecutor;
        private readonly ToolStore _toolStore;

        public ToolCatalogService(IOfficeApplicationAdapter adapter, OfficeToolExecutor toolExecutor, ToolStore toolStore)
        {
            _adapter = adapter;
            _toolExecutor = toolExecutor;
            _toolStore = toolStore;
        }

        public List<ToolDefinition> GetVisibleTools()
        {
            var result = new Dictionary<string, ToolDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var tool in _adapter.GetBuiltInTools() ?? new ToolDefinition[0])
            {
                result[tool.Id] = tool;
            }

            foreach (var tool in _toolExecutor.GetControllerTools())
            {
                result[tool.Id] = tool;
            }

            foreach (var tool in _toolStore.Load().Where(s =>
                string.Equals(s.Host, _adapter.HostName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Host, "Common", StringComparison.OrdinalIgnoreCase)))
            {
                if (!string.IsNullOrWhiteSpace(tool.Id) && !result.ContainsKey(tool.Id))
                {
                    if (string.Equals(tool.Executor, "vba", StringComparison.OrdinalIgnoreCase))
                    {
                        tool.Scope = "global";
                        tool.InstallationStatus = "not_installed";
                    }
                    result.Add(tool.Id, tool);
                }
            }

            DiscoverDocumentVbaTools(result);

            return result.Values.OrderBy(s => s.Host).ThenBy(s => s.Id).ToList();
        }

        private void DiscoverDocumentVbaTools(IDictionary<string, ToolDefinition> result)
        {
            if (!SupportsVbaHost()) return;
            ToolResult read;
            try
            {
                var command = new ToolCommand { ToolId = (_adapter.HostName ?? string.Empty).ToLowerInvariant() + ".vba_list_project_components_internal" };
                read = _adapter.ExecuteTool(command);
            }
            catch { return; }
            if (read == null || !read.Success || string.IsNullOrWhiteSpace(read.DataJson)) return;

            JArray modules;
            try { modules = JObject.Parse(read.DataJson)["modules"] as JArray; }
            catch (JsonException) { return; }
            if (modules == null) return;
            var moduleMap = modules.OfType<JObject>()
                .Where(module => !string.IsNullOrWhiteSpace((string)module["name"]))
                .ToDictionary(module => (string)module["name"], StringComparer.OrdinalIgnoreCase);
            foreach (var moduleInfo in moduleMap.Values.Where(module => string.Equals((string)module["type"], "StdModule", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                var module = ReadDocumentModule(moduleMap, (string)moduleInfo["name"]);
                if (module == null) continue;
                var code = (string)module["code"] ?? string.Empty;
                if (code.IndexOf("<RNAssistantTool>", StringComparison.Ordinal) < 0 || code.EndsWith("\n...[truncated]", StringComparison.Ordinal)) continue;
                var parsed = new VbaToolManifestParser().Parse((string)module["name"], code);
                if (!parsed.Success || !string.Equals(parsed.Tool.Host, _adapter.HostName, StringComparison.OrdinalIgnoreCase)) continue;
                var discovered = parsed.Tool;
                discovered.Scope = "document";
                discovered.StoragePath = "VBA project: " + _adapter.DocumentTitle;
                discovered.Components = ResolveDocumentComponents(discovered, moduleMap);
                if (discovered.Components.Any(component => string.IsNullOrWhiteSpace(component.Code)))
                {
                    discovered.CapabilityStatus = "unavailable";
                    discovered.Limitations = "One or more declared VBA components are missing or unsupported.";
                }

                ToolDefinition existing;
                if (result.TryGetValue(discovered.Id, out existing))
                {
                    if (!existing.BuiltIn && string.Equals(existing.Executor, "vba", StringComparison.OrdinalIgnoreCase))
                    {
                        existing.InstallationStatus = PackageMatches(existing, discovered) ? "installed" : "modified_local";
                        if (existing.InstallationStatus == "modified_local") existing.Limitations = "Document VBA components differ from the global package.";
                        continue;
                    }
                    discovered.Id = discovered.Id + "#document";
                    discovered.Enabled = false;
                    discovered.CapabilityStatus = "id_collision";
                    discovered.Limitations = "Document-local tool id collides with a built-in or global tool and cannot run until renamed.";
                }
                discovered.InstallationStatus = "document_local";
                result[discovered.Id] = discovered;
            }
        }

        private List<VbaToolComponent> ResolveDocumentComponents(ToolDefinition tool, IDictionary<string, JObject> modules)
        {
            var result = new List<VbaToolComponent>();
            foreach (var declared in tool.Components ?? new List<VbaToolComponent>())
            {
                var module = ReadDocumentModule(modules, declared.Name);
                var type = module == null ? string.Empty : (string)module["type"] ?? string.Empty;
                var supported = string.Equals(type, "StdModule", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "ClassModule", StringComparison.OrdinalIgnoreCase);
                var code = supported ? (string)module["code"] ?? string.Empty : string.Empty;
                result.Add(new VbaToolComponent
                {
                    Name = declared.Name,
                    Type = type,
                    FileName = declared.Name + (string.Equals(type, "ClassModule", StringComparison.OrdinalIgnoreCase) ? ".cls" : ".bas"),
                    Code = code,
                    CodeSha256 = string.IsNullOrWhiteSpace(code) ? string.Empty : VbaToolManifestParser.CodeSha256(code)
                });
            }
            return result;
        }

        private JObject ReadDocumentModule(IDictionary<string, JObject> modules, string moduleName)
        {
            JObject module;
            if (modules == null || !modules.TryGetValue(moduleName ?? string.Empty, out module)) return null;
            if (module["code"] != null) return module;

            ToolResult read;
            try
            {
                var command = new ToolCommand { ToolId = (_adapter.HostName ?? string.Empty).ToLowerInvariant() + ".vba_read_module" };
                command.Arguments["moduleName"] = moduleName;
                command.Arguments["maxChars"] = 2000000;
                read = _adapter.ExecuteTool(command);
            }
            catch { return null; }
            if (read == null || !read.Success || string.IsNullOrWhiteSpace(read.DataJson)) return null;

            try
            {
                var data = JObject.Parse(read.DataJson);
                module["code"] = data["code"];
                module["type"] = data["type"] ?? module["type"];
                module["lineCount"] = data["lineCount"] ?? module["lineCount"];
                return module;
            }
            catch (JsonException) { return null; }
        }

        private static bool PackageMatches(ToolDefinition global, ToolDefinition document)
        {
            var documentComponents = (document.Components ?? new List<VbaToolComponent>()).ToDictionary(component => component.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var component in global.Components ?? new List<VbaToolComponent>())
            {
                VbaToolComponent current;
                if (!documentComponents.TryGetValue(component.Name, out current) ||
                    !string.Equals(VbaToolManifestParser.CodeSha256(component.Code), VbaToolManifestParser.CodeSha256(current.Code), StringComparison.OrdinalIgnoreCase)) return false;
            }
            return (global.Components ?? new List<VbaToolComponent>()).Count == documentComponents.Count;
        }

        private bool SupportsVbaHost()
        {
            return string.Equals(_adapter.HostName, "Excel", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_adapter.HostName, "Word", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_adapter.HostName, "PowerPoint", StringComparison.OrdinalIgnoreCase);
        }
    }
}
