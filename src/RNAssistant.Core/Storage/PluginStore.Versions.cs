using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Core.Storage
{
    public sealed partial class PluginStore
    {
        private SavedVersionCatalog VersionCatalog(string id)
        {
            RequireId(id);
            return new SavedVersionCatalog(_authority, _authority, _blobs, Scope, ResourceUri.Create("plugin", id, "saved-versions"));
        }

        public SavedVersionCatalogState VersionState(string id)
        { using (_journal.AcquireScope(Scope, true)) { MigrateVersions(id); return VersionCatalog(id).State(); } }

        public SavedVersionPage ListVersions(string id, string cursor, int limit = 12)
        { using (_journal.AcquireScope(Scope, true)) { MigrateVersions(id); return VersionCatalog(id).List(cursor, limit); } }

        public SavedVersionPage SuggestVersions(string id, int limit)
        { using (_journal.AcquireScope(Scope, true)) { return VersionCatalog(id).Peek(limit); } }
        public SavedVersionRecord SavedVersion(string id, string versionId, bool allowRemoved = false)
        { using (_journal.AcquireScope(Scope, true)) { MigrateVersions(id); return VersionCatalog(id).Get(versionId, allowRemoved); } }

        public PluginReleaseRecord ReadSavedRelease(string id, string versionId)
        { using (_journal.AcquireScope(Scope, true)) { MigrateVersions(id); return SavedRelease(id, VersionCatalog(id).Get(versionId)); } }

        public SavedVersionOperation SaveVersion(string id, PluginSourceWorkspace source, VendorCatalog vendors, string expectedState,
            string expectedContent, string operationId, SavedVersionActor actor, IEnumerable<PluginReleaseRecord> reusable = null,
            CancellationToken token = default(CancellationToken), Action beforeDispatch = null)
        {
            if (source == null || actor == null || actor.Kind != SavedVersionActorKind.User && actor.Kind != SavedVersionActorKind.Run ||
                actor.Kind == SavedVersionActorKind.User && (actor.ChatId != null || actor.RunId != null) ||
                actor.Kind == SavedVersionActorKind.Run && (string.IsNullOrEmpty(actor.ChatId) || string.IsNullOrEmpty(actor.RunId)))
                throw new SavedVersionException("version_actor_invalid", "A real User or Run actor is required.");
            var catalog = VersionCatalog(id);
            var intent = "save:" + expectedContent + ":" + actor.Kind + ":" + actor.ChatId + ":" + actor.RunId;
            var retained = (reusable ?? Enumerable.Empty<PluginReleaseRecord>()).ToList();
            using (_journal.AcquireScope(Scope, true, token))
            {
                MigrateVersions(id);
                var old = catalog.Operation(operationId, intent); if (old != null) return old;
                if (actor.Kind == SavedVersionActorKind.Run)
                {
                    var chat = new ChatStore(_paths).ReadSessionHeader(actor.ChatId);
                    var lifecycle = chat?.RunViewState?.Lifecycle ?? chat?.RunStatus;
                    var activeRun = lifecycle == RunViewLifecycles.Running || lifecycle == RunViewLifecycles.AwaitingConfirmation || lifecycle == RunViewLifecycles.AwaitingUser;
                    if (chat?.WorkspaceId != source.SourceWorkspaceId || chat?.RunId != actor.RunId || !activeRun)
                        throw new SavedVersionException("version_actor_invalid", "The source run belongs to another workspace or has expired.");
                }
                if (Get(id)?.SourceWorkspaceId != source.SourceWorkspaceId)
                    throw new SavedVersionException("version_source_invalid", "The plugin belongs to another source workspace.");
                var state = catalog.State();
                if (state.StateRevision != expectedState) throw new SavedVersionException("version_state_changed", "Refresh saved versions before saving.");
                if (state.LatestAvailableVersionId != null) retained.Add(SavedRelease(id, catalog.Get(state.LatestAvailableVersionId)));
            }
            SavedVersionOperation result = null;
            Capture(source, vendors, retained, onCaptured: package => {
                using (_journal.AcquireScope(Scope, true, token))
                {
                    var registration = Get(id);
                    if (registration == null || registration.SourceWorkspaceId != source.SourceWorkspaceId || package.PluginId != id)
                        throw new SavedVersionException("version_source_invalid", "The plugin source identity changed.");
                    if (string.IsNullOrEmpty(expectedContent) || package.PackageSha256 != expectedContent)
                        throw new SavedVersionException("version_source_changed", "Source changed after its check; check it again.");
                    result = catalog.Save(new SavedVersionRecord { ContentReference = ReleaseRef(id, package.ReleaseId),
                        ContentFingerprint = package.PackageSha256, ManifestVersion = PluginPackageFiles.ParseManifest(package.ManifestJson).Version,
                        Actor = actor }, expectedState, operationId, intent, beforeDispatch: beforeDispatch);
                }
            }, token: token);
            return result;
        }

        public SavedVersionOperation ActivateVersion(string id, string versionId, string expectedActiveRevision,
            string expectedActiveVersion, string expectedActiveRelease, string operationId, Action beforeDispatch = null, CancellationToken token = default(CancellationToken))
        {
            using (_journal.AcquireScope(Scope, true, token))
            {
                MigrateVersions(id); var catalog = VersionCatalog(id);
                var intent = "activate:" + versionId + ":" + expectedActiveVersion + ":" + expectedActiveRelease + ":" + expectedActiveRevision;
                var old = catalog.Operation(operationId, intent); if (old != null) return old;
                var entry = catalog.Get(versionId); var release = SavedRelease(id, entry);
                var current = Get(id); RequireActive(current, expectedActiveVersion, expectedActiveRelease);
                if (current.ActiveRevision != expectedActiveRevision)
                    throw new SavedVersionException("version_active_changed", "The active publication changed; prepare activation again.");
                if (current.Hidden) throw new SavedVersionException("version_source_invalid", "Reconnect the plugin before activating a version.");
                foreach (var file in release.Files)
                {
                    token.ThrowIfCancellationRequested();
                    if (!_blobs.HasVerifiedReference(file.Payload.ToBlobReference()))
                        throw new SavedVersionException("version_source_invalid", "A retained package member is unavailable: " + file.Path);
                }
                token.ThrowIfCancellationRequested();
                var manifest = PluginPackageFiles.ParseManifest(release.ManifestJson);
                current.ActiveReleaseId = release.ReleaseId; current.ActiveSavedVersionId = versionId;
                current.ActiveRevision = Guid.NewGuid().ToString("N");
                current.Name = manifest.Name; current.Icon = manifest.Icon; current.Enabled = true;
                var result = catalog.Activate(versionId, catalog.State().StateRevision, operationId, intent,
                    frozen => new[] { StageVersionRegistration(frozen, current, expectedActiveVersion, expectedActiveRelease, expectedActiveRevision) }, beforeDispatch);
                try
                {
                    var verified = Get(id);
                    if (verified.ActiveSavedVersionId != versionId || verified.ActiveReleaseId != release.ReleaseId || !verified.Enabled)
                        throw new SavedVersionException("version_operation_unknown", "Active saved-entry read-back failed; inspect the original operation.", true);
                }
                catch (Exception error) { throw new SavedVersionException("version_operation_unknown", "Active saved-entry read-back failed. " + error.Message, true); }
                return result;
            }
        }

        public SavedVersionOperation DeleteVersion(string id, string versionId, string expectedState,
            string expectedActiveVersion, string expectedActiveRelease, string operationId, bool confirmed, bool inUse)
        {
            using (_journal.AcquireScope(Scope, true))
            {
                MigrateVersions(id); var catalog = VersionCatalog(id);
                var intent = "delete:" + versionId + ":" + expectedActiveVersion + ":" + expectedActiveRelease;
                var old = catalog.Operation(operationId, intent); if (old != null) return old;
                if (!confirmed) throw new SavedVersionException("version_confirmation_required", "Confirm removal of this saved entry.");
                var active = Get(id); RequireActive(active, expectedActiveVersion, expectedActiveRelease);
                if (active.ActiveSavedVersionId == versionId) throw new SavedVersionException("version_active", "The active saved version cannot be removed.");
                if (inUse) throw new SavedVersionException("version_in_use", "A pending operation is bound to this saved entry.");
                return catalog.Delete(versionId, expectedState, operationId, intent);
            }
        }

        private PluginReleaseRecord SavedRelease(string id, SavedVersionRecord entry)
        {
            var release = Read<PluginReleaseRecord>(entry.ContentReference, "plugin-release");
            if (release.PluginId != id || release.SourceWorkspaceId != Get(id)?.SourceWorkspaceId ||
                release.PackageSha256 != entry.ContentFingerprint || ReleaseRef(id, release.ReleaseId).Uri != entry.ContentReference.Uri)
                throw new SavedVersionException("version_source_invalid", "Saved entry does not match the exact retained plugin source.");
            ValidateRelease(release); return release;
        }

        // Only the registration chain at the recorded cutoff is imported. A checked
        // never-active candidate is not a user version. Each entry has a durable
        // migration receipt; interruption resumes without duplicate ordinals/bytes.
        private void MigrateVersions(string id)
        {
            var registration = Get(id) ?? throw new PluginException("plugin_missing", "Plugin is not connected.");
            var catalog = VersionCatalog(id);
            var state = catalog.State() ?? catalog.Initialize(registration.ActiveReleaseId == null ? null :
                _authority.GetHead(Scope, RegistrationRef(id, "0").Identity).Revision);
            if (!state.MigrationComplete)
            {
                var releases = PublishedReleaseIds(id, state.MigrationCutoff).Select(release => new {
                    Id = release, Created = _authority.GetRevision(Scope, ReleaseRef(id, release))?.CreatedUtc ?? DateTime.MinValue
                }).OrderBy(item => item.Created).ThenBy(item => item.Id, StringComparer.Ordinal);
                foreach (var item in releases)
                {
                    var record = ReadRelease(new PluginReleaseIdentity(id, item.Id));
                    catalog.Save(new SavedVersionRecord { ContentReference = ReleaseRef(id, item.Id), ContentFingerprint = record.PackageSha256,
                        ManifestVersion = PluginPackageFiles.ParseManifest(record.ManifestJson).Version, CreatedUtc = item.Created,
                        Actor = new SavedVersionActor { Kind = SavedVersionActorKind.Legacy } }, catalog.State().StateRevision,
                        "legacy:" + item.Id, "legacy:" + state.MigrationCutoff.Revision + ":" + item.Id, migrating: true);
                }
                catalog.CompleteMigration();
            }
            if (registration.ActiveReleaseId != null && registration.ActiveSavedVersionId == null)
            {
                var imported = state.MigrationCutoff == null ? null : catalog.Operation("legacy:" + registration.ActiveReleaseId,
                    "legacy:" + state.MigrationCutoff.Revision + ":" + registration.ActiveReleaseId);
                if (imported == null) throw new SavedVersionException("version_catalog_invalid", "The active legacy publication has no saved entry.");
                registration.ActiveSavedVersionId = imported.VersionId;
                var previousActiveRevision = registration.ActiveRevision;
                registration.ActiveRevision = Guid.NewGuid().ToString("N");
                catalog.UpdateOwner(frozen => new[] { StageVersionRegistration(frozen, registration, null, registration.ActiveReleaseId, previousActiveRevision) });
            }
        }

        private ResourceHeadChange StageVersionRegistration(ResourceAuthoritySnapshot frozen, PluginRegistration next,
            string expectedVersion, string expectedRelease, string expectedActiveRevision)
        {
            var before = frozen.GetHead(RegistrationRef(next.PluginId, "0").Identity);
            var current = before?.Knowledge == HeadKnowledge.Known ? Read<PluginRegistration>(before.Revision, "plugin-registration") : null;
            RequireActive(current, expectedVersion, expectedRelease);
            if (current.ActiveRevision != expectedActiveRevision)
                throw new SavedVersionException("version_active_changed", "The active publication changed before dispatch.");
            var exact = RegistrationRef(next.PluginId, "reg_" + Guid.NewGuid().ToString("N"));
            var payload = PayloadRef.FromBlob(_blobs.StoreText(JsonConvert.SerializeObject(next), "application/json"));
            _authority.RegisterRevision(Scope, new ResourceRevisionMetadata(exact, payload.Sha256, payload, before.Revision));
            _authority.RegisterView(Scope, new ResourceRevisionView(exact, "plugin-registration", payload.Sha256, payload, ResourceCoverage.Whole()));
            return new ResourceHeadChange(exact.Identity, before, ResourceHeadState.Known(exact, frozen.Generation + 1));
        }

        private static void RequireActive(PluginRegistration current, string version, string release)
        {
            if (current == null || current.ActiveReleaseId != release || current.ActiveSavedVersionId != version)
                throw new SavedVersionException("version_active_changed", "The active saved entry changed; refresh before continuing.");
        }
    }
}
