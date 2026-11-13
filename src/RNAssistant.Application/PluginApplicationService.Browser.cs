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
                var workspace = RequireProjectWorkspace(target.WorkspaceId);
                var files = new WorkspaceFileService(_paths);
                if (target.Kind == BrowserTargetKind.PluginRelease)
                {
                    var release = _store.ReadPublishedRelease(target.PluginId, target.ReleaseId);
                    if (release.SourceWorkspaceId != workspace.WorkspaceId) throw new PluginException("browser_owner_changed", "Контекст исходника изменился.");
                    return BrowserSourceReader.Read(target, request, release.Files.Select(file => file.Path),
                        path => SourceComparisonService.Utf8(_store.ReadFile(release, path)));
                }
                if (target.Kind == BrowserTargetKind.PluginSource)
                {
                    var registration = RequireRegistration(target.PluginId);
                    if (registration.SourceWorkspaceId != workspace.WorkspaceId) throw new PluginException("browser_owner_changed", "Контекст исходника изменился.");
                    var manifest = PluginPackageFiles.ParseManifest(files.ReadText(workspace, "plugin.json").Text);
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
                    Conversations.GetSession(roots[0], caller.ChatId) == null)
                    throw new PluginException("browser_owner_changed", "Чат не принадлежит выбранному проекту.");
                var plugins = _store.List().Where(item => !item.Hidden).ToArray();
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
                        Capabilities = new BrowserTargetCapabilities { Source = true, Edit = !workspace.ReadOnly,
                            PreviewReason = reason, SaveReason = reason == BrowserRefusalCode.ProjectDefinitionRequired ? reason : BrowserRefusalCode.None } });
                }
                return targets;
            }
        }
    }
}
