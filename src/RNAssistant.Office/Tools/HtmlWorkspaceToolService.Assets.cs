using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.Tools
{
    internal sealed partial class HtmlWorkspaceToolService
    {
        private HtmlWorkspaceToolOutcome PublishAssetPackage(
            ChatSession session, IDictionary<string, object> arguments,
            Action markDispatchPossible)
        {
            if (_assets == null)
                throw new InvalidOperationException("Local HTML asset library is unavailable.");
            var requested = JArray.Parse(ToolArgumentReader.String(arguments, "files", "[]"))
                .Values<string>().ToArray();
            var workspace = NormalizedWorkspaceCopy(session.HtmlWorkspace);
            var selected = new List<HtmlAssetFile>();
            foreach (var requestedPath in requested)
            {
                var path = NormalizePath(requestedPath);
                var file = workspace.Files.SingleOrDefault(item => item != null &&
                    string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
                if (file == null)
                    return HtmlWorkspaceToolOutcome.Error(
                        "Workspace file was not found: " + path, null,
                        "html_asset_source_missing", false);
                selected.Add(new HtmlAssetFile
                { Path = file.Path, Kind = file.Kind, Content = file.Content });
            }
            var package = _assets.Publish(
                ToolArgumentReader.String(arguments, "id", string.Empty),
                ToolArgumentReader.String(arguments, "version", string.Empty),
                ToolArgumentReader.String(arguments, "title", string.Empty),
                ToolArgumentReader.String(arguments, "description", string.Empty),
                ToolArgumentReader.String(arguments, "usage", null),
                ToolArgumentReader.String(arguments, "license", null),
                ToolArgumentReader.String(arguments, "provenance", null),
                selected, markDispatchPossible);
            var summary = new HtmlAssetCatalogItem
            {
                Id = package.Id, Version = package.Version, Kind = package.Kind,
                Title = package.Title,
                Description = package.Description, Usage = package.Usage,
                License = package.License, Provenance = package.Provenance,
                Revision = package.Revision,
                FileCount = package.Files.Count,
                Files = package.Files.Select(file => file.Path).ToArray()
            };
            return HtmlWorkspaceToolOutcome.Ok(
                "Published local HTML asset package " + package.Id + "@" +
                package.Version + " for reuse across workspaces.",
                JsonConvert.SerializeObject(summary, new JsonSerializerSettings
                { ContractResolver = new CamelCasePropertyNamesContractResolver() }),
                HtmlWorkspaceEffect.VerifiedChange,
                new[] { _assets.CapturePublishedPackage(package) });
        }

        private HtmlWorkspaceToolOutcome ImportAssetPackage(
            ChatSession session, IDictionary<string, object> arguments,
            Action markDispatchPossible)
        {
            if (_assets == null)
                throw new InvalidOperationException("Local HTML asset library is unavailable.");
            var id = ToolArgumentReader.String(arguments, "id", string.Empty);
            var version = ToolArgumentReader.String(arguments, "version", string.Empty);
            var observed = _observedAssets.GetValue(session,
                _ => new Dictionary<string, string>(StringComparer.Ordinal));
            string expectedRevision;
            lock (observed)
                observed.TryGetValue(id + "@" + version, out expectedRevision);
            if (string.IsNullOrWhiteSpace(expectedRevision))
                return HtmlWorkspaceToolOutcome.Error(
                    "List this asset package before import to pin its current source.",
                    null, "html_asset_not_listed", false);
            var package = _assets.Load(id, version);
            if (!string.Equals(package.Revision, expectedRevision, StringComparison.Ordinal))
                return HtmlWorkspaceToolOutcome.Error(
                    "Asset package changed since catalog discovery. List it again before import.",
                    null, "html_asset_revision_changed", false);

            var workspace = NormalizedWorkspaceCopy(session.HtmlWorkspace);
            var now = DateTime.UtcNow;
            foreach (var assetFile in package.Files)
            {
                ValidateFile(assetFile.Path, assetFile.Kind, assetFile.Content);
                var path = NormalizePath(assetFile.Path);
                var fileId = FileId(path);
                if (workspace.Files.Any(file => file != null &&
                    string.Equals(file.Id, fileId, StringComparison.OrdinalIgnoreCase)))
                    return HtmlWorkspaceToolOutcome.Error(
                        "Asset import would replace existing workspace file: " + path +
                        ". Choose a new package path or resolve the conflict explicitly.",
                        null, "html_asset_file_conflict", false);
                workspace.Files.Add(new HtmlWorkspaceFile
                {
                    Id = fileId, Path = path, Kind = assetFile.Kind,
                    Content = assetFile.Content, CreatedUtc = now, UpdatedUtc = now
                });
            }
            ValidateWorkspaceCapacity(workspace, null, null, null, null);
            workspace.UpdatedUtc = now;
            NormalizeWorkspace(workspace);
            markDispatchPossible();
            session.HtmlWorkspace = workspace;
            HtmlWorkspaceArtifactService.CaptureCurrent(session,
                "HTML assets: " + package.Id + "@" + package.Version);
            return HtmlWorkspaceToolOutcome.Ok(
                "Imported " + package.Files.Count + " local HTML asset file(s) from " +
                package.Id + "@" + package.Version + " into this workspace. Review preflight and use the imported files deliberately.",
                WorkspaceMutationJson(session, "asset-package", package.Id + "@" + package.Version),
                HtmlWorkspaceEffect.VerifiedChange);
        }
    }
}
