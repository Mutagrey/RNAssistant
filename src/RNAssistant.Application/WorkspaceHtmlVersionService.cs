using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;

namespace RNAssistant.Application
{
    // Coordinates existing definition, file and artifact owners. Holds no history.
    public sealed partial class WorkspaceHtmlVersionService
    {
        private readonly AppDataPaths _paths;
        private readonly WorkspaceStore _workspaces;
        private readonly WorkspaceFileService _files;
        private readonly IConversationStore _chats;
        private readonly IResourceMutationJournal _journal;
        private readonly DocumentArtifactStore _artifacts;
        public event Action<BrowserVersionStateChanged> VersionChanged;
        public WorkspaceHtmlVersionService(AppDataPaths paths, IConversationStore chats = null, WorkspaceFileService files = null,
            IResourceAuthorityStore authority = null, IResourceMutationJournal mutations = null)
        {
            if ((chats != null || files != null || authority != null || mutations != null) &&
                (chats == null || files == null || authority == null || mutations == null))
                throw new ArgumentException("HTML versions require the complete conversation/file/resource composition.");
            _paths = paths; _workspaces = new WorkspaceStore(paths);
            _files = files ?? new WorkspaceFileService(paths); _chats = chats ?? new ChatConversationStoreAdapter(new ChatStore(paths));
            _journal = mutations ?? new ResourceMutationJournal(paths);
            var owner = authority ?? new ResourceAuthorityStore(paths);
            _artifacts = new DocumentArtifactStore(owner, owner as IResourceRevisionStore ??
                throw new ArgumentException("The resource owner must retain revisions."), new ChatBlobStore(paths));
        }

        public BrowserResolveResult Resolve(BrowserResolveRequest request)
        {
            BrowserLocalLocator locator;
            BrowserTargetDescriptor target = null;
            if (!BrowserLocalLocator.TryParse(request?.Locator, out locator) ||
                locator.Kind != BrowserLocatorKind.ProjectSource && locator.Kind != BrowserLocatorKind.ProjectVersion)
                return new BrowserResolveResult { Reason = BrowserRefusalCode.InvalidLocator, Message = "Укажите локальный HTML-проект." };
            try
            {
                RequireOwner(request.OwnerScope, locator.Key);
                var workspace = _workspaces.OpenConnectedHistory(locator.Key);
                target = new BrowserTargetDescriptor { Kind = BrowserTargetKind.WorkspaceHtmlProject, State = BrowserTargetState.Ready,
                    WorkspaceId = locator.Key, SavedVersionId = locator.VersionId, Immutable = locator.VersionId != null,
                    OwnerScope = new BrowserOwnerScope { Kind = BrowserOwnerKind.Workspace, ScopeId = locator.Key, ChatId = request.OwnerScope.ChatId },
                    ChatId = request.OwnerScope.ChatId, RelativePath = locator.RelativePath, DisplayLocator = locator.ContentLocator, Title = workspace.Name,
                    Capabilities = new BrowserTargetCapabilities { Source = true, Versions = true, PreviewReason = BrowserRefusalCode.NativeUnavailable,
                        SaveReason = locator.VersionId != null ? BrowserRefusalCode.ReadOnly : BrowserRefusalCode.None } };
                if (locator.VersionId != null)
                {
                    using (_journal.AcquireScope(DocumentArtifactStore.WorkspaceHtmlScope(locator.Key), true))
                    {
                        var entry = _artifacts.WorkspaceHtmlVersion(locator.Key, locator.VersionId, true);
                        if (entry.Removed)
                        {
                            target.State = BrowserTargetState.Removed; target.Reason = BrowserRefusalCode.Removed; target.Message = "Сохранённая версия удалена.";
                            target.Capabilities.Source = false; target.Capabilities.SaveReason = BrowserRefusalCode.Removed;
                        }
                        else
                        {
                            var snapshot = _artifacts.ReadWorkspaceHtml(locator.Key, locator.VersionId);
                            if (!snapshot.Members.Any(m => m.Path == locator.RelativePath)) throw new InvalidDataException("Файла нет в сохранённой версии.");
                            target.EntryPath = snapshot.EntryPath; target.ContentRevision = entry.ContentFingerprint;
                            target.Capabilities.ExportProject = target.Capabilities.CreateProject = true;
                        }
                    }
                }
                else
                {
                    workspace = _workspaces.OpenConnected(locator.Key);
                    var definition = _workspaces.GetHtmlProjectDefinition(workspace);
                    target.EntryPath = definition.Definition?.EntryPath ?? workspace.HtmlEntryPath;
                    if (!(definition.Definition?.Members ?? new[] { target.EntryPath }).Contains(locator.RelativePath))
                        throw new InvalidDataException("Файл не входит в объявленный состав проекта.");
                    target.Capabilities.Edit = !workspace.ReadOnly;
                    target.Capabilities.SaveVersion = !workspace.ReadOnly && definition.Definition != null;
                    target.Capabilities.SaveReason = workspace.ReadOnly ? BrowserRefusalCode.ReadOnly : definition.Definition == null ?
                        BrowserRefusalCode.ProjectDefinitionRequired : BrowserRefusalCode.None;
                }
                return new BrowserResolveResult { Locator = locator.Canonical, Target = target, Reason = target.Reason, Message = target.Message };
            }
            catch (Exception error) when (error is IOException || error is ArgumentException || error is InvalidOperationException)
            {
                if (target != null)
                {
                    target.State = BrowserTargetState.Unavailable; target.Reason = BrowserRefusalCode.TargetUnavailable; target.Message = error.Message;
                    target.Capabilities = new BrowserTargetCapabilities { Versions = true, SaveReason = BrowserRefusalCode.TargetUnavailable, PreviewReason = BrowserRefusalCode.NativeUnavailable };
                }
                return new BrowserResolveResult { Target = target, Reason = BrowserRefusalCode.TargetUnavailable, Message = error.Message };
            }
        }

