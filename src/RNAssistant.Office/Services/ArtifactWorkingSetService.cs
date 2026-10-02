using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.Services
{
    internal sealed class ArtifactWorkingSetService
    {
        private readonly DocumentArtifactStore _artifacts;
        private readonly ResourceMutationJournal _mutations;

        public ArtifactWorkingSetService(DocumentArtifactStore artifacts, ResourceMutationJournal mutations)
        {
            _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
            _mutations = mutations ?? throw new ArgumentNullException(nameof(mutations));
        }

        internal IEnumerable<ChatArtifact> CurrentItems(ChatSession session)
        {
            foreach (var group in _artifacts.InspectMetadataList(session)
                .Concat((session.Artifacts ?? new List<ChatArtifact>()).Where(item => !string.IsNullOrEmpty(item.AvailabilityIssue)))
                .GroupBy(item => item.Id, StringComparer.Ordinal).Select(items => items.First())
                .GroupBy(item => ArtifactWorkingSet.Identity(session, item)))
            {
                var first = group.First();
                if (first.Kind != ChatArtifactKinds.PlanDocument && first.Kind != ChatArtifactKinds.HtmlWorkspace && MarkdownDocumentIdentity.LogicalId(first.Id) == null && DocumentArtifactStore.ContextLogicalId(first.Id) == null) { yield return group.Single(); continue; }
                ResourceRef current = null;
                var unavailableHead = false;
                try { current = first.Kind == ChatArtifactKinds.PlanDocument
                    ? _artifacts.CurrentPlan(session, DocumentArtifactStore.PlanIdFromArtifact(first))
                    : _artifacts.CurrentSnapshot(session, ArtifactWorkingSet.Identity(session, first)); }
                catch (InvalidDataException) { unavailableHead = true; }
                catch (IOException) { unavailableHead = true; }
                if (current == null) unavailableHead = true;
                if (unavailableHead)
                {
                    var retained = group.FirstOrDefault(item => item.Id == session.ActivePlanDocumentArtifactId || item.Id == session.ActiveHtmlArtifactId) ??
                        group.OrderByDescending(item => item.Revision).First();
                    retained.AvailabilityIssue = "head_unavailable";
                    yield return retained;
                    continue;
                }
                var currentItem = group.SingleOrDefault(candidate => ChatResourceUri.CreateArtifactRevisionUri(session, candidate) == current.Uri)
                    ?? _artifacts.InspectMetadata(session, current);
                if (currentItem != null && !PlanDocumentService.IsTombstone(currentItem)) yield return currentItem;
            }
        }

        public void Change(ChatSession session, ArtifactLinkChangeRequest request, Action<ChatSession> persist)
        {
            RequireChat(session, request?.ChatId);
            if (request.ExpectedSessionRevision != session.Revision || !request.Detached.HasValue)
                throw new InvalidOperationException("Состояние чата изменилось. Обновите список ресурсов и повторите действие.");
            if (persist == null) throw new ArgumentNullException(nameof(persist));
            var reference = new ResourceRef(request.ResourceUri);
            if (!DocumentArtifactStore.Owns(session, reference))
                throw new InvalidOperationException("Этот ресурс не принадлежит текущему документу.");
            string owner, id;
            int revision;
            ChatResourceUri.TryParseArtifactRevision(reference, out owner, out id, out revision);
            reference = ChatResourceUri.CreateArtifactRevision(session, new ChatArtifact
                { Id = id, Revision = revision, DocumentAuthorityId = owner });
            if (reference.Uri != request.ResourceUri) throw new InvalidOperationException("Требуется точная ссылка ресурса.");
            using (_mutations.AcquireScope(Scope(session)))
            {
                var artifact = request.Detached.Value
                    ? _artifacts.InspectMetadata(session, reference) : _artifacts.Read(session, reference, false);
                if (request.Detached.Value && !ArtifactWorkingSet.IsLinked(session, artifact))
                    throw new InvalidOperationException("В этом чате нет такой подключённой ссылки.");
                if (!request.Detached.Value && artifact.Kind == ChatArtifactKinds.PlanDocument &&
                    (PlanDocumentService.IsTombstone(artifact) ||
                     _artifacts.CurrentPlan(session, ArtifactWorkingSet.PlanId(artifact))?.Uri != reference.Uri))
                    throw new InvalidOperationException("Версия Plan изменилась или удалена. Обновите список и выберите актуальную версию.");
                if (!request.Detached.Value && (artifact.Kind == ChatArtifactKinds.HtmlWorkspace || MarkdownDocumentIdentity.LogicalId(artifact.Id) != null) &&
                    _artifacts.CurrentSnapshot(session, ArtifactWorkingSet.Identity(session, artifact))?.Uri != reference.Uri)
                    throw new InvalidOperationException("Версия ресурса изменилась. Выберите актуальную версию из документа.");
                ChatSession selectedHtml = null;
                if (!request.Detached.Value && artifact.Kind == ChatArtifactKinds.HtmlWorkspace)
                {
                    // Validate the complete selected aggregate before changing chat membership.
                    selectedHtml = new ChatSession { Id = session.Id, DocumentAuthorityId = session.DocumentAuthorityId,
                        ActiveHtmlArtifactId = artifact.Id,
                        Artifacts = _artifacts.SnapshotHistory(session, HtmlWorkspaceIdentity.LogicalId(artifact.Id))
                            .Select(item => item.Id == artifact.Id ? _artifacts.Read(session, reference) : item).ToList() };
                    if (!HtmlWorkspaceArtifactService.Restore(selectedHtml, artifact.Id))
                        throw new InvalidDataException("Выбранная версия HTML недоступна. Ссылка чата не изменена.");
                }
                session.Artifacts = session.Artifacts ?? new List<ChatArtifact>();
                if (!session.Artifacts.Any(item => item.Id == artifact.Id)) session.Artifacts.Add(artifact);
                ArtifactWorkingSet.Set(session, artifact, request.Detached.Value);
                if (!request.Detached.Value && artifact.Kind == ChatArtifactKinds.PlanDocument)
                    session.ActivePlanDocumentArtifactId = artifact.Id;
                if (selectedHtml != null)
                {
                    var ids = new HashSet<string>(selectedHtml.Artifacts.Select(item => item.Id), StringComparer.Ordinal);
                    session.Artifacts.RemoveAll(item => ids.Contains(item.Id));
                    session.Artifacts.AddRange(selectedHtml.Artifacts);
                    session.ActiveHtmlArtifactId = selectedHtml.ActiveHtmlArtifactId;
                    session.HtmlWorkspace = selectedHtml.HtmlWorkspace;
                    session.HtmlWorkspaceRecovery = selectedHtml.HtmlWorkspaceRecovery;
                }
                persist(session);
            }
        }

        private static ResourceAuthorityScopeId Scope(ChatSession session)
        {
            return ResourceAuthorityScopeId.Document(new DocumentAuthorityId(session.DocumentAuthorityId));
        }

        private static void RequireChat(ChatSession session, string chatId)
        {
            if (session == null || string.IsNullOrWhiteSpace(chatId) || session.Id != chatId ||
                string.IsNullOrWhiteSpace(session.DocumentAuthorityId))
                throw new InvalidOperationException("Для управления ссылками требуется явный чат документа.");
        }
    }
}
