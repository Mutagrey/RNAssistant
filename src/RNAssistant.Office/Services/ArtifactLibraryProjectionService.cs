using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.Services
{
    internal static class ArtifactLibraryProjectionService
    {
        public static ArtifactLibraryProjectionDto Project(ChatSession session)
        {
            var projection = new ArtifactLibraryProjectionDto
            {
                SessionRevision = session == null ? 0 : session.Revision,
                Heads = new List<ArtifactLibraryHeadDto>(),
                RemovedResourceUris = new List<string>()
            };
            if (session == null || string.IsNullOrWhiteSpace(session.Id)) return projection;

            var artifacts = (session.Artifacts ?? new List<ChatArtifact>())
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Id))
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() == 1)
                .Select(group => group.Single())
                .ToList();
            var byId = artifacts.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
            var heads = new List<ArtifactLibraryHeadDto>();
            var removedResourceUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var versioned = new Dictionary<string, List<ChatArtifact>>(StringComparer.OrdinalIgnoreCase);

            foreach (var artifact in artifacts)
            {
                var resourceClass = ResourceClass(artifact);
                if (IsVersioned(resourceClass))
                {
                    var key = resourceClass + ":" + NormalizeKind(artifact.Kind) + ":" + LogicalId(artifact, byId);
                    List<ChatArtifact> revisions;
                    if (!versioned.TryGetValue(key, out revisions))
                    {
                        revisions = new List<ChatArtifact>();
                        versioned[key] = revisions;
                    }
                    revisions.Add(artifact);
                }
                else
                {
                    heads.Add(CreateHead(session, artifact, artifact.Id, resourceClass,
                        new[] { artifact }, byId));
                }
            }

            foreach (var pair in versioned)
            {
                var revisions = pair.Value;
                if (revisions.Any(item => PlanDocumentService.IsApplicableTombstone(session, item)))
                {
                    foreach (var revision in revisions)
                    {
                        removedResourceUris.Add(ChatResourceUri.CreateArtifactRevisionUri(session, revision));
                    }
                    continue;
                }
                var head = SelectHead(session, revisions);
                if (head == null) continue;
                heads.Add(CreateHead(session, head, LogicalId(head, byId),
                    ResourceClass(head), revisions, byId));
            }

            projection.Heads = heads
                .OrderBy(item => GroupOrder(item.Group))
                .ThenByDescending(item => item.CreatedUtc)
                .ThenBy(item => item.Title ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.ArtifactId ?? string.Empty, StringComparer.Ordinal)
                .ToList();
            projection.RemovedResourceUris = removedResourceUris
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return projection;
        }

        private static ArtifactLibraryHeadDto CreateHead(
            ChatSession session,
            ChatArtifact head,
            string logicalId,
            string resourceClass,
            IEnumerable<ChatArtifact> revisions,
            IReadOnlyDictionary<string, ChatArtifact> byId)
        {
            var activeBranch = ActiveBranch(head, revisions, byId);
            var history = (revisions ?? new ChatArtifact[0])
                .Where(item => item != null)
                .OrderByDescending(item => Math.Max(1, item.Revision))
                .ThenByDescending(item => item.CreatedUtc)
                .ThenBy(item => item.Id ?? string.Empty, StringComparer.Ordinal)
                .Select(item => CreateRevision(session, item, head, activeBranch, byId))
                .ToList();
            return new ArtifactLibraryHeadDto
            {
                ArtifactId = head.Id,
                LogicalId = logicalId,
                ResourceClass = resourceClass,
                Group = Group(head, resourceClass),
                Kind = NormalizeKind(head.Kind),
                DisplayKind = DisplayKind(head),
                Title = head.Title,
                MimeType = head.MimeType,
                ContentByteLength = head.ContentByteLength,
                Revision = Math.Max(1, head.Revision),
                VersionLabel = VersionLabel(resourceClass, head.Revision),
                Status = MetadataText(head, "status"),
                ResourceUri = ChatResourceUri.CreateArtifactRevisionUri(session, head),
                DerivedFromResourceUri = DerivedFromResourceUri(session, head, byId),
                SourceMessageId = head.SourceMessageId,
                RunId = head.RunId,
                CreatedUtc = head.CreatedUtc,
                History = history
            };
        }

        private static ArtifactLibraryRevisionDto CreateRevision(
            ChatSession session,
            ChatArtifact artifact,
            ChatArtifact head,
            ISet<string> activeBranch,
            IReadOnlyDictionary<string, ChatArtifact> byId)
        {
            var isHead = string.Equals(artifact.Id, head.Id, StringComparison.OrdinalIgnoreCase);
            var onActiveBranch = activeBranch.Contains(artifact.Id);
            var restoredFrom = MetadataText(artifact, "restoredFromArtifactId");
            var restoredFromUri = MetadataText(artifact, "restoredFromUri");
            var legacyRestoredFrom = MetadataText(artifact, "restoredFrom");
            if (string.IsNullOrWhiteSpace(restoredFrom) && !string.IsNullOrWhiteSpace(legacyRestoredFrom))
            {
                if (legacyRestoredFrom.StartsWith("rna://", StringComparison.OrdinalIgnoreCase))
                    restoredFromUri = legacyRestoredFrom;
                else
                    restoredFrom = legacyRestoredFrom;
            }
            ChatArtifact parent;
            ChatArtifact restored;
            byId.TryGetValue(artifact.ParentArtifactId ?? string.Empty, out parent);
            byId.TryGetValue(restoredFrom ?? string.Empty, out restored);
            return new ArtifactLibraryRevisionDto
            {
                ArtifactId = artifact.Id,
                Revision = Math.Max(1, artifact.Revision),
                Title = artifact.Title,
                ResourceUri = ChatResourceUri.CreateArtifactRevisionUri(session, artifact),
                ParentArtifactId = artifact.ParentArtifactId,
                ParentResourceUri = parent == null ? null : ChatResourceUri.CreateArtifactRevisionUri(session, parent),
                RestoredFromArtifactId = restoredFrom,
                RestoredFromResourceUri = restored == null ? restoredFromUri : ChatResourceUri.CreateArtifactRevisionUri(session, restored),
                SourceMessageId = artifact.SourceMessageId,
                RunId = artifact.RunId,
                CreatedUtc = artifact.CreatedUtc,
                Relation = isHead ? "head" : (onActiveBranch ? "ancestor" : "branch"),
                IsHead = isHead,
                IsOnActiveBranch = onActiveBranch
            };
        }

        private static ISet<string> ActiveBranch(
            ChatArtifact head,
            IEnumerable<ChatArtifact> revisions,
            IReadOnlyDictionary<string, ChatArtifact> byId)
        {
            var allowed = new HashSet<string>((revisions ?? new ChatArtifact[0])
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Id))
                .Select(item => item.Id), StringComparer.OrdinalIgnoreCase);
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var current = head;
            while (current != null && allowed.Contains(current.Id) && result.Add(current.Id))
            {
                ChatArtifact parent;
                current = byId.TryGetValue(current.ParentArtifactId ?? string.Empty, out parent) ? parent : null;
            }
            return result;
        }

        private static ChatArtifact SelectHead(ChatSession session, IList<ChatArtifact> revisions)
        {
            if (session == null || revisions == null || revisions.Count == 0) return null;
            var kind = NormalizeKind(revisions[0].Kind);
            var preferredId = string.Equals(kind, ChatArtifactKinds.HtmlWorkspace, StringComparison.OrdinalIgnoreCase)
                ? session.ActiveHtmlArtifactId
                : string.Equals(kind, ChatArtifactKinds.PlanDocument, StringComparison.OrdinalIgnoreCase)
                    ? session.ActivePlanDocumentArtifactId
                    : string.Equals(kind, ChatArtifactKinds.TaskList, StringComparison.OrdinalIgnoreCase)
                        ? session.ActiveTaskListArtifactId
                        : null;
            var preferred = revisions.FirstOrDefault(item =>
                string.Equals(item.Id, preferredId, StringComparison.OrdinalIgnoreCase));
            return preferred ?? revisions
                .OrderByDescending(item => Math.Max(1, item.Revision))
                .ThenByDescending(item => item.CreatedUtc)
                .ThenByDescending(item => item.Id ?? string.Empty, StringComparer.Ordinal)
                .First();
        }

        private static string LogicalId(ChatArtifact artifact, IReadOnlyDictionary<string, ChatArtifact> byId)
        {
            var kind = NormalizeKind(artifact == null ? null : artifact.Kind);
            if (string.Equals(kind, ChatArtifactKinds.HtmlWorkspace, StringComparison.OrdinalIgnoreCase))
                return "html_workspace";
            if (string.Equals(kind, ChatArtifactKinds.PlanDocument, StringComparison.OrdinalIgnoreCase))
                return MetadataText(artifact, "planId") ?? LineageRoot(artifact, kind, byId);
            if (string.Equals(kind, ChatArtifactKinds.TaskList, StringComparison.OrdinalIgnoreCase))
                return MetadataText(artifact, "taskListId") ?? LineageRoot(artifact, kind, byId);
            if (string.Equals(kind, ChatArtifactKinds.Markdown, StringComparison.OrdinalIgnoreCase))
                return MetadataText(artifact, "documentId", "logicalId") ?? LineageRoot(artifact, kind, byId);
            return artifact == null ? string.Empty : artifact.Id;
        }

        private static string LineageRoot(
            ChatArtifact artifact,
            string kind,
            IReadOnlyDictionary<string, ChatArtifact> byId)
        {
            var current = artifact;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (current != null && visited.Add(current.Id ?? string.Empty))
            {
                ChatArtifact parent;
                if (!byId.TryGetValue(current.ParentArtifactId ?? string.Empty, out parent) ||
                    !string.Equals(NormalizeKind(parent.Kind), kind, StringComparison.OrdinalIgnoreCase)) break;
                current = parent;
            }
            return current == null ? string.Empty : current.Id;
        }

        private static string ResourceClass(ChatArtifact artifact)
        {
            if (!string.IsNullOrWhiteSpace(MetadataText(artifact, "derivedFromUri", "derivedFromArtifactId")))
                return ArtifactLibraryResourceClasses.DerivedResource;
            var kind = NormalizeKind(artifact == null ? null : artifact.Kind);
            if (string.Equals(kind, ChatArtifactKinds.PlanDocument, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kind, ChatArtifactKinds.Markdown, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kind, ChatArtifactKinds.TaskList, StringComparison.OrdinalIgnoreCase))
                return ArtifactLibraryResourceClasses.VersionedDocument;
            if (string.Equals(kind, ChatArtifactKinds.HtmlWorkspace, StringComparison.OrdinalIgnoreCase))
                return ArtifactLibraryResourceClasses.VersionedAggregate;
            if (string.Equals(kind, ChatArtifactKinds.Attachment, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kind, ChatArtifactKinds.File, StringComparison.OrdinalIgnoreCase) ||
                ((string.Equals(kind, ChatArtifactKinds.Image, StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(kind, "audio", StringComparison.OrdinalIgnoreCase)) && HasAttachmentIdentity(artifact)))
                return ArtifactLibraryResourceClasses.ImmutableOriginal;
            return ArtifactLibraryResourceClasses.ImmutableSnapshot;
        }

        private static string Group(ChatArtifact artifact, string resourceClass)
        {
            var kind = NormalizeKind(artifact == null ? null : artifact.Kind);
            if (string.Equals(resourceClass, ArtifactLibraryResourceClasses.ImmutableOriginal, StringComparison.Ordinal))
                return ArtifactLibraryGroups.FilesMedia;
            if (string.Equals(resourceClass, ArtifactLibraryResourceClasses.DerivedResource, StringComparison.Ordinal))
                return ArtifactLibraryGroups.GeneratedSnapshots;
            if (string.Equals(kind, ChatArtifactKinds.PlanDocument, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kind, ChatArtifactKinds.Markdown, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kind, ChatArtifactKinds.HtmlWorkspace, StringComparison.OrdinalIgnoreCase))
                return ArtifactLibraryGroups.AuthoredDocuments;
            if (string.Equals(kind, ChatArtifactKinds.TaskList, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kind, ChatArtifactKinds.Compaction, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kind, ChatArtifactKinds.ToolResult, StringComparison.OrdinalIgnoreCase))
                return ArtifactLibraryGroups.SystemEvidence;
            return ArtifactLibraryGroups.GeneratedSnapshots;
        }

        private static string DisplayKind(ChatArtifact artifact)
        {
            var kind = NormalizeKind(artifact == null ? null : artifact.Kind);
            if (string.Equals(kind, ChatArtifactKinds.PlanDocument, StringComparison.OrdinalIgnoreCase)) return "plan";
            if (string.Equals(kind, ChatArtifactKinds.Attachment, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kind, ChatArtifactKinds.File, StringComparison.OrdinalIgnoreCase))
            {
                var attachmentKind = (MetadataText(artifact, "kind") ?? string.Empty).ToLowerInvariant();
                if (attachmentKind == "image" || attachmentKind == "audio") return attachmentKind;
                var mimeType = (artifact == null ? null : artifact.MimeType) ?? string.Empty;
                if (mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return "image";
                if (mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return "audio";
                return "file";
            }
            return string.IsNullOrWhiteSpace(kind) ? "file" : kind;
        }

        private static bool HasAttachmentIdentity(ChatArtifact artifact)
        {
            return !string.IsNullOrWhiteSpace(MetadataText(artifact, "attachmentId"));
        }

        private static string DerivedFromResourceUri(
            ChatSession session,
            ChatArtifact artifact,
            IReadOnlyDictionary<string, ChatArtifact> byId)
        {
            var uri = MetadataText(artifact, "derivedFromUri");
            if (!string.IsNullOrWhiteSpace(uri)) return uri;
            var id = MetadataText(artifact, "derivedFromArtifactId");
            ChatArtifact source;
            return byId.TryGetValue(id ?? string.Empty, out source)
                ? ChatResourceUri.CreateArtifactRevisionUri(session, source)
                : null;
        }

        private static bool IsVersioned(string resourceClass)
        {
            return string.Equals(resourceClass, ArtifactLibraryResourceClasses.VersionedDocument, StringComparison.Ordinal) ||
                string.Equals(resourceClass, ArtifactLibraryResourceClasses.VersionedAggregate, StringComparison.Ordinal);
        }

        private static string VersionLabel(string resourceClass, int revision)
        {
            if (string.Equals(resourceClass, ArtifactLibraryResourceClasses.ImmutableOriginal, StringComparison.Ordinal))
                return "Original";
            if (string.Equals(resourceClass, ArtifactLibraryResourceClasses.DerivedResource, StringComparison.Ordinal))
                return "Derived";
            return IsVersioned(resourceClass) ? "v" + Math.Max(1, revision) : null;
        }

        private static string MetadataText(ChatArtifact artifact, params string[] names)
        {
            if (artifact == null || string.IsNullOrWhiteSpace(artifact.MetadataJson)) return null;
            try
            {
                var metadata = JObject.Parse(artifact.MetadataJson);
                foreach (var name in names ?? new string[0])
                {
                    var value = metadata.GetValue(name, StringComparison.OrdinalIgnoreCase);
                    if (value != null && value.Type == JTokenType.String &&
                        !string.IsNullOrWhiteSpace((string)value)) return (string)value;
                }
            }
            catch (Newtonsoft.Json.JsonException)
            {
            }
            return null;
        }

        private static string NormalizeKind(string kind)
        {
            return (kind ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static int GroupOrder(string group)
        {
            if (string.Equals(group, ArtifactLibraryGroups.AuthoredDocuments, StringComparison.Ordinal)) return 0;
            if (string.Equals(group, ArtifactLibraryGroups.FilesMedia, StringComparison.Ordinal)) return 1;
            if (string.Equals(group, ArtifactLibraryGroups.GeneratedSnapshots, StringComparison.Ordinal)) return 2;
            return 3;
        }
    }
}
