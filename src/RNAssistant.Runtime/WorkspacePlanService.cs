using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;

namespace RNAssistant.Runtime
{
    // Workspace composition for the shared Plan owner. ChatStore retains links
    // and selection; immutable records and heads belong to workspace authority.
    internal sealed class WorkspacePlanService : IToolMutationObserver
    {
        private readonly WorkspaceDescriptor _workspace;
        private readonly ChatSession _session;
        private readonly ChatStore _chats;
        private readonly ChatBlobStore _payloads;
        private readonly ResourceAuthorityStore _authority;
        private readonly ResourceMutationJournal _journal;
        private readonly DocumentArtifactStore _owner;
        private MutationAttempt _attempt;
        private IDisposable _lease;
        private string _operationKey;
        private string _readSelection;
        private bool _selectionCaptured;
        internal ResourceAuthorityScopeId Scope { get { return new ResourceAuthorityScopeId("workspace", _workspace.WorkspaceId); } }

        internal WorkspacePlanService(AppDataPaths paths, ChatStore chats, ChatBlobStore payloads,
            WorkspaceDescriptor workspace, ChatSession session)
        {
            if (session != null && (session.WorkspaceId != workspace.WorkspaceId || !string.IsNullOrWhiteSpace(session.DocumentAuthorityId)))
                throw new InvalidOperationException("Plan access requires this workspace's independent session.");
            _workspace = workspace; _session = session; _chats = chats; _payloads = payloads;
            _authority = new ResourceAuthorityStore(paths); _journal = new ResourceMutationJournal(paths);
            _owner = chats.DocumentArtifacts;
        }

        internal void ReconcileInterrupted()
        { using (_journal.AcquireScope(Scope, true)) ReconcileUnderLease(); }

        private void ReconcileUnderLease()
        {
            foreach (var pending in _journal.Unresolved().Where(item => item.ScopeId.Equals(Scope) && PlanDocumentToolCatalog.Owns(item.Operation)))
            {
                if (pending.State == MutationAttemptState.Prepared) _journal.AbandonBeforeDispatch(pending.AttemptId);
                else PlanDocumentPublication.Publish(_authority, _authority, _journal, pending, null);
            }
        }

        public string Prepare(ToolExecutionContext context, IDictionary<string, object> arguments, string preparedStateJson = null)
        {
            if (!PlanDocumentToolCatalog.Owns(context.Call.Name)) throw new InvalidOperationException("An exact Plan tool is required.");
            if (_attempt != null) throw new InvalidOperationException("A Plan mutation is already active.");
            _lease = _journal.AcquireScope(Scope, true);
            try
            {
                ReconcileUnderLease();
                var snapshot = _authority.Capture(Scope);
                var impacts = PlanDocumentPublication.Prepare(_session, context, arguments, _owner);
                _operationKey = PlanDocumentService.CreationId(_session, context);
                _attempt = _journal.Prepare(Scope, context.Call.Name, impacts[0].Identity,
                    snapshot.GetHead(impacts[0].Identity)?.Revision?.Revision,
                    PayloadRef.FromBlob(_payloads.StoreText(context.Call.ArgumentsJson, "application/json")), intendedImpacts: impacts);
                return _attempt.AttemptId;
            }
            catch { Release(); throw; }
        }

        public void MarkDispatchMayHaveOccurred(string id)
        { RequireAttempt(id); _attempt = _journal.MarkDispatchMayHaveOccurred(id); }

        public ResourceAuthorityCommit Complete(string id, ToolExecutionRecord record, string preparedStateJson = null)
        {
            RequireAttempt(id);
            try
            {
                var changed = record.Evidence?.Effect == ToolEffectEvidence.VerifiedChange;
                var readBack = changed ? PlanDocumentPublication.Retain(_session, _attempt, _operationKey, _owner) : null;
                ResourceAuthorityCommit commit = null;
                for (var retry = 0; retry < 8; retry++)
                {
                    try { commit = PlanDocumentPublication.Publish(_authority, _authority, _journal, _attempt, record, readBack); break; }
                    catch (ResourceAuthorityConflictException) when (retry < 7) { /* Publication only; never repeat mutation. */ }
                }
                if (changed)
                {
                    var exact = _owner.FindPlanOperation(_session, _operationKey);
                    var artifact = _owner.Read(_session, exact, false);
                    ArtifactWorkingSet.Set(_session, artifact, false);
                    _chats.Save(_session);
                }
                return commit;
            }
            finally { Release(); }
        }

