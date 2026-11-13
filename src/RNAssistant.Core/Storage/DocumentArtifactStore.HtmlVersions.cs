using System;
using System.IO;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;

namespace RNAssistant.Core.Storage
{
    public sealed partial class DocumentArtifactStore
    {
        // Caller holds the existing document publication scope. These metadata
        // checkpoints neither rewrite aggregates nor delay automatic durability.
        private SavedVersionCatalog HtmlCatalog(ConversationMetadata owner, string logicalId)
        {
            HtmlWorkspaceIdentity.Identity(owner.DocumentAuthorityId, logicalId);
            return new SavedVersionCatalog(_authority, _revisions, _payloads, Scope(owner),
                ResourceUri.Create("state", "document", owner.DocumentAuthorityId, "html-saved-versions", logicalId));
        }
        public ResourceRef CurrentHtmlSnapshot(ConversationMetadata owner, string logicalId)
        {
            var scope = Scope(owner);
            var head = _authority.GetHead(scope, HtmlWorkspaceIdentity.Identity(owner.DocumentAuthorityId, logicalId));
            if (head?.Knowledge != HeadKnowledge.Known) throw new SavedVersionException("version_source_unavailable", "The HTML working copy is unavailable or removed.");
            var snapshots = _revisions.GetRevision(scope, head.Revision)?.Dependencies.Where(d => d.Kind == "immutable-snapshot").ToArray();
            if (snapshots?.Length != 1 || !Owns(owner, snapshots[0].Resource)) throw new InvalidDataException("The HTML head has no exact snapshot.");
            return snapshots[0].Resource.Copy();
        }
        public SavedVersionCatalogState HtmlVersionState(ConversationMetadata owner, string logicalId)
        { MigrateHtmlVersions(owner, logicalId); return HtmlCatalog(owner, logicalId).State(); }
        public void BeginHtmlVersionCutover(ConversationMetadata owner, string logicalId)
        {
            if (HtmlCatalog(owner, logicalId).State() != null) { MigrateHtmlVersions(owner, logicalId); return; }
            if (_authority.GetHead(Scope(owner), HtmlWorkspaceIdentity.Identity(owner.DocumentAuthorityId, logicalId)) == null)
                HtmlCatalog(owner, logicalId).Initialize(null);
            else MigrateHtmlVersions(owner, logicalId);
        }
        public SavedVersionPage HtmlVersions(ConversationMetadata owner, string logicalId, string cursor, int limit = 12)
        { MigrateHtmlVersions(owner, logicalId); return HtmlCatalog(owner, logicalId).List(cursor, limit); }
        public SavedVersionRecord HtmlVersion(ConversationMetadata owner, string logicalId, string versionId, bool allowRemoved = false)
        { MigrateHtmlVersions(owner, logicalId); return HtmlCatalog(owner, logicalId).Get(versionId, allowRemoved); }

