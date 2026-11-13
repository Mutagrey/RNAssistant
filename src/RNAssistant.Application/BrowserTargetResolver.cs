using System;
using System.IO;
using System.Linq;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;

namespace RNAssistant.Application
{
    // Existing resource/HTML owners supply verified metadata. This projection grants no access
    // and stores no session, revision or selected target; every operation keeps its owner guard.
    public static partial class BrowserTargetResolver
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
            return Saved(ConversationMetadata.From(source), artifact, exactUri);
        }
        public static BrowserTargetDescriptor Saved(ConversationMetadata source, ChatArtifact artifact, string exactUri)
        {
            if (string.IsNullOrWhiteSpace(source?.Id)) throw new InvalidOperationException("Для Browser требуется явный чат источника.");
            if (artifact == null || artifact.Revision < 1 || !string.IsNullOrEmpty(artifact.WorkspaceId) ||
                !string.IsNullOrEmpty(artifact.DocumentAuthorityId) && artifact.DocumentAuthorityId != source.DocumentAuthorityId ||
                ChatResourceUri.CreateArtifactRevision(source.Id, artifact).Uri != exactUri ||
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
                LogicalId = project ? HtmlWorkspaceIdentity.LogicalId(artifact.Id) : null,
                OwnerScope = new BrowserOwnerScope { Kind = BrowserOwnerKind.Document, ScopeId = source.DocumentAuthorityId ?? source.Id, ChatId = source.Id },
                ContentRevision = artifact.Id + ":" + artifact.Revision, Immutable = true, Title = artifact.Title,
                DisplayLocator = BrowserLocalLocator.ForResource(artifact.Id + "~" + artifact.Revision),
                Capabilities = new BrowserTargetCapabilities { Source = true, PreviewReason = BrowserRefusalCode.NativeUnavailable,
                    Versions = project && !string.IsNullOrEmpty(source.DocumentAuthorityId), ExportProject = project,
                    CreateProject = !string.IsNullOrEmpty(source.DocumentAuthorityId) && (project || artifact.ContentByteLength <= HtmlProjectImportService.MaximumSourceBytes),
                    CreateProjectReason = !project && artifact.ContentByteLength > HtmlProjectImportService.MaximumSourceBytes ? BrowserRefusalCode.InvalidContent : BrowserRefusalCode.None }
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
            result.Immutable = false;
            result.DisplayLocator = BrowserLocalLocator.ForResource(HtmlWorkspaceIdentity.LogicalId(artifact.Id));
            var files = source.HtmlWorkspace?.Files;
            result.Capabilities = new BrowserTargetCapabilities {
                Source = files != null && files.Count != 0, PreviewReason = BrowserRefusalCode.NativeUnavailable,
                Edit = mutable, Versions = true, SaveVersion = mutable, Restore = mutable, ExportProject = true,
                Annotations = false
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
