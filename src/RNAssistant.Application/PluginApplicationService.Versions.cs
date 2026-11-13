using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Application
{
    public sealed partial class PluginApplicationService
    {
        public event Action<BrowserVersionStateChanged> VersionChanged;
        private void NotifyVersionChanged(string plugin, BrowserVersionAction action, string versionId = null)
        {
            try
            {
                var registration = _store.Get(plugin);
                var message = new BrowserVersionStateChanged { Target = new BrowserTargetDescriptor { Kind = BrowserTargetKind.PluginSource, State = BrowserTargetState.Ready, PluginId = plugin,
                    WorkspaceId = registration.SourceWorkspaceId, OwnerScope = new BrowserOwnerScope { Kind = BrowserOwnerKind.Workspace, ScopeId = registration.SourceWorkspaceId } },
                    StateRevision = _store.VersionState(plugin).StateRevision, Reason = action, VersionId = versionId };
                // Publication is already verified. A dropped UI notification cannot undo it.
                VersionChanged?.Invoke(message);
            } catch { }
        }

        // Ephemeral intent, never a second durable catalog or an approval restored on restart.
        private readonly Dictionary<string, SavedIntent> _savedIntents = new Dictionary<string, SavedIntent>();
        private readonly Dictionary<string, SavedPreparation> _savedPreparations = new Dictionary<string, SavedPreparation>();
        private sealed class SavedIntent { public string Plugin, Version, Chat, Run; }
        private sealed class SavedPreparation
        {
            public int Contract = 2;
            public string Plugin, Workspace, Chat, Run, Operation, Version, ActiveVersion, ActiveRelease, ActiveRevision;
            public string Candidate, Content, Catalog, Project;
        }

        public BrowserVersionState BrowserVersionState(BrowserVersionRequest request, CancellationToken token = default(CancellationToken))
        {
            if (request?.Target?.Kind == BrowserTargetKind.WorkspaceHtmlProject) return HtmlProjects.State(request, token);
            using (PluginStore.AcquireRuntimeUse(_paths)) lock (_sync)
            {
                var target = RequirePluginVersionTarget(request?.Target);
                PruneSavedIntents();
                var state = _store.VersionState(target.PluginId);
                var registration = RequireRegistration(target.PluginId);
                string content = null;
                if (target.Kind == BrowserTargetKind.PluginSource)
                {
                    try { var current = _store.Capture(Source(registration), Vendors, retain: false, token: token); content = current.PackageSha256; }
                    catch (Exception error) when (error is PluginException || error is WorkspaceFileException || error is System.IO.IOException)
                    { target.Capabilities.SaveVersion = false; target.Capabilities.SaveReason = BrowserRefusalCode.TargetUnavailable; target.Message = error.Message; }
                }
                else if (target.State == BrowserTargetState.Ready)
                    content = _store.SavedVersion(target.PluginId, target.SavedVersionId).ContentFingerprint;
                return VersionState(target, state, registration, content);
            }
        }

        public BrowserVersionPage BrowserVersions(BrowserVersionRequest request)
        {
            if (request?.Target?.Kind == BrowserTargetKind.WorkspaceHtmlProject) return HtmlProjects.Versions(request);
            using (PluginStore.AcquireRuntimeUse(_paths)) lock (_sync)
            {
                var target = RequirePluginVersionTarget(request?.Target);
                PruneSavedIntents();
                var page = _store.ListVersions(target.PluginId, request.Cursor, request.Limit);
                var registration = RequireRegistration(target.PluginId);
                return new BrowserVersionPage { Items = page.Items.Select(entry => VersionEntry(registration, entry)).ToArray(),
                    Cursor = page.Cursor, StateRevision = page.StateRevision };
            }
        }

        public BrowserVersionResult BrowserVersionCommand(BrowserVersionCommand request, CancellationToken token = default(CancellationToken))
        {
            if (request?.Target?.Kind == BrowserTargetKind.WorkspaceHtmlProject) return HtmlProjects.Command(request, token);
            using (PluginStore.AcquireRuntimeUse(_paths)) lock (_sync)
            {
                token.ThrowIfCancellationRequested();
                var target = RequirePluginVersionTarget(request?.Target);
                PruneSavedIntents();
                SavedVersionOperation committed = null;
                try
                {
                    SavedVersionOperation result;
                    var registration = RequireRegistration(target.PluginId);
                    switch (request.Action)
                    {
                        case BrowserVersionAction.Save:
                            if (target.Kind != BrowserTargetKind.PluginSource || !target.Capabilities.SaveVersion)
                                throw new SavedVersionException("version_read_only", "Выберите рабочую копию для сохранения версии.");
                            result = SavePluginVersion(target.PluginId, request.ExpectedStateRevision, request.ExpectedContentRevision,
                                request.OperationId, new SavedVersionActor { Kind = SavedVersionActorKind.User }, token: token);
                            break;
                        case BrowserVersionAction.Activate:
                            if (!request.Confirmed) throw new SavedVersionException("version_confirmation_required", "Подтвердите применение выбранной версии.");
                            result = ActivatePluginVersion(target.PluginId, request.VersionId, request.ExpectedActiveRevision,
                                request.ExpectedActiveVersionId, request.ExpectedActiveReleaseId, request.OperationId, token: token);
                            break;
                        case BrowserVersionAction.Delete:
                            result = _store.DeleteVersion(target.PluginId, request.VersionId, request.ExpectedStateRevision,
                                request.ExpectedActiveVersionId, request.ExpectedActiveReleaseId, request.OperationId,
                                request.Confirmed, VersionInUse(target.PluginId, request.VersionId));
                            NotifyVersionChanged(target.PluginId, BrowserVersionAction.Delete, request.VersionId);
                            break;
                        default: throw new ArgumentOutOfRangeException(nameof(request.Action));
                    }
                    committed = result;
                    registration = RequireRegistration(target.PluginId);
                    return new BrowserVersionResult { Outcome = (BrowserVersionOutcome)Enum.Parse(typeof(BrowserVersionOutcome), result.Outcome),
                        Version = VersionEntry(registration, result.Version),
                        State = VersionState(target, _store.VersionState(target.PluginId), registration, null) };
                }
                catch (SavedVersionException error)
                {
                    return new BrowserVersionResult { Outcome = error.MayHavePublished || committed != null ? BrowserVersionOutcome.Unknown : BrowserVersionOutcome.Rejected,
                        Reason = committed != null ? BrowserRefusalCode.OperationUnknown : VersionReason(error.Code), Message = error.Message };
                }
                catch (Exception error) when (committed != null)
                { return new BrowserVersionResult { Outcome = BrowserVersionOutcome.Unknown, Reason = BrowserRefusalCode.OperationUnknown,
                    Message = "Публикация выполнена, но состояние не удалось перечитать. Проверьте исходную операцию. " + error.Message }; }
            }
        }

        private SavedVersionOperation SavePluginVersion(string plugin, string catalog, string content, string operation,
            SavedVersionActor actor, Action beforeDispatch = null, CancellationToken token = default(CancellationToken))
        {
            var result = _store.SaveVersion(plugin, Source(RequireRegistration(plugin)), Vendors, catalog, content, operation, actor,
                _candidates.Values.Where(item => item.Package.PluginId == plugin).Select(item => item.Package).ToArray(), token, beforeDispatch);
            if (actor.Kind == SavedVersionActorKind.Run)
            {
                PruneSavedIntents();
                var key = plugin + ":" + actor.ChatId + ":" + actor.RunId;
                if (_savedIntents.Count >= 128 && !_savedIntents.ContainsKey(key))
                    throw new SavedVersionException("version_operation_unknown", "Saved; runtime intent limit reached. Select its ordinal explicitly.", true);
                _savedIntents[key] = new SavedIntent { Plugin = plugin, Version = result.VersionId, Chat = actor.ChatId, Run = actor.RunId };
            }
            NotifyVersionChanged(plugin, BrowserVersionAction.Save);
            return result;
        }

        private SavedVersionOperation ActivatePluginVersion(string plugin, string version, string activeRevision,
            string activeVersion, string activeRelease, string operation, Action beforeDispatch = null, CancellationToken token = default(CancellationToken))
        {
            var result = _store.ActivateVersion(plugin, version, activeRevision, activeVersion, activeRelease, operation, beforeDispatch, token);
            _revoked.Remove(plugin);
            NotifyVersionChanged(plugin, BrowserVersionAction.Activate);
            return result;
        }

        private BrowserVersionState VersionState(BrowserTargetDescriptor target, SavedVersionCatalogState catalog,
            PluginRegistration registration, string content)
        {
            var latest = catalog.LatestAvailableVersionId == null ? null : _store.SavedVersion(registration.PluginId, catalog.LatestAvailableVersionId);
            var active = registration.ActiveSavedVersionId == null ? null : _store.SavedVersion(registration.PluginId, registration.ActiveSavedVersionId);
            return new BrowserVersionState { Target = target, StateRevision = catalog.StateRevision,
                ActiveStateRevision = registration.ActiveRevision, ContentRevision = content,
                Dirty = target.Kind == BrowserTargetKind.PluginSource && content != null && latest?.ContentFingerprint != content,
                LatestAvailableSavedVersion = VersionEntry(registration, latest, content),
                ActiveSavedVersion = VersionEntry(registration, active, content), Capabilities = target.Capabilities };
        }

        private BrowserVersionEntry VersionEntry(PluginRegistration plugin, SavedVersionRecord entry, string content = null)
        {
            if (entry == null) return null;
            var active = plugin.ActiveSavedVersionId == entry.VersionId;
            var inUse = VersionInUse(plugin.PluginId, entry.VersionId);
            return new BrowserVersionEntry { VersionId = entry.VersionId, Ordinal = entry.Ordinal, Label = entry.Label,
                CreatedUtc = entry.CreatedUtc, ManifestVersion = entry.ManifestVersion, Removed = entry.Removed,
                ReleaseId = Uri.UnescapeDataString(entry.ContentReference.Uri.Split('/').Last()),
                ContentRevision = entry.ContentFingerprint, Current = content != null && content == entry.ContentFingerprint,
                Active = active, CanDelete = !entry.Removed && !active && !inUse,
                DeleteBlockedReason = entry.Removed ? BrowserRefusalCode.Removed : active ? BrowserRefusalCode.ActiveVersion :
                    inUse ? BrowserRefusalCode.VersionInUse : BrowserRefusalCode.None };
        }

        private BrowserTargetDescriptor RequirePluginVersionTarget(BrowserTargetDescriptor requested)
        {
            RequireOpen();
            BrowserLocalLocator locator;
            var scope = requested?.OwnerScope;
            var registration = _store.Get(requested?.PluginId ?? "");
            if (!BrowserLocalLocator.TryParse(requested?.DisplayLocator, out locator) || registration == null || registration.Hidden ||
                locator.Key != registration.PluginId || locator.VersionId != requested.SavedVersionId || requested.WorkspaceId != registration.SourceWorkspaceId ||
                locator.Kind != BrowserLocatorKind.PluginSource && locator.Kind != BrowserLocatorKind.PluginVersion || scope == null ||
                scope.Kind != BrowserOwnerKind.Workspace || scope.ScopeId != registration.SourceWorkspaceId ||
                !string.IsNullOrEmpty(scope.ChatId) && new ChatStore(_paths).ReadMetadata(scope.ChatId)?.WorkspaceId != scope.ScopeId)
                throw new PluginException("browser_owner_changed", "Выберите точный источник плагина заново.");
            var removed = locator.VersionId != null && _store.SavedVersion(registration.PluginId, locator.VersionId, true).Removed;
            var writable = locator.VersionId == null && !Source(registration).Workspace.ReadOnly;
            var target = new BrowserTargetDescriptor { Kind = locator.VersionId == null ? BrowserTargetKind.PluginSource : BrowserTargetKind.PluginRelease,
                State = removed ? BrowserTargetState.Removed : BrowserTargetState.Ready, PluginId = registration.PluginId,
                WorkspaceId = registration.SourceWorkspaceId, OwnerScope = scope, DisplayLocator = locator.ContentLocator,
                SavedVersionId = locator.VersionId, EntryPath = requested.EntryPath, RelativePath = locator.RelativePath,
                Capabilities = new BrowserTargetCapabilities { Source = !removed, Versions = true, SaveVersion = writable,
                    ActivateVersion = locator.VersionId != null && !removed, PreviewReason = BrowserRefusalCode.IsolationUnqualified,
                    SaveReason = writable ? BrowserRefusalCode.None : BrowserRefusalCode.ReadOnly } };
            return target;
        }

        private bool VersionInUse(string plugin, string version)
        {
            return _instances.Values.Any(item => item.Manifest.Id == plugin && item.SavedVersionId == version &&
                !item.Token.IsCancellationRequested) ||
                _savedPreparations.Values.Any(item => item.Plugin == plugin && item.Version == version);
        }

        private bool RunOwns(string plugin, string chat, string run, string workspace = null)
        {
            if (string.IsNullOrEmpty(chat) || string.IsNullOrEmpty(run)) return false;
            var header = new ChatStore(_paths).ReadSessionHeader(chat);
            if (header?.WorkspaceId != (workspace ?? _store.Get(plugin)?.SourceWorkspaceId) || header?.RunId != run) return false;
            var lifecycle = header.RunViewState?.Lifecycle ?? header.RunStatus;
            return lifecycle == RunViewLifecycles.Running || lifecycle == RunViewLifecycles.AwaitingConfirmation || lifecycle == RunViewLifecycles.AwaitingUser;
        }

        private void PruneSavedIntents()
        {
            foreach (var pair in _savedIntents.Where(item => !RunOwns(item.Value.Plugin, item.Value.Chat, item.Value.Run)).ToArray()) _savedIntents.Remove(pair.Key);
            foreach (var pair in _savedPreparations.Where(item => !RunOwns(item.Value.Plugin, item.Value.Chat, item.Value.Run, item.Value.Workspace)).ToArray()) _savedPreparations.Remove(pair.Key);
        }

        private SavedVersionRecord SelectSavedVersion(string plugin, string chat, string run, string semantic)
        {
            if (string.IsNullOrWhiteSpace(semantic))
            {
                SavedIntent intent;
                if (!_savedIntents.TryGetValue(plugin + ":" + chat + ":" + run, out intent))
                    throw new SavedVersionException("saved_version_required", "Save this run's checked source first, or select a saved version by ordinal/label.");
                return _store.SavedVersion(plugin, intent.Version);
            }
            long ordinal; var numeric = long.TryParse(semantic.TrimStart('v', 'V'), out ordinal);
            string cursor = null; SavedVersionRecord match = null;
            do
            {
                var page = _store.ListVersions(plugin, cursor, 50); cursor = page.Cursor;
                foreach (var entry in page.Items.Where(item => numeric ? item.Ordinal == ordinal : item.Label == semantic))
                { if (match != null) throw new SavedVersionException("version_ambiguous", "Use the unique version ordinal."); match = entry; }
                if (numeric && match != null) break;
            } while (cursor != null);
            return match ?? throw new SavedVersionException("saved_version_required", "The selected saved version is unavailable or removed.");
        }

        private static BrowserRefusalCode VersionReason(string code)
        {
            switch (code)
            {
                case "version_removed": return BrowserRefusalCode.Removed;
                case "version_active": return BrowserRefusalCode.ActiveVersion;
                case "version_in_use": return BrowserRefusalCode.VersionInUse;
                case "version_operation_unknown": return BrowserRefusalCode.OperationUnknown;
                case "saved_version_required": return BrowserRefusalCode.SavedVersionRequired;
                case "version_read_only": return BrowserRefusalCode.ReadOnly;
                default: return BrowserRefusalCode.StaleBinding;
            }
        }
    }
}