        public BrowserSourcePage Source(BrowserSourceRequest request, BrowserTargetDescriptor target)
        {
            RequireOwner(request.OwnerScope, target.WorkspaceId);
            if (target.SavedVersionId != null)
            {
                var snapshot = _artifacts.ReadWorkspaceHtml(target.WorkspaceId, target.SavedVersionId);
                return BrowserSourceReader.Read(target, request, snapshot.Members.Select(m => m.Path), path =>
                    SourceComparisonService.Utf8(_artifacts.ReadWorkspaceHtmlMember(target.WorkspaceId, target.SavedVersionId, path)));
            }
            var workspace = _workspaces.OpenConnected(target.WorkspaceId);
            var definition = _workspaces.GetHtmlProjectDefinition(workspace);
            return BrowserSourceReader.Read(target, request, definition.Definition?.Members ?? new[] { workspace.HtmlEntryPath },
                path => _files.ReadText(workspace, path).Text);
        }

        public System.Collections.Generic.IReadOnlyList<BrowserTargetDescriptor> SavedBrowserTargets(BrowserOwnerScope owner, string title, int limit)
        {
            RequireOwner(owner, owner.ScopeId);
            using (_journal.AcquireScope(DocumentArtifactStore.WorkspaceHtmlScope(owner.ScopeId), true))
                return _artifacts.SuggestWorkspaceHtmlVersions(owner.ScopeId, limit).Items.Select(entry => {
                    var snapshot = _artifacts.ReadWorkspaceHtml(owner.ScopeId, entry.VersionId);
                    return new BrowserTargetDescriptor { Kind = BrowserTargetKind.WorkspaceHtmlProject, State = BrowserTargetState.Ready,
                        OwnerScope = owner, WorkspaceId = owner.ScopeId, SavedVersionId = entry.VersionId, Immutable = true,
                        DisplayLocator = BrowserLocalLocator.ForProject(owner.ScopeId, snapshot.EntryPath, entry.VersionId), Title = title + " · v" + entry.Ordinal };
                }).ToArray();
        }

