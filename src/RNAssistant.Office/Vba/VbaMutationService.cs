using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.Vba
{
    internal sealed partial class VbaMutationService
    {
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly VbaJournalStore _journalStore;
        private readonly VbaReader _reader;
        private readonly VbaVerifier _verifier;
        private readonly object _observationsSync = new object();
        private readonly Dictionary<string, string> _observedModuleHashes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public VbaMutationService(
            IOfficeApplicationAdapter adapter,
            VbaJournalStore journalStore,
            VbaReader reader)
        {
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            _journalStore = journalStore ?? throw new ArgumentNullException(nameof(journalStore));
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _verifier = new VbaVerifier(reader, RecordObservation, RemoveObservation);
        }

        public VbaVerifier Verifier { get { return _verifier; } }

        public bool TryReadExistingModule(
            string requestedModuleName,
            out string resolvedModuleName,
            out VbaModuleState module,
            out ToolResult error)
        {
            requestedModuleName = (requestedModuleName ?? string.Empty).Trim();
            resolvedModuleName = requestedModuleName;
            if (_reader.TryReadModule(requestedModuleName, 1000000, out module, out error))
            {
                resolvedModuleName = string.IsNullOrWhiteSpace(module.Name) ? requestedModuleName : module.Name;
                return true;
            }
            if (!VbaReader.IsModuleNotFound(error)) return false;

            var normalizedName = VbaReader.NormalizeModuleName(requestedModuleName);
            if (!string.Equals(requestedModuleName, normalizedName, StringComparison.OrdinalIgnoreCase) &&
                _reader.TryReadModule(normalizedName, 1000000, out module, out error))
            {
                resolvedModuleName = string.IsNullOrWhiteSpace(module.Name) ? normalizedName : module.Name;
                return true;
            }
            if (!VbaReader.IsModuleNotFound(error)) return false;

            resolvedModuleName = normalizedName;
            error = ToolResult.Fail(
                "VBA module not found: " + requestedModuleName +
                (string.Equals(requestedModuleName, normalizedName, StringComparison.Ordinal)
                    ? "."
                    : ". Runtime also tried the normalized name " + normalizedName + ".") +
                " To create it, call common.vba_write_module with moduleName, complete code, and mode=upsert. " +
                "When the existing target name is unknown, list provider vba with kind vba-component.",
                JsonConvert.SerializeObject(new
                {
                    requestedModuleName = requestedModuleName,
                    normalizedModuleName = normalizedName,
                    discoveryTool = "common.resources_list",
                    resourceProvider = VbaResourceProvider.ProviderName,
                    resourceKind = VbaResourceProvider.ComponentKind,
                    creationTool = ToolId("vba_write_module"),
                    creationMode = "upsert"
                }),
                "vba_module_not_found",
                true);
            module = null;
            return false;
        }

        public void RecordObservation(ChatSession session, string moduleName, string hash)
        {
            if (string.IsNullOrWhiteSpace(moduleName) || string.IsNullOrWhiteSpace(hash)) return;
            var key = ObservationKey(session, moduleName);
            lock (_observationsSync)
            {
                if (_observedModuleHashes.Count >= 1024 && !_observedModuleHashes.ContainsKey(key))
                {
                    _observedModuleHashes.Clear();
                }
                _observedModuleHashes[key] = hash;
            }
        }

        public bool TryGetObservation(ChatSession session, string moduleName, out string hash)
        {
            lock (_observationsSync)
            {
                return _observedModuleHashes.TryGetValue(ObservationKey(session, moduleName), out hash);
            }
        }

        public void RemoveObservation(ChatSession session, string moduleName)
        {
            lock (_observationsSync)
            {
                _observedModuleHashes.Remove(ObservationKey(session, moduleName));
            }
        }

        private string ObservationKey(ChatSession session, string moduleName)
        {
            var runtimeKey = _adapter.RuntimeDocumentKey ?? string.Empty;
            var documentIdentity = string.IsNullOrWhiteSpace(runtimeKey)
                ? "document:" + (_adapter.DocumentKey ?? string.Empty)
                : "runtime:" + runtimeKey;
            return (session == null ? string.Empty : session.Id ?? string.Empty) + "|" +
                (_adapter.HostName ?? string.Empty) + "|" + documentIdentity + "|" + (moduleName ?? string.Empty);
        }

        private string ToolId(string suffix)
        {
            return "common." + suffix;
        }

        private string BackendToolId(string suffix)
        {
            return (_adapter.HostName ?? string.Empty).ToLowerInvariant() + "." + suffix;
        }

        private static string CodeSha256(string code)
        {
            return VbaTextCanonicalizer.LiveCodeSha256(code);
        }
    }
}
