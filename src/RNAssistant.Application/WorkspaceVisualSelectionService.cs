using System;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;

namespace RNAssistant.Application
{
    // Source validation shared by staging, chat intake and retained presentation.
    public static class WorkspaceVisualSelectionService
    {
        public static ResourceRef RequireSource(AppDataPaths paths, WorkspaceDescriptor workspace, ChatAttachment attachment)
        {
            var source = attachment?.VisualSelectionSource;
            ResourceAddress address;
            if (source?.IsExact != true || !ResourceUri.TryParse(source.Uri, out address))
                throw new InvalidOperationException("Точный источник аннотации недоступен.");
            var page = attachment.VisualSelectionPage;
            if (page != null && (page.WorkspaceId != workspace.WorkspaceId || !BrowserContentSession.ValidPath(page.Path)))
                throw new InvalidOperationException("Страница аннотации принадлежит другому проекту.");
            page?.Identity(source);
            if (address.Provider == "plugin" && address.Segments.Count == 3 && address.Segments[1] == "release" && source.Revision == "1") {
                var plugins = new PluginStore(paths);
                var release = plugins.ReadRelease(new PluginReleaseIdentity(address.Segments[0], address.Segments[2]));
                if (release.SourceWorkspaceId != workspace.WorkspaceId || plugins.Get(address.Segments[0])?.SourceWorkspaceId != workspace.WorkspaceId)
                    throw new InvalidOperationException("Аннотация принадлежит другому workspace.");
            } else if (address.Provider == "file" && page != null) {
                // A newer working file cannot replace the captured source.
                new WorkspaceFileService(paths).DescribeExactSource(workspace, page.Path, source);
            } else throw new InvalidOperationException("Источник аннотации не поддерживается.");
            return source;
        }
    }
}