        public SavedVersionOperation SaveHtmlVersion(ConversationMetadata owner, string logicalId, string expectedState,
            string expectedCurrentUri, string operationId, SavedVersionActor actor, Action beforeDispatch = null)
        {
            if (!ValidWorkspaceActor(actor) || actor.Kind == SavedVersionActorKind.Run && actor.ChatId != owner.Id)
                throw new SavedVersionException("version_actor_invalid", "An exact HTML checkpoint actor is required.");
            MigrateHtmlVersions(owner, logicalId);
            var catalog = HtmlCatalog(owner, logicalId); var intent = "save:" + expectedCurrentUri + ":" + actor.Kind + ":" + actor.ChatId + ":" + actor.RunId;
            var old = catalog.Operation(operationId, intent); if (old != null) return old;
            var current = CurrentHtmlSnapshot(owner, logicalId);
            if (current.Uri != expectedCurrentUri) throw new SavedVersionException("version_source_changed", "The HTML publication changed; save the exact working copy again.");
            var artifact = Read(owner, current, false);
            if (artifact.Kind != ChatArtifactKinds.HtmlWorkspace || HtmlWorkspaceIdentity.LogicalId(artifact.Id) != logicalId ||
                !_payloads.HasVerifiedReference(new PayloadRef(artifact.ContentSha256, artifact.ContentByteLength ?? -1, artifact.MimeType).ToBlobReference()))
                throw new SavedVersionException("version_source_invalid", "The published HTML aggregate is unavailable.");
            return catalog.Save(new SavedVersionRecord { ContentReference = current, ContentFingerprint = artifact.ContentSha256, Actor = actor },
                expectedState, operationId, intent, beforeDispatch: beforeDispatch);
        }
        public SavedVersionOperation DeleteHtmlVersion(ConversationMetadata owner, string logicalId, string versionId,
            string expectedState, string expectedCurrentUri, string operationId, bool confirmed)
        {
            if (!confirmed) throw new SavedVersionException("version_confirmation_required", "Confirm removal of the saved entry.");
            MigrateHtmlVersions(owner, logicalId);
            var catalog = HtmlCatalog(owner, logicalId); var intent = "delete:" + versionId + ":" + expectedCurrentUri;
            var old = catalog.Operation(operationId, intent); if (old != null) return old;
            var current = CurrentHtmlSnapshot(owner, logicalId);
            if (current.Uri != expectedCurrentUri) throw new SavedVersionException("version_active_changed", "The current HTML publication changed.");
            var selected = catalog.Get(versionId);
            if (selected.ContentReference.Uri == current.Uri) throw new SavedVersionException("version_active", "The current HTML checkpoint cannot be removed.");
            return catalog.Delete(versionId, expectedState, operationId, intent);
        }
        private void MigrateHtmlVersions(ConversationMetadata owner, string logicalId)
        {
            var scope = Scope(owner); var catalog = HtmlCatalog(owner, logicalId);
            var state = catalog.State();
            if (state == null)
            {
                var frozen = _authority.Capture(scope);
                var head = frozen.GetHead(HtmlWorkspaceIdentity.Identity(owner.DocumentAuthorityId, logicalId));
                if (head?.Knowledge != HeadKnowledge.Known) throw new InvalidDataException("The HTML publication is unavailable.");
                state = catalog.Initialize(head.Revision, frozen.Generation);
            }
            if (state.MigrationComplete) return;
            if (!state.MigrationGeneration.HasValue) throw new InvalidDataException("The HTML version migration cutoff is unavailable.");
            // Snapshot heads are immutable; generation excludes any publication
            // after the persisted cutoff, including after an interrupted migration.
            var published = _authority.Capture(scope).Heads.Values.Where(head => head.Knowledge == HeadKnowledge.Known &&
                head.AuthorityGeneration <= state.MigrationGeneration.Value && Owns(owner, head.Revision) && IsSnapshotReference(head.Revision))
                .Select(head => head.Revision).Where(reference => {
                    string id, document; int revision;
                    return ChatResourceUri.TryParseArtifactRevision(reference, out document, out id, out revision) && HtmlWorkspaceIdentity.LogicalId(id) == logicalId;
                }).Select(reference => new { Reference = reference, Artifact = Read(owner, reference, false) })
                .OrderBy(item => item.Artifact.Revision).ThenBy(item => item.Artifact.CreatedUtc).ThenBy(item => item.Artifact.Id, StringComparer.Ordinal);
            foreach (var item in published)
                catalog.Save(new SavedVersionRecord { ContentReference = item.Reference, ContentFingerprint = item.Artifact.ContentSha256,
                    CreatedUtc = item.Artifact.CreatedUtc, Actor = new SavedVersionActor { Kind = SavedVersionActorKind.Legacy } },
                    catalog.State().StateRevision, "legacy:" + item.Artifact.Id, "legacy:" + state.MigrationCutoff.Uri + ":" + item.Artifact.Id, migrating: true);
            catalog.CompleteMigration();
        }
    }
}