        public SourceComparisonDto Compare(BrowserSourceComparisonRequest request)
        {
            SourceComparisonService.Validate(request);
            var target = request.Target;
            RequireOwner(target?.OwnerScope, target?.WorkspaceId);
            if (target.Kind != BrowserTargetKind.WorkspaceHtmlProject || string.IsNullOrEmpty(target.SavedVersionId) || request.AfterId != target.SavedVersionId)
                throw new InvalidOperationException("Выберите сохранённую версию проекта для сравнения.");
            var before = _artifacts.ReadWorkspaceHtml(target.WorkspaceId, request.BeforeId);
            var after = _artifacts.ReadWorkspaceHtml(target.WorkspaceId, request.AfterId);
            Func<HtmlProjectSnapshotV1, string, IEnumerable<SourceComparisonMember>> members = (snapshot, version) => snapshot.Members.Select(member =>
                new SourceComparisonMember { Path = member.Path, Fingerprint = member.Sha256, Length = member.ByteLength,
                    Read = () => SourceComparisonService.Utf8(_artifacts.ReadWorkspaceHtmlMember(target.WorkspaceId, version, member.Path, SourceComparisonService.MaximumText)) });
            var a = _artifacts.WorkspaceHtmlVersion(target.WorkspaceId, request.BeforeId);
            var b = _artifacts.WorkspaceHtmlVersion(target.WorkspaceId, request.AfterId);
            return SourceComparisonService.Read(request, "v" + a.Ordinal, "v" + b.Ordinal, members(before, request.BeforeId), members(after, request.AfterId));
        }

        public BrowserVersionState State(BrowserVersionRequest request, CancellationToken token = default(CancellationToken))
        {
            var target = Target(request?.Target);
            string content = target.ContentRevision, definition = null;
            if (target.SavedVersionId == null && target.Capabilities.SaveVersion)
            {
                try { Capture(target.WorkspaceId, token, (snapshot, validate) => { content = DocumentArtifactStore.WorkspaceHtmlFingerprint(snapshot); definition = snapshot.DefinitionFingerprint; }); }
                catch (Exception error) when (error is IOException || error is InvalidOperationException)
                { target.Capabilities.SaveVersion = false; target.Capabilities.SaveReason = BrowserRefusalCode.TargetUnavailable; target.Message = error.Message; }
            }
            using (_journal.AcquireScope(DocumentArtifactStore.WorkspaceHtmlScope(target.WorkspaceId), true, token))
            {
                var state = _artifacts.WorkspaceHtmlVersionState(target.WorkspaceId);
                return new BrowserVersionState { Target = target, StateRevision = state.StateRevision, ContentRevision = content,
                    DefinitionRevision = definition, Dirty = content != null && content != state.LastSavedFingerprint,
                    LatestAvailableSavedVersion = state.LatestAvailableVersionId == null ? null : Entry(_artifacts.WorkspaceHtmlVersion(target.WorkspaceId, state.LatestAvailableVersionId)),
                    Capabilities = target.Capabilities };
            }
        }
        public BrowserVersionPage Versions(BrowserVersionRequest request)
        {
            var target = Target(request?.Target);
            using (_journal.AcquireScope(DocumentArtifactStore.WorkspaceHtmlScope(target.WorkspaceId), true))
            {
                var page = _artifacts.WorkspaceHtmlVersions(target.WorkspaceId, request.Cursor, request.Limit);
                return new BrowserVersionPage { StateRevision = page.StateRevision, Cursor = page.Cursor, Items = page.Items.Select(Entry).ToArray() };
            }
        }
        public BrowserVersionResult Command(BrowserVersionCommand request, CancellationToken token = default(CancellationToken))
        {
            SavedVersionOperation result = null;
            try
            {
                var target = Target(request?.Target);
                if (request.Action == BrowserVersionAction.Save && !target.Immutable)
                    result = Save(target, new SavedVersionActor { Kind = SavedVersionActorKind.User }, request, token);
                else if (request.Action == BrowserVersionAction.Delete)
                    using (_journal.AcquireScope(DocumentArtifactStore.WorkspaceHtmlScope(target.WorkspaceId), true, token))
                        result = _artifacts.DeleteWorkspaceHtml(target.WorkspaceId, request.VersionId, request.ExpectedStateRevision, request.OperationId, request.Confirmed);
                else throw new SavedVersionException("version_read_only", "Эта операция не поддерживается для HTML-проекта.");
                try { VersionChanged?.Invoke(new BrowserVersionStateChanged { Target = target, Reason = request.Action, VersionId = result.VersionId }); } catch { }
                return new BrowserVersionResult { Outcome = (BrowserVersionOutcome)Enum.Parse(typeof(BrowserVersionOutcome), result.Outcome),
                    Version = Entry(result.Version), State = State(new BrowserVersionRequest { Target = target }, token) };
            }
            catch (Exception error) when (!(error is OperationCanceledException) || result != null)
            {
                var unknown = result != null || (error as SavedVersionException)?.MayHavePublished == true;
                return new BrowserVersionResult { Outcome = unknown ? BrowserVersionOutcome.Unknown : BrowserVersionOutcome.Rejected,
                    Reason = unknown ? BrowserRefusalCode.OperationUnknown : BrowserRefusalCode.StaleBinding, Message = error.Message };
            }
        }

