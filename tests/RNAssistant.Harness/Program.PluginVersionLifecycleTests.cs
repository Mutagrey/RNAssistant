using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static BrowserVersionResult SavePluginCurrent(PluginApplicationService service, string id)
        {
            var item = service.List().Items.Single(value => value.PluginId == id);
            var target = service.ResolveBrowserLocator(new BrowserResolveRequest { OwnerScope = new BrowserOwnerScope { Kind = BrowserOwnerKind.User, ScopeId = "local" },
                Locator = BrowserLocalLocator.ForPlugin(id, "plugin.json") }).Target;
            var state = service.BrowserVersionState(new BrowserVersionRequest { Target = target });
            var saved = service.BrowserVersionCommand(new BrowserVersionCommand { Target = target, Action = BrowserVersionAction.Save,
                ExpectedStateRevision = state.StateRevision, ExpectedContentRevision = state.ContentRevision, OperationId = Guid.NewGuid().ToString("N") });
            AssertTrue(saved.Outcome == BrowserVersionOutcome.Saved || saved.Outcome == BrowserVersionOutcome.AlreadySaved, saved.Message ?? "checkpoint saved");
            return saved;
        }
        private static PluginItemDto SaveAndActivatePlugin(PluginApplicationService service, string id)
        {
            var saved = SavePluginCurrent(service, id); var active = saved.State.ActiveSavedVersion;
            return service.ActivateSavedVersion(new PluginActivateRequest { PluginId = id, VersionId = saved.Version.VersionId,
                ExpectedActiveRevision = saved.State.ActiveStateRevision, ExpectedActiveVersionId = active?.VersionId,
                ExpectedActiveReleaseId = active?.ReleaseId, OperationId = Guid.NewGuid().ToString("N") });
        }
        private static void SavedFailure(Action action, string code)
        { try { action(); throw new Exception("Expected " + code); } catch (SavedVersionException error) { AssertEqual(code, error.Code, error.Message); } }

        private static void PluginBrowserVersionOwnership()
        {
            WithPluginPaths(paths => {
                using (var service = new PluginApplicationService(paths))
                {
                    var plugin = PluginFixture(service, paths, false);
                    var caller = new BrowserOwnerScope { Kind = BrowserOwnerKind.User, ScopeId = "local" };
                    var locator = BrowserLocalLocator.ForPlugin(plugin.PluginId, "index.html");
                    var target = service.ResolveBrowserLocator(new BrowserResolveRequest { OwnerScope = caller, Locator = locator }).Target;
                    AssertTrue(target != null && target.Capabilities.SaveVersion, "direct source resolution does not depend on a suggestion page");
                    var foreign = service.ResolveBrowserLocator(new BrowserResolveRequest { Locator = locator,
                        OwnerScope = new BrowserOwnerScope { Kind = BrowserOwnerKind.Workspace, ScopeId = "other-workspace" } });
                    AssertTrue(foreign.Target == null, "locator is not an access grant");
                    var state = service.BrowserVersionState(new BrowserVersionRequest { Target = target });
                    var file = Path.Combine(plugin.SourceRoot, "index.html"); var stamp = File.GetLastWriteTimeUtc(file);
                    var original = File.ReadAllText(file); File.WriteAllText(file, original.Replace("Plugin", "Draft!")); File.SetLastWriteTimeUtc(file, stamp);
                    var stale = service.BrowserVersionCommand(new BrowserVersionCommand { Target = target, Action = BrowserVersionAction.Save,
                        ExpectedStateRevision = state.StateRevision, ExpectedContentRevision = state.ContentRevision, OperationId = "stale-working" });
                    AssertEqual(BrowserVersionOutcome.Rejected, stale.Outcome, "Save rejects actual content drift independent of mtime");
                    var saved = SavePluginCurrent(service, plugin.PluginId);
                    AssertEqual(2L, saved.Version.Ordinal, "manual Save needs no chat and creates a never-active checkpoint");
                    AssertEqual(plugin.SavedVersionId, saved.State.ActiveSavedVersion.VersionId, "manual Save preserves active entry");
                    var manifest = Path.Combine(plugin.SourceRoot, "plugin.json"); File.WriteAllText(manifest, "{ broken definition");
                    var invalid = service.BrowserVersionState(new BrowserVersionRequest { Target = target });
                    AssertTrue(!invalid.Capabilities.SaveVersion && invalid.Capabilities.Versions && invalid.Target.Message != null,
                        "invalid current source keeps history with an explicit Save refusal");
                    AssertEqual(2, service.BrowserVersions(new BrowserVersionRequest { Target = target }).Items.Count, "history does not parse or capture broken source");
                    Directory.Move(plugin.SourceRoot, plugin.SourceRoot + "-offline");
                    var savedLocator = BrowserLocalLocator.ForPlugin(plugin.PluginId, "index.html", saved.Version.VersionId);
                    var exact = service.ResolveBrowserLocator(new BrowserResolveRequest { OwnerScope = caller, Locator = savedLocator }).Target;
                    var text = service.ReadBrowserSource(new BrowserSourceRequest { OwnerScope = caller, Locator = savedLocator,
                        Count = 32000, RequestGeneration = 1 }, exact).Text;
                    AssertEqual(original.Replace("Plugin", "Draft!"), text, "saved Browser source reads retained CAS after source root disappears");
                    var suggestions = BrowserTargetResolver.QueryNavigationSuggestions(caller,
                        new BrowserSuggestionRequest { OwnerScope = caller, Prefix = "/plugins/" + plugin.PluginId + "/versions/", Limit = 12 },
                        service.BrowserTargets(caller, "/plugins/" + plugin.PluginId + "/versions/"));
                    AssertTrue(suggestions.Items.Any(s => s.DisplayLocator == savedLocator), "saved completion reads retained manifest metadata offline");
                    var removed = service.BrowserVersionCommand(new BrowserVersionCommand { Target = exact, Action = BrowserVersionAction.Delete,
                        VersionId = saved.Version.VersionId, ExpectedStateRevision = saved.State.StateRevision,
                        ExpectedActiveVersionId = plugin.SavedVersionId, ExpectedActiveReleaseId = plugin.ReleaseId,
                        OperationId = "remove-offline", Confirmed = true });
                    AssertEqual(BrowserVersionOutcome.Removed, removed.Outcome, "read-only saved viewer does not retain execution authority");
                    AssertEqual(BrowserTargetState.Removed, service.ResolveBrowserLocator(new BrowserResolveRequest {
                        OwnerScope = caller, Locator = savedLocator }).Target.State, "a deleted local address never substitutes a newer version");
                }
            });
        }

        // Construct historical metadata directly. Production has no old Apply writer after cutover.
        private static PluginItemDto SeedLegacyPlugin(PluginApplicationService service, AppDataPaths paths, string id)
        {
            var store = new PluginStore(paths); var registration = store.Get(id);
            var release = store.Capture(new PluginSourceWorkspace(new WorkspaceStore(paths).Open(registration.SourceRoot, false)), service.Vendors);
            registration.ActiveReleaseId = release.ReleaseId; registration.Enabled = true;
            var authority = new ResourceAuthorityStore(paths); var scope = new ResourceAuthorityScopeId("plugins", "local");
            var exact = new ResourceRef(ResourceUri.Create("plugin", id, "registration", "current"), "legacy_" + Guid.NewGuid().ToString("N"));
            var payload = PayloadRef.FromBlob(new ChatBlobStore(paths).StoreText(Newtonsoft.Json.JsonConvert.SerializeObject(registration), "application/json"));
            var frozen = authority.Capture(scope); var before = frozen.GetHead(exact.Identity);
            authority.RegisterRevision(scope, new ResourceRevisionMetadata(exact, payload.Sha256, payload, parent: before.Revision));
            authority.RegisterView(scope, new ResourceRevisionView(exact, "plugin-registration", payload.Sha256, payload, ResourceCoverage.Whole()));
            authority.Publish(ResourceAuthorityCommit.Create(scope, frozen.Generation, null,
                new[] { new ResourceHeadChange(exact.Identity, before, ResourceHeadState.Known(exact, frozen.Generation + 1, "plugin-registration")) }, AuthorityCommitReason.CatalogResourcePublication));
            return service.List().Items.Single(item => item.PluginId == id);
        }

        private static void PluginSavedVersionCatalogLifecycle()
        {
            WithPluginPaths(paths => {
                using (var service = new PluginApplicationService(paths))
                {
                    // Existing public path is used only to construct the pre-cutover
                    // registration chain. It refuses further writes after migration.
                    var plugin = PluginFixture(service, paths, false, legacy: true);
                    var store = new PluginStore(paths);
                    var source = new PluginSourceWorkspace(new WorkspaceStore(paths).Open(plugin.SourceRoot, false));
                    var entryPath = PluginPackageFiles.ParseManifest(File.ReadAllText(Path.Combine(plugin.SourceRoot, "plugin.json"))).Entry;
                    var path = Path.Combine(plugin.SourceRoot, entryPath);
                    var first = File.ReadAllText(path); var second = first + "\n<!-- second -->";
                    File.WriteAllText(path, second);
                    var checkedSecond = service.Check(plugin.PluginId);
                    plugin = SeedLegacyPlugin(service, paths, plugin.PluginId);
                    File.WriteAllText(path, second + "\n<!-- candidate only -->");
                    var candidate = store.Capture(source, service.Vendors);
                    var initial = store.VersionState(plugin.PluginId);
                    AssertEqual(2L, initial.LastOrdinal, "all published legacy releases migrated; never-active candidates excluded");
                    var active = store.Get(plugin.PluginId);
                    AssertTrue(active.ActiveSavedVersionId != null && active.ActiveReleaseId == plugin.ReleaseId,
                        "migration binds active entry separately from release");
                    var oldPage = store.ListVersions(plugin.PluginId, null, 1);
                    AssertTrue(oldPage.Cursor != null && oldPage.Items.Single().Ordinal == 2, "bounded newest-first metadata page");
                    var actor = new SavedVersionActor { Kind = SavedVersionActorKind.User };
                    Func<string, PluginReleaseRecord, SavedVersionOperation> save = (operation, content) =>
                        store.SaveVersion(plugin.PluginId, source, service.Vendors, store.VersionState(plugin.PluginId).StateRevision,
                            content.PackageSha256, operation, actor, new[] { content });
                    var saved = save("save-candidate", candidate);
                    AssertEqual("Saved", saved.Outcome, "Check becomes a saved entry only by explicit Save");
                    AssertEqual(3L, saved.Version.Ordinal, "next monotonic ordinal");
                    AssertEqual(active.ActiveSavedVersionId, store.Get(plugin.PluginId).ActiveSavedVersionId, "Save never activates code");
                    AssertEqual("AlreadySaved", save("save-equivalent", candidate).Outcome, "dedup compares the latest available checkpoint");
                    var stale = false;
                    try { store.ListVersions(plugin.PluginId, oldPage.Cursor, 1); }
                    catch (SavedVersionException error) { stale = error.Code == "version_state_changed"; }
                    AssertTrue(stale, "catalog cursor is revision-pinned");
                    var deletion = store.DeleteVersion(plugin.PluginId, saved.VersionId, store.VersionState(plugin.PluginId).StateRevision,
                        active.ActiveSavedVersionId, active.ActiveReleaseId, "remove-candidate", true, false);
                    AssertTrue(deletion.Version.Removed, "deletion tombstones only the selected entry");
                    File.WriteAllText(path, "changed after original save");
                    var sourceStale = false;
                    try { save("stale-source-save", candidate); }
                    catch (SavedVersionException error) { sourceStale = error.Code == "version_source_changed" && !error.MayHavePublished; }
                    AssertTrue(sourceStale && store.VersionState(plugin.PluginId).LastOrdinal == 3, "source drift creates no partial checkpoint");
                    var originalReceipt = store.SaveVersion(plugin.PluginId, source, service.Vendors, initial.StateRevision,
                        candidate.PackageSha256, "save-candidate", actor);
                    AssertTrue(originalReceipt.Version.Removed && originalReceipt.VersionId == saved.VersionId,
                        "old operation receipt does not resurrect a removed entry or recapture changed source");
                    File.WriteAllText(path, second + "\n<!-- candidate only -->");
                    var again = save("new-checkpoint-same-bytes", candidate);
                    AssertEqual(4L, again.Version.Ordinal, "a new save after removal consumes a new ordinal");
                    AssertEqual(saved.Version.ContentReference.Uri, again.Version.ContentReference.Uri, "catalog entries can share one retained release");
                    AssertEqual(active.ActiveRevision, store.Get(plugin.PluginId).ActiveRevision,
                        "other actors' catalog saves do not revoke the pinned active identity");
                    var activated = store.ActivateVersion(plugin.PluginId, again.VersionId, active.ActiveRevision,
                        active.ActiveSavedVersionId, active.ActiveReleaseId, "activate-saved");
                    active = store.Get(plugin.PluginId);
                    AssertEqual(again.VersionId, active.ActiveSavedVersionId, "activation verifies exact entry pointer");
                    var blocked = false;
                    try { store.DeleteVersion(plugin.PluginId, again.VersionId, store.VersionState(plugin.PluginId).StateRevision,
                        active.ActiveSavedVersionId, active.ActiveReleaseId, "remove-active", true, false); }
                    catch (SavedVersionException error) { blocked = error.Code == "version_active"; }
                    AssertTrue(blocked, "active entry cannot be removed");
                    var beforeDisable = store.VersionState(plugin.PluginId).StateRevision;
                    store.Disable(plugin.PluginId);
                    AssertTrue(store.VersionState(plugin.PluginId).StateRevision != beforeDisable, "disable invalidates pending catalog activation guards");
                    var dispatched = false; var staleActivation = false;
                    try { store.ActivateVersion(plugin.PluginId, again.VersionId, active.ActiveRevision, active.ActiveSavedVersionId,
                        active.ActiveReleaseId, "stale-activation", () => dispatched = true); }
                    catch (SavedVersionException error) { staleActivation = error.Code == "version_active_changed"; }
                    AssertTrue(staleActivation && !dispatched, "disable revokes prior activation preparation before dispatch");
                    store = new PluginStore(paths);
                    AssertEqual(4L, store.VersionState(plugin.PluginId).LastOrdinal, "catalog and tombstones survive restart");
                    AssertEqual(again.Version.ContentReference.Uri, PluginStore.ReleaseRef(plugin.PluginId,
                        store.ReadSavedRelease(plugin.PluginId, again.VersionId).ReleaseId).Uri, "never-live saved source remains readable through catalog");
                    AssertTrue(store.SavedVersion(plugin.PluginId, saved.VersionId, true).Removed, "restart preserves removed identity");
                    var inUse = false;
                    var older = store.ListVersions(plugin.PluginId, null).Items.Last();
                    try { store.DeleteVersion(plugin.PluginId, older.VersionId, store.VersionState(plugin.PluginId).StateRevision,
                        active.ActiveSavedVersionId, active.ActiveReleaseId, "remove-pending", true, true); }
                    catch (SavedVersionException error) { inUse = error.Code == "version_in_use"; }
                    AssertTrue(inUse, "exact pending entry is protected independently of its content hash");
                    var oldSaved = oldPage.Items.Single();
                    var oldRelease = store.ReadSavedRelease(plugin.PluginId, oldSaved.VersionId);
                    File.WriteAllText(path, second);
                    var newSameRelease = save("checkpoint-after-rollback", oldRelease);
                    AssertEqual(5L, newSameRelease.Version.Ordinal, "content different from latest checkpoint gets a new ordinal even when retained before");
                    active = store.Get(plugin.PluginId);
                    store.ActivateVersion(plugin.PluginId, newSameRelease.VersionId, active.ActiveRevision, active.ActiveSavedVersionId,
                        active.ActiveReleaseId, "activate-same-release-new-entry");
                    active = store.Get(plugin.PluginId);
                    store.DeleteVersion(plugin.PluginId, oldSaved.VersionId, store.VersionState(plugin.PluginId).StateRevision,
                        active.ActiveSavedVersionId, active.ActiveReleaseId, "remove-other-entry-same-bytes", true, false);
                    AssertEqual(oldRelease.ReleaseId, store.ReadSavedRelease(plugin.PluginId, newSameRelease.VersionId).ReleaseId,
                        "removing another entry with the same release does not revoke the active entry or delete CAS");
                }
            });
        }

        private sealed class VersionReceiptFault : IResourceAuthorityStore
        {
            private readonly IResourceAuthorityStore _inner;
            internal bool LoseNext, ConflictNext;
            internal VersionReceiptFault(IResourceAuthorityStore inner) { _inner = inner; }
            public event EventHandler<ResourceAuthorityChangedEventArgs> Changed { add { _inner.Changed += value; } remove { _inner.Changed -= value; } }
            public ResourceAuthoritySnapshot Capture(ResourceAuthorityScopeId scope) { return _inner.Capture(scope); }
            public ResourceAuthoritySnapshotSet CaptureMany(IReadOnlyList<ResourceAuthorityScopeId> scopes) { return _inner.CaptureMany(scopes); }
            public ResourceHeadState GetHead(ResourceAuthorityScopeId scope, ResourceIdentity identity) { return _inner.GetHead(scope, identity); }
            public ResourceHeadPage ReadHeads(ResourceAuthorityScopeId scope, IReadOnlyList<ResourceHeadRange> ranges, int offset, int limit)
            { return _inner.ReadHeads(scope, ranges, offset, limit); }
            public AuthorityCommitResult Publish(ResourceAuthorityCommit commit)
            {
                if (ConflictNext) { ConflictNext = false; throw new ResourceAuthorityConflictException("injected before publication"); }
                var result = _inner.Publish(commit);
                if (LoseNext) { LoseNext = false; throw new IOException("lost committed receipt"); }
                return result;
            }
        }

        private static void SavedVersionReceiptsReconcileWithoutReplay()
        {
            WithTempPaths(paths => {
                var store = new ResourceAuthorityStore(paths); var blobs = new ChatBlobStore(paths);
                var authority = new VersionReceiptFault(store); var scope = new ResourceAuthorityScopeId("plugins", "local");
                var catalog = new SavedVersionCatalog(authority, store, blobs, scope, ResourceUri.Create("plugin", "fixture", "saved-versions"));
                var initial = catalog.Initialize(null);
                var content = new SavedVersionRecord { ContentReference = PluginStore.ReleaseRef("fixture", "rel_fixture"),
                    ContentFingerprint = TextPatternEngine.Sha256("fixture"), Actor = new SavedVersionActor { Kind = SavedVersionActorKind.User } };
                var dispatches = 0; authority.LoseNext = true;
                var unknown = false;
                try { catalog.Save(content, initial.StateRevision, "lost-save", "same-intent", beforeDispatch: () => dispatches++); }
                catch (SavedVersionException error) { unknown = error.MayHavePublished; }
                AssertTrue(unknown, "lost post-commit receipt is Unknown");
                var recovered = catalog.Save(content, initial.StateRevision, "lost-save", "same-intent", beforeDispatch: () => dispatches++);
                AssertTrue(recovered.Outcome == "Saved" && recovered.Version.Ordinal == 1 && dispatches == 1,
                    "same operation reconciles committed entry without another dispatch or ordinal");
                catalog.Delete(recovered.VersionId, catalog.State().StateRevision, "delete", "remove-exact-entry");
                AssertTrue(catalog.Operation("lost-save", "same-intent").Version.Removed, "receipt exposes subsequent removal without resurrection");
                var expected = catalog.State().StateRevision; authority.ConflictNext = true;
                var rejected = false;
                try { catalog.Save(content, expected, "before-commit", "new-intent", beforeDispatch: () => dispatches++); }
                catch (SavedVersionException error) { rejected = !error.MayHavePublished && error.Code == "version_state_changed"; }
                AssertTrue(rejected, "authority conflict before publication is a proven no-effect refusal");
                var retried = catalog.Save(content, expected, "before-commit", "new-intent");
                AssertEqual(2L, retried.Version.Ordinal, "unpublished staging never consumes an ordinal or poisons the retry");
                AssertEqual(1, catalog.List(null).Items.Count, "removed and uncommitted entries do not appear in available versions");
            });
        }
    }
}
