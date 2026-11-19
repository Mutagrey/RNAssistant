using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private sealed class LegacyPluginImportFixture
        {
            internal ResourceAuthorityStore Authority;
            internal PluginStore Store;
            internal PluginRegistration Registration;
            internal WorkspaceDescriptor Workspace;
            internal ChatBlobStore Blobs;
            internal SavedVersionRecord First, Removed, Latest;
            internal string Origin;
            internal ResourceAuthorityScopeId Scope = new ResourceAuthorityScopeId("plugins", "local");
        }

        private static LegacyPluginImportFixture SeedLegacyPluginImport(AppDataPaths paths, StorageProtector protector,
            string sourceRoot = null)
        {
            var fixture = new LegacyPluginImportFixture { Authority = new ResourceAuthorityStore(paths),
                Blobs = new ChatBlobStore(paths, () => protector) };
            var old = new ChatConversationStoreAdapter(new ChatStore(paths, () => protector));
            var mutations = new ResourceMutationJournal(paths);
            var files = new WorkspaceFileService(paths, fixture.Authority, fixture.Authority, mutations);
            fixture.Store = new PluginStore(paths, old, fixture.Authority, mutations, files, fixture.Blobs);
            fixture.Workspace = new WorkspaceStore(paths).Open(sourceRoot ?? Path.Combine(paths.Root, "old-plugin-source"));
            var manifest = new PluginManifestV1 { Id = "retained-plugin", Name = "Original plugin", Version = "1.0.0",
                Entry = "index.html", Files = new[] { "plugin.json", "index.html" }, FormatVersion = 1, HostProtocol = 1,
                Vendors = new[] { new PluginVendorDependency { Id = "retained-vendor", Version = "1.2.3", Mount = "vendor/retained" } } };
            var json = JsonConvert.SerializeObject(manifest);
            File.WriteAllText(Path.Combine(fixture.Workspace.RootPath, "plugin.json"), json);
            File.WriteAllText(Path.Combine(fixture.Workspace.RootPath, "index.html"), "Current editable draft");
            fixture.Registration = new PluginRegistration { PluginId = manifest.Id, Name = manifest.Name,
                SourceWorkspaceId = fixture.Workspace.WorkspaceId, SourceRoot = fixture.Workspace.RootPath, Enabled = false };
            // Seed the legacy wire directly so the encrypted fixture uses one
            // protector for every retained payload, including the initial manifest.
            PublishLegacyPluginRecord(fixture, new ResourceRef(ResourceUri.Create("plugin", manifest.Id, "registration", "current"), "reg_seed"),
                "plugin-registration", fixture.Registration, new PayloadRef[0]);
            var catalog = new SavedVersionCatalog(fixture.Authority, fixture.Authority, fixture.Blobs, fixture.Scope,
                ResourceUri.Create("plugin", manifest.Id, "saved-versions"));
            catalog.Initialize(null);
            var versions = new List<SavedVersionRecord>();
            for (var index = 1; index <= 3; index++)
            {
                var release = new PluginReleaseRecord { PluginId = manifest.Id, ReleaseId = "rel_" + Guid.NewGuid().ToString("N"),
                    SourceWorkspaceId = fixture.Workspace.WorkspaceId, ManifestJson = json,
                    Files = new List<PluginPackageFile> {
                        new PluginPackageFile { Path = "plugin.json", Payload = PayloadRef.FromBlob(fixture.Blobs.StoreText(json, "application/json")) },
                        new PluginPackageFile { Path = "index.html", Payload = PayloadRef.FromBlob(fixture.Blobs.StoreText("<main>Saved version " + index + "</main>", "text/html")) },
                        new PluginPackageFile { Path = "vendor/retained/library.js", Payload = PayloadRef.FromBlob(fixture.Blobs.StoreText("/* exact retained vendor bytes */", "text/javascript")) }
                    } };
                release.PackageSha256 = TextPatternEngine.Sha256(string.Join("\n", release.Files.OrderBy(file => file.Path, StringComparer.Ordinal)
                    .Select(file => file.Path + " " + file.Payload.Sha256)));
                PublishLegacyPluginRecord(fixture, PluginStore.ReleaseRef(manifest.Id, release.ReleaseId), "plugin-release", release,
                    release.Files.Select(file => file.Payload));
                var saved = catalog.Save(new SavedVersionRecord { ContentReference = PluginStore.ReleaseRef(manifest.Id, release.ReleaseId),
                    ContentFingerprint = release.PackageSha256, ManifestVersion = manifest.Version,
                    CreatedUtc = new DateTime(2026, 10, index, 0, 0, 0, DateTimeKind.Utc),
                    Actor = new SavedVersionActor { Kind = SavedVersionActorKind.User } }, catalog.State().StateRevision,
                    "old-save-" + index, "old-save-intent-" + index);
                versions.Add(saved.Version);
                if (index == 1) fixture.Store.ActivateVersion(manifest.Id, saved.VersionId, null, null, null, "old-activate");
            }
            fixture.First = versions[0]; fixture.Removed = versions[1]; fixture.Latest = versions[2];
            var active = fixture.Store.Get(manifest.Id);
            fixture.Store.DeleteVersion(manifest.Id, fixture.Removed.VersionId, fixture.Store.VersionState(manifest.Id).StateRevision,
                active.ActiveSavedVersionId, active.ActiveReleaseId, "old-remove", true, false);
            fixture.Registration = fixture.Store.SetAppearance(manifest.Id,
                new PluginAppearance { Name = "My retained title", Icon = "Folder", IconColor = "#123456" });
            fixture.Origin = LegacyPluginRecoveryService.ImportRevision(fixture.Authority.Capture(fixture.Scope), manifest.Id);
            return fixture;
        }

        private static void PublishLegacyPluginRecord(LegacyPluginImportFixture fixture, ResourceRef reference, string view,
            object record, IEnumerable<PayloadRef> parts, IEnumerable<ResourceDependency> dependencies = null)
        {
            var snapshot = fixture.Authority.Capture(fixture.Scope); var before = snapshot.GetHead(reference.Identity);
            var payload = PayloadRef.FromBlob(fixture.Blobs.StoreText(JsonConvert.SerializeObject(record), "application/json"));
            fixture.Authority.RegisterRevision(fixture.Scope, new ResourceRevisionMetadata(reference, payload.Sha256, payload,
                parent: before?.Revision, dependencies: dependencies));
            fixture.Authority.RegisterView(fixture.Scope, new ResourceRevisionView(reference, view, payload.Sha256, payload, ResourceCoverage.Whole(), parts));
            fixture.Authority.Publish(ResourceAuthorityCommit.Create(fixture.Scope, snapshot.Generation, null,
                new[] { new ResourceHeadChange(reference.Identity, before, ResourceHeadState.Known(reference, snapshot.Generation + 1)) }, AuthorityCommitReason.CatalogResourcePublication));
        }

        private static PluginStore NativeLegacyPluginTarget(AppDataPaths paths, StorageProtector protector, out ResourceJournal resources)
        {
            resources = new ResourceJournal(paths, protector);
            return new PluginStore(paths, new ConversationJournal(paths, "workspace:import-fixture", protector), resources, resources,
                new WorkspaceFileService(paths, resources, resources, resources), new ChatBlobStore(paths, () => protector));
        }

        private static void LegacyPluginImportFailure(Action action, string code)
        {
            try { action(); throw new Exception("Expected " + code); }
            catch (PluginException error) { AssertEqual("legacy_plugin_import_" + code, error.Code, error.Message); }
        }

        private static void LegacyPluginImportRetainedVersions()
        {
            foreach (var encrypted in new[] { false, true }) WithTempPaths(paths => {
                var protector = encrypted ? new StorageProtector(HistoryIntegrityModes.HmacSha256,
                    HistoryEncryptionModes.Aes256CbcHmacSha256, "import-key", Enumerable.Repeat((byte)41, 16).ToArray()) : StorageProtector.None;
                var fixture = SeedLegacyPluginImport(paths, protector);
                var moved = fixture.Workspace.RootPath + "-offline"; Directory.Move(fixture.Workspace.RootPath, moved);
                var original = LegacyRecoveryBytes(paths.ResourceAuthorityDirectory, paths.ChatBlobDirectory, moved);
                var listed = new LegacyPluginRecoveryService(paths, () => protector).List().Items.Single();
                AssertTrue(listed.CanImport && !listed.CanConnect && listed.Status == "missingSource", "retained publication can migrate while source is offline");
                AssertEqual(fixture.Origin, listed.RegistrationRevision, "list pins the whole version namespace");
                ResourceJournal resources; var native = NativeLegacyPluginTarget(paths, protector, out resources);
                AssertTrue(native.ReadLegacyRegistrationImport(fixture.Registration.PluginId, fixture.Origin) == null, "read-only receipt is absent before migration");
                var result = native.ImportLegacyRegistration(fixture.Registration.PluginId, fixture.Origin);
                AssertTrue(!result.AlreadyImported && result.SavedVersions == 3 && result.Releases == 3, "entire retained catalog is adopted");
                AssertEqual(JsonConvert.SerializeObject(fixture.Registration), JsonConvert.SerializeObject(result.Registration), "registration including appearance, active refs and enabled state is exact");
                AssertEqual(JsonConvert.SerializeObject(fixture.Latest), JsonConvert.SerializeObject(native.SavedVersion(fixture.Registration.PluginId, fixture.Latest.VersionId)), "saved identity, ordinal, label, actor and date are exact");
                AssertTrue(native.SavedVersion(fixture.Registration.PluginId, fixture.Removed.VersionId, true).Removed, "removed saved entry stays removed");
                AssertEqual(2, native.ListVersions(fixture.Registration.PluginId, null).Items.Count, "removed entry does not reappear in the version list");
                var first = native.ReadSavedRelease(fixture.Registration.PluginId, fixture.First.VersionId);
                AssertEqual("<main>Saved version 1</main>", Encoding.UTF8.GetString(native.ReadFile(first, "index.html")), "active bytes come from retained CAS, not today's draft");
                AssertEqual("/* exact retained vendor bytes */", Encoding.UTF8.GetString(native.ReadFile(first, "vendor/retained/library.js")), "captured vendor mount is recovered without a live vendor/source lookup");
                var catalog = new SavedVersionCatalog(resources, resources, new ChatBlobStore(paths, () => protector), fixture.Scope,
                    ResourceUri.Create("plugin", fixture.Registration.PluginId, "saved-versions"));
                AssertTrue(catalog.Operation("old-save-2", "old-save-intent-2").Version.Removed, "old receipt cannot resurrect a deleted entry");
                AssertEqual(0, resources.Unresolved().Count, "migration introduces no mutation or invocation attempt");
                native.Disable(fixture.Registration.PluginId); native.SetHidden(fixture.Registration.PluginId, true);
                ResourceJournal reopenedResources; var reopened = NativeLegacyPluginTarget(paths, protector, out reopenedResources);
                var count = Directory.GetFiles(paths.JournalResourceDirectory, "*.jsonl", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
                var retry = reopened.ImportLegacyRegistration(fixture.Registration.PluginId, fixture.Origin);
                AssertTrue(retry.AlreadyImported && !retry.Registration.Enabled && retry.Registration.Hidden, "restart/retry returns receipt without overriding subsequent native edits");
                AssertEqual(count, Directory.GetFiles(paths.JournalResourceDirectory, "*.jsonl", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length), "duplicate imports append no facts");
                RequireLegacyRecoveryBytes(original);
            });
        }

        private static void LegacyPluginImportInterruptionAndReceipt()
        {
            foreach (var afterPublication in new[] { false, true }) WithTempPaths(paths => {
                var fixture = SeedLegacyPluginImport(paths, StorageProtector.None);
                var snapshot = fixture.Authority.Capture(fixture.Scope);
                var references = snapshot.Commits.SelectMany(commit => commit.HeadChanges)
                    .SelectMany(change => new[] { change.Before?.Revision, change.After?.Revision }).Where(reference => reference != null)
                    .Select(reference => reference.Uri + "\n" + reference.Revision).Distinct().Count();
                ResourceJournal resources; var native = NativeLegacyPluginTarget(paths, StorageProtector.None, out resources);
                var writes = 0; resources.Journal.Fault = point => {
                    if (point == "after_flush" && ++writes == (afterPublication ? references * 2 + 3 : 1))
                        throw new IOException("simulated process/ack loss");
                };
                if (afterPublication) LegacyPluginImportFailure(() => native.ImportLegacyRegistration(fixture.Registration.PluginId, fixture.Origin), "unknown");
                else RuntimeThrows<IOException>(() => native.ImportLegacyRegistration(fixture.Registration.PluginId, fixture.Origin));
                resources.Journal.Fault = null;
                ResourceJournal reopenedResources; var reopened = NativeLegacyPluginTarget(paths, StorageProtector.None, out reopenedResources);
                var receipt = reopened.ReadLegacyRegistrationImport(fixture.Registration.PluginId, fixture.Origin);
                AssertEqual(afterPublication, receipt != null, "only the final atomic fact makes a receipt visible");
                AssertEqual(afterPublication ? 1 : 0, reopened.List().Count, "no partial registration becomes visible during staging");
                var result = reopened.ImportLegacyRegistration(fixture.Registration.PluginId, fixture.Origin);
                AssertEqual(afterPublication, result.AlreadyImported, "same exact import resumes staging or reads its committed receipt");
                AssertEqual(2, reopened.ListVersions(fixture.Registration.PluginId, null).Items.Count, "restart has complete versions and tombstones");
            });
        }

        private static void LegacyPluginImportRejectsIncompleteClosure()
        {
            foreach (var scenario in new[] { "native-conflict", "missing-cas", "cross-scope", "wrong-key", "stale" }) WithTempPaths(paths => {
                var protector = scenario == "wrong-key" ? new StorageProtector(HistoryIntegrityModes.HmacSha256,
                    HistoryEncryptionModes.Aes256CbcHmacSha256, "old-key", Enumerable.Repeat((byte)43, 16).ToArray()) : StorageProtector.None;
                var fixture = SeedLegacyPluginImport(paths, protector);
                ResourceJournal resources; var native = NativeLegacyPluginTarget(paths, StorageProtector.None, out resources);
                var expected = "payload_unavailable";
                if (scenario == "native-conflict")
                {
                    native.Connect(new PluginSourceWorkspace(fixture.Workspace));
                    native.SetAppearance(fixture.Registration.PluginId, new PluginAppearance { Name = "New native title" });
                    expected = "target_exists";
                }
                else if (scenario == "missing-cas")
                {
                    var release = fixture.Store.ReadSavedRelease(fixture.Registration.PluginId, fixture.First.VersionId);
                    File.Delete(fixture.Blobs.PathFor(release.Files.Single(file => file.Path == "index.html").Payload.Sha256));
                }
                else if (scenario == "cross-scope")
                {
                    var reference = new ResourceRef(ResourceUri.Create("plugin", fixture.Registration.PluginId, "registration", "current"), "foreign-dependency");
                    PublishLegacyPluginRecord(fixture, reference, "plugin-registration", fixture.Registration, new PayloadRef[0],
                        new[] { new ResourceDependency(new ResourceRef(ResourceUri.Create("state", "workspace", "other", "html-saved-versions", "catalog"), "1")) });
                    fixture.Origin = LegacyPluginRecoveryService.ImportRevision(fixture.Authority.Capture(fixture.Scope), fixture.Registration.PluginId);
                    expected = "dependency_unsupported";
                }
                else if (scenario == "stale")
                {
                    fixture.Store.DeleteVersion(fixture.Registration.PluginId, fixture.Latest.VersionId,
                        fixture.Store.VersionState(fixture.Registration.PluginId).StateRevision,
                        fixture.Registration.ActiveSavedVersionId, fixture.Registration.ActiveReleaseId, "later-old-removal", true, false);
                    AssertEqual(JsonConvert.SerializeObject(fixture.Registration), JsonConvert.SerializeObject(fixture.Store.Get(fixture.Registration.PluginId)),
                        "version-only changes leave the registration head untouched");
                    expected = "source_changed";
                }
                var before = resources.Journal.WrittenBytes;
                LegacyPluginImportFailure(() => native.ImportLegacyRegistration(fixture.Registration.PluginId, fixture.Origin), expected);
                AssertEqual(before, resources.Journal.WrittenBytes, "validation/conflict failure stages no native resource facts");
                AssertEqual(scenario == "native-conflict" ? 1 : 0, native.List().Count, "rejection preserves the existing native catalog");
                if (scenario == "native-conflict") AssertEqual("New native title", native.Get(fixture.Registration.PluginId).Appearance.Name, "an existing source reconnect is not overwritten or claimed as imported");
                AssertTrue(native.ReadLegacyRegistrationImport(fixture.Registration.PluginId, fixture.Origin) == null, "rejected import has no success receipt");
            });
        }
    }
}