        public void AbandonBeforeDispatch(string id)
        { RequireAttempt(id); try { _journal.AbandonBeforeDispatch(id); } finally { Release(); } }
        public void ReleaseUnresolved(string id)
        { if (_attempt != null) { RequireAttempt(id); Release(); } }
        private void RequireAttempt(string id)
        { if (_attempt?.AttemptId != id) throw new InvalidOperationException("Plan mutation attempt is missing."); }
        private void Release()
        { _attempt = null; _operationKey = null; _lease?.Dispose(); _lease = null; }

        internal ResourceFindPage Find(string query)
        {
            using (_journal.AcquireScope(Scope, true))
            {
                ReconcileUnderLease();
                var page = _owner.InspectCurrentMetadata(_session, 0, 100);
                var matches = page.Items.Where(item => !PlanDocumentService.IsTombstone(item) &&
                    (string.IsNullOrWhiteSpace(query) || item.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)).ToArray();
                var result = new ResourceFindPage { Scope = "workspace", Query = query,
                    ScannedEntries = Math.Min(100, page.Total), Total = matches.Length };
                if (page.UnavailableResources > 0) result.UnavailableScopes.Add("plans");
                result.Complete = !result.Partial && !page.NextOffset.HasValue && matches.Length <= 50;
                if (!result.Complete) result.AvailabilityHint = "Plan discovery is incomplete or contains unavailable metadata; a title match is not proof of uniqueness.";
                foreach (var artifact in matches.Take(50))
                {
                    var descriptor = Describe(artifact, false);
                    result.Items.Add(new ResourceFindCandidate { Target = artifact.Title, Title = artifact.Title,
                        Type = "plan", Scope = "workspace", MimeType = artifact.MimeType, Mutable = true,
                        ByteLength = artifact.ContentByteLength, Representations = descriptor.Representations.ToList(),
                        Description = "Version " + artifact.Revision + ", " + PlanDocumentService.Status(artifact),
                        Usage = "Read by exact unique title; omit target to read the selected active plan.",
                        Reference = descriptor.Reference, Descriptor = descriptor });
                    result.ResourceRefs.Add(descriptor.Reference);
                }
                return result;
            }
        }

        internal ResourceReadObservation Read(string target, int? version)
        {
            using (_journal.AcquireScope(Scope, true))
            {
                ReconcileUnderLease();
                ChatArtifact current;
                if (string.IsNullOrWhiteSpace(target))
                {
                    if (!_selectionCaptured) throw new InvalidOperationException("Plan reads require the current model-step selection snapshot.");
                    if (string.IsNullOrWhiteSpace(_readSelection)) throw Error("plan_not_found", "There is no selected Plan. Find a unique title first.");
                    var selected = _owner.ReadPlanSelection(_session, _readSelection, false);
                    var exact = _owner.CurrentPlan(_session, DocumentArtifactStore.PlanIdFromArtifact(selected));
                    current = _owner.Read(_session, exact, false);
                }
                else
                {
                    var page = _owner.InspectCurrentMetadata(_session, 0, 100);
                    if (page.NextOffset.HasValue || page.UnavailableResources > 0)
                        throw Error("plan_discovery_incomplete", "Cannot establish a unique title within complete available Plan metadata; use the selected active plan.");
                    var matches = page.Items.Where(item => !PlanDocumentService.IsTombstone(item) && item.Title == target).ToArray();
                    if (matches.Length != 1) throw Error("plan_target_ambiguous", "Plan title was not found or is ambiguous. Use an exact unique title, or omit target for the selected plan.");
                    current = matches[0];
                }
                if (PlanDocumentService.IsTombstone(current)) throw Error("resource_removed", "This Plan has been removed.");
                var planId = DocumentArtifactStore.PlanIdFromArtifact(current);
                var artifact = version.HasValue ? _owner.PlanHistory(_session, planId, false)
                    .SingleOrDefault(item => item.Revision == version.Value && !PlanDocumentService.IsTombstone(item)) : current;
                if (artifact == null) throw Error("plan_revision_not_found", "That Plan version is unavailable.");
                var reference = ChatResourceUri.CreateArtifactRevision(_session, artifact);
                artifact = _owner.Read(_session, reference);
                if (artifact.InlineText.Length > PlanDocumentService.MaximumMarkdownCharacters)
                    throw Error("read_too_large", "The Plan exceeds the complete read bound.");
                var frozen = _authority.Capture(Scope);
                var logical = frozen.GetHead(DocumentArtifactStore.PlanIdentity(_session, planId));
                var payload = _authority.GetRevision(Scope, reference).Payload;
                var evidence = new ResourceEvidence("ev_" + Guid.NewGuid().ToString("N"), Scope, reference,
                    "text", ResourceCoverage.Whole(), true, frozen.Generation, payload,
                    version.HasValue ? null : new[] { new ResourceDependency(logical.Revision, kind: "current-plan") },
                    immutable: version.HasValue, contentSha256: payload.Sha256);
                var result = new ResourceReadResult { Resource = Describe(artifact, version.HasValue), Representation = "text",
                    Text = artifact.InlineText, Complete = true, Coverage = ResourceCoverage.Whole(),
                    ReturnedCharacters = artifact.InlineText.Length, TotalCharacters = artifact.InlineText.Length,
                    RawContentIncluded = true, CompleteViewPayload = payload, ContentSha256 = payload.Sha256 };
                if (!version.HasValue)
                {
                    // Selection is chat state, not a source mutation or a new authority.
                    _session.Artifacts.RemoveAll(item => item.Id == artifact.Id);
                    _session.Artifacts.Add(artifact);
                    _session.ActivePlanDocumentArtifactId = artifact.Id;
                    ArtifactWorkingSet.Set(_session, artifact, false);
                }
                return new ResourceReadObservation(result, new[] { evidence });
            }
        }

