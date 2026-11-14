using System;
using System.IO;
using System.Linq;
using System.Threading;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;

namespace RNAssistant.Application
{
    public sealed class DocumentHtmlVersionService
    {
        private readonly DocumentArtifactStore _artifacts;
        private readonly IResourceMutationJournal _journal;
        public DocumentHtmlVersionService(AppDataPaths paths, DocumentArtifactStore artifacts, IResourceMutationJournal mutations = null)
        { _journal = mutations ?? new ResourceMutationJournal(paths); _artifacts = artifacts; }
        private static void RequireOwner(ConversationMetadata owner, BrowserOwnerScope caller)
        {
            if (owner == null || caller?.Kind != BrowserOwnerKind.Document || caller.ScopeId != owner.DocumentAuthorityId || caller.ChatId != owner.Id)
                throw new InvalidOperationException("Контекст документа Browser изменился.");
        }
        private IDisposable Lease(ConversationMetadata owner, CancellationToken token = default(CancellationToken))
        { return _journal.AcquireScope(ResourceAuthorityScopeId.Document(new DocumentAuthorityId(owner.DocumentAuthorityId)), true, token); }
        public BrowserResolveResult Resolve(ConversationMetadata owner, BrowserResolveRequest request)
        {
            RequireOwner(owner, request?.OwnerScope);
            BrowserLocalLocator locator;
            if (!BrowserLocalLocator.TryParse(request.Locator, out locator) || locator.Kind != BrowserLocatorKind.Resource && locator.Kind != BrowserLocatorKind.ResourceVersion)
                return new BrowserResolveResult { Reason = BrowserRefusalCode.InvalidLocator, Message = "Укажите локальный материал документа." };
            using (Lease(owner))
            {
                ResourceRef exact; SavedVersionRecord entry = null; string logical = null;
                if (locator.VersionId != null || locator.Key.StartsWith("html_ws_", StringComparison.Ordinal) && !locator.Key.Contains("~"))
                {
                    logical = locator.Key;
                    if (locator.VersionId != null) entry = _artifacts.HtmlVersion(owner, logical, locator.VersionId, true);
                    exact = entry?.ContentReference ?? _artifacts.CurrentHtmlSnapshot(owner, logical);
                }
                else
                {
                    var key = locator.Key.Split('~'); int revision;
                    if (key.Length != 2 || !int.TryParse(key[1], out revision) || revision < 1)
                        return new BrowserResolveResult { Reason = BrowserRefusalCode.InvalidLocator, Message = "Требуется точный локальный материал." };
                    exact = new ResourceRef(ResourceUri.Create("chat", owner.DocumentAuthorityId, "artifact", key[0], "revision", key[1]), key[1]);
                }
                var artifact = _artifacts.Read(owner, exact, false);
                var target = BrowserTargetResolver.Saved(owner, artifact, exact.Uri);
                target.DisplayLocator = locator.ContentLocator; target.RelativePath = locator.RelativePath; target.SavedVersionId = entry?.VersionId;
                if (logical != null && entry == null)
                { target.Kind = BrowserTargetKind.HtmlWorkspace; target.Immutable = false; target.Capabilities.SaveVersion = true; target.Capabilities.Edit = true; }
                if (entry?.Removed == true || _artifacts.IsRemoved(owner, artifact))
                {
                    target.State = BrowserTargetState.Removed; target.Reason = BrowserRefusalCode.Removed; target.Message = "Сохранённая запись удалена.";
                    target.Capabilities = new BrowserTargetCapabilities { Versions = logical != null, SaveReason = BrowserRefusalCode.Removed };
                }
                return new BrowserResolveResult { Locator = locator.Canonical, Target = target, Reason = target.Reason, Message = target.Message };
            }
        }
        private BrowserTargetDescriptor Target(ConversationMetadata owner, BrowserTargetDescriptor target)
        {
            if (target?.ChatId != owner?.Id) throw new InvalidOperationException("Выберите точный чат HTML-источника.");
            var resolved = Resolve(owner, new BrowserResolveRequest { OwnerScope = target?.OwnerScope, Locator = target?.DisplayLocator });
            if (resolved.Target == null || resolved.Target.DocumentId != target.DocumentId || resolved.Target.SavedVersionId != target.SavedVersionId ||
                resolved.Target.Immutable && resolved.Target.ResourceUri != target.ResourceUri)
                throw new InvalidOperationException(resolved.Message ?? "Выбранная версия изменилась.");
            return resolved.Target;
        }
        public BrowserVersionState State(ConversationMetadata owner, BrowserVersionRequest request)
        {
            var target = Target(owner, request?.Target); var logical = Logical(target);
            using (Lease(owner))
            {
                var state = _artifacts.HtmlVersionState(owner, logical); var current = _artifacts.CurrentHtmlSnapshot(owner, logical);
                var artifact = _artifacts.Read(owner, current, false);
                var latest = state.LatestAvailableVersionId == null ? null : Entry(_artifacts.HtmlVersion(owner, logical, state.LatestAvailableVersionId), current);
                return new BrowserVersionState { Target = target, StateRevision = state.StateRevision, ActiveStateRevision = current.Uri,
                    ContentRevision = current.Uri, Dirty = artifact.ContentSha256 != state.LastSavedFingerprint,
                    LatestAvailableSavedVersion = latest, ActiveSavedVersion = latest?.Current == true ? latest : null, Capabilities = target.Capabilities };
            }
        }
        public BrowserVersionPage Versions(ConversationMetadata owner, BrowserVersionRequest request)
        {
            var target = Target(owner, request?.Target); var logical = Logical(target);
            using (Lease(owner))
            {
                var current = _artifacts.CurrentHtmlSnapshot(owner, logical);
                var page = _artifacts.HtmlVersions(owner, logical, request.Cursor, request.Limit);
                return new BrowserVersionPage { StateRevision = page.StateRevision, Cursor = page.Cursor, Items = page.Items.Select(item => Entry(item, current)).ToArray() };
            }
        }
        public System.Collections.Generic.IReadOnlyList<BrowserTargetDescriptor> SavedBrowserTargets(ConversationMetadata owner, BrowserTargetDescriptor target, int limit)
        {
            RequireOwner(owner, target.OwnerScope);
            using (Lease(owner)) return _artifacts.SuggestHtmlVersions(owner, target.LogicalId, limit).Items.Select(entry =>
                new BrowserTargetDescriptor { Kind = BrowserTargetKind.HtmlArtifact, State = BrowserTargetState.Ready, OwnerScope = target.OwnerScope,
                    SavedVersionId = entry.VersionId, DisplayLocator = BrowserLocalLocator.ForResource(target.LogicalId, entry.VersionId), Title = target.Title + " · v" + entry.Ordinal }).ToArray();
        }
        public SourceComparisonDto Compare(ConversationMetadata owner, BrowserSourceComparisonRequest request)
        {
            SourceComparisonService.Validate(request);
            var target = Target(owner, request.Target); var logical = Logical(target);
            using (Lease(owner))
            {
                var before = _artifacts.HtmlVersion(owner, logical, request.BeforeId);
                var after = target.SavedVersionId == null ? null : _artifacts.HtmlVersion(owner, logical, target.SavedVersionId);
                if (request.AfterId != (after?.VersionId ?? target.ResourceUri)) throw new InvalidOperationException("Открытая версия изменилась.");
                var left = _artifacts.Read(owner, before.ContentReference, false);
                var afterRef = after?.ContentReference ?? ChatResourceUri.ArtifactSnapshot(new ResourceRef(target.ResourceUri));
                var right = _artifacts.Read(owner, afterRef, false);
                if (left.ContentByteLength > 16 * 1024 * 1024 || right.ContentByteLength > 16 * 1024 * 1024)
                    throw new InvalidOperationException("Снимок превышает лимит сравнения 16 МиБ.");
                left = _artifacts.Read(owner, before.ContentReference); right = _artifacts.Read(owner, afterRef);
                return SourceComparisonService.Read(request, "v" + before.Ordinal, after == null ? "Открытый снимок" : "v" + after.Ordinal,
                    SourceComparisonService.HtmlMembers(left), SourceComparisonService.HtmlMembers(right));
            }
        }
        public BrowserVersionResult Command(ConversationMetadata owner, BrowserVersionCommand request, CancellationToken token)
        {
            SavedVersionOperation result = null;
            try
            {
                var target = Target(owner, request?.Target); var logical = Logical(target);
                using (Lease(owner, token))
                {
                    if (request.Action == BrowserVersionAction.Save && !target.Immutable)
                        result = _artifacts.SaveHtmlVersion(owner, logical, request.ExpectedStateRevision, request.ExpectedContentRevision, request.OperationId,
                            new SavedVersionActor { Kind = SavedVersionActorKind.User }, beforeDispatch: token.ThrowIfCancellationRequested);
                    else if (request.Action == BrowserVersionAction.Delete)
                        result = _artifacts.DeleteHtmlVersion(owner, logical, request.VersionId, request.ExpectedStateRevision,
                            request.ExpectedActiveRevision, request.OperationId, request.Confirmed);
                    else throw new InvalidOperationException("Сохранение доступно для рабочей копии HTML. Восстановление — отдельное действие редактора.");
                }
                var state = State(owner, new BrowserVersionRequest { Target = target });
                return new BrowserVersionResult { Outcome = (BrowserVersionOutcome)Enum.Parse(typeof(BrowserVersionOutcome), result.Outcome),
                    Version = Entry(result.Version, new ResourceRef(state.ActiveStateRevision)), State = state };
            }
            catch (Exception error) when (!(error is OperationCanceledException) || result != null)
            {
                var unknown = result != null || (error as SavedVersionException)?.MayHavePublished == true;
                return new BrowserVersionResult { Outcome = unknown ? BrowserVersionOutcome.Unknown : BrowserVersionOutcome.Rejected,
                    Reason = unknown ? BrowserRefusalCode.OperationUnknown : (error as SavedVersionException)?.Code == "version_active" ? BrowserRefusalCode.ActiveVersion : BrowserRefusalCode.StaleBinding,
                    Message = error.Message };
            }
        }
        private static string Logical(BrowserTargetDescriptor target)
        { return HtmlWorkspaceIdentity.LogicalId(target.ArtifactId) ?? throw new InvalidDataException("Этот HTML не имеет истории проекта."); }
        private static BrowserVersionEntry Entry(SavedVersionRecord entry, ResourceRef current)
        {
            var active = entry.ContentReference.Uri == current.Uri;
            return new BrowserVersionEntry { VersionId = entry.VersionId, Ordinal = entry.Ordinal, Label = entry.Label, CreatedUtc = entry.CreatedUtc,
                ContentRevision = entry.ContentReference.Uri, Current = active, Active = active, Removed = entry.Removed, CanDelete = !active && !entry.Removed,
                DeleteBlockedReason = entry.Removed ? BrowserRefusalCode.Removed : active ? BrowserRefusalCode.ActiveVersion : BrowserRefusalCode.None };
        }
    }
}