        public SavedVersionOperation Save(BrowserTargetDescriptor target, SavedVersionActor actor, BrowserVersionCommand command, CancellationToken token)
        {
            RequireOwner(target.OwnerScope, target.WorkspaceId);
            RequireActor(target.WorkspaceId, actor);
            var scope = DocumentArtifactStore.WorkspaceHtmlScope(target.WorkspaceId);
            using (_journal.AcquireScope(scope, true, token))
            {
                var old = _artifacts.WorkspaceHtmlOperation(target.WorkspaceId, command.OperationId,
                    DocumentArtifactStore.WorkspaceHtmlSaveIntent(command.ExpectedContentRevision, actor));
                if (old != null) return old;
            }
            SavedVersionOperation result = null;
            Capture(target.WorkspaceId, token, (snapshot, validate) => {
                if (snapshot.DefinitionFingerprint != command.ExpectedDefinitionRevision)
                    throw new SavedVersionException("version_source_changed", "Состав проекта изменился.");
                using (_journal.AcquireScope(scope, true, token))
                {
                    validate(); RequireActor(target.WorkspaceId, actor); token.ThrowIfCancellationRequested();
                    result = _artifacts.SaveWorkspaceHtml(snapshot, actor, command.ExpectedStateRevision, command.ExpectedContentRevision,
                        command.OperationId, beforeDispatch: () => { validate(); token.ThrowIfCancellationRequested(); });
                }
            });
            return result;
        }
        private void Capture(string workspaceId, CancellationToken token, Action<HtmlProjectSnapshotV1, Action> publish, bool readOnlyPreview = false)
        {
            var workspace = _workspaces.OpenConnected(workspaceId);
            if (workspace.ReadOnly && !readOnlyPreview) throw new InvalidOperationException("Проект доступен только для чтения.");
            using (var definition = _workspaces.PinHtmlProjectDefinition(workspace, token))
            {
                if (definition.State.Definition == null) throw new WorkspaceFileException("project_definition_required", "Укажите состав HTML-проекта.");
                using (var files = _files.CaptureSet(workspace, definition.State.Definition.Members, token: token))
                {
                    Action validate = () => { definition.Validate(); files.Validate(); };
                    validate();
                    publish(new HtmlProjectSnapshotV1 { WorkspaceId = workspaceId, EntryPath = definition.State.Definition.EntryPath,
                        DefinitionFingerprint = definition.State.Fingerprint, Members = files.Files.Select(file => new HtmlProjectSnapshotMember {
                            Path = file.RelativePath, ExactRef = file.Reference, Sha256 = file.ContentSha256, ByteLength = file.ByteLength,
                            Mime = file.Evidence.Payload.ContentType ?? "application/octet-stream" }).ToArray() }, validate);
                }
            }
        }
        public HtmlProjectDefinitionState GetDefinition(HtmlProjectDefinitionRequest request)
        { RequireOwner(request.OwnerScope, request.WorkspaceId); return _workspaces.GetHtmlProjectDefinition(_workspaces.OpenConnected(request.WorkspaceId)); }
        public HtmlProjectDefinitionState SetDefinition(HtmlProjectDefinitionRequest request, CancellationToken token)
        { RequireOwner(request.OwnerScope, request.WorkspaceId); return _workspaces.SetHtmlProjectDefinition(_workspaces.OpenConnected(request.WorkspaceId),
            request.ExpectedFingerprint, request.Definition, token); }