        internal WorkspacePlanState Inspect()
        {
            if (_journal.Unresolved().Any(item => item.ScopeId.Equals(Scope) && PlanDocumentToolCatalog.Owns(item.Operation) &&
                item.State == MutationAttemptState.DispatchMayHaveOccurred))
                return new WorkspacePlanState { Available = false };
            return Project(_authority.CaptureMany(new[] { Scope }));
        }

        internal WorkspacePlanState Project(ResourceAuthoritySnapshotSet frozen)
        {
            // All independent reads in one accepted model step use this same
            // implicit target. A read's new selection applies to the next step.
            _readSelection = _session.ActivePlanDocumentArtifactId;
            _selectionCaptured = true;
            if (string.IsNullOrWhiteSpace(_session.ActivePlanDocumentArtifactId)) return null;
            try
            {
                var artifact = _owner.ReadPlanSelection(_session, _session.ActivePlanDocumentArtifactId, false);
                var head = frozen.Get(Scope).GetHead(DocumentArtifactStore.PlanIdentity(_session, DocumentArtifactStore.PlanIdFromArtifact(artifact)));
                var reference = ChatResourceUri.CreateArtifactRevision(_session, artifact);
                var dependencies = head?.Knowledge == HeadKnowledge.Known ? _authority.GetRevision(Scope, head.Revision)?.Dependencies : null;
                var available = !PlanDocumentService.IsTombstone(artifact) && dependencies?.Any(item =>
                    item.Kind == "immutable-snapshot" && item.Resource.Uri == reference.Uri && item.Resource.Revision == reference.Revision) == true;
                return available ? new WorkspacePlanState { Available = true, Title = artifact.Title,
                    Status = PlanDocumentService.Status(artifact), Version = artifact.Revision } : new WorkspacePlanState { Available = false };
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is JsonException)
            { return new WorkspacePlanState { Available = false }; }
        }

        private ResourceDescriptor Describe(ChatArtifact artifact, bool historical)
        {
            var descriptor = new ResourceDescriptor { Reference = ChatResourceUri.CreateArtifactRevision(_session, artifact),
                Provider = "chat", Kind = "plan", Title = artifact.Title, Mutable = !historical,
                ByteLength = artifact.ContentByteLength, ContentSha256 = artifact.ContentSha256,
                Tracking = historical ? "snapshot" : "current", Coverage = ResourceCoverage.Whole() };
            descriptor.Representations.Add("metadata"); descriptor.Representations.Add("text");
            descriptor.Metadata["version"] = artifact.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
            descriptor.Metadata["historical"] = historical.ToString();
            return descriptor;
        }
        private static ResourceRequestException Error(string code, string message)
        { return new ResourceRequestException(message, code, false); }
    }

    public sealed class WorkspacePlanState
    {
        [JsonProperty("available")] public bool Available { get; set; }
        [JsonProperty("title", NullValueHandling = NullValueHandling.Ignore)] public string Title { get; set; }
        [JsonProperty("status", NullValueHandling = NullValueHandling.Ignore)] public string Status { get; set; }
        [JsonProperty("version", NullValueHandling = NullValueHandling.Ignore)] public int? Version { get; set; }
    }
}
