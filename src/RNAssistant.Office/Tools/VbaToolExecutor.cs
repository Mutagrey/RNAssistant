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
        private readonly ResourceAuthorityService _authority;

        public VbaToolExecutor(IOfficeApplicationAdapter adapter, VbaJournalStore vbaJournalStore,
            ResourceAuthorityService authority)
        {
            _adapter = adapter;
            _authority = authority ?? throw new ArgumentNullException(nameof(authority));
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
                        Operation = "replace",
                        Find = (string)item["find"],
                        Text = (string)item["text"],
                        ContextBefore = (string)item["contextBefore"],
                        ContextAfter = (string)item["contextAfter"]
                    });
            }
            return operations;
        }

        private VbaNativeOutcome ResolveRestoreIntent(
            IDictionary<string, object> arguments,
            out string backupId,
            out string moduleName)
        {
            backupId = string.Empty;
            moduleName = ToolArgumentReader.String(
                arguments, "moduleName", string.Empty).Trim();
            var target = ToolArgumentReader.String(
                arguments, "target", string.Empty).Trim();
            if (target.Length == 0) return null;

            IReadOnlyList<VbaModuleBackup> backups;
            try
            {
                backups = LoadBackups();
            }
            catch (VbaJournalException ex)
            {
                return VbaNativeOutcome.Error(
                    ex.Message,
                    "vba_backup_unavailable",
                    false);
            }

            var matches = BackupIntentTargets(backups)
                .Where(item => string.Equals(
                    item.Item2,
                    target,
                    StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (matches.Length == 0)
            {
                return VbaNativeOutcome.Error(
                    "The selected VBA backup target is no longer available. Run common.resources_find with scope=backups and choose one exact returned target.",
                    "vba_backup_target_not_found",
                    true);
            }
            if (matches.Length > 1)
            {
                return VbaNativeOutcome.Error(
                    "The selected VBA backup target is ambiguous. Run common.resources_find with scope=backups and choose one exact returned target.",
                    "vba_backup_target_ambiguous",
                    false);
            }

            backupId = matches[0].Item1.BackupId;
            moduleName = matches[0].Item1.ModuleName;
            return null;
        }

        internal string BackupSemanticTarget(string backupId)
        {
            backupId = (backupId ?? string.Empty).Trim();
            var matches = BackupIntentTargets(LoadBackups())
                .Where(item => string.Equals(
                    item.Item1.BackupId,
                    backupId,
                    StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();
            if (matches.Length != 1)
            {
                throw new InvalidOperationException(
                    "The selected VBA backup is no longer available.");
            }
            return matches[0].Item2;
        }

        private IReadOnlyList<VbaModuleBackup> LoadBackups()
        {
            if (_vbaJournalStore == null) return new VbaModuleBackup[0];
            return _vbaJournalStore.List(
                _adapter.HostName, _adapter.DocumentKey);
        }

        private static IReadOnlyList<Tuple<VbaModuleBackup, string>> BackupIntentTargets(
            IEnumerable<VbaModuleBackup> backups)
        {
            return (backups ?? new VbaModuleBackup[0])
                .Where(item => item != null)
                .Select(item => Tuple.Create(
                    item,
                    VbaResourceProvider.BackupSemanticTarget(item)))
                .ToArray();
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
