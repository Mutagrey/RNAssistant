using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.Vba;
using RNAssistant.Office.Domains.Vba;

namespace RNAssistant.Office.Tools
{
    internal sealed partial class VbaToolExecutor : IVbaResourceSource
    {
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly VbaJournalStore _vbaJournalStore;
        private readonly VbaReader _reader;
        private readonly VbaMutationService _mutationService;
        private readonly VbaPackageService _packageService;
        private readonly IVbaHostBackend _backend;
        private readonly VbaDispatchBoundary _dispatchBoundary;

        public VbaToolExecutor(IOfficeApplicationAdapter adapter, VbaJournalStore vbaJournalStore)
        {
            _adapter = adapter;
            _vbaJournalStore = vbaJournalStore;
            _dispatchBoundary = new VbaDispatchBoundary();
            _backend = VbaBackendProvider.Resolve(adapter);
            if (_backend == null) return;
            _reader = new VbaReader(_backend);
            _mutationService = new VbaMutationService(
                new VbaMutationHostDocumentContext(_backend),
                new VbaMutationJournalStoreAdapter(vbaJournalStore),
                new VbaMutationHostReader(_reader),
                new VbaMutationHostBackend(_backend, _dispatchBoundary),
                new VbaRenameJournalStoreAdapter(vbaJournalStore));
            _packageService = new VbaPackageService(
                new VbaMutationHostDocumentContext(_backend),
                new VbaPackageJournalStoreAdapter(vbaJournalStore),
                new VbaMutationHostReader(_reader),
                new VbaPackageHostBackend(_backend));
        }

        internal VbaReader Reader { get { return _reader; } }

        public string ToolId(string suffix)
        {
            return "common." + suffix;
        }

        public void ObserveExpectedHash(ChatSession session, string moduleName, string codeSha256)
        {
            if (!string.IsNullOrWhiteSpace(moduleName) && !string.IsNullOrWhiteSpace(codeSha256))
            {
                RecordObservation(session, moduleName, codeSha256);
            }
        }

        ToolRunResult IVbaResourceSource.ListResourceModules()
        {
            var reconciliationError = ReconcilePendingMutations();
            if (reconciliationError != null) return reconciliationError;
            IReadOnlyList<VbaModuleState> project;
            ToolRunResult error;
            if (!_reader.TryReadProject(out project, out error)) return error;
            var modules = new JArray(project.Select(module => new JObject
            {
                ["name"] = module.Name,
                ["type"] = module.ComponentType,
                ["lineCount"] = module.LineCount
            }));
            return ToolRunResult.Ok(
                "VBA modules listed: " + modules.Count + ".",
                JsonConvert.SerializeObject(new { modules = modules }));
        }

        ToolRunResult IVbaResourceSource.ReadResourceModule(
            ChatSession session,
            string moduleName,
            int maxChars)
        {
            var reconciliationError = ReconcilePendingMutations();
            if (reconciliationError != null) return reconciliationError;
            VbaModuleState module;
            ToolRunResult result;
            if (!_reader.TryReadResourceModule(moduleName, maxChars, out module, out result)) return result;
            RecordObservationFromModule(session, moduleName, module);
            return result;
        }

        private static IReadOnlyList<VbaPatchOperationRequest> ParsePatchOperations(JArray patch)
        {
            var operations = new List<VbaPatchOperationRequest>();
            if (patch == null) return operations;
            foreach (var token in patch)
            {
                var item = token as JObject;
                operations.Add(item == null
                    ? null
                    : new VbaPatchOperationRequest
                    {
                        Operation = (string)item["op"],
                        Find = (string)item["find"],
                        Text = (string)item["text"]
                    });
            }
            return operations;
        }

        private static VbaWholeModuleWriteMode WholeModuleWriteMode(string mode)
        {
            if (string.Equals(mode, "createOnly", StringComparison.OrdinalIgnoreCase))
            {
                return VbaWholeModuleWriteMode.CreateOnly;
            }
            if (string.Equals(mode, "updateOnly", StringComparison.OrdinalIgnoreCase))
            {
                return VbaWholeModuleWriteMode.UpdateOnly;
            }
            if (string.IsNullOrWhiteSpace(mode) ||
                string.Equals(mode, "upsert", StringComparison.OrdinalIgnoreCase))
            {
                return VbaWholeModuleWriteMode.Upsert;
            }
            return VbaWholeModuleWriteMode.Unknown;
        }

        private static VbaMutationCorrelation MutationCorrelation(
            ToolInvocation command,
            ChatSession session)
        {
            return new VbaMutationCorrelation
            {
                SessionId = SessionId(session),
                RunId = session == null || session.LastRun == null ? null : session.LastRun.RunId,
                TurnId = session == null || session.LastRun == null ? null : session.LastRun.TurnId,
                StepId = command == null ? null : command.RuntimeStepId,
                ToolCallId = command == null ? null : command.ToolCallId
            };
        }

        private static string SessionId(ChatSession session)
        {
            return session == null ? string.Empty : session.Id ?? string.Empty;
        }

        internal bool HostSupportsVba()
        {
            return _backend != null &&
                (string.Equals(_adapter.HostName, "Excel", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_adapter.HostName, "Word", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_adapter.HostName, "PowerPoint", StringComparison.OrdinalIgnoreCase));
        }


    }

}
