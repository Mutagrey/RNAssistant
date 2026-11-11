using System;
using System.IO;
using System.Linq;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Application
{
    // Existing resource/HTML owners supply verified metadata. This projection grants no access
    // and stores no session, revision or selected target; every operation keeps its owner guard.
    public static class BrowserTargetResolver
    {
        public static bool IsHtmlResource(ChatArtifact artifact)
        {
            if (artifact == null || !string.Equals(artifact.Kind, ChatArtifactKinds.Attachment, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(artifact.Kind, ChatArtifactKinds.File, StringComparison.OrdinalIgnoreCase)) return false;
            var mime = (artifact.MimeType ?? "").Split(';')[0].Trim();
            var extension = Path.GetExtension(artifact.Title ?? "");
            return mime.Equals("text/html", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".html", StringComparison.OrdinalIgnoreCase) || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase);
        }

        public static BrowserTargetDescriptor Saved(ChatSession source, ChatArtifact artifact, string exactUri)
        {
            RequireScope(source);
            if (artifact == null || artifact.Revision < 1 || !string.IsNullOrEmpty(artifact.WorkspaceId) ||
                !string.IsNullOrEmpty(artifact.DocumentAuthorityId) && artifact.DocumentAuthorityId != source.DocumentAuthorityId ||
                ChatResourceUri.CreateArtifactRevisionUri(source, artifact) != exactUri ||
                ChatResourceUri.ArtifactSnapshot(new ResourceRef(exactUri)).Uri != exactUri)
                throw new InvalidOperationException("Цель Browser не совпадает с точным источником документа.");
            var project = artifact.Kind == ChatArtifactKinds.HtmlWorkspace;
            if (!project && !IsHtmlResource(artifact))
                throw new InvalidOperationException("Этот ресурс не поддерживает HTML Browser.");
            if (!string.IsNullOrEmpty(artifact.AvailabilityIssue))
                throw new InvalidOperationException("Источник Browser недоступен: " + artifact.AvailabilityIssue);
            return new BrowserTargetDescriptor {
                Kind = project ? BrowserTargetKind.HtmlArtifact : BrowserTargetKind.HtmlResource,
                State = BrowserTargetState.Ready, ChatId = source.Id, DocumentId = source.DocumentAuthorityId ?? "",
                ResourceUri = exactUri, ArtifactId = artifact.Id, Revision = artifact.Revision,
                Capabilities = new BrowserTargetCapabilities { Source = true, Preview = project,
                    Versions = project && !string.IsNullOrEmpty(source.DocumentAuthorityId), ExportProject = project,
                    CreateProject = project && !string.IsNullOrEmpty(source.DocumentAuthorityId) }
            };
        }

        // Called after the existing HTML owner refreshes its exact selected publication.
        public static BrowserTargetDescriptor Live(ChatSession source)
        {
            RequireScope(source);
            var mutable = source.HtmlWorkspaceRecovery == null || source.HtmlWorkspaceRecovery.CanMutate;
            var id = source.ActiveHtmlArtifactId ?? "";
            var result = new BrowserTargetDescriptor { Kind = BrowserTargetKind.HtmlWorkspace,
                State = mutable && id.Length == 0 ? BrowserTargetState.Empty : BrowserTargetState.Unavailable,
                ChatId = source.Id, DocumentId = source.DocumentAuthorityId ?? "", ArtifactId = id,
                ResourceUri = "", Capabilities = new BrowserTargetCapabilities { Edit = mutable && id.Length == 0 } };
            if (id.Length == 0) return result;
            var matches = source.Artifacts.Where(item => item.Id == id).ToArray();
            if (matches.Length > 1) throw new InvalidOperationException("Неоднозначная цель HTML Browser.");
            if (matches.Length == 0) return result;
            var artifact = matches[0];
            if (artifact.Kind != ChatArtifactKinds.HtmlWorkspace)
                throw new InvalidOperationException("Выбранный источник не является HTML workspace.");
            result = Saved(source, artifact, ChatResourceUri.CreateArtifactRevisionUri(source, artifact));
            result.Kind = BrowserTargetKind.HtmlWorkspace;
            var files = source.HtmlWorkspace?.Files;
            result.Capabilities = new BrowserTargetCapabilities {
                Source = files != null && files.Count != 0, Preview = files != null && files.Any(file => file.Kind == "html"),
                Edit = mutable, Versions = true, Restore = mutable, ExportProject = true,
                Annotations = mutable && files != null && files.Any(file => file.Kind == "html")
            };
            return result;
        }

        private static void RequireScope(ChatSession source)
        {
            if (source == null || string.IsNullOrWhiteSpace(source.Id))
                throw new InvalidOperationException("Для Browser требуется явный чат источника.");
        }
    }
}
