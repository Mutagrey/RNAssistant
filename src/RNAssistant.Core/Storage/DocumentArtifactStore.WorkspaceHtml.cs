using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Storage
{
    public sealed partial class DocumentArtifactStore
    {
        private const string WorkspaceHtmlRecordView = "artifact-workspace-html-record";
        public static ResourceAuthorityScopeId WorkspaceHtmlScope(string workspaceId)
        { return new ResourceAuthorityScopeId("workspace", workspaceId); }
        private static bool IsWorkspaceHtmlId(string id)
        { return Regex.IsMatch(id ?? "", @"^html_project_[0-9a-f]{32}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); }
        private static bool IsWorkspaceHtml(ConversationMetadata owner, ResourceRef reference)
        {
            string scope, id; int revision;
            return owner != null && string.IsNullOrEmpty(owner.DocumentAuthorityId) && Owns(owner, reference) &&
                ChatResourceUri.TryParseArtifactRevision(reference, out scope, out id, out revision) && revision == 1 && IsWorkspaceHtmlId(id);
        }
        private static bool ValidWorkspaceActor(SavedVersionActor actor)
        { return actor != null && (actor.Kind == SavedVersionActorKind.User && actor.ChatId == null && actor.RunId == null ||
            actor.Kind == SavedVersionActorKind.Run && !string.IsNullOrWhiteSpace(actor.ChatId) && !string.IsNullOrWhiteSpace(actor.RunId)); }
        private SavedVersionCatalog WorkspaceHtmlCatalog(string workspaceId)
        { return new SavedVersionCatalog(_authority, _revisions, _payloads, WorkspaceHtmlScope(workspaceId),
            ResourceUri.Create("state", "workspace", workspaceId, "html-saved-versions")); }

        // Caller holds the existing workspace publication scope. File/definition
        // leases precede it and remain held until this publication is verified.
        public SavedVersionCatalogState WorkspaceHtmlVersionState(string workspaceId)
        { var catalog = WorkspaceHtmlCatalog(workspaceId); return catalog.State() ?? catalog.Initialize(null); }
        public SavedVersionPage WorkspaceHtmlVersions(string workspaceId, string cursor, int limit = 12)
        { WorkspaceHtmlVersionState(workspaceId); return WorkspaceHtmlCatalog(workspaceId).List(cursor, limit); }
        public SavedVersionRecord WorkspaceHtmlVersion(string workspaceId, string versionId, bool allowRemoved = false)
        { WorkspaceHtmlVersionState(workspaceId); return WorkspaceHtmlCatalog(workspaceId).Get(versionId, allowRemoved); }
        public SavedVersionOperation WorkspaceHtmlOperation(string workspaceId, string operationId, string intent)
        { WorkspaceHtmlVersionState(workspaceId); return WorkspaceHtmlCatalog(workspaceId).Operation(operationId, intent); }

        public static string WorkspaceHtmlFingerprint(HtmlProjectSnapshotV1 snapshot)
        {
            ValidateWorkspaceSnapshot(snapshot);
            return TextPatternEngine.Sha256(JsonConvert.SerializeObject(new { snapshot.WorkspaceId, snapshot.EntryPath,
                snapshot.DefinitionFingerprint, Members = snapshot.Members.OrderBy(m => m.Path, StringComparer.Ordinal)
                    .Select(m => new { m.Path, m.Sha256, m.ByteLength, m.Mime }).ToArray() }));
        }

        public SavedVersionOperation SaveWorkspaceHtml(HtmlProjectSnapshotV1 snapshot, SavedVersionActor actor,
            string expectedState, string expectedContent, string operationId, Action beforeDispatch = null)
        {
            if (!ValidWorkspaceActor(actor)) throw new SavedVersionException("version_actor_invalid", "A real User or Run actor is required.");
            ValidateWorkspaceSnapshot(snapshot);
            var catalog = WorkspaceHtmlCatalog(snapshot.WorkspaceId);
            var intent = WorkspaceHtmlSaveIntent(expectedContent, actor);
            var receipt = catalog.Operation(operationId, intent); if (receipt != null) return receipt;
            if (catalog.State()?.StateRevision != expectedState)
                throw new SavedVersionException("version_state_changed", "Saved versions changed; refresh before saving.");
            var fingerprint = WorkspaceHtmlFingerprint(snapshot);
            if (fingerprint != expectedContent) throw new SavedVersionException("version_source_changed", "Project source changed after its check.");
            foreach (var member in snapshot.Members) WorkspaceHtmlPayload(member);
            var state = catalog.State();
            if (state.LastSavedFingerprint == fingerprint && state.LatestAvailableVersionId != null)
            {
                foreach (var member in ReadWorkspaceHtml(snapshot.WorkspaceId, state.LatestAvailableVersionId).Members)
                    if (!_payloads.HasVerifiedReference(WorkspaceHtmlPayload(member).ToBlobReference()))
                        throw new InvalidDataException("The latest saved project's bytes are unavailable.");
                return catalog.Save(new SavedVersionRecord { ContentReference = catalog.Get(state.LatestAvailableVersionId).ContentReference,
                    ContentFingerprint = fingerprint, Actor = actor }, expectedState, operationId, intent, beforeDispatch: beforeDispatch);
            }
            var artifact = new ChatArtifact { Kind = ChatArtifactKinds.HtmlProjectVersion, WorkspaceId = snapshot.WorkspaceId,
                Id = "html_project_" + Guid.NewGuid().ToString("N"), Revision = 1, Title = snapshot.EntryPath,
                CreatedUtc = DateTime.UtcNow, MimeType = "application/vnd.rnassistant.html-project-snapshot+json", RunId = actor.RunId };
            var manifest = JsonConvert.SerializeObject(snapshot);
            if (System.Text.Encoding.UTF8.GetByteCount(manifest) > WorkspaceFileService.MaximumDefinitionBytes)
                throw new InvalidDataException("The saved manifest exceeds one MiB.");
            var body = PayloadRef.FromBlob(_payloads.StoreText(manifest, artifact.MimeType));
            var retained = RetainRecordPayload(null, artifact, body, WorkspaceHtmlRecordView, "application/vnd.rnassistant.artifact-record+json",
                operationId, intent, dependencies: snapshot.Members.Select(m => new ResourceDependency(m.ExactRef, "raw", ResourceCoverage.Whole(), "project-member")),
                ownerScope: WorkspaceHtmlScope(snapshot.WorkspaceId), actor: actor);
            var result = catalog.Save(new SavedVersionRecord { ContentReference = retained.Revision, ContentFingerprint = fingerprint, Actor = actor },
                expectedState, operationId, intent, additional: new[] { new ResourceHeadChange(retained.Identity, null, ResourceHeadState.Known(retained.Revision, 0)) },
                beforeDispatch: beforeDispatch);
            try { ReadWorkspaceHtml(snapshot.WorkspaceId, result.VersionId); }
            catch (Exception error) { throw new SavedVersionException("version_operation_unknown", "Saved project read-back failed: " + error.Message, true); }
            return result;
        }
        public static string WorkspaceHtmlSaveIntent(string content, SavedVersionActor actor)
        { return "save:" + content + ":" + actor?.Kind + ":" + actor?.ChatId + ":" + actor?.RunId; }

        public SavedVersionOperation DeleteWorkspaceHtml(string workspaceId, string versionId, string expectedState,
            string operationId, bool confirmed)
        {
            if (!confirmed) throw new SavedVersionException("version_confirmation_required", "Confirm removal of this saved entry.");
            return WorkspaceHtmlCatalog(workspaceId).Delete(versionId, expectedState, operationId, "delete:" + versionId);
        }

        public HtmlProjectSnapshotV1 ReadWorkspaceHtml(string workspaceId, string versionId)
        {
            var entry = WorkspaceHtmlVersion(workspaceId, versionId);
            var metadata = new ConversationMetadata(null, 0, null, null, workspaceId);
            var artifact = Read(metadata, entry.ContentReference);
            var snapshot = JsonConvert.DeserializeObject<HtmlProjectSnapshotV1>(artifact.InlineText);
            if (snapshot?.WorkspaceId != workspaceId || WorkspaceHtmlFingerprint(snapshot) != entry.ContentFingerprint)
                throw new InvalidDataException("The saved project does not match its retained manifest.");
            foreach (var member in snapshot.Members) WorkspaceHtmlPayload(member);
            return snapshot;
        }
        public byte[] ReadWorkspaceHtmlMember(string workspaceId, string versionId, string path, int maximumBytes = 16 * 1024 * 1024)
        {
            var snapshot = ReadWorkspaceHtml(workspaceId, versionId);
            var member = snapshot.Members.SingleOrDefault(m => m.Path == path) ?? throw new InvalidDataException("The file is not a saved project member.");
            if (member.ByteLength > maximumBytes) throw new InvalidDataException("The saved member exceeds this read's byte limit.");
            return _payloads.ReadBytes(WorkspaceHtmlPayload(member).ToBlobReference()) ?? throw new InvalidDataException("The exact saved member is unavailable.");
        }
        private PayloadRef WorkspaceHtmlPayload(HtmlProjectSnapshotMember member)
        {
            var address = ResourceUri.Parse(member.ExactRef.Uri);
            if (address.Provider != "file" || address.Segments.Count != 1) throw new InvalidDataException("A retained file reference is required.");
            var scope = new ResourceAuthorityScopeId("file", address.Segments[0]);
            var revision = _revisions.GetRevision(scope, member.ExactRef);
            if (revision?.Payload == null || revision.Payload.Sha256 != member.Sha256 || revision.Payload.ByteLength != member.ByteLength)
                throw new InvalidDataException("A saved member's exact payload is unavailable.");
            return revision.Payload;
        }
        private static void ValidateWorkspaceSnapshot(HtmlProjectSnapshotV1 snapshot)
        {
            if (snapshot?.ProtocolVersion != 1 || string.IsNullOrEmpty(snapshot.WorkspaceId) || snapshot.DefinitionFingerprint?.Length != 64 ||
                snapshot.Members == null || snapshot.Members.Count < 1 || snapshot.Members.Count > WorkspaceFileService.MaximumCaptureSetMembers ||
                !snapshot.Members.Any(m => m?.Path == snapshot.EntryPath) || snapshot.Members.Any(m => m == null || m.ExactRef?.IsExact != true ||
                    m.Sha256?.Length != 64 || m.ByteLength < 0 || m.ByteLength > WorkspaceFileService.MaximumCaptureSetBytes) ||
                snapshot.Members.Select(m => m.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != snapshot.Members.Count ||
                snapshot.Members.Sum(m => m.ByteLength) > WorkspaceFileService.MaximumCaptureSetBytes)
                throw new InvalidDataException("A bounded explicit HTML project manifest is required.");
            foreach (var member in snapshot.Members) WorkspaceFileService.ValidateWritePath(member.Path);
        }
    }
}