        private BrowserTargetDescriptor Target(BrowserTargetDescriptor target)
        {
            if (target?.Kind != BrowserTargetKind.WorkspaceHtmlProject || target.WorkspaceId == null) throw new InvalidOperationException("Требуется точный HTML-проект.");
            var result = Resolve(new BrowserResolveRequest { OwnerScope = target.OwnerScope, Locator = target.DisplayLocator });
            if (result.Target == null || result.Target.WorkspaceId != target.WorkspaceId || result.Target.SavedVersionId != target.SavedVersionId)
                throw new InvalidOperationException(result.Message ?? "Контекст HTML-проекта изменился.");
            return result.Target;
        }
        private void RequireOwner(BrowserOwnerScope owner, string workspaceId)
        {
            if (owner == null || !(owner.Kind == BrowserOwnerKind.User && owner.ScopeId == "local" && string.IsNullOrEmpty(owner.ChatId) ||
                owner.Kind == BrowserOwnerKind.Workspace && owner.ScopeId == workspaceId))
                throw new InvalidOperationException("HTML-проект принадлежит другому контексту.");
            _workspaces.OpenConnectedHistory(workspaceId);
            if (!string.IsNullOrEmpty(owner.ChatId) && _chats.ReadHeader(owner.ChatId)?.WorkspaceId != workspaceId)
                throw new InvalidOperationException("Чат принадлежит другому проекту.");
        }
        private void RequireActor(string workspaceId, SavedVersionActor actor)
        {
            if (actor?.Kind == SavedVersionActorKind.User && actor.ChatId == null && actor.RunId == null) return;
            var chat = actor?.Kind == SavedVersionActorKind.Run ? _chats.ReadHeader(actor.ChatId) : null;
            var lifecycle = chat?.RunViewState?.Lifecycle ?? chat?.RunStatus;
            if (chat?.WorkspaceId != workspaceId || chat?.RunId != actor?.RunId ||
                lifecycle != RunViewLifecycles.Running && lifecycle != RunViewLifecycles.AwaitingConfirmation && lifecycle != RunViewLifecycles.AwaitingUser)
                throw new SavedVersionException("version_actor_invalid", "Требуется действующий run этого проекта.");
        }
        private static BrowserVersionEntry Entry(SavedVersionRecord value)
        { return new BrowserVersionEntry { VersionId = value.VersionId, Ordinal = value.Ordinal, Label = value.Label,
            CreatedUtc = value.CreatedUtc, ContentRevision = value.ContentFingerprint, Removed = value.Removed, CanDelete = !value.Removed,
            DeleteBlockedReason = value.Removed ? BrowserRefusalCode.Removed : BrowserRefusalCode.None }; }
    }
}
