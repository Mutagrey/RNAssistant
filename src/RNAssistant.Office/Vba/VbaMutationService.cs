using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
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
            _verifier = new VbaVerifier(reader);
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
                "When the existing target name is unknown, run common.resources_find with scope=vba.",
                new Newtonsoft.Json.Linq.JObject
                {
                    ["requestedModuleName"] = requestedModuleName,
                    ["normalizedModuleName"] = normalizedName,
                    ["discoveryTool"] = "common.resources_find",
                    ["discoveryScope"] = "vba",
                    ["creationTool"] = ToolId("vba_write_module"),
                    ["creationMode"] = "upsert"
                },
                "vba_module_not_found",
                true);
        }

        private static IEnumerable<ResourceEvidence> ModuleEvidence(VbaMutationCorrelation correlation, string moduleName)
        {
            if (correlation == null || string.IsNullOrWhiteSpace(correlation.DocumentAuthorityId))
                return Enumerable.Empty<ResourceEvidence>();
            var identity = VbaResourceProvider.ComponentIdentity(correlation.DocumentAuthorityId, moduleName);
            return (correlation.Evidence ?? new ResourceEvidence[0]).Where(item =>
                item.Resource.Identity.Equals(identity) && item.Complete &&
                item.Coverage.Kind == ResourceCoverageKinds.Whole && item.View == ResourceRepresentations.Source);
        }

        private static bool TryGetObservation(VbaMutationCorrelation correlation, string moduleName, out string hash)
        {
            // An editor's explicit expected hash is a guard, never a model observation.
            hash = correlation == null ? null : correlation.ExpectedContentSha256;
            if (!string.IsNullOrWhiteSpace(hash)) return true;
            var reducer = new EvidenceStateReducer();
            var evidence = ModuleEvidence(correlation, moduleName).LastOrDefault(item =>
                reducer.Reduce(item, correlation.Authority).State == EvidenceState.Current);
            hash = evidence == null ? null : evidence.ContentSha256;
            return !string.IsNullOrWhiteSpace(hash);
        }

        private static bool RequiresObservationRefresh(VbaMutationCorrelation correlation, string moduleName)
        {
            string hash;
            if (TryGetObservation(correlation, moduleName, out hash)) return false;
            if (correlation == null || correlation.Authority == null ||
                string.IsNullOrWhiteSpace(correlation.DocumentAuthorityId)) return false;
            var identity = VbaResourceProvider.ComponentIdentity(correlation.DocumentAuthorityId, moduleName);
            var scope = correlation.Authority.Get(ResourceAuthorityScopeId.Document(
                new DocumentAuthorityId(correlation.DocumentAuthorityId)));
            // A head advanced by a committed effect must be observed in the model's
            // conversation again. Runtime read-back does not grant model knowledge.
            return ModuleEvidence(correlation, moduleName).Any() || scope != null &&
                scope.Commits.Any(commit => commit.Effect != null &&
                    commit.Effect.Outcome != ResourceEffectOutcome.VerifiedNoChange &&
                    commit.Effect.Outcome != ResourceEffectOutcome.FailedNoEffect &&
                    commit.Effect.Impacts.Any(impact => impact.Identity.Equals(identity)));
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

        private static VbaMutationOutcome ValidateLiveCodeForWrite(
            string moduleName,
            string code)
        {
            string validationError;
            if (VbaSourceValidator.TryValidateLiveCode(code ?? string.Empty, out validationError))
            {
                return null;
            }

            return VbaMutationOutcome.Error(
                validationError,
                new Newtonsoft.Json.Linq.JObject
                {
                    ["moduleName"] = moduleName ?? string.Empty,
                    ["retrySameTool"] = false,
                    ["inspectTool"] = "common.resources_read",
                    ["discoveryScope"] = "vba"
                },
                "vba_code_invalid",
                true);
        }
    }
}
