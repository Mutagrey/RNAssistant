using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.Services
{
    internal static class ArtifactLibraryProjectionService
    {
        internal const int PageSize = 50;

        internal sealed class Presentation
        {
            public ArtifactLibraryProjectionDto Library { get; set; }
            public IReadOnlyList<ChatArtifactDto> Artifacts { get; set; }
        }

        private sealed class BuildResult
        {
            public List<ArtifactLibraryHeadDto> Heads { get; set; }
            public Dictionary<string, ArtifactLibraryHeadDto> HeadByArtifactId { get; set; }
            public Dictionary<string, List<ChatArtifact>> RevisionsByHeadId { get; set; }
            public List<string> RemovedResourceUris { get; set; }
            public Dictionary<string, ChatArtifact> ArtifactsById { get; set; }
            public string HeadStamp { get; set; }
        }

        public static ArtifactLibraryProjectionDto Project(ChatSession session)
        {
            int startIndex;
            var messages = ChatCloneService.CloneRecentMessagesForBridge(session == null ? null : session.Messages, out startIndex);
            return ProjectState(session, messages).Library;
        }

        public static Presentation ProjectState(ChatSession session, IReadOnlyList<ChatMessageViewDto> visibleMessages)
        {
            return ProjectState(session, visibleMessages, true);
        }

        public static Presentation ProjectMessagePage(ChatSession session, IReadOnlyList<ChatMessageViewDto> messages)
        {
            return ProjectState(session, messages, false);
        }

        private static Presentation ProjectState(ChatSession session, IReadOnlyList<ChatMessageViewDto> visibleMessages, bool includeHeads)
        {
            var visibleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var message in visibleMessages ?? new ChatMessageViewDto[0])
            {
                foreach (var reference in message.ResourceRefs ?? new ResourceRef[0])
                {
                    string id;
                    if (ChatResourceUri.TryGetCurrentArtifactId(session, reference, out id))
                        visibleIds.Add(id);
                }
            }
            var build = Build(session, visibleIds);
            var library = includeHeads ? InitialPage(session, build) : new ArtifactLibraryProjectionDto
            {
                SessionRevision = session == null ? 0 : session.Revision,
                Heads = new ArtifactLibraryHeadDto[0], RemovedResourceUris = build.RemovedResourceUris
            };
            if (includeHeads)
            {
                foreach (var head in library.Heads) visibleIds.Add(head.ArtifactId);
                if (session != null)
                {
                    visibleIds.Add(session.ActivePlanDocumentArtifactId ?? string.Empty);
                    visibleIds.Add(session.ActiveHtmlArtifactId ?? string.Empty);
                    visibleIds.Add(session.ActiveTaskListArtifactId ?? string.Empty);
                }
            }
            var artifacts = ChatArtifactDto.From(session, visibleIds);
            AttachExactDetails(session, build, artifacts);
            return new Presentation { Library = library, Artifacts = artifacts };
        }

        public static ArtifactLibraryPageResponse Page(ChatSession session, ArtifactLibraryPageRequest request)
        {
            RequireSession(session, request == null ? null : request.ChatId,
                request == null ? -1 : request.ExpectedSessionRevision);
            var build = Build(session, null);
            var offset = ParseCursor(session, "heads", string.Empty, build.HeadStamp, request.Cursor, build.Heads.Count);
            if (offset == 0) throw new InvalidOperationException("Для следующей страницы нужен cursor.");
            var heads = build.Heads.Skip(offset).Take(PageSize).ToArray();
            var ids = new HashSet<string>(heads.Select(item => item.ArtifactId), StringComparer.OrdinalIgnoreCase);
            var artifacts = ChatArtifactDto.From(session, ids);
            AttachExactDetails(session, build, artifacts);
            return new ArtifactLibraryPageResponse
            {
                ChatId = session.Id, SessionRevision = session.Revision, Heads = heads, Artifacts = artifacts,
                NextCursor = offset + PageSize < build.Heads.Count
                    ? Cursor(session, "heads", string.Empty, build.HeadStamp, offset + PageSize) : null
            };
        }

        private static void AttachExactDetails(ChatSession session, BuildResult build, IReadOnlyList<ChatArtifactDto> artifacts)
        {
            var branches = new Dictionary<string, ISet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var card in artifacts)
            {
                ArtifactLibraryHeadDto head;
                ChatArtifact exact;
                List<ChatArtifact> revisions;
                if (!build.HeadByArtifactId.TryGetValue(card.Id, out head) ||
                    !build.ArtifactsById.TryGetValue(card.Id, out exact) ||
                    !build.RevisionsByHeadId.TryGetValue(head.ArtifactId, out revisions)) continue;
                card.LibraryHead = head;
                ISet<string> branch;
                if (string.Equals(card.Id, head.ArtifactId, StringComparison.OrdinalIgnoreCase))
                    branch = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { head.ArtifactId };
                else if (!branches.TryGetValue(head.ArtifactId, out branch))
                {
                    branch = ActiveBranch(build.ArtifactsById[head.ArtifactId], revisions, build.ArtifactsById);
                    branches[head.ArtifactId] = branch;
                }
                card.LibraryRevision = CreateRevision(session, exact, build.ArtifactsById[head.ArtifactId], branch, build.ArtifactsById);
            }
        }

        public static ArtifactLibraryHistoryResponse History(ChatSession session, ArtifactLibraryHistoryRequest request)
        {
            RequireSession(session, request == null ? null : request.ChatId,
                request == null ? -1 : request.ExpectedSessionRevision);
            if (string.IsNullOrWhiteSpace(request.HeadArtifactId))
                throw new InvalidOperationException("Требуется точная голова истории.");
            var build = Build(session, null);
            List<ChatArtifact> revisions;
            ArtifactLibraryHeadDto head;
            if (!build.RevisionsByHeadId.TryGetValue(request.HeadArtifactId, out revisions) ||
                !build.HeadByArtifactId.TryGetValue(request.HeadArtifactId, out head) ||
                !string.Equals(head.ArtifactId, request.HeadArtifactId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("История ресурса недоступна.");
            if (!string.IsNullOrEmpty(request.TargetArtifactId) && !string.IsNullOrEmpty(request.Cursor))
                throw new InvalidOperationException("Точный ресурс и cursor несовместимы.");
            var activeBranch = ActiveBranch(build.ArtifactsById[head.ArtifactId], revisions, build.ArtifactsById);
            var ordered = revisions.OrderByDescending(item => Math.Max(1, item.Revision))
                .ThenByDescending(item => item.CreatedUtc)
                .ThenBy(item => item.Id ?? string.Empty, StringComparer.Ordinal).ToList();
            var historyStamp = TextPatternEngine.Sha256(JsonConvert.SerializeObject(ordered.Select(item =>
                new object[] { item.Id, item.Revision, item.ParentArtifactId, item.CreatedUtc,
                    MetadataText(item, "restoredFromArtifactId", "restoredFromUri", "restoredFrom") })));
            var offset = string.IsNullOrEmpty(request.TargetArtifactId)
                ? ParseCursor(session, "history", head.ArtifactId, historyStamp, request.Cursor, ordered.Count) : 0;
            if (!string.IsNullOrEmpty(request.TargetArtifactId))
            {
                var exact = ordered.SingleOrDefault(item =>
                    string.Equals(item.Id, request.TargetArtifactId, StringComparison.OrdinalIgnoreCase));
                if (exact == null) throw new InvalidOperationException("Точная ревизия не принадлежит ресурсу.");
                ordered = new List<ChatArtifact> { exact };
            }
            return new ArtifactLibraryHistoryResponse
            {
                ChatId = session.Id, SessionRevision = session.Revision, HeadArtifactId = head.ArtifactId,
                TotalCount = revisions.Count,
                Items = ordered.Skip(offset).Take(PageSize)
                    .Select(item => CreateRevision(session, item, build.ArtifactsById[head.ArtifactId], activeBranch, build.ArtifactsById)).ToArray(),
                NextCursor = string.IsNullOrEmpty(request.TargetArtifactId) && offset + PageSize < ordered.Count
                    ? Cursor(session, "history", head.ArtifactId, historyStamp, offset + PageSize) : null
            };
        }

        private static ArtifactLibraryProjectionDto InitialPage(ChatSession session, BuildResult build)
        {
            var first = build.Heads.Take(PageSize).ToList();
            if (session != null)
            {
                foreach (var id in new[] { session.ActivePlanDocumentArtifactId, session.ActiveHtmlArtifactId, session.ActiveTaskListArtifactId })
                {
                    ArtifactLibraryHeadDto head;
                    if (!string.IsNullOrEmpty(id) && build.HeadByArtifactId.TryGetValue(id, out head) &&
                        !first.Contains(head)) first.Add(head);
                }
            }
            return new ArtifactLibraryProjectionDto
            {
                SessionRevision = session == null ? 0 : session.Revision,
                Heads = first, TotalHeads = build.Heads.Count,
                NextCursor = build.Heads.Count > PageSize ? Cursor(session, "heads", string.Empty, build.HeadStamp, PageSize) : null,
                RemovedResourceUris = build.RemovedResourceUris
            };
        }

        private static BuildResult Build(ChatSession session, ISet<string> visibleIds)
        {
            var empty = new BuildResult
            {
                Heads = new List<ArtifactLibraryHeadDto>(),
                HeadByArtifactId = new Dictionary<string, ArtifactLibraryHeadDto>(StringComparer.OrdinalIgnoreCase),
                RevisionsByHeadId = new Dictionary<string, List<ChatArtifact>>(StringComparer.OrdinalIgnoreCase),
                RemovedResourceUris = new List<string>(),
                ArtifactsById = new Dictionary<string, ChatArtifact>(StringComparer.OrdinalIgnoreCase),
                HeadStamp = string.Empty
            };
            if (session == null || string.IsNullOrWhiteSpace(session.Id)) return empty;

            var artifacts = (session.Artifacts ?? new List<ChatArtifact>())
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Id))
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() == 1)
                .Select(group => group.Single())
                .Where(item => !ArtifactWorkingSet.IsDetached(session, item))
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
                    var head = CreateHead(session, artifact, artifact.Id, resourceClass, 1, byId);
                    heads.Add(head);
                    empty.HeadByArtifactId[artifact.Id] = head;
                    empty.RevisionsByHeadId[artifact.Id] = new List<ChatArtifact> { artifact };
                }
            }

            foreach (var pair in versioned)
            {
                var revisions = pair.Value;
                if (revisions.Any(item => PlanDocumentService.IsApplicableTombstone(session, item)))
                {
                    foreach (var revision in revisions)
                    {
                        if (visibleIds != null && visibleIds.Contains(revision.Id))
                            removedResourceUris.Add(ChatResourceUri.CreateArtifactRevisionUri(session, revision));
                    }
                    continue;
                }
                var head = SelectHead(session, revisions);
                if (head == null) continue;
                var projected = CreateHead(session, head, LogicalId(head, byId),
                    ResourceClass(head), revisions.Count, byId);
                heads.Add(projected);
                empty.RevisionsByHeadId[head.Id] = revisions;
                foreach (var revision in revisions) empty.HeadByArtifactId[revision.Id] = projected;
            }

            empty.Heads = heads
                .OrderBy(item => GroupOrder(item.Group))
                .ThenByDescending(item => item.CreatedUtc)
                .ThenBy(item => item.Title ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.ArtifactId ?? string.Empty, StringComparer.Ordinal)
                .ToList();
            empty.RemovedResourceUris = removedResourceUris
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ToList();
            empty.ArtifactsById = byId;
            empty.HeadStamp = TextPatternEngine.Sha256(JsonConvert.SerializeObject(empty.Heads));
            return empty;
        }

        private static ArtifactLibraryHeadDto CreateHead(
            ChatSession session,
            ChatArtifact head,
            string logicalId,
            string resourceClass,
            int historyCount,
            IReadOnlyDictionary<string, ChatArtifact> byId)
        {
            return new ArtifactLibraryHeadDto
            {
                CanDetach = RNAssistant.Core.Storage.DocumentArtifactStore.Owns(session, ChatResourceUri.CreateArtifactRevision(session, head)),
                AvailabilityIssue = head.AvailabilityIssue,
                ArtifactId = head.Id,
                LogicalId = logicalId,
                ResourceClass = resourceClass,
                Group = Group(head, resourceClass),
                Kind = NormalizeKind(head.Kind),
                DisplayKind = DisplayKind(head),
                Title = string.IsNullOrEmpty(head.AvailabilityIssue) ? head.Title : "Недоступный ресурс",
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
                HistoryCount = historyCount
            };
        }

        private static void RequireSession(ChatSession session, string chatId, long revision)
        {
            if (session == null || string.IsNullOrWhiteSpace(chatId) ||
                !string.Equals(session.Id, chatId, StringComparison.Ordinal) || session.Revision != revision)
                throw new InvalidOperationException("Чат изменился. Обновите библиотеку артефактов.");
        }

        private static string Cursor(ChatSession session, string kind, string id, string collectionStamp, int offset)
        {
            var stamp = TextPatternEngine.Sha256(session.Id + "|" + session.Revision.ToString(CultureInfo.InvariantCulture) +
                "|" + kind + "|" + id + "|" + collectionStamp);
            return stamp + "." + offset.ToString(CultureInfo.InvariantCulture);
        }

        private static int ParseCursor(ChatSession session, string kind, string id, string collectionStamp, string cursor, int count)
        {
            if (string.IsNullOrEmpty(cursor)) return 0;
            var parts = cursor.Split('.');
            int offset;
            if (parts.Length != 2 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out offset) ||
                offset <= 0 || offset >= count || offset % PageSize != 0 ||
                !string.Equals(cursor, Cursor(session, kind, id, collectionStamp, offset), StringComparison.Ordinal))
                throw new InvalidOperationException("Страница артефактов устарела. Обновите список.");
            return offset;
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
                return HtmlWorkspaceIdentity.LogicalId(artifact.Id) ?? "html_workspace";
            if (string.Equals(kind, ChatArtifactKinds.PlanDocument, StringComparison.OrdinalIgnoreCase))
                return !string.IsNullOrWhiteSpace(artifact.DocumentAuthorityId)
                    ? RNAssistant.Core.Storage.DocumentArtifactStore.PlanIdFromArtifact(artifact)
                    : MetadataText(artifact, "planId") ?? LineageRoot(artifact, kind, byId);
            if (string.Equals(kind, ChatArtifactKinds.TaskList, StringComparison.OrdinalIgnoreCase))
                return MetadataText(artifact, "taskListId") ?? LineageRoot(artifact, kind, byId);
            if (string.Equals(kind, ChatArtifactKinds.Markdown, StringComparison.OrdinalIgnoreCase))
                return MarkdownDocumentIdentity.LogicalId(artifact.Id) ?? MetadataText(artifact, "documentId", "logicalId") ?? LineageRoot(artifact, kind, byId);
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
            var uri = MetadataText(artifact, "derivedFromUri", "importedFromUri");
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
