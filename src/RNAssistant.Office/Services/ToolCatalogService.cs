using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Tools;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Vba;

namespace RNAssistant.Office.Services
{
    public sealed class ToolCatalogService
    {
        private static readonly TimeSpan DocumentVbaCacheDuration = TimeSpan.FromMinutes(1);
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly OfficeToolExecutor _toolExecutor;
        private readonly VbaReader _vbaReader;
        private readonly ToolStore _toolStore;
        private readonly object _documentVbaCacheSync = new object();
        private string _documentVbaCacheKey;
        private DateTime _documentVbaCacheUtc;
        private long _documentVbaCacheGeneration;
        private List<ToolDefinition> _documentVbaCache = new List<ToolDefinition>();

        public ToolCatalogService(IOfficeApplicationAdapter adapter, OfficeToolExecutor toolExecutor, ToolStore toolStore)
        {
            _adapter = adapter;
            _toolExecutor = toolExecutor;
            _vbaReader = toolExecutor.VbaReader;
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
                !string.Equals(s.Executor, "pipeline", StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(s.Host, _adapter.HostName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Host, "Common", StringComparison.OrdinalIgnoreCase))))
            {
                if (!string.IsNullOrWhiteSpace(tool.Id) &&
                    !result.ContainsKey(tool.Id) &&
                    !_toolExecutor.IsProtectedToolId(tool.Id))
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

        public List<ToolDefinition> GetFreshConversationTools()
        {
            InvalidateDocumentVbaTools();
            return GetVisibleTools();
        }

        private void DiscoverDocumentVbaTools(IDictionary<string, ToolDefinition> result)
        {
            var matchedGlobalPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var discovered in GetDocumentVbaTools())
            {
                ToolDefinition existing;
                if (result.TryGetValue(discovered.Id, out existing))
                {
                    if (!existing.BuiltIn &&
                        string.Equals(existing.Scope, "global", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(existing.Executor, "vba", StringComparison.OrdinalIgnoreCase) &&
                        matchedGlobalPackageIds.Add(discovered.Id))
                    {
                        existing.InstallationStatus = _toolExecutor.GetVbaInstallationStatus(existing, discovered);
                        if (existing.InstallationStatus == "modified_local" || existing.InstallationStatus == "partial")
                        {
                            existing.Limitations = "Document VBA components differ from the global package.";
                        }
                        else if (existing.InstallationStatus == "session_cleanup_required" ||
                            existing.InstallationStatus == "recovery_required")
                        {
                            existing.Limitations = "A temporary or ambiguously owned VBA package remains. Run explicit Uninstall cleanup before execution.";
                        }
                        continue;
                    }
                    var collisionId = discovered.Id + "#document";
                    var suffix = 2;
                    while (result.ContainsKey(collisionId))
                    {
                        collisionId = discovered.Id + "#document" + suffix;
                        suffix += 1;
                    }
                    discovered.Id = collisionId;
                    discovered.Enabled = false;
                    discovered.CapabilityStatus = "id_collision";
                    discovered.Limitations = "Document-local tool id collides with a built-in or global tool and cannot run until renamed.";
                }
                discovered.InstallationStatus = "document_local";
                result[discovered.Id] = discovered;
            }
        }

        public void InvalidateDocumentVbaTools()
        {
            lock (_documentVbaCacheSync)
            {
                _documentVbaCacheKey = null;
                _documentVbaCacheUtc = DateTime.MinValue;
                _documentVbaCacheGeneration += 1;
                _documentVbaCache.Clear();
            }
        }

        private List<ToolDefinition> GetDocumentVbaTools()
        {
            if (!SupportsVbaHost()) return new List<ToolDefinition>();
            try
            {
                // Cache identity, list and every component read share one access.
                // Failed access is not a successful empty catalog and is not cached.
                return _toolExecutor.DocumentRuntime.ReadDocument(null, ReadDocumentVbaTools);
            }
            catch (OfficeDocumentGuardException) { return new List<ToolDefinition>(); }
            catch (HostRuntime.MutationLockException) { return new List<ToolDefinition>(); }
        }

        private List<ToolDefinition> ReadDocumentVbaTools()
        {
            var host = _adapter.HostName ?? string.Empty;
            var documentKey = _adapter.DocumentKey ?? string.Empty;
            var runtimeDocumentKey = _adapter.RuntimeDocumentKey ?? string.Empty;
            var cacheKey = DocumentVbaCacheKey(host, documentKey, runtimeDocumentKey);
            long cacheGeneration;
            lock (_documentVbaCacheSync)
            {
                if (string.Equals(cacheKey, _documentVbaCacheKey, StringComparison.OrdinalIgnoreCase) &&
                    DateTime.UtcNow - _documentVbaCacheUtc <= DocumentVbaCacheDuration)
                {
                    return _documentVbaCache.Select(tool => tool.Clone()).ToList();
                }
                cacheGeneration = _documentVbaCacheGeneration;
            }

            var loaded = LoadDocumentVbaTools();
            if (loaded == null || !string.Equals(cacheKey, CurrentDocumentVbaCacheKey(), StringComparison.OrdinalIgnoreCase))
            {
                return new List<ToolDefinition>();
            }

            lock (_documentVbaCacheSync)
            {
                if (cacheGeneration != _documentVbaCacheGeneration)
                {
                    return new List<ToolDefinition>();
                }
                if (string.Equals(cacheKey, _documentVbaCacheKey, StringComparison.OrdinalIgnoreCase) &&
                    DateTime.UtcNow - _documentVbaCacheUtc <= DocumentVbaCacheDuration)
                {
                    return _documentVbaCache.Select(tool => tool.Clone()).ToList();
                }
                _documentVbaCache = loaded;
                _documentVbaCacheKey = cacheKey;
                _documentVbaCacheUtc = DateTime.UtcNow;
                return _documentVbaCache.Select(tool => tool.Clone()).ToList();
            }
        }

        private string CurrentDocumentVbaCacheKey()
        {
            return DocumentVbaCacheKey(
                _adapter.HostName,
                _adapter.DocumentKey,
                _adapter.RuntimeDocumentKey);
        }

        private static string DocumentVbaCacheKey(string host, string documentKey, string runtimeDocumentKey)
        {
            return (host ?? string.Empty) + "|" +
                (documentKey ?? string.Empty) + "|" +
                (runtimeDocumentKey ?? string.Empty);
        }

        private List<ToolDefinition> LoadDocumentVbaTools()
        {
            // Null means a backend read failed. Never publish a partial load or
            // confuse failed access with a successfully empty document catalog.
            var result = new List<ToolDefinition>();
            IReadOnlyList<VbaModuleState> modules;
            if (!TryReadDocumentVbaProject(out modules)) return null;
            var moduleMap = new Dictionary<string, VbaModuleState>(StringComparer.OrdinalIgnoreCase);
            foreach (var module in modules)
            {
                if (!moduleMap.ContainsKey(module.Name)) moduleMap.Add(module.Name, module);
            }
            foreach (var moduleInfo in moduleMap.Values.Where(module =>
                string.Equals(module.ComponentType, "StdModule", StringComparison.OrdinalIgnoreCase) &&
                module.HasToolManifest != false).ToList())
            {
                bool readFailed;
                var module = ReadDocumentModule(moduleMap, moduleInfo.Name, out readFailed);
                if (readFailed) return null;
                if (module == null) continue;
                var code = module.Code ?? string.Empty;
                if (code.IndexOf("<RNAssistantTool>", StringComparison.Ordinal) < 0 || code.EndsWith("\n...[truncated]", StringComparison.Ordinal)) continue;
                var parsed = new VbaToolManifestParser().Parse(module.Name, code);
                if (!parsed.Success || !string.Equals(parsed.Tool.Host, _adapter.HostName, StringComparison.OrdinalIgnoreCase)) continue;
                var discovered = parsed.Tool;
                discovered.Scope = "document";
                discovered.StoragePath = "VBA project: " + _adapter.DocumentTitle;
                discovered.Components = ResolveDocumentComponents(discovered, moduleMap, out readFailed);
                if (readFailed) return null;
                if (discovered.Components.Any(component => string.IsNullOrWhiteSpace(component.Code)))
                {
                    discovered.CapabilityStatus = "unavailable";
                    discovered.Limitations = "One or more declared VBA components are missing or unsupported.";
                }
                discovered.InstallationStatus = "document_local";
                result.Add(discovered);
            }
            return result;
        }

        private List<VbaToolComponent> ResolveDocumentComponents(
            ToolDefinition tool,
            IDictionary<string, VbaModuleState> modules,
            out bool readFailed)
        {
            readFailed = false;
            var result = new List<VbaToolComponent>();
            foreach (var declared in tool.Components ?? new List<VbaToolComponent>())
            {
                var module = ReadDocumentModule(modules, declared.Name, out readFailed);
                if (readFailed) return null;
                var type = module == null ? string.Empty : module.ComponentType ?? string.Empty;
                var supported = string.Equals(type, "StdModule", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type, "ClassModule", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type, "MSForm", StringComparison.OrdinalIgnoreCase) && module.CodeOnlyUserForm == true;
                var code = supported ? module.Code ?? string.Empty : string.Empty;
                result.Add(new VbaToolComponent
                {
                    Name = declared.Name,
                    Type = type,
                    FileName = declared.Name + (string.Equals(type, "ClassModule", StringComparison.OrdinalIgnoreCase)
                        ? ".cls"
                        : string.Equals(type, "MSForm", StringComparison.OrdinalIgnoreCase) ? ".form.vba" : ".bas"),
                    Code = code,
                    CodeSha256 = string.IsNullOrWhiteSpace(code) ? string.Empty : VbaTextCanonicalizer.PackageCodeSha256(code)
                });
            }
            return result;
        }

        private VbaModuleState ReadDocumentModule(
            IDictionary<string, VbaModuleState> modules,
            string moduleName,
            out bool readFailed)
        {
            readFailed = false;
            VbaModuleState module;
            if (modules == null || !modules.TryGetValue(moduleName ?? string.Empty, out module)) return null;
            if (module.HasCode) return module;

            VbaModuleState loaded;
            if (!TryReadDocumentVbaModule(moduleName, out loaded))
            {
                readFailed = true;
                return null;
            }
            modules[moduleName] = loaded;
            return loaded;
        }

        private bool TryReadDocumentVbaProject(out IReadOnlyList<VbaModuleState> modules)
        {
            try
            {
                ToolResult error;
                return _vbaReader.TryReadProject(out modules, out error);
            }
            catch (OfficeDocumentGuardException) { throw; }
            catch (HostRuntime.MutationLockException) { throw; }
            catch
            {
                modules = null;
                return false;
            }
        }

        private bool TryReadDocumentVbaModule(string moduleName, out VbaModuleState module)
        {
            try
            {
                ToolResult error;
                return _vbaReader.TryReadModule(moduleName, 2000000, out module, out error);
            }
            catch (OfficeDocumentGuardException) { throw; }
            catch (HostRuntime.MutationLockException) { throw; }
            catch
            {
                module = null;
                return false;
            }
        }

        private bool SupportsVbaHost()
        {
            return string.Equals(_adapter.HostName, "Excel", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_adapter.HostName, "Word", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_adapter.HostName, "PowerPoint", StringComparison.OrdinalIgnoreCase);
        }
    }
}
