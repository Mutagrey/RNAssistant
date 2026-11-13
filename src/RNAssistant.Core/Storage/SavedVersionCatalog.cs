using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Storage
{
    public sealed class SavedVersionException : IOException
    {
        public string Code { get; private set; }
        public bool MayHavePublished { get; private set; }
        public SavedVersionException(string code, string message, bool mayHavePublished = false) : base(message)
        { Code = code; MayHavePublished = mayHavePublished; }
    }

    // Shared metadata algorithm used only under each existing domain owner's
    // publication lease. No database, file tree, content copy or independent head.
    internal sealed class SavedVersionCatalog
    {
        private const string View = "saved-version-metadata";
        private readonly IResourceAuthorityStore _authority;
        private readonly IResourceRevisionStore _revisions;
        private readonly ChatBlobStore _blobs;
        private readonly ResourceAuthorityScopeId _scope;
        private readonly string _root;
        internal SavedVersionCatalog(IResourceAuthorityStore authority, IResourceRevisionStore revisions,
            ChatBlobStore blobs, ResourceAuthorityScopeId scope, string root)
        { _authority = authority; _revisions = revisions; _blobs = blobs; _scope = scope; _root = root.TrimEnd('/'); }

        internal SavedVersionCatalogState State()
        {
            var head = _authority.GetHead(_scope, CatalogRef("0").Identity);
            if (head == null) return null;
            if (head.Knowledge != HeadKnowledge.Known) throw Error("version_catalog_unavailable", "Saved version catalog is unavailable.");
            var state = Read<SavedVersionCatalogState>(head.Revision); state.StateRevision = head.Revision.Revision;
            return state;
        }

        internal SavedVersionCatalogState Initialize(ResourceRef cutoff, long? generation = null)
        {
            var state = State(); if (state != null) return state;
            state = new SavedVersionCatalogState { MigrationCutoff = cutoff?.Copy(), MigrationGeneration = generation, MigrationComplete = cutoff == null };
            Commit(state, null, new List<ResourceHeadChange>(), null);
            return State();
        }

        internal void CompleteMigration()
        {
            var state = RequireState(null, false); if (state.MigrationComplete) return;
            state.MigrationComplete = true;
            Commit(state, state.StateRevision, new List<ResourceHeadChange>(), null);
        }

        internal SavedVersionRecord Get(string versionId, bool allowRemoved = false)
        {
            var exact = EntryRef(versionId);
            var head = _authority.GetHead(_scope, exact.Identity);
            if (head == null || head.Knowledge != HeadKnowledge.Known && head.Knowledge != HeadKnowledge.Removed)
                throw Error("version_unavailable", "The exact saved version is unavailable.");
            var entry = Read<SavedVersionRecord>(exact);
            if (head.Knowledge == HeadKnowledge.Known && head.Revision.Revision != exact.Revision ||
                entry.VersionId != versionId || entry.Ordinal != Ordinal(versionId) || entry.ContentReference?.IsExact != true)
                throw Error("version_catalog_invalid", "Saved version metadata is invalid.");
            entry.Removed = head.Knowledge == HeadKnowledge.Removed;
            if (entry.Removed && !allowRemoved) throw Error("version_removed", "This saved entry was removed.");
            return entry;
        }

        internal SavedVersionPage List(string cursor, int limit = 12)
        {
            var state = RequireState(null, false); var offset = 0;
            if (limit < 1 || limit > 50) throw new ArgumentOutOfRangeException(nameof(limit));
            if (cursor != null)
            {
                var parts = cursor.Split('.');
                if (parts.Length != 2 || parts[0] != state.StateRevision || !int.TryParse(parts[1], out offset) || offset < 0)
                    throw Error("version_state_changed", "Saved version list changed; refresh it.");
            }
            var prefix = _root + "/entries/";
            var page = _authority.ReadHeads(_scope, new[] { new ResourceHeadRange(prefix, prefix + '\uffff') }, offset, limit);
            if (State().StateRevision != state.StateRevision) throw Error("version_state_changed", "Saved version list changed; refresh it.");
            return new SavedVersionPage { Items = page.Items.Where(head => head.Knowledge == HeadKnowledge.Known)
                .Select(head => Read<SavedVersionRecord>(head.Revision)).ToArray(), StateRevision = state.StateRevision,
                Cursor = page.NextOffset.HasValue ? state.StateRevision + "." + page.NextOffset.Value.ToString(CultureInfo.InvariantCulture) : null };
        }

        internal SavedVersionOperation Operation(string operationId, string intent)
        {
            var exact = ReceiptRef(operationId);
            var head = _authority.GetHead(_scope, exact.Identity);
            if (head == null) return null;
            if (head.Knowledge != HeadKnowledge.Known) throw Unknown("Version operation receipt is unavailable; inspect it before retrying.");
            try
            {
                var result = Read<SavedVersionOperation>(head.Revision);
                if (result.OperationId != operationId || result.Intent != intent)
                    throw Error("version_operation_changed", "Operation identity was reused with a different intent.");
                result.Version = Get(result.VersionId, true);
                return result;
            }
            catch (SavedVersionException error) when (error.Code == "version_operation_changed") { throw; }
            catch (Exception error) { throw Unknown("The committed version receipt cannot be read. " + error.Message); }
        }

        internal SavedVersionOperation Save(SavedVersionRecord content, string expectedState, string operationId,
            string intent, bool migrating = false, IEnumerable<ResourceHeadChange> additional = null, Action beforeDispatch = null)
        {
            var receipt = Operation(operationId, intent); if (receipt != null) return receipt;
            var state = RequireState(expectedState);
            if (!migrating && !state.MigrationComplete) throw Error("version_migration_pending", "Saved version migration is incomplete.");
            if (content?.ContentReference?.IsExact != true || content.ContentFingerprint == null || content.ContentFingerprint.Length != 64 ||
                content.ContentFingerprint.Any(character => !Uri.IsHexDigit(character)) || content.Actor == null ||
                !Enum.IsDefined(typeof(SavedVersionActorKind), content.Actor.Kind) || content.Actor.Kind == SavedVersionActorKind.Legacy && !migrating ||
                content.Actor.Kind == SavedVersionActorKind.Run && (string.IsNullOrEmpty(content.Actor.ChatId) || string.IsNullOrEmpty(content.Actor.RunId)) ||
                content.Actor.Kind == SavedVersionActorKind.User && (content.Actor.ChatId != null || content.Actor.RunId != null))
                throw Error("version_source_invalid", "An exact retained source and actor are required.");
            var latest = state.LatestAvailableVersionId == null ? null : Get(state.LatestAvailableVersionId);
            if (latest?.ContentFingerprint != state.LastSavedFingerprint)
                throw Error("version_catalog_invalid", "The latest available entry does not match the catalog fingerprint.");
            if (!migrating && latest?.ContentFingerprint == content.ContentFingerprint)
            {
                receipt = Receipt(operationId, intent, "AlreadySaved", latest.VersionId);
                Commit(null, expectedState, new List<ResourceHeadChange>(), receipt, beforeDispatch: beforeDispatch);
                return Operation(operationId, intent);
            }
            if (state.LastOrdinal == long.MaxValue) throw Error("version_catalog_full", "Version sequence is exhausted.");
            var ordinal = state.LastOrdinal + 1;
            var entry = new SavedVersionRecord { VersionId = VersionId(ordinal), Ordinal = ordinal, Label = "v" + ordinal,
                CreatedUtc = content.CreatedUtc == default(DateTime) ? DateTime.UtcNow : content.CreatedUtc,
                ContentReference = content.ContentReference.Copy(), ContentFingerprint = content.ContentFingerprint,
                ManifestVersion = content.ManifestVersion, Actor = content.Actor };
            var reference = EntryRef(entry.VersionId);
            Stage(reference, entry, dependencies: new[] { new ResourceDependency(entry.ContentReference, "metadata", ResourceCoverage.Whole(), "immutable-snapshot") });
            var changes = (additional ?? Enumerable.Empty<ResourceHeadChange>()).ToList();
            changes.Add(new ResourceHeadChange(reference.Identity, null, ResourceHeadState.Known(reference, 0)));
            state.LastOrdinal = ordinal; state.LatestAvailableVersionId = entry.VersionId; state.LastSavedFingerprint = entry.ContentFingerprint;
            receipt = Receipt(operationId, intent, "Saved", entry.VersionId);
            Commit(state, expectedState, changes, receipt, beforeDispatch: beforeDispatch);
            return Operation(operationId, intent);
        }

        internal SavedVersionOperation Delete(string versionId, string expectedState, string operationId, string intent)
        {
            var receipt = Operation(operationId, intent); if (receipt != null) return receipt;
            var state = RequireState(expectedState); Get(versionId);
            var exact = EntryRef(versionId); var head = _authority.GetHead(_scope, exact.Identity);
            var changes = new List<ResourceHeadChange> { new ResourceHeadChange(exact.Identity, head,
                new ResourceHeadState(exact.Identity, HeadKnowledge.Removed, null, "saved-version-removal", 0)) };
            if (state.LatestAvailableVersionId == versionId)
            {
                SavedVersionRecord latest = null; string cursor = null;
                do {
                    var page = List(cursor, 50);
                    latest = page.Items.FirstOrDefault(item => item.VersionId != versionId);
                    cursor = page.Cursor;
                } while (latest == null && cursor != null);
                state.LatestAvailableVersionId = latest?.VersionId; state.LastSavedFingerprint = latest?.ContentFingerprint;
            }
            Commit(state, expectedState, changes, Receipt(operationId, intent, "Removed", versionId));
            return Operation(operationId, intent);
        }

        internal SavedVersionOperation Activate(string versionId, string expectedState, string operationId, string intent,
            Func<ResourceAuthoritySnapshot, IReadOnlyList<ResourceHeadChange>> stageRegistration, Action beforeDispatch = null)
        {
            var receipt = Operation(operationId, intent); if (receipt != null) return receipt;
            var state = RequireState(expectedState); Get(versionId);
            var frozen = _authority.Capture(_scope);
            var changes = stageRegistration(frozen).ToList();
            Commit(state, expectedState, changes, Receipt(operationId, intent, "Activated", versionId), frozen, beforeDispatch);
            return Operation(operationId, intent);
        }

        internal void UpdateOwner(Func<ResourceAuthoritySnapshot, IReadOnlyList<ResourceHeadChange>> stage)
        {
            var state = RequireState(null, false);
            var frozen = _authority.Capture(_scope);
            Commit(state, state.StateRevision, stage(frozen).ToList(), null, frozen);
        }

        private SavedVersionCatalogState RequireState(string expected, bool check = true)
        {
            var state = State() ?? throw Error("version_catalog_unavailable", "Saved version catalog is not initialized.");
            if (check && state.StateRevision != expected) throw Error("version_state_changed", "Saved versions changed; refresh before writing.");
            return state;
        }

        private void Commit(SavedVersionCatalogState state, string expectedState, List<ResourceHeadChange> changes,
            SavedVersionOperation receipt, ResourceAuthoritySnapshot frozen = null, Action beforeDispatch = null)
        {
            frozen = frozen ?? _authority.Capture(_scope);
            var current = frozen.GetHead(CatalogRef("0").Identity);
            if (current?.Revision?.Revision != expectedState) throw Error("version_state_changed", "Saved versions changed before publication.");
            if (state != null)
            {
                var next = CatalogRef("cat_" + Guid.NewGuid().ToString("N"));
                Stage(next, state, current?.Revision, state.MigrationCutoff == null ? null :
                    new[] { new ResourceDependency(state.MigrationCutoff, kind: "migration-cutoff") });
                changes.Add(new ResourceHeadChange(next.Identity, current, ResourceHeadState.Known(next, frozen.Generation + 1)));
            }
            if (receipt != null)
            {
                var exact = ReceiptRef(receipt.OperationId, "op_" + Guid.NewGuid().ToString("N")); Stage(exact, receipt);
                changes.Add(new ResourceHeadChange(exact.Identity, null, ResourceHeadState.Known(exact, frozen.Generation + 1)));
            }
            changes = changes.Select(change => new ResourceHeadChange(change.Identity, change.Before, change.After.AtGeneration(frozen.Generation + 1))).ToList();
            var commit = ResourceAuthorityCommit.Create(_scope, frozen.Generation, null, changes, AuthorityCommitReason.CatalogResourcePublication);
            beforeDispatch?.Invoke();
            try { _authority.Publish(commit); }
            catch (ResourceAuthorityConflictException)
            {
                if (receipt != null && Operation(receipt.OperationId, receipt.Intent) != null) return;
                throw Error("version_state_changed", "Version publication conflicted; refresh the original operation.");
            }
            catch (Exception error) { throw Unknown("Version publication receipt is unknown; reconcile the same operation. " + error.Message); }
            try
            {
                if (State() == null || receipt != null && Operation(receipt.OperationId, receipt.Intent) == null)
                    throw Unknown("Catalog read-back failed.");
            }
            catch (Exception error) { throw Unknown("Catalog publication could not be confirmed. " + error.Message); }
        }

        internal void Stage(ResourceRef exact, object record, ResourceRef parent = null, IEnumerable<ResourceDependency> dependencies = null)
        {
            var json = JsonConvert.SerializeObject(record);
            if (System.Text.Encoding.UTF8.GetByteCount(json) > 512 * 1024) throw Error("version_metadata_too_large", "Version metadata exceeds its bound.");
            var payload = PayloadRef.FromBlob(_blobs.StoreText(json, "application/json"));
            _revisions.RegisterRevision(_scope, new ResourceRevisionMetadata(exact, payload.Sha256, payload, parent, dependencies: dependencies));
            _revisions.RegisterView(_scope, new ResourceRevisionView(exact, View, payload.Sha256, payload, ResourceCoverage.Whole()));
        }

        private T Read<T>(ResourceRef exact) where T : class
        {
            var view = _revisions.GetView(_scope, exact, View);
            if (view?.Payload == null || view.Payload.ByteLength > 512 * 1024) throw Error("version_catalog_invalid", "Version metadata is unavailable.");
            return JsonConvert.DeserializeObject<T>(_blobs.ReadText(view.Payload.ToBlobReference())) ??
                throw Error("version_catalog_invalid", "Version metadata is empty.");
        }
        private ResourceRef CatalogRef(string revision) { return new ResourceRef(_root + "/catalog", revision); }
        private ResourceRef EntryRef(string id)
        { return new ResourceRef(_root + "/entries/" + (long.MaxValue - Ordinal(id)).ToString("D19", CultureInfo.InvariantCulture) + "-" + id.Substring(23), id); }
        private ResourceRef ReceiptRef(string operation, string revision = "0")
        {
            if (string.IsNullOrWhiteSpace(operation) || operation.Length > 200) throw Error("version_operation_invalid", "A bounded operation identity is required.");
            var hash = TextPatternEngine.Sha256(operation);
            return new ResourceRef(_root + "/operations/" + hash, revision);
        }
        private static SavedVersionOperation Receipt(string operation, string intent, string outcome, string version)
        { return new SavedVersionOperation { OperationId = operation, Intent = intent, Outcome = outcome, VersionId = version }; }
        private static string VersionId(long ordinal) { return "sv_" + ordinal.ToString("D19", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N"); }
        private static long Ordinal(string id)
        {
            long ordinal; Guid unique;
            if (id == null || id.Length != 55 || id[22] != '_' || !id.StartsWith("sv_", StringComparison.Ordinal) ||
                !Guid.TryParseExact(id.Substring(23), "N", out unique) ||
                !long.TryParse(id.Substring(3, 19), NumberStyles.None, CultureInfo.InvariantCulture, out ordinal) || ordinal < 1)
                throw Error("version_unavailable", "An exact saved version entry is required.");
            return ordinal;
        }
        private static SavedVersionException Error(string code, string message) { return new SavedVersionException(code, message); }
        private static SavedVersionException Unknown(string message) { return new SavedVersionException("version_operation_unknown", message, true); }
    }
}
