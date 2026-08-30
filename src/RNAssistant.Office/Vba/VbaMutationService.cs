using System;
using System.Collections.Generic;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.Vba
{
    internal sealed partial class VbaMutationService
    {
        private readonly IVbaMutationDocumentContext _document;
        private readonly IVbaMutationJournal _journal;
        private readonly IVbaMutationBackend _backend;
        private readonly IVbaRenameJournal _renameJournal;
        private readonly IVbaMutationReader _reader;
        private readonly VbaVerifier _verifier;
        private readonly object _observationsSync = new object();
        private readonly Dictionary<string, string> _observedModuleHashes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal VbaMutationService(
            IVbaMutationDocumentContext document,
            IVbaMutationJournal journal,
            IVbaMutationReader reader,
            IVbaMutationBackend backend)
            : this(document, journal, reader, backend, null)
        {
        }

        internal VbaMutationService(
            IVbaMutationDocumentContext document,
            IVbaMutationJournal journal,
            IVbaMutationReader reader,
            IVbaMutationBackend backend,
            IVbaRenameJournal renameJournal)
        {
            _document = document ?? throw new ArgumentNullException(nameof(document));
            _journal = journal ?? throw new ArgumentNullException(nameof(journal));
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _renameJournal = renameJournal;
            _verifier = new VbaVerifier(reader, RecordObservation, RemoveObservation);
        }

        public VbaMutationOutcome TryReadExistingModule(
            string requestedModuleName,
            out string resolvedModuleName,
            out VbaModuleState module)
        {
            requestedModuleName = (requestedModuleName ?? string.Empty).Trim();
            resolvedModuleName = requestedModuleName;
            module = null;
            var result = _reader.ReadModule(requestedModuleName, 1000000);
            if (result == null) return ReadFailure(null);
            if (result.Success)
            {
                module = result.Module;
                resolvedModuleName = string.IsNullOrWhiteSpace(module.Name) ? requestedModuleName : module.Name;
                return null;
            }
            if (!result.IsNotFound) return ReadFailure(result);

            var normalizedName = VbaReader.NormalizeModuleName(requestedModuleName);
            if (!string.Equals(requestedModuleName, normalizedName, StringComparison.OrdinalIgnoreCase))
            {
                result = _reader.ReadModule(normalizedName, 1000000);
                if (result == null) return ReadFailure(null);
                if (result.Success)
                {
                    module = result.Module;
                    resolvedModuleName = string.IsNullOrWhiteSpace(module.Name) ? normalizedName : module.Name;
                    return null;
                }
                if (!result.IsNotFound) return ReadFailure(result);
            }

            resolvedModuleName = normalizedName;
            module = null;
            return VbaMutationOutcome.Error(
                "VBA module not found: " + requestedModuleName +
                (string.Equals(requestedModuleName, normalizedName, StringComparison.Ordinal)
                    ? "."
                    : ". Runtime also tried the normalized name " + normalizedName + ".") +
                " To create it, call common.vba_write_module with moduleName, complete code, and mode=upsert. " +
                "When the existing target name is unknown, list provider vba with kind vba-component.",
                new Newtonsoft.Json.Linq.JObject
                {
                    ["requestedModuleName"] = requestedModuleName,
                    ["normalizedModuleName"] = normalizedName,
                    ["discoveryTool"] = "common.resources_list",
                    ["resourceProvider"] = VbaResourceProvider.ProviderName,
                    ["resourceKind"] = VbaResourceProvider.ComponentKind,
                    ["creationTool"] = ToolId("vba_write_module"),
                    ["creationMode"] = "upsert"
                },
                "vba_module_not_found",
                true);
        }

        public void RecordObservation(string sessionId, string moduleName, string hash)
        {
            if (string.IsNullOrWhiteSpace(moduleName) || string.IsNullOrWhiteSpace(hash)) return;
            var key = ObservationKey(sessionId, moduleName);
            lock (_observationsSync)
            {
                if (_observedModuleHashes.Count >= 1024 && !_observedModuleHashes.ContainsKey(key))
                {
                    _observedModuleHashes.Clear();
                }
                _observedModuleHashes[key] = hash;
            }
        }

        public bool TryGetObservation(string sessionId, string moduleName, out string hash)
        {
            lock (_observationsSync)
            {
                return _observedModuleHashes.TryGetValue(ObservationKey(sessionId, moduleName), out hash);
            }
        }

        public void RemoveObservation(string sessionId, string moduleName)
        {
            lock (_observationsSync)
            {
                _observedModuleHashes.Remove(ObservationKey(sessionId, moduleName));
            }
        }

        private string ObservationKey(string sessionId, string moduleName)
        {
            var runtimeKey = _document.RuntimeDocumentKey ?? string.Empty;
            var documentIdentity = string.IsNullOrWhiteSpace(runtimeKey)
                ? "document:" + (_document.DocumentKey ?? string.Empty)
                : "runtime:" + runtimeKey;
            return (sessionId ?? string.Empty) + "|" +
                (_document.HostName ?? string.Empty) + "|" + documentIdentity + "|" + (moduleName ?? string.Empty);
        }

        private static VbaMutationOutcome ReadFailure(VbaMutationReadResult error)
        {
            return error == null
                ? VbaMutationOutcome.Error(
                    "VBA module read returned no result.",
                    null,
                    "vba_read_missing_result",
                    true)
                : VbaMutationOutcome.Error(
                    error.Message,
                    error.Data,
                    error.ErrorCode,
                    error.Retryable);
        }

        private string ToolId(string suffix)
        {
            return "common." + suffix;
        }

        private static string CodeSha256(string code)
        {
            return VbaTextCanonicalizer.LiveCodeSha256(code);
        }
    }
}
