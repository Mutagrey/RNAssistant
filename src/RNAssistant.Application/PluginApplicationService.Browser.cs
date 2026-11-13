using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Application
{
    public sealed partial class PluginApplicationService
    {
        public BrowserResolveResult ResolveBrowserLocator(BrowserResolveRequest request)
        {
            using (PluginStore.AcquireRuntimeUse(_paths)) lock (_sync)
            {
                RequireOpen();
                BrowserLocalLocator locator;
                if (!BrowserLocalLocator.TryParse(request?.Locator, out locator))
                    return new BrowserResolveResult { Reason = BrowserRefusalCode.InvalidLocator, Message = "Укажите локальный адрес Browser." };
                var caller = request.OwnerScope;
                if (caller == null || caller.Kind != BrowserOwnerKind.User && caller.Kind != BrowserOwnerKind.Workspace ||
                    caller.Kind == BrowserOwnerKind.User && (caller.ScopeId != "local" || !string.IsNullOrEmpty(caller.ChatId)))
                    return new BrowserResolveResult { Reason = BrowserRefusalCode.OutsideScope, Message = "Контекст Browser недоступен." };
                if (locator.Kind == BrowserLocatorKind.Home)
                    return BrowserTargetResolver.ResolveLocalLocator(caller, "/", null);
                if (locator.Kind == BrowserLocatorKind.ProjectSource || locator.Kind == BrowserLocatorKind.ProjectVersion)
                    return HtmlProjects.Resolve(request);
                if (locator.Kind != BrowserLocatorKind.PluginSource && locator.Kind != BrowserLocatorKind.PluginVersion)
                    return BrowserTargetResolver.ResolveLocalLocator(caller, request.Locator, BrowserTargets(caller));
                var registration = _store.Get(locator.Key);
                if (registration == null || registration.Hidden || caller.Kind == BrowserOwnerKind.Workspace && caller.ScopeId != registration.SourceWorkspaceId)
                    return new BrowserResolveResult { Reason = BrowserRefusalCode.TargetUnavailable, Message = "Плагин недоступен в этом контексте." };
                var workspace = locator.VersionId == null ? Source(registration).Workspace : null;
                var workspaceId = registration.SourceWorkspaceId;
                if (!string.IsNullOrEmpty(caller.ChatId) && new ChatStore(_paths).ReadMetadata(caller.ChatId)?.WorkspaceId != workspaceId)
                    return new BrowserResolveResult { Reason = BrowserRefusalCode.OutsideScope, Message = "Чат принадлежит другому проекту." };
                var target = new BrowserTargetDescriptor { PluginId = registration.PluginId, WorkspaceId = workspaceId,
                    OwnerScope = new BrowserOwnerScope { Kind = BrowserOwnerKind.Workspace, ScopeId = workspaceId, ChatId = caller.ChatId },
                    ChatId = caller.ChatId, Title = registration.Appearance?.Name ?? registration.Name,
                    DisplayLocator = locator.ContentLocator, RelativePath = locator.RelativePath, SavedVersionId = locator.VersionId,
                    Kind = locator.Kind == BrowserLocatorKind.PluginVersion ? BrowserTargetKind.PluginRelease : BrowserTargetKind.PluginSource,
                    State = BrowserTargetState.Ready, Immutable = locator.Kind == BrowserLocatorKind.PluginVersion,
                    Capabilities = new BrowserTargetCapabilities { Source = true, Versions = true,
                        SaveVersion = locator.VersionId == null && workspace?.ReadOnly != true, Edit = locator.VersionId == null && workspace?.ReadOnly != true,
                        ActivateVersion = locator.VersionId != null, PreviewReason = BrowserRefusalCode.IsolationUnqualified,
                        SaveReason = locator.VersionId != null || workspace?.ReadOnly == true ? BrowserRefusalCode.ReadOnly : BrowserRefusalCode.None } };
                PluginManifestV1 manifest;
                if (locator.VersionId != null)
                {
                    var saved = _store.SavedVersion(registration.PluginId, locator.VersionId, true);
                    if (saved.Removed)
                    {
                        target.State = BrowserTargetState.Removed; target.Reason = BrowserRefusalCode.Removed;
                        target.Message = "Сохранённая версия удалена."; target.Capabilities = new BrowserTargetCapabilities { Versions = true, SaveReason = BrowserRefusalCode.Removed };
                        return new BrowserResolveResult { Locator = locator.Canonical, Target = target, Reason = target.Reason, Message = target.Message };
                    }
                    var release = _store.ReadSavedRelease(registration.PluginId, locator.VersionId);
                    manifest = PluginPackageFiles.ParseManifest(release.ManifestJson);
                    target.ReleaseId = release.ReleaseId; target.ContentRevision = saved.ContentFingerprint;
                    if (!release.Files.Any(file => file.Path == locator.RelativePath))
                        return new BrowserResolveResult { Reason = BrowserRefusalCode.TargetUnavailable, Message = "Файла нет в сохранённой версии." };
                }
                else
                {
                    try { manifest = PluginPackageFiles.ParseManifest(new WorkspaceFileService(_paths).ReadText(workspace, "plugin.json").Text); }
                    catch (PluginException error)
                    {
                        target.EntryPath = "plugin.json"; target.Capabilities.SaveVersion = false;
                        target.Capabilities.SaveReason = BrowserRefusalCode.TargetUnavailable; target.Message = error.Message;
                        return new BrowserResolveResult { Locator = locator.Canonical, Target = target };
                    }
                    if (manifest.Id != registration.PluginId || locator.RelativePath != "plugin.json" && !manifest.Files.Contains(locator.RelativePath))
                        return new BrowserResolveResult { Reason = BrowserRefusalCode.TargetUnavailable, Message = "Файл не входит в состав плагина." };
                }
                target.EntryPath = manifest.Entry;
                return new BrowserResolveResult { Locator = locator.Canonical, Target = target };
            }
        }

        public BrowserSourcePage ReadBrowserSource(BrowserSourceRequest request, BrowserTargetDescriptor target)
        {
            if (target?.Kind == BrowserTargetKind.WorkspaceHtmlProject) return HtmlProjects.Source(request, target);
            using (PluginStore.AcquireRuntimeUse(_paths)) lock (_sync)
            {
                RequireOpen();
                if (target?.OwnerScope == null || target.State != BrowserTargetState.Ready)
                    throw new PluginException("browser_source_unavailable", "Исходник недоступен.");
                if (request?.OwnerScope == null || !(request.OwnerScope.Kind == BrowserOwnerKind.User
                    ? request.OwnerScope.ScopeId == "local" && string.IsNullOrEmpty(request.OwnerScope.ChatId)
                    : request.OwnerScope.Matches(target.OwnerScope)))
                    throw new PluginException("browser_owner_changed", "Контекст исходника изменился.");
                var files = new WorkspaceFileService(_paths);
                if (target.Kind == BrowserTargetKind.PluginRelease)
                {
                    var release = _store.ReadSavedRelease(target.PluginId, target.SavedVersionId);
                    if (release.SourceWorkspaceId != target.WorkspaceId) throw new PluginException("browser_owner_changed", "Контекст исходника изменился.");
                    return BrowserSourceReader.Read(target, request, release.Files.Select(file => file.Path),
                        path => SourceComparisonService.Utf8(_store.ReadFile(release, path)));
                }
                if (target.Kind == BrowserTargetKind.PluginSource)
                {
                    var workspace = Source(RequireRegistration(target.PluginId)).Workspace;
                    var registration = RequireRegistration(target.PluginId);
                    if (registration.SourceWorkspaceId != workspace.WorkspaceId) throw new PluginException("browser_owner_changed", "Контекст исходника изменился.");
                    PluginManifestV1 manifest;
                    try { manifest = PluginPackageFiles.ParseManifest(files.ReadText(workspace, "plugin.json").Text); }
                    catch (PluginException)
                    {
                        return BrowserSourceReader.Read(target, request, new[] { "plugin.json" }, path => files.ReadText(workspace, path).Text);
                    }
                    if (manifest.Id != target.PluginId) throw new PluginException("browser_owner_changed", "Идентичность плагина изменилась.");
                    return BrowserSourceReader.Read(target, request, manifest.Files.Concat(new[] { "plugin.json" }).Distinct(StringComparer.Ordinal),
                        path => files.ReadText(workspace, path).Text);
                }
                throw new PluginException("browser_source_unavailable", "Этот источник не принадлежит владельцу плагинов.");
            }
        }

        // Metadata query. Does not Check, launch plugin scripts or capture project files.
        public SourceComparisonDto CompareBrowserSources(BrowserSourceComparisonRequest request)
        {
            SourceComparisonService.Validate(request);
            if (request.Target?.Kind == BrowserTargetKind.WorkspaceHtmlProject) return HtmlProjects.Compare(request);
            var target = ResolveBrowserLocator(new BrowserResolveRequest { OwnerScope = request.Target?.OwnerScope, Locator = request.Target?.DisplayLocator }).Target;
            if (target?.Kind != BrowserTargetKind.PluginRelease || target.State != BrowserTargetState.Ready ||
                target.PluginId != request.Target.PluginId || target.SavedVersionId != request.AfterId)
                throw new InvalidOperationException("Выберите точную сохранённую версию плагина.");
            return CompareSources(new PluginSourceComparisonRequest { PluginId = target.PluginId,
                BeforeId = request.BeforeId, AfterId = request.AfterId, Path = request.Path });
        }

        public IReadOnlyList<BrowserTargetDescriptor> BrowserTargets(BrowserOwnerScope caller, string suggestionsPrefix = null)
        {
            using (PluginStore.AcquireRuntimeUse(_paths)) lock (_sync)
            {
                RequireOpen();
                if (caller == null || caller.Kind != BrowserOwnerKind.User && caller.Kind != BrowserOwnerKind.Workspace ||
                    caller.Kind == BrowserOwnerKind.User && (caller.ScopeId != "local" || !string.IsNullOrEmpty(caller.ChatId)))
                    throw new PluginException("browser_owner_invalid", "Выберите локальный контекст Browser.");
                if (caller.Kind == BrowserOwnerKind.Workspace && !string.IsNullOrEmpty(caller.ChatId) &&
                    new ChatStore(_paths).ReadMetadata(caller.ChatId)?.WorkspaceId != caller.ScopeId)
                    throw new PluginException("browser_owner_changed", "Чат не принадлежит выбранному проекту.");
                var plugins = _store.List().Where(item => !item.Hidden).ToArray();
                var ids = caller.Kind == BrowserOwnerKind.User ? _workspaces.ListConnectedMetadata().Take(200).Select(w => w.WorkspaceId)
                    .Concat(plugins.Take(200).Select(p => p.SourceWorkspaceId)).Distinct(StringComparer.Ordinal).ToArray() : new[] { caller.ScopeId };
                var targets = new List<BrowserTargetDescriptor>(); var savedFamilies = 0;
                foreach (var workspaceId in ids)
                {
                    WorkspaceDescriptor workspace;
                    try { workspace = _workspaces.OpenConnectedHistory(workspaceId); }
                    catch (Exception error) when (error is System.IO.IOException || error is PluginException || error is ArgumentException) { continue; }
                    var plugin = plugins.FirstOrDefault(item => item.SourceWorkspaceId == workspaceId);
                    var scope = new BrowserOwnerScope { Kind = BrowserOwnerKind.Workspace, ScopeId = workspaceId,
                        ChatId = caller.Kind == BrowserOwnerKind.Workspace ? caller.ChatId : null };
                    var title = plugin?.Appearance?.Name ?? plugin?.Name ?? workspace.Name;
                    var family = plugin == null ? "/projects/" + Uri.EscapeDataString(workspaceId) : "/plugins/" + Uri.EscapeDataString(plugin.PluginId);
                    if (suggestionsPrefix != null && savedFamilies < 8 && BrowserTargetResolver.SuggestionFamilyMatches(suggestionsPrefix, title, family))
                    {
                        savedFamilies++;
                        if (plugin == null) targets.AddRange(HtmlProjects.SavedBrowserTargets(scope, title, 8));
                        else foreach (var entry in _store.SuggestVersions(plugin.PluginId, 8).Items)
                        {
                            var release = _store.ReadSavedRelease(plugin.PluginId, entry.VersionId); // manifest metadata only, no member bytes
                            var manifest = PluginPackageFiles.ParseManifest(release.ManifestJson);
                            targets.Add(new BrowserTargetDescriptor { Kind = BrowserTargetKind.PluginRelease, State = BrowserTargetState.Ready,
                                OwnerScope = scope, PluginId = plugin.PluginId, WorkspaceId = workspaceId, SavedVersionId = entry.VersionId,
                                DisplayLocator = BrowserLocalLocator.ForPlugin(plugin.PluginId, manifest.Entry, entry.VersionId), Title = title + " · v" + entry.Ordinal });
                        }
                    }
                    string entryPath = plugin == null ? null : "plugin.json";
                    try { if (plugin == null) entryPath = _workspaces.GetHtmlProjectDefinition(_workspaces.OpenConnected(workspaceId)).Definition?.EntryPath; }
                    catch (Exception error) when (error is System.IO.IOException || error is PluginException || error is InvalidOperationException) { }
                    if (entryPath == null) continue;
                    var sourceAvailable = System.IO.Directory.Exists(workspace.RootPath) && !StorageFileSystem.IsReparsePoint(workspace.RootPath);
                    targets.Add(new BrowserTargetDescriptor { Kind = plugin == null ? BrowserTargetKind.WorkspaceHtmlProject : BrowserTargetKind.PluginSource,
                        State = sourceAvailable ? BrowserTargetState.Ready : BrowserTargetState.Unavailable,
                        Reason = sourceAvailable ? BrowserRefusalCode.None : BrowserRefusalCode.TargetUnavailable, OwnerScope = scope, WorkspaceId = workspaceId, ChatId = scope.ChatId,
                        PluginId = plugin?.PluginId, EntryPath = entryPath, RelativePath = entryPath, Title = title,
                        DisplayLocator = plugin == null ? BrowserLocalLocator.ForProject(workspaceId, entryPath) : BrowserLocalLocator.ForPlugin(plugin.PluginId, entryPath),
                        Capabilities = new BrowserTargetCapabilities { Source = true, Versions = true, PreviewReason = BrowserRefusalCode.IsolationUnqualified } });
                }
                return targets;
            }
        }
    }
}
