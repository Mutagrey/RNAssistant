using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Core.Storage
{
    public sealed partial class PluginStore
    {
        private sealed class LegacyPluginImportReceipt
        {
            public string PluginId { get; set; }
            public string RegistrationRevision { get; set; }
            public ResourceRef OriginalRegistration { get; set; }
            public int SavedVersions { get; set; }
            public int Releases { get; set; }
        }

        private sealed class LegacyPluginRevision
        {
            internal ResourceRevisionMetadata Metadata;
            internal ResourceRevisionView View;
        }

        // Recovery adopts retained immutable bytes. It does not capture today's
        // workspace, publish a candidate, replay an invocation or create an instance.
        public LegacyPluginImportResult ImportLegacyRegistration(string pluginId, string expectedRegistrationRevision,
            CancellationToken token = default(CancellationToken))
        {
            RequireLegacyImportRequest(pluginId, expectedRegistrationRevision);
            if (!(_authority is ResourceJournal))
                throw ImportError("target_invalid", "Перенос требует текущего native хранилища.");
            using (RuntimeStorageActivity.Acquire(_paths, token))
            using (_journal.AcquireScope(Scope, true, token))
            {
                var retained = ReadLegacyRegistrationImport(pluginId, expectedRegistrationRevision, token);
                if (retained != null) return retained;
                var prefix = ResourceUri.Create("plugin", pluginId) + "/";
                var target = _authority.Capture(Scope);
                if (target.Heads.Keys.Any(uri => uri.StartsWith(prefix, StringComparison.Ordinal)))
                    throw ImportError("target_exists", "В текущем хранилище уже есть этот плагин. Его регистрация и версии не изменены.");
                using (var read = LegacyPluginRecoveryService.ReadAuthority(_paths, token))
                {
                    var source = read.Owner.Capture(Scope);
                    if (LegacyPluginRecoveryService.ImportRevision(source, pluginId) != expectedRegistrationRevision)
                        throw ImportError("source_changed", "Старый каталог изменился. Обновите список перед переносом.");
                    var heads = source.Heads.Values.Where(head => head.Identity.Uri.StartsWith(prefix, StringComparison.Ordinal))
                        .OrderBy(head => head.Identity.Uri, StringComparer.Ordinal).ToArray();
                    var registrationHead = source.GetHead(RegistrationRef(pluginId, "0").Identity);
                    if (registrationHead?.Knowledge != HeadKnowledge.Known ||
                        heads.Any(head => head.Knowledge != HeadKnowledge.Known && head.Knowledge != HeadKnowledge.Removed))
                        throw ImportError("unavailable", "Старая регистрация или часть её каталога недоступна; частичный перенос не выполнен.");
                    var revisions = ReadLegacyPluginGraph(read.Owner, source, heads, pluginId, token);
                    var receipt = ValidateLegacyPluginGraph(read.Owner, heads, revisions, registrationHead.Revision,
                        pluginId, expectedRegistrationRevision, token);
                    var receiptReference = ImportReceiptRef(pluginId, expectedRegistrationRevision);
                    var changes = heads.Select(head => new ResourceHeadChange(head.Identity, null,
                        head.AtGeneration(target.Generation + 1))).ToList();
                    changes.Add(new ResourceHeadChange(receiptReference.Identity, null,
                        ResourceHeadState.Known(receiptReference, target.Generation + 1, "legacy-plugin-import")));
                    var commit = new ResourceAuthorityCommit("legacy-plugin-import:" + pluginId + ":" + expectedRegistrationRevision,
                        Scope, target.Generation, target.Generation + 1, null, changes, AuthorityCommitReason.Restore);
                    RequireImportFrame(commit);
                    // Validate everything before staging. An interruption below can
                    // leave immutable metadata, never a partially visible registration.
                    foreach (var revision in revisions)
                    {
                        token.ThrowIfCancellationRequested();
                        _revisions.RegisterRevision(Scope, revision.Metadata);
                        _revisions.RegisterView(Scope, revision.View);
                    }
                    var body = PayloadRef.FromBlob(_blobs.StoreText(JsonConvert.SerializeObject(receipt), "application/json"));
                    _revisions.RegisterRevision(Scope, new ResourceRevisionMetadata(receiptReference, body.Sha256, body,
                        dependencies: new[] { new ResourceDependency(registrationHead.Revision, kind: "legacy-registration") },
                        createdUtc: read.Owner.GetRevision(Scope, registrationHead.Revision).CreatedUtc));
                    _revisions.RegisterView(Scope, new ResourceRevisionView(receiptReference, "legacy-plugin-import",
                        body.Sha256, body, ResourceCoverage.Whole()));
                    token.ThrowIfCancellationRequested();
                    try { _authority.Publish(commit); }
                    catch (ResourceAuthorityConflictException)
                    { throw ImportError("target_changed", "Текущий каталог изменился во время переноса. Существующие записи не заменены."); }
                    catch (Exception error)
                    { throw ImportError("unknown", "Результат переноса нужно проверить по квитанции; повторная публикация не запущена. " + error.Message); }
                    try
                    {
                        var result = ReadLegacyRegistrationImport(pluginId, expectedRegistrationRevision, token);
                        if (result == null) throw new InvalidDataException("Import receipt is unavailable.");
                        result.AlreadyImported = false;
                        return result;
                    }
                    catch (Exception error)
                    { throw ImportError("unknown", "Перенос опубликован, но квитанция не прочитана. Проверьте результат. " + error.Message); }
                }
            }
        }

        // This operation performs no writes and does not need the legacy source.
        // Later native edits remain authoritative after the original import.
        public LegacyPluginImportResult ReadLegacyRegistrationImport(string pluginId, string expectedRegistrationRevision,
            CancellationToken token = default(CancellationToken))
        {
            RequireLegacyImportRequest(pluginId, expectedRegistrationRevision);
            token.ThrowIfCancellationRequested();
            var head = _authority.GetHead(Scope, ImportReceiptRef(pluginId, expectedRegistrationRevision).Identity);
            if (head == null) return null;
            if (head.Knowledge != HeadKnowledge.Known)
                throw ImportError("unknown", "Квитанция переноса недоступна; существующий плагин не заменён.");
            var receipt = Read<LegacyPluginImportReceipt>(head.Revision, "legacy-plugin-import");
            if (receipt?.PluginId != pluginId || receipt.RegistrationRevision != expectedRegistrationRevision ||
                receipt.OriginalRegistration?.Identity.Equals(RegistrationRef(pluginId, "0").Identity) != true)
                throw ImportError("source_changed", "Этот плагин уже перенесён из другого снимка старого каталога.");
            var registration = Get(pluginId);
            if (registration == null) throw ImportError("target_changed", "Перенесённая регистрация больше недоступна. Автоматическое восстановление поверх текущего состояния не выполнено.");
            return new LegacyPluginImportResult { Registration = registration, RegistrationRevision = receipt.RegistrationRevision,
                AlreadyImported = true, SavedVersions = receipt.SavedVersions, Releases = receipt.Releases };
        }

        private List<LegacyPluginRevision> ReadLegacyPluginGraph(ResourceAuthorityStore authority,
            ResourceAuthoritySnapshot source, ResourceHeadState[] heads, string pluginId, CancellationToken token)
        {
            var prefix = ResourceUri.Create("plugin", pluginId) + "/";
            var pending = new Queue<ResourceRef>(heads.Where(head => head.Revision != null).Select(head => head.Revision));
            // Removed saved entries still need their immutable metadata for old
            // receipts. Retain past revisions, without importing the old commits.
            foreach (var change in source.Commits.SelectMany(commit => commit.HeadChanges)
                .Where(change => change.Identity.Uri.StartsWith(prefix, StringComparison.Ordinal)))
            {
                if (change.Before?.Revision != null) pending.Enqueue(change.Before.Revision);
                if (change.After?.Revision != null) pending.Enqueue(change.After.Revision);
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var verified = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<LegacyPluginRevision>();
            while (pending.Count != 0)
            {
                token.ThrowIfCancellationRequested();
                var reference = pending.Dequeue();
                if (!reference.Uri.StartsWith(prefix, StringComparison.Ordinal))
                    throw ImportError("dependency_unsupported", "Каталог ссылается на ресурс вне этого плагина. Перенос не выполнен.");
                if (!seen.Add(reference.Uri + "\n" + reference.Revision)) continue;
                if (seen.Count > 10000) throw ImportError("too_large", "История плагина превышает предел переноса 10000 записей; исходные данные сохранены.");
                var expectedView = LegacyPluginView(reference, pluginId);
                var metadata = authority.GetRevision(Scope, reference);
                var views = authority.ReadViews(Scope, reference, 0, 2);
                if (metadata?.Payload == null || views.SnapshotCount != 1 || views.Items.Count != 1 ||
                    views.Items[0].View != expectedView || views.Items[0].Coverage.Kind != ResourceCoverageKinds.Whole ||
                    views.Items[0].Payload == null || metadata.Payload.Sha256 != views.Items[0].Payload.Sha256 ||
                    metadata.ContentSha256 != metadata.Payload.Sha256 || views.Items[0].ContentSha256 != metadata.ContentSha256 ||
                    metadata.Payload.ByteLength > 1024 * 1024)
                    throw ImportError("invalid", "Неполные или неподдерживаемые метаданные старого каталога: " + reference.Uri);
                var view = views.Items[0];
                RequireImportFrame(metadata); RequireImportFrame(view);
                foreach (var payload in new[] { metadata.Payload, view.Payload }.Concat(view.Parts))
                {
                    if (payload == null || payload.ByteLength > WorkspaceFileService.MaximumCaptureSetBytes)
                        throw ImportError("too_large", "Размер сохранённого пакета превышает предел переноса.");
                    var key = JsonConvert.SerializeObject(payload);
                    if (verified.Add(key))
                    {
                        try { LegacyRecoveryFiles.RequireRegular(_blobs.PathFor(payload.Sha256)); }
                        catch (Exception error) when (LegacyRecoveryFiles.ReadFailure(error))
                        { throw ImportError("payload_unavailable", "Сохранённые байты недоступны: " + error.Message); }
                        if (!_blobs.HasVerifiedReference(payload.ToBlobReference()))
                            throw ImportError("payload_unavailable", "Сохранённые байты отсутствуют, повреждены или недоступны с текущим ключом защиты.");
                    }
                }
                if (metadata.Parent != null) pending.Enqueue(metadata.Parent);
                if (metadata.RestoredFrom != null) pending.Enqueue(metadata.RestoredFrom);
                foreach (var dependency in metadata.Dependencies) pending.Enqueue(dependency.Resource);
                result.Add(new LegacyPluginRevision { Metadata = metadata, View = view });
            }
            return result;
        }

        private LegacyPluginImportReceipt ValidateLegacyPluginGraph(ResourceAuthorityStore authority,
            ResourceHeadState[] heads, List<LegacyPluginRevision> revisions, ResourceRef registrationReference,
            string pluginId, string origin, CancellationToken token)
        {
            Func<ResourceRef, string, LegacyPluginRevision> exact = (reference, view) => revisions.SingleOrDefault(item =>
                item.Metadata.Reference.Uri == reference.Uri && item.Metadata.Reference.Revision == reference.Revision && item.View.View == view);
            var registration = LegacyValue<PluginRegistration>(exact(registrationReference, "plugin-registration"));
            var entries = new Dictionary<string, SavedVersionRecord>(StringComparer.Ordinal);
            var releases = new Dictionary<string, PluginReleaseRecord>(StringComparer.Ordinal);
            var registrations = new List<PluginRegistration>();
            var operations = new List<SavedVersionOperation>();
            foreach (var item in revisions)
            {
                token.ThrowIfCancellationRequested();
                var reference = item.Metadata.Reference;
                if (item.View.View == "plugin-registration")
                {
                    var value = LegacyValue<PluginRegistration>(item);
                    if (value.PluginId != pluginId || string.IsNullOrWhiteSpace(value.SourceWorkspaceId) ||
                        value.SourceWorkspaceId != registration.SourceWorkspaceId || string.IsNullOrWhiteSpace(value.SourceRoot) ||
                        !Path.IsPathRooted(value.SourceRoot) || value.Enabled && value.ActiveReleaseId == null)
                        throw ImportError("invalid", "Идентичность старой регистрации не подтверждена.");
                    registrations.Add(value);
                }
                else if (item.View.View == "plugin-release")
                {
                    var release = LegacyValue<PluginReleaseRecord>(item); ValidateRelease(release);
                    if (release.PluginId != pluginId || release.SourceWorkspaceId != registration.SourceWorkspaceId ||
                        !SameLegacyRef(reference, ReleaseRef(pluginId, release.ReleaseId)) ||
                        release.Files.Sum(file => file.Payload.ByteLength) > WorkspaceFileService.MaximumCaptureSetBytes ||
                        !release.Files.Select(file => JsonConvert.SerializeObject(file.Payload)).OrderBy(value => value, StringComparer.Ordinal)
                            .SequenceEqual(item.View.Parts.Select(payload => JsonConvert.SerializeObject(payload)).OrderBy(value => value, StringComparer.Ordinal)) ||
                        !heads.Any(head => head.Knowledge == HeadKnowledge.Known && SameLegacyRef(head.Revision, reference)))
                        throw ImportError("invalid", "Сохранённая публикация не соответствует её исходному workspace или CAS.");
                    var manifest = PluginPackageFiles.ParseManifest(release.ManifestJson);
                    if (manifest.Files.Any(path => !release.Files.Any(file => file.Path == path)) ||
                        manifest.Vendors.Any(vendor => !release.Files.Any(file => file.Path.StartsWith(vendor.Mount + "/", StringComparison.Ordinal))) ||
                        release.Files.Any(file => !manifest.Files.Contains(file.Path) && !manifest.Vendors.Any(vendor => file.Path.StartsWith(vendor.Mount + "/", StringComparison.Ordinal))) ||
                        _blobs.ReadText(release.Files.Single(file => file.Path == "plugin.json").Payload.ToBlobReference()) != release.ManifestJson)
                        throw ImportError("dependency_unsupported", "Состав сохранённой публикации или встроенных vendor-файлов неполон.");
                    releases.Add(release.ReleaseId, release);
                }
                else
                {
                    var address = ResourceUri.Parse(reference.Uri);
                    if (address.Segments[2] == "entries")
                    {
                        var entry = LegacyValue<SavedVersionRecord>(item);
                        if (entry.Actor == null || !Enum.IsDefined(typeof(SavedVersionActorKind), entry.Actor.Kind))
                            throw ImportError("invalid", "Автор сохранённой версии не определён.");
                        entries.Add(entry.VersionId, entry);
                    }
                    else if (address.Segments[2] == "operations") operations.Add(LegacyValue<SavedVersionOperation>(item));
                    else LegacyValue<SavedVersionCatalogState>(item);
                }
            }
            foreach (var entry in entries.Values)
            {
                PluginReleaseRecord release;
                if (entry.ContentReference == null || !releases.TryGetValue(ResourceUri.Parse(entry.ContentReference.Uri).Segments.Last(), out release) ||
                    !SameLegacyRef(entry.ContentReference, ReleaseRef(pluginId, release.ReleaseId)) || entry.ContentFingerprint != release.PackageSha256)
                    throw ImportError("dependency_unsupported", "Сохранённая версия ссылается на отсутствующую публикацию или другой ресурс.");
            }
            var catalog = new SavedVersionCatalog(authority, authority, _blobs, Scope, ResourceUri.Create("plugin", pluginId, "saved-versions"));
            var state = catalog.State();
            if (state == null && (entries.Count != 0 || operations.Count != 0)) throw ImportError("invalid", "Отсутствует каталог сохранённых версий.");
            foreach (var entry in entries.Values)
            {
                var verifiedEntry = catalog.Get(entry.VersionId, true);
                if (verifiedEntry.Ordinal > state.LastOrdinal || verifiedEntry.ContentReference.Uri != entry.ContentReference.Uri)
                    throw ImportError("invalid", "Порядок сохранённых версий не подтверждён.");
            }
            if (state != null && (state.LastOrdinal < 0 ||
                (state.LatestAvailableVersionId == null ? state.LastSavedFingerprint != null :
                    catalog.Get(state.LatestAvailableVersionId).ContentFingerprint != state.LastSavedFingerprint)))
                throw ImportError("invalid", "Последняя сохранённая версия не соответствует каталогу.");
            foreach (var operation in operations)
                if (catalog.Operation(operation.OperationId, operation.Intent) == null)
                    throw ImportError("invalid", "Квитанция сохранённой версии недоступна.");
            foreach (var value in registrations)
            {
                if (value.ActiveReleaseId != null && !releases.ContainsKey(value.ActiveReleaseId))
                    throw ImportError("invalid", "Активная публикация отсутствует в сохранённом каталоге.");
                if (value.ActiveSavedVersionId != null &&
                    (!entries.ContainsKey(value.ActiveSavedVersionId) || entries[value.ActiveSavedVersionId].ContentReference.Uri != ReleaseRef(pluginId, value.ActiveReleaseId).Uri))
                    throw ImportError("invalid", "Активная сохранённая версия не соответствует публикации.");
            }
            if (registration.ActiveSavedVersionId != null) catalog.Get(registration.ActiveSavedVersionId);
            return new LegacyPluginImportReceipt { PluginId = pluginId, RegistrationRevision = origin,
                OriginalRegistration = registrationReference, SavedVersions = entries.Count, Releases = releases.Count };
        }

        private T LegacyValue<T>(LegacyPluginRevision revision) where T : class
        {
            if (revision == null) throw ImportError("invalid", "Не найдены точные метаданные старого каталога.");
            return DeserializePluginRecord<T>(_blobs.ReadText(revision.View.Payload.ToBlobReference())) ??
                throw ImportError("invalid", "Старые метаданные не прочитаны.");
        }

        private static string LegacyPluginView(ResourceRef reference, string pluginId)
        {
            var address = ResourceUri.Parse(reference.Uri); var parts = address.Segments;
            if (address.Provider != "plugin" || parts.Count < 3 || parts[0] != pluginId)
                throw ImportError("dependency_unsupported", "Ресурс принадлежит другому каталогу.");
            if (parts.Count == 3 && parts[1] == "registration" && parts[2] == "current") return "plugin-registration";
            if (parts.Count == 3 && parts[1] == "release") return "plugin-release";
            if (parts[1] == "saved-versions" && (parts.Count == 3 && parts[2] == "catalog" ||
                parts.Count == 4 && (parts[2] == "entries" || parts[2] == "operations"))) return "saved-version-metadata";
            throw ImportError("dependency_unsupported", "Неподдерживаемый ресурс в старом каталоге: " + reference.Uri);
        }

        private static ResourceRef ImportReceiptRef(string pluginId, string revision)
        { return new ResourceRef(ResourceUri.Create("plugin", pluginId, "legacy-import", "registration"), revision); }
        private static void RequireLegacyImportRequest(string pluginId, string revision)
        {
            RequireId(pluginId);
            if (revision == null || revision.Length != 64 || revision.Any(value => !Uri.IsHexDigit(value)))
                throw ImportError("source_changed", "Выберите точный снимок регистрации из старого каталога.");
        }
        private static bool SameLegacyRef(ResourceRef left, ResourceRef right)
        { return left?.Uri == right?.Uri && left?.Revision == right?.Revision; }
        private static void RequireImportFrame(object value)
        {
            if (Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(value)) > StorageJournal.MaximumFrameBytes / 2)
                throw ImportError("too_large", "Каталог превышает размер атомарного переноса; исходные данные сохранены.");
        }
        private static PluginException ImportError(string code, string message)
        { return new PluginException("legacy_plugin_import_" + code, message); }
    }
}
