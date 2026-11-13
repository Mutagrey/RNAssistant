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
                    if (manifest.Id != registration.PluginId || !manifest.Files.Contains(locator.RelativePath))
                        return new BrowserResolveResult { Reason = BrowserRefusalCode.TargetUnavailable, Message = "Файл не входит в состав плагина." };
                }
                target.EntryPath = manifest.Entry;
                return new BrowserResolveResult { Locator = locator.Canonical, Target = target };
            }
        }

        public BrowserSourcePage ReadBrowserSource(BrowserSourceRequest request, BrowserTargetDescriptor target)
        {
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
                var workspace = target.PluginId == null ? RequireProjectWorkspace(target.WorkspaceId) : Source(RequireRegistration(target.PluginId)).Workspace;
                if (target.Kind == BrowserTargetKind.PluginSource)
                {
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
                // The same declared member-set is used by Browser and checkpoint capture.
                // Entry-only legacy projects stay readable with ProjectDefinitionRequired for Save.
                var definition = _workspaces.GetHtmlProjectDefinition(workspace);
                return BrowserSourceReader.Read(target, request, definition.Definition?.Members ?? new[] { workspace.HtmlEntryPath },
                    path => files.ReadText(workspace, path).Text);
            }
        }

        // Metadata query. Does not Check, launch plugin scripts or capture project files.
        public IReadOnlyList<BrowserTargetDescriptor> BrowserTargets(BrowserOwnerScope caller)
        {
            using (PluginStore.AcquireRuntimeUse(_paths)) lock (_sync)
            {
                RequireOpen();
                if (caller == null || caller.Kind != BrowserOwnerKind.User && caller.Kind != BrowserOwnerKind.Workspace ||
                    caller.Kind == BrowserOwnerKind.User && (caller.ScopeId != "local" || !string.IsNullOrEmpty(caller.ChatId)))
                    throw new PluginException("browser_owner_invalid", "Выберите локальный контекст Browser.");
                var roots = caller.Kind == BrowserOwnerKind.User
                    ? Conversations.ListConnectedWorkspaces().Where(item => item.Available).Take(200)
                        .Select(item => _workspaces.OpenConnected(item.WorkspaceId)).ToArray()
                    : new[] { RequireProjectWorkspace(caller.ScopeId) };
                if (caller.Kind == BrowserOwnerKind.Workspace && !string.IsNullOrEmpty(caller.ChatId) &&
                    new ChatStore(_paths).ReadMetadata(caller.ChatId)?.WorkspaceId != roots[0].WorkspaceId)
                    throw new PluginException("browser_owner_changed", "Чат не принадлежит выбранному проекту.");
                var plugins = _store.List().Where(item => !item.Hidden).ToArray();
                if (caller.Kind == BrowserOwnerKind.User)
                    roots = roots.Concat(plugins.Take(200).Select(item => Source(item).Workspace)).GroupBy(item => item.WorkspaceId).Select(group => group.First()).ToArray();
                var files = new WorkspaceFileService(_paths);
                var targets = new List<BrowserTargetDescriptor>();
                foreach (var workspace in roots)
                {
                    var plugin = plugins.FirstOrDefault(item => item.SourceWorkspaceId == workspace.WorkspaceId);
                    var scope = new BrowserOwnerScope { Kind = BrowserOwnerKind.Workspace, ScopeId = workspace.WorkspaceId,
                        ChatId = caller.Kind == BrowserOwnerKind.Workspace ? caller.ChatId : null };
                    string entry = workspace.HtmlEntryPath;
                    BrowserRefusalCode reason = BrowserRefusalCode.ProjectDefinitionRequired;
                    if (plugin != null)
                    {
                        try
                        {
                            var manifest = PluginPackageFiles.ParseManifest(files.ReadText(workspace, "plugin.json").Text);
                            if (manifest.Id != plugin.PluginId) continue;
                            entry = manifest.Entry; reason = BrowserRefusalCode.IsolationUnqualified;
                        }
                        catch (Exception error) when (error is PluginException || error is WorkspaceFileException)
                        { continue; }
                    }
                    if (string.IsNullOrEmpty(entry)) continue;
                    targets.Add(new BrowserTargetDescriptor { Kind = plugin == null ? BrowserTargetKind.WorkspaceHtmlProject : BrowserTargetKind.PluginSource,
                        State = BrowserTargetState.Ready, OwnerScope = scope, WorkspaceId = workspace.WorkspaceId, ChatId = scope.ChatId,
                        PluginId = plugin?.PluginId, EntryPath = entry, RelativePath = entry,
                        Title = plugin?.Appearance?.Name ?? plugin?.Name ?? workspace.Name,
                        DisplayLocator = plugin == null ? BrowserLocalLocator.ForProject(workspace.WorkspaceId, entry) : BrowserLocalLocator.ForPlugin(plugin.PluginId, entry),
                        Capabilities = new BrowserTargetCapabilities { Source = true, Edit = workspace?.ReadOnly != true,
                            Versions = plugin != null, SaveVersion = plugin != null && workspace?.ReadOnly != true, ActivateVersion = plugin != null,
                            PreviewReason = reason, SaveReason = reason == BrowserRefusalCode.ProjectDefinitionRequired ? reason : BrowserRefusalCode.None } });
                }
                return targets;
            }
        }
    }
}
