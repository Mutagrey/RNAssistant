using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Services
{
    internal sealed class ResourceAuthorityService
    {
        private readonly IResourceAuthorityStore _authority;
        private readonly IResourceRevisionStore _revisions;
        private readonly ResourceMutationJournal _mutations;
        private readonly ChatBlobStore _payloads;

        internal ResourceAuthorityService(IResourceAuthorityStore authority, IResourceRevisionStore revisions,
            ResourceMutationJournal mutations = null, ChatBlobStore payloads = null)
        {
            _authority = authority ?? throw new ArgumentNullException(nameof(authority));
            _revisions = revisions ?? throw new ArgumentNullException(nameof(revisions));
            _mutations = mutations;
            _payloads = payloads;
        }

        internal IResourceAuthorityStore Store { get { return _authority; } }
        internal ChatBlobStore Payloads { get { return _payloads; } }

        internal void ReportExternalDrift(ResourceAuthorityScopeId scope, ResourceIdentity identity)
        {
            var snapshot = _authority.Capture(scope);
            var before = snapshot.GetHead(identity);
            if (before != null && before.Knowledge == HeadKnowledge.Unknown) return;
            var effect = new ResourceEffect("re_" + Guid.NewGuid().ToString("N"), "guard", ResourceEffectOutcome.ExternalDriftObserved,
                new[] { new ResourceImpact(identity, ResourceImpactRelation.Exact, before: before?.Revision,
                    changeKind: "external-drift") }, "expected-state guard mismatch; replacement not captured");
            _authority.Publish(ResourceAuthorityCommit.Create(scope, snapshot.Generation, effect,
                new[] { new ResourceHeadChange(identity, before,
                    ResourceHeadState.Unknown(identity, snapshot.Generation + 1, "external-drift")) }, AuthorityCommitReason.ExternalDrift));
        }

        internal ResourceAuthoritySnapshotSet CaptureMany(IReadOnlyList<ResourceAuthorityScopeId> scopes)
        {
            EnsureReady(scopes);
            var snapshot = _authority.CaptureMany(scopes);
            EnsureReady(scopes);
            return snapshot;
        }

        private void EnsureReady(IEnumerable<ResourceAuthorityScopeId> scopes)
        {
            if (_mutations != null && _mutations.Unresolved().Any(attempt =>
                attempt.State == MutationAttemptState.DispatchMayHaveOccurred && scopes.Contains(attempt.ScopeId)))
                throw new ResourceRequestException("A resource mutation has not reached its authority publication barrier.",
                    "RESOURCE_AUTHORITY_NOT_READY", true);
        }

        internal void ObserveNote(ChatSession session, ContextNote note, ChatBlobStore payloads)
        {
            if (note == null || payloads == null) throw new ArgumentNullException();
            if (note.Role != ContextNoteRole.OfficeObservation && note.Role != ContextNoteRole.SuppliedData)
                throw new ArgumentException("Only typed observations or supplied data can become resource evidence.", nameof(note));
            var mutable = note.Role == ContextNoteRole.OfficeObservation;
            var scope = Scope(session, mutable);
            var identity = new ResourceIdentity(ResourceUri.Create("context", scope.Kind, scope.Id, note.Id));
            var snapshot = _authority.Capture(scope);
            var before = snapshot.GetHead(identity);
            var reference = new ResourceRef(identity.Uri, "r_" + Guid.NewGuid().ToString("N"));
            var payload = PayloadRef.FromBlob(payloads.StoreText(note.Text ?? note.Preview ?? string.Empty, "text/plain; charset=utf-8"));
            _revisions.RegisterRevision(scope, new ResourceRevisionMetadata(reference, payload.Sha256, payload, before?.Revision));
            _authority.Publish(ResourceAuthorityCommit.Create(scope, snapshot.Generation, null,
                new[] { new ResourceHeadChange(identity, before, ResourceHeadState.Known(reference, snapshot.Generation + 1)) },
                AuthorityCommitReason.InitialObservation));
            note.Evidence = new ResourceEvidence("ev_" + Guid.NewGuid().ToString("N"), scope, reference,
                "text", ResourceCoverage.Whole(), true, snapshot.Generation + 1, payload, immutable: !mutable);
            note.InstructionPayload = null;
            note.Preview = ContextNormalizer.TrimForContext(note.Text ?? note.Preview, 360);
            note.Text = note.Preview; // UI preview only; the compiler reads the exact evidence payload.
        }

        internal IReadOnlyList<ResourceEvidence> Observe(ChatSession session, ResourceReadResult result, bool live)
        {
            var scope = ScopeFor(session, result.Resource.Reference, live);
            var snapshot = _authority.Capture(scope);
            return new[] { new ResourceEvidence("ev_" + Guid.NewGuid().ToString("N"), scope,
                result.Resource.Reference, result.Representation ?? "content",
                result.Coverage ?? CoverageFor(result), result.Complete, result.AuthorityGeneration ?? snapshot.Generation,
                result.Payload, result.Resource.Dependencies, immutable: !live,
                contentSha256: result.ContentSha256 ?? result.Resource.ContentSha256) };
        }

        internal ResourceAuthorityScopeId Scope(ChatSession session, bool documentScoped)
        {
            if (documentScoped)
            {
                if (session == null || string.IsNullOrWhiteSpace(session.DocumentAuthorityId))
                    throw new ResourceRequestException("Document resource authority is not ready.",
                        "RESOURCE_AUTHORITY_NOT_READY", true);
                return ResourceAuthorityScopeId.Document(new DocumentAuthorityId(session.DocumentAuthorityId));
            }
            if (session == null || string.IsNullOrWhiteSpace(session.Id))
                throw new ResourceRequestException("Conversation resource authority is not ready.",
                    "RESOURCE_AUTHORITY_NOT_READY", true);
            return new ResourceAuthorityScopeId("conversation", session.Id);
        }

        internal ResourceAuthorityScopeId ScopeFor(ChatSession session, ResourceRef reference, bool live)
        {
            return reference != null && ResourceUri.Parse(reference.Uri).Provider == "catalog"
                ? CatalogPublicationService.ScopeId : Scope(session, live);
        }

        internal void RetainView(ChatSession session, ResourceReadResult result, bool live)
        {
            if (result == null || result.Resource == null || !result.Resource.Reference.IsExact) return;
            var scope = ScopeFor(session, result.Resource.Reference, live);
            result.Coverage = result.Coverage ?? CoverageFor(result);
            if (result.Payload == null && result.Text != null && _payloads != null)
                result.Payload = PayloadRef.FromBlob(_payloads.StoreText(result.Text, result.Resource.MimeType ?? "text/plain; charset=utf-8"));
            _revisions.RegisterView(scope, new ResourceRevisionView(result.Resource.Reference, result.Representation,
                result.ContentSha256, result.Payload, result.Coverage));
        }

        internal ResourceReadSelection ReadRetained(ChatSession session, ResourceReadRequest request, bool live)
        {
            if (!live || request.Reference == null || !request.Reference.IsExact || _payloads == null) return null;
            var scope = Scope(session, true);
            var head = _authority.GetHead(scope, request.Reference.Identity);
            if (head != null && head.Knowledge == HeadKnowledge.Known && head.Revision.Revision == request.Reference.Revision &&
                string.IsNullOrEmpty(request.Cursor)) return null;
            var view = _revisions.GetView(scope, request.Reference, request.Representation);
            if (view?.Payload == null || view.Coverage.Kind != ResourceCoverageKinds.Whole) return null;
            if (view.Payload.ByteLength > 8L * 1024 * 1024)
                throw new ResourceRequestException("This retained view requires the bounded data plane.", "RESOURCE_BATCH_TOO_LARGE", false);
            var content = _payloads.ReadText(view.Payload.ToBlobReference());
            if (content == null) throw new ResourceRequestException("The retained view payload is unavailable.", "RESOURCE_SNAPSHOT_UNAVAILABLE", false);
            var binding = ResourceReadCursor.ReadBinding(request.Reference.Uri, view.View);
            var position = ResourceReadCursor.ParseRevisionBound(request, binding);
            ResourceReadCursor.ValidateContinuation(position, view.ContentSha256);
            var offset = position.Offset;
            if (offset > content.Length) throw new ResourceRequestException("Cursor exceeds the retained view.", "resource_cursor_invalid", false);
            var count = Math.Min(content.Length - offset, Math.Max(1, Math.Min(32000, request.MaxChars <= 0 ? 32000 : request.MaxChars)));
            var next = offset + count;
            var revision = _revisions.GetRevision(scope, request.Reference);
            return new ResourceReadSelection { Result = new ResourceReadResult {
                Resource = new ResourceDescriptor { Reference = request.Reference.Copy(), MimeType = view.Payload.ContentType,
                    ContentSha256 = view.ContentSha256, Dependencies = revision?.Dependencies.ToList() ?? new List<ResourceDependency>() }, Representation = view.View,
                Text = content.Substring(offset, count), ContentSha256 = view.ContentSha256,
                Offset = offset, ReturnedCharacters = count, TotalCharacters = content.Length,
                Complete = next == content.Length, Truncated = next < content.Length,
                NextCursor = next < content.Length ? ResourceReadCursor.CreateRevisionBound(next, view.ContentSha256, binding) : null,
                AuthorityGeneration = _authority.Capture(scope).Generation,
                Coverage = offset == 0 && next == content.Length ? ResourceCoverage.Whole() :
                    new ResourceCoverage(ResourceCoverageKinds.CharacterRange, start: offset, end: next)
            }, ResourceRefs = new[] { request.Reference.Copy() } };
        }

        internal ResourceReadRequest PrepareRead(ChatSession session, ResourceReadRequest request, bool live)
        {
            EnsureReady(new[] { ScopeFor(session, request.Reference, live) });
            if (!live || request == null || request.Reference == null || !request.Reference.IsExact) return request;
            var scope = Scope(session, true);
            var head = _authority.GetHead(scope, request.Reference.Identity);
            if (head == null || head.Knowledge == HeadKnowledge.Unknown)
                throw new ResourceRequestException("The current resource head is unknown; reconcile it before an exact read.",
                    "RESOURCE_HEAD_UNKNOWN", true);
            if (head.Knowledge != HeadKnowledge.Known || !string.Equals(
                head.Revision.Revision, request.Reference.Revision, StringComparison.Ordinal))
            {
                var historical = _revisions.GetRevision(scope, request.Reference);
                throw new ResourceRequestException(historical == null
                    ? "The requested exact resource revision is unavailable."
                    : "The historical resource revision is retained but has no provider snapshot for this view.",
                    "RESOURCE_SNAPSHOT_UNAVAILABLE", false);
            }
            var metadata = _revisions.GetView(scope, head.Revision, request.Representation);
            return new ResourceReadRequest
            {
                Reference = new ResourceRef(request.Reference.Uri,
                    metadata == null ? null : metadata.ContentSha256),
                Representation = request.Representation,
                Cursor = request.Cursor,
                MaxChars = request.MaxChars
            };
        }

        internal ResourceReadSelection PublishRead(ChatSession session, ResourceReadSelection selection,
            ResourceReadRequest originalRequest, bool live)
        {
            if (selection == null || selection.Result == null ||
                selection.Result.Resource == null || selection.Result.Resource.Reference == null)
                return selection;
            var result = selection.Result;
            var identity = new ResourceIdentity(result.Resource.Reference.Uri);
            var scope = ScopeFor(session, result.Resource.Reference, live);
            var snapshot = _authority.Capture(scope);
            var head = snapshot.GetHead(identity);
            ResourceAuthorityCommit publication = null;
            var contentSha256 = string.IsNullOrWhiteSpace(result.ContentSha256)
                ? result.Resource.ContentSha256
                : result.ContentSha256;
            ResourceRef exact = live ? null : result.Resource.Reference.Copy();
            if (!live)
            {
                if (_revisions.GetRevision(scope, exact) == null)
                    _revisions.RegisterRevision(scope, new ResourceRevisionMetadata(exact, result.Resource.ContentSha256,
                        result.Resource.Payload, dependencies: result.Resource.Dependencies));
                string artifactId;
                if (scope.Kind != "catalog" && (head == null || RNAssistant.Core.Services.ChatResourceUri.TryGetCurrentArtifactId(session, exact, out artifactId) &&
                    (head.Revision == null || head.Revision.Revision != exact.Revision)))
                    publication = ResourceAuthorityCommit.Create(scope, snapshot.Generation, null,
                    new[] { new ResourceHeadChange(identity, head, ResourceHeadState.Known(exact, snapshot.Generation + 1)) },
                    AuthorityCommitReason.InitialObservation);
            }
            if (live && head != null && head.Knowledge == HeadKnowledge.Known)
            {
                var currentMetadata = _revisions.GetView(scope, head.Revision, result.Representation);
                if (currentMetadata == null || string.Equals(currentMetadata.ContentSha256,
                    contentSha256, StringComparison.OrdinalIgnoreCase))
                    exact = head.Revision.Copy();
            }
            if (exact == null)
            {
                exact = new ResourceRef(identity.Uri, "r_" + Guid.NewGuid().ToString("N"));
                var metadata = new ResourceRevisionMetadata(exact, null,
                    null, head != null && head.Knowledge == HeadKnowledge.Known
                        ? head.Revision : null);
                _revisions.RegisterRevision(scope, metadata);
                var newGeneration = snapshot.Generation + 1;
                var after = ResourceHeadState.Known(exact, newGeneration);
                ResourceEffect effect = null;
                var reason = AuthorityCommitReason.InitialObservation;
                if (head != null)
                {
                    reason = head.Knowledge == HeadKnowledge.Unknown
                        ? AuthorityCommitReason.Reconciliation
                        : AuthorityCommitReason.ExternalDrift;
                    effect = new ResourceEffect("re_" + Guid.NewGuid().ToString("N"),
                        "resource.read", ResourceEffectOutcome.ExternalDriftObserved,
                        new[] { new ResourceImpact(identity, ResourceImpactRelation.Intersects,
                            result.Coverage ?? ResourceCoverage.Whole(),
                            head.Knowledge == HeadKnowledge.Known ? head.Revision : null, exact,
                            head.Knowledge == HeadKnowledge.Unknown ? "reconciled" : "external-drift") },
                        "provider observation");
                }
                var change = new ResourceHeadChange(identity, head, after);
                var commit = ResourceAuthorityCommit.Create(scope, snapshot.Generation,
                    effect, new[] { change }, reason);
                publication = commit;
            }

            result.Resource.Reference = exact.Copy();
            if (!live)
            {
                string artifactId;
                if (ChatResourceUri.TryGetCurrentArtifactId(session, exact, out artifactId))
                {
                    var name = artifactId == session.ActiveHtmlArtifactId ? "html-workspace" :
                        artifactId == session.ActivePlanDocumentArtifactId ? "plan-document" :
                        artifactId == session.ActiveTaskListArtifactId ? "task-list" : null;
                    if (name != null)
                    {
                        var state = _authority.GetHead(scope, ResourceStateProvider.Identity(scope, name));
                        if (state?.Knowledge == HeadKnowledge.Known)
                            result.Resource.Dependencies.Add(new ResourceDependency(state.Revision, "text", ResourceCoverage.Whole(), "current-state"));
                    }
                }
            }
            result.Resource.ContentSha256 = contentSha256;
            result.Resource.Coverage = result.Coverage ?? CoverageFor(result);
            result.Coverage = result.Resource.Coverage;
            if (result.Text != null && _payloads != null)
                result.Payload = PayloadRef.FromBlob(_payloads.StoreText(result.Text, result.Resource.MimeType ?? "text/plain; charset=utf-8"));
            _revisions.RegisterView(scope, new ResourceRevisionView(exact, result.Representation,
                contentSha256, result.Payload, result.Coverage));
            if (result.CompleteViewPayload != null)
                _revisions.RegisterView(scope, new ResourceRevisionView(exact, result.Representation,
                    contentSha256, result.CompleteViewPayload, ResourceCoverage.Whole()));
            // Every referenced view is durable before the head becomes visible to another reader.
            if (publication != null) _authority.Publish(publication);
            result.AuthorityGeneration = _authority.Capture(scope).Generation;
            if (originalRequest?.Reference?.IsExact == true && originalRequest.Reference.Revision != exact.Revision)
                throw new ResourceRequestException("The resource changed while resolving the requested revision.",
                    "RESOURCE_REVISION_CHANGED", true);
            selection.ResourceRefs = (selection.ResourceRefs ?? new ResourceRef[0])
                .Select(reference => reference != null && reference.Identity.Equals(identity)
                    ? exact.Copy() : reference == null ? null : reference.Copy())
                .Where(reference => reference != null)
                .ToArray();
            if (selection.ResourceRefs.Count == 0) selection.ResourceRefs = new[] { exact.Copy() };
            return selection;
        }

        internal void ApplyHead(ResourceDescriptor descriptor, ChatSession session, bool live)
        {
            if (!live || descriptor == null || descriptor.Reference == null) return;
            var head = _authority.GetHead(Scope(session, true), descriptor.Reference.Identity);
            if (head != null && head.Knowledge == HeadKnowledge.Known)
                descriptor.Reference = head.Revision.Copy();
            else descriptor.Reference = new ResourceRef(descriptor.Reference.Uri);
        }

        private static ResourceCoverage CoverageFor(ResourceReadResult result)
        {
            if (result == null) return ResourceCoverage.Whole();
            if (result.Offset == 0 && result.Complete) return ResourceCoverage.Whole();
            return new ResourceCoverage(ResourceCoverageKinds.CharacterRange, start: result.Offset,
                end: result.Offset + Math.Max(0, result.ReturnedCharacters));
        }
    }

    internal sealed class ResourceMutationAuthorityObserver : IToolMutationObserver
    {
        private readonly ResourceAuthorityService _authority;
        private readonly ResourceMutationJournal _journal;
        private readonly ChatSession _session;
        private readonly ChatBlobStore _payloads;
        private readonly Action<ChatSession> _persistResources;
        private readonly Func<ResourceIdentity, ResourceMutationReadBack> _captureCatalog;
        private readonly Dictionary<string, MutationAttempt> _attempts =
            new Dictionary<string, MutationAttempt>(StringComparer.Ordinal);
        private readonly object _sync = new object();
        private readonly Dictionary<string, IDisposable> _leases = new Dictionary<string, IDisposable>(StringComparer.Ordinal);

        internal ResourceMutationAuthorityObserver(ResourceAuthorityService authority,
            ResourceMutationJournal journal, ChatSession session, ChatBlobStore payloads,
            Action<ChatSession> persistResources = null,
            Func<ResourceIdentity, ResourceMutationReadBack> captureCatalog = null)
        {
            _authority = authority ?? throw new ArgumentNullException(nameof(authority));
            _journal = journal ?? throw new ArgumentNullException(nameof(journal));
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _payloads = payloads ?? throw new ArgumentNullException(nameof(payloads));
            _persistResources = persistResources;
            _captureCatalog = captureCatalog;
        }

        public string Prepare(ToolExecutionContext context, IDictionary<string, object> arguments)
        {
            var scope = ResourceMutationDomains.Scope(_authority, _session, context.Call.Name);
            var expected = StringArgument(arguments, "expectedRevision");
            var lease = _journal.AcquireScope(scope);
            try
            {
                var snapshot = _authority.CaptureMany(new[] { scope }).Get(scope);
                var impacts = ResourceMutationDomains.Impacts(scope, context.Call.Name, arguments, snapshot);
                var target = impacts[0].Identity;
                var payload = PayloadRef.FromBlob(_payloads.StoreText(context.Call.ArgumentsJson, "application/json"));
                var attempt = _journal.Prepare(scope, context.Call.Name, target, expected, payload, intendedImpacts: impacts);
                lock (_sync) { _attempts[attempt.AttemptId] = attempt; _leases[attempt.AttemptId] = lease; }
                return attempt.AttemptId;
            }
            catch { lease.Dispose(); throw; }
        }

        public void MarkDispatchMayHaveOccurred(string attemptId)
        {
            var next = _journal.MarkDispatchMayHaveOccurred(attemptId);
            lock (_sync) _attempts[attemptId] = next;
        }

        public ResourceAuthorityCommit Complete(string attemptId, ToolExecutionRecord record)
        {
            MutationAttempt attempt;
            lock (_sync)
            {
                if (!_attempts.TryGetValue(attemptId, out attempt))
                    throw new InvalidOperationException("Mutation authority attempt is missing.");
            }
            if (attempt.State != MutationAttemptState.DispatchMayHaveOccurred)
                throw new InvalidOperationException("A dispatched mutation lacks its durable dispatch marker.");
            try
            {
                var readBack = record.ResourceReadBack.ToList();
                if (attempt.ScopeId.Kind == "catalog" && record.Evidence?.Effect == ToolEffectEvidence.VerifiedChange)
                {
                    if (_captureCatalog == null) throw new InvalidOperationException("Catalog publication requires its typed read-back owner.");
                    readBack.AddRange(attempt.IntendedImpacts.Select(impact => _captureCatalog(impact.Identity)));
                }
                if (attempt.ScopeId.Kind == "conversation" && record.Evidence?.Effect == ToolEffectEvidence.VerifiedChange)
                {
                    readBack.AddRange(CaptureConversationReadBack(attempt));
                    if (_persistResources != null) _persistResources(_session);
                }
                return PublishAttempt(_authority, _journal, attempt, record, readBack);
            }
            finally { Release(attemptId); }
        }

        private IEnumerable<ResourceMutationReadBack> CaptureConversationReadBack(MutationAttempt attempt)
        {
            foreach (var impact in attempt.IntendedImpacts.Where(item => ResourceMutationDomains.Provider(item.Identity) == "state"))
            {
                var name = ResourceUri.Parse(impact.Identity.Uri).Segments.Last();
                if (attempt.Operation == "common.chat_clear")
                {
                    if (!string.IsNullOrEmpty(_session.ActiveHtmlArtifactId) || !string.IsNullOrEmpty(_session.ActivePlanDocumentArtifactId) ||
                        !string.IsNullOrEmpty(_session.ActiveTaskListArtifactId) || (_session.Artifacts?.Count ?? 0) != 0)
                        throw new InvalidOperationException("Chat clear did not remove its active resource membership.");
                    yield return new ResourceMutationReadBack(impact.Identity, false);
                    continue;
                }
                if (name == "artifacts")
                {
                    var references = (_session.Artifacts ?? new List<ChatArtifact>()).Where(item => item != null)
                        .Select(item => ChatResourceUri.CreateArtifactRevision(_session, item))
                        .OrderBy(item => item.Uri, StringComparer.Ordinal).ToArray();
                    var manifest = PayloadRef.FromBlob(_payloads.StoreText(JsonConvert.SerializeObject(references), "application/json"));
                    yield return new ResourceMutationReadBack(impact.Identity, true, "text", manifest.Sha256, manifest,
                        dependencies: references.Select(reference => new ResourceDependency(reference, kind: "member")));
                    continue;
                }
                if (name != "html-workspace" && name != "plan-document" && name != "task-list") continue;
                var artifactId = name == "html-workspace" ? _session.ActiveHtmlArtifactId :
                    name == "plan-document" ? _session.ActivePlanDocumentArtifactId : _session.ActiveTaskListArtifactId;
                if (string.IsNullOrWhiteSpace(artifactId))
                {
                    yield return new ResourceMutationReadBack(impact.Identity, false);
                    continue;
                }
                var artifact = (_session.Artifacts ?? new List<ChatArtifact>()).SingleOrDefault(item => item != null && item.Id == artifactId);
                // A dangling pointer or missing CAS body is unknown, not proven removal.
                if (artifact == null) continue;
                var body = artifact.InlineText;
                if (body == null && artifact.ContentByteLength.HasValue && artifact.ContentByteLength <= 8L * 1024 * 1024)
                    body = _payloads.ReadText(new ChatBlobReference { Sha256 = artifact.ContentSha256,
                        ByteLength = artifact.ContentByteLength.Value, ContentType = artifact.MimeType });
                if (body == null) continue;
                var payload = PayloadRef.FromBlob(_payloads.StoreText(body, artifact.MimeType ?? "text/plain"));
                yield return new ResourceMutationReadBack(impact.Identity, true, "text", payload.Sha256, payload,
                    dependencies: new[] { new ResourceDependency(ChatResourceUri.CreateArtifactRevision(_session, artifact), kind: "immutable-snapshot") });
            }
        }

        private static bool SameCapturedState(IResourceRevisionStore revisions, ResourceAuthorityScopeId scope,
            ResourceHeadState before, ResourceMutationReadBack captured)
        {
            if (captured == null) return false;
            if (!captured.Exists) return before == null || before.Knowledge == HeadKnowledge.Unavailable;
            if (before?.Knowledge != HeadKnowledge.Known) return false;
            var prior = revisions.GetRevision(scope, before.Revision);
            // A no-op requires the same immutable source(s), not merely equal bytes.
            return prior != null && prior.Payload != null && captured.Payload != null &&
                prior.ContentSha256 == captured.ContentSha256 &&
                JsonConvert.SerializeObject(prior.Dependencies) == JsonConvert.SerializeObject(captured.Dependencies);
        }

        private static ResourceAuthorityCommit PublishAttempt(ResourceAuthorityService authority,
            ResourceMutationJournal journal, MutationAttempt attempt, ToolExecutionRecord record,
            IReadOnlyList<ResourceMutationReadBack> capturedReadBack = null)
        {
            var prior = authority.Store.Capture(attempt.ScopeId).Commits.FirstOrDefault(item => item.MutationAttemptId == attempt.AttemptId);
            if (prior != null) { journal.Resolve(attempt.AttemptId, prior.CommitId); return prior; }
            var snapshot = authority.Store.Capture(attempt.ScopeId);
            var outcome = Outcome(record);
            if (outcome == ResourceEffectOutcome.VerifiedChanged && (attempt.Operation == "common.vba_restore_backup" ||
                attempt.Operation == "common.plan_doc_restore" || attempt.Operation == "common.html_workspace_restore" ||
                attempt.Operation == "common.html_workspace_redo" || attempt.Operation == "common.chat_edit" || attempt.Operation == "resource.restore"))
                outcome = ResourceEffectOutcome.Restored;
            var readBack = capturedReadBack ?? (record == null ? null : record.ResourceReadBack) ?? new ResourceMutationReadBack[0];
            var returned = record?.Result?.Resources.Where(reference => reference.IsExact &&
                ResourceMutationDomains.Provider(reference.Identity) == "chat" &&
                snapshot.GetHead(reference.Identity) == null).ToArray() ?? new ResourceRef[0];
            var candidates = attempt.IntendedImpacts.Concat(readBack.Select(item =>
                new ResourceImpact(item.Identity, ResourceImpactRelation.Exact, item.Coverage)))
                .Concat(returned.Select(item => new ResourceImpact(item.Identity, ResourceImpactRelation.Exact)))
                .GroupBy(item => item.Identity.Uri, StringComparer.Ordinal).Select(group => group.First()).ToArray();
            var changes = new List<ResourceHeadChange>();
            var impacts = new List<ResourceImpact>();
            var changed = outcome == ResourceEffectOutcome.VerifiedChanged || outcome == ResourceEffectOutcome.Restored;
            foreach (var impact in candidates)
            {
                var before = snapshot.GetHead(impact.Identity);
                var captured = readBack.FirstOrDefault(item => item.Identity.Equals(impact.Identity));
                if (changed && ConversationResourceMutationDomain.IsHistoryMutation(attempt.Operation) &&
                    SameCapturedState((IResourceRevisionStore)authority.Store, attempt.ScopeId, before, captured)) continue;
                var exact = changed ? captured?.Revision ?? returned.FirstOrDefault(item => item.Identity.Equals(impact.Identity)) : null;
                ResourceHeadState after = before;
                if (changed && (exact != null || captured != null && captured.Exists))
                {
                    exact = exact ?? new ResourceRef(impact.Identity.Uri, "r_" + Guid.NewGuid().ToString("N"));
                    var revisions = (IResourceRevisionStore)authority.Store;
                    ResourceRef restoredFrom = outcome == ResourceEffectOutcome.Restored ? captured?.RestoredFrom : null;
                    if (outcome == ResourceEffectOutcome.Restored && captured != null && restoredFrom == null)
                    {
                        var source = captured.Dependencies.FirstOrDefault(item => item.Kind == "immutable-snapshot")?.Resource;
                        restoredFrom = snapshot.Commits.SelectMany(item => item.HeadChanges)
                            .Where(item => item.Identity.Equals(impact.Identity) && item.After.Knowledge == HeadKnowledge.Known)
                            .Select(item => item.After.Revision)
                            .FirstOrDefault(reference => revisions.GetRevision(attempt.ScopeId, reference)?.Dependencies
                                .Any(dependency => source != null && dependency.Resource.Uri == source.Uri && dependency.Resource.Revision == source.Revision) == true);
                        restoredFrom = restoredFrom ?? source;
                    }
                    if (revisions.GetRevision(attempt.ScopeId, exact) == null)
                        revisions.RegisterRevision(attempt.ScopeId,
                            new ResourceRevisionMetadata(exact, captured?.ContentSha256, captured?.Payload, before?.Revision,
                                restoredFrom: restoredFrom, dependencies: captured?.Dependencies));
                    if (captured != null && !string.IsNullOrWhiteSpace(captured.View))
                        ((IResourceRevisionStore)authority.Store).RegisterView(attempt.ScopeId,
                            new ResourceRevisionView(exact, captured.View, captured.ContentSha256, captured.Payload, captured.Coverage, captured.Parts));
                    after = ResourceHeadState.Known(exact, snapshot.Generation + 1);
                }
                else if (changed && captured != null && !captured.Exists)
                    after = ResourceHeadState.Unavailable(impact.Identity, snapshot.Generation + 1, "verified removal");
                else if (changed || outcome == ResourceEffectOutcome.UnknownAfterDispatch)
                    after = ResourceHeadState.Unknown(impact.Identity, snapshot.Generation + 1,
                        changed ? "effect verified; this resource has no captured after-state" : "mutation:" + attempt.AttemptId);
                if (after != before) changes.Add(new ResourceHeadChange(impact.Identity, before, after));
                impacts.Add(new ResourceImpact(impact.Identity, impact.Relation, impact.Coverage,
                    before?.Revision, after?.Revision, impact.ChangeKind ?? "tool-mutation"));
            }
            if (changed && changes.Count == 0 && ConversationResourceMutationDomain.IsHistoryMutation(attempt.Operation))
                outcome = ResourceEffectOutcome.VerifiedNoChange;
            var effect = new ResourceEffect("re_" + Guid.NewGuid().ToString("N"),
                attempt.Operation, outcome, impacts, Verification(record));
            var commit = ResourceAuthorityCommit.Create(attempt.ScopeId, snapshot.Generation,
                effect, changes, outcome == ResourceEffectOutcome.Restored
                    ? AuthorityCommitReason.Restore : AuthorityCommitReason.MutationEffect,
                attempt.AttemptId);
            authority.Store.Publish(commit);
            journal.Resolve(attempt.AttemptId, commit.CommitId);
            return commit;
        }

        public void AbandonBeforeDispatch(string attemptId)
        {
            try { _journal.AbandonBeforeDispatch(attemptId); }
            finally { Release(attemptId); }
        }

        private void Release(string attemptId)
        {
            lock (_sync)
            {
                IDisposable lease;
                if (_leases.TryGetValue(attemptId, out lease)) { _leases.Remove(attemptId); lease.Dispose(); }
                _attempts.Remove(attemptId);
            }
        }

        public void ReleaseUnresolved(string attemptId) { Release(attemptId); }

        internal static void ReconcileInterrupted(ResourceAuthorityService authority, ResourceMutationJournal journal)
        {
            foreach (var pending in journal.Unresolved())
            {
                IDisposable lease;
                try { lease = journal.AcquireScope(pending.ScopeId); }
                catch (System.IO.IOException) { continue; }
                using (lease)
                {
                    var current = journal.Unresolved().FirstOrDefault(item => item.AttemptId == pending.AttemptId);
                    if (current == null) continue;
                    if (current.State == MutationAttemptState.Prepared) journal.AbandonBeforeDispatch(current.AttemptId);
                    else PublishAttempt(authority, journal, current, null);
                }
            }
        }

        private static ResourceEffectOutcome Outcome(ToolExecutionRecord record)
        {
            if (record == null || record.Evidence == null) return ResourceEffectOutcome.UnknownAfterDispatch;
            if (record.Evidence.Effect == ToolEffectEvidence.VerifiedChange) return ResourceEffectOutcome.VerifiedChanged;
            if (record.Evidence.Effect == ToolEffectEvidence.VerifiedNoChange) return ResourceEffectOutcome.VerifiedNoChange;
            if (record.Evidence.Effect == ToolEffectEvidence.Unknown || record.Outcome == ToolExecutionOutcome.Unknown)
                return ResourceEffectOutcome.UnknownAfterDispatch;
            return record.MayHaveDispatched ? ResourceEffectOutcome.UnknownAfterDispatch : ResourceEffectOutcome.FailedNoEffect;
        }

        private static string Verification(ToolExecutionRecord record)
        {
            return record == null || record.Evidence == null ? "unknown" : record.Evidence.Effect.ToString();
        }


        private static string StringArgument(IDictionary<string, object> arguments, string key)
        {
            object value;
            return arguments != null && arguments.TryGetValue(key, out value) && value != null
                ? Convert.ToString(value) : null;
        }
    }
}
