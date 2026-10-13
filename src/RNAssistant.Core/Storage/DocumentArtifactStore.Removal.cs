using System;
using System.IO;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Core.Storage
{
    public sealed partial class DocumentArtifactStore
    {
        private bool HasPublishedSnapshot(ResourceAuthorityScopeId scope, ResourceRef reference)
        {
            var head = _authority.GetHead(scope, reference.Identity);
            if (head?.Knowledge == HeadKnowledge.Known)
                return head.Revision.Uri == reference.Uri && head.Revision.Revision == reference.Revision;
            // A removal is not permission to read a speculative registration.
            // Exact historical reads still require a prior publication barrier.
            return head?.Knowledge == HeadKnowledge.Removed && _authority.Capture(scope).Commits
                .SelectMany(commit => commit.HeadChanges).Any(change => change.After.Knowledge == HeadKnowledge.Known &&
                    change.After.Revision.Uri == reference.Uri && change.After.Revision.Revision == reference.Revision);
        }

        public bool IsRemoved(ChatSession session, ChatArtifact artifact)
        {
            if (artifact == null || string.IsNullOrEmpty(session?.DocumentAuthorityId) ||
                !Owns(session, ChatResourceUri.CreateArtifactRevision(session, artifact))) return false;
            return _authority.GetHead(Scope(session), ArtifactWorkingSet.Identity(session, artifact))?.Knowledge == HeadKnowledge.Removed;
        }

        // Caller holds the document mutation lease. One exact-current publication
        // removes the logical resource across chats; immutable history stays retained.
        public ChatArtifact Remove(ChatSession session, ResourceRef reference)
        {
            var scope = Scope(session);
            if (!Owns(session, reference) || !IsSnapshotReference(reference))
                throw new InvalidDataException("Требуется точный артефакт текущего документа.");
            var artifact = InspectMetadata(session, reference);
            if (artifact.Kind == SharedContextKind) throw new InvalidOperationException("Служебный ресурс нельзя удалить из библиотеки.");
            var identity = ArtifactWorkingSet.Identity(session, artifact);
            var before = _authority.Capture(scope);
            var head = before.GetHead(identity);
            if (head?.Knowledge != HeadKnowledge.Known)
                throw new InvalidOperationException("Артефакт удалён или недоступен. Обновите дерево.");
            var current = head.Revision;
            if (!identity.Equals(reference.Identity))
            {
                var dependencies = _revisions.GetRevision(scope, head.Revision)?.Dependencies
                    .Where(item => item.Kind == "immutable-snapshot").ToArray();
                if (dependencies == null || dependencies.Length != 1)
                    throw new InvalidDataException("Не удалось проверить актуальную версию артефакта.");
                current = dependencies[0].Resource;
            }
            if (current.Uri != reference.Uri)
                throw new InvalidOperationException("Версия артефакта изменилась. Обновите дерево перед удалением.");
            _authority.Publish(ResourceAuthorityCommit.Create(scope, before.Generation, null,
                new[] { new ResourceHeadChange(identity, head, ResourceHeadState.Removed(identity, before.Generation + 1)) },
                AuthorityCommitReason.DerivedPublication));
            if (_authority.GetHead(scope, identity)?.Knowledge != HeadKnowledge.Removed)
                throw new InvalidDataException("Не удалось подтвердить удаление артефакта. Обновите дерево.");
            artifact.RemovedFromDocument = true;
            return artifact;
        }

        public void ProjectRemovals(ChatSession session)
        {
            if (session?.Artifacts == null || string.IsNullOrEmpty(session.DocumentAuthorityId)) return;
            foreach (var artifact in session.Artifacts.Where(item => IsRemoved(session, item)))
            {
                artifact.RemovedFromDocument = true;
                if (artifact.Id == session.ActiveHtmlArtifactId)
                {
                    session.ActiveHtmlArtifactId = null;
                    session.HtmlWorkspace = new HtmlWorkspace();
                    session.HtmlWorkspaceRecovery = new HtmlWorkspaceRecoveryState();
                }
                if (artifact.Id == session.ActivePlanDocumentArtifactId) session.ActivePlanDocumentArtifactId = null;
            }
        }
    }
}
