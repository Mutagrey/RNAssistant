using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.Services
{
    internal sealed class ModelContextInspectorService
    {
        internal const string Owner = "model-context";
        private readonly IEventStore _events;
        private readonly ChatBlobStore _payloads;
        private readonly ResourceDataPlaneService _data;
        private readonly ResourceAuthorityService _authority;
        internal ModelContextInspectorService(IEventStore events, ChatBlobStore payloads, ResourceDataPlaneService data,
            ResourceAuthorityService authority = null)
        { _events = events; _payloads = payloads; _data = data; _authority = authority; }

        internal ModelContextResponse Query(ChatSession session, ModelContextQuery query, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!string.IsNullOrEmpty(query.VerifyResourceAlias) &&
                (string.IsNullOrEmpty(query.RequestEventId) || !string.IsNullOrEmpty(query.Alias) || query.BeforeSequence.HasValue))
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: payload verification requires an exact saved request and its resource alias.");
            if (!string.IsNullOrEmpty(query.VerifyRetainedView) && string.IsNullOrEmpty(query.VerifyResourceAlias))
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: retained view verification requires a resource alias.");
            var inventoryRequested = !string.IsNullOrEmpty(query.ViewInventoryAlias);
            var inventoryOffset = query.ViewInventoryOffset ?? 0;
            if ((!inventoryRequested && (query.ViewInventoryOffset.HasValue || query.ViewInventorySnapshotCount.HasValue)) ||
                (inventoryRequested &&
                 (string.IsNullOrEmpty(query.RequestEventId) || !string.IsNullOrEmpty(query.Alias) ||
                  query.BeforeSequence.HasValue || !string.IsNullOrEmpty(query.VerifyResourceAlias) ||
                  inventoryOffset < 0 || query.ViewInventorySnapshotCount < 0 ||
                  (inventoryOffset > 0 && !query.ViewInventorySnapshotCount.HasValue))))
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: view inventory requires an exact saved request and a valid page.");
            var variantRequested = query.VerifyViewVariantOrdinal.HasValue;
            if (variantRequested != query.VerifyViewVariantSnapshotCount.HasValue ||
                variantRequested && (string.IsNullOrEmpty(query.VerifyResourceAlias) ||
                    !string.IsNullOrEmpty(query.VerifyRetainedView) || inventoryRequested ||
                    query.VerifyViewVariantOrdinal < 0 || query.VerifyViewVariantSnapshotCount <= 0 ||
                    query.VerifyViewVariantOrdinal >= query.VerifyViewVariantSnapshotCount))
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: exact retained view variant and inventory snapshot are required.");
            var response = new ModelContextResponse { ChatId = session.Id, Revision = session.Revision,
                Events = new ModelContextEventDto[0] };
            if (!query.BeforeSequence.HasValue && string.IsNullOrEmpty(query.RequestEventId) &&
                string.IsNullOrEmpty(query.Alias) &&
                query.KnownRevision == session.Revision)
            { response.Unchanged = true; return response; }
            var all = _events.Read(session, SessionEventReadMode.RequireComplete);
            token.ThrowIfCancellationRequested();
            response.Revision = all.LastOrDefault()?.Sequence ?? session.Revision;
            var requestEventId = query.RequestEventId;
            var aliases = new AliasIndex(all, !string.IsNullOrEmpty(requestEventId) ||
                !string.IsNullOrEmpty(query.Alias));
            if (!string.IsNullOrEmpty(query.Alias))
            {
                if (!string.IsNullOrEmpty(requestEventId) || !aliases.TryResolve(query.Alias, out requestEventId))
                    throw new InvalidOperationException("RESOURCE_NOT_FOUND: the alias is absent from this chat's saved requests.");
                response.SelectedAlias = query.Alias;
                response.SelectedResourceAlias = aliases.ResourceFor(query.Alias);
            }
            if (!string.IsNullOrEmpty(requestEventId))
            {
                var request = all.SingleOrDefault(e => e.EventId == requestEventId && e.Type == SessionEventTypes.LlmRequest);
                if (request == null) throw new InvalidOperationException("RESOURCE_SNAPSHOT_UNAVAILABLE: request event not found.");
                var trace = Trace(request);
                // Never correlate unrelated historical events with absent identifiers.
                var next = all.FirstOrDefault(e => e.Sequence > request.Sequence && e.Type == SessionEventTypes.LlmRequest &&
                    !string.IsNullOrEmpty(trace.RequestId) && Trace(e).RequestId == trace.RequestId);
                response.Events = all.Where(e => e.EventId == request.EventId ||
                    e.Sequence > request.Sequence && (next == null || e.Sequence < next.Sequence) && IsModelEvent(e.Type) &&
                    !string.IsNullOrEmpty(trace.RequestId) && e.RunId == request.RunId && Trace(e).RequestId == trace.RequestId)
                    .Select(e => Describe(e, true, aliases.Request(e.EventId))).ToArray();
                response.Resources = DescribeResources(session, trace, aliases, request.EventId);
                if (!string.IsNullOrEmpty(query.ViewInventoryAlias))
                    response.ViewInventory = DescribeViewInventory(response.Resources, query);
                if (!string.IsNullOrEmpty(query.VerifyResourceAlias))
                {
                    var resource = response.Resources.SingleOrDefault(row => row.Alias == query.VerifyResourceAlias);
                    if (resource == null)
                        throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: resource alias is absent from the selected request.");
                    if (variantRequested)
                        response.ViewVariantVerification = VerifyViewVariant(resource,
                            query.VerifyViewVariantOrdinal.Value, query.VerifyViewVariantSnapshotCount.Value, token);
                    else if (string.IsNullOrEmpty(query.VerifyRetainedView)) VerifyPayload(resource, token);
                    else VerifyView(resource, query.VerifyRetainedView, token);
                }
                response.Operations = DescribeIdentities(trace, entry => new[] { entry.ToolCallId }, aliases.Operation);
                response.Evidence = DescribeIdentities(trace, entry =>
                    (entry.EvidenceLinks ?? new List<ContextEvidenceLink>()).Select(link => link?.EvidenceId), aliases.Evidence);
            }
            else
            {
                var requests = all.Where(e => e.Type == SessionEventTypes.LlmRequest &&
                    (!query.BeforeSequence.HasValue || e.Sequence < query.BeforeSequence.Value))
                    .OrderByDescending(e => e.Sequence).Take(51).ToArray();
                response.Events = requests.Take(50).Select(e => Describe(e, false, aliases.Request(e.EventId))).ToArray();
                if (requests.Length > 50) response.NextBeforeSequence = requests[49].Sequence;
            }
            return response;
        }

        internal ModelContextPayloadResponse Open(ChatSession session, ModelContextPayloadQuery query, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var item = _events.Read(session, SessionEventReadMode.RequireComplete)
                .SingleOrDefault(e => e.SessionId == session.Id && e.EventId == query.EventId && IsModelEvent(e.Type));
            if (item == null) throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: exact model event required.");
            var source = item.Payload;
            if (query.OriginalIndex.HasValue)
            {
                var entries = Trace(item).ContextMessages;
                var index = query.OriginalIndex.Value;
                if (item.Type != SessionEventTypes.LlmRequest || entries == null || index < 0 || index >= entries.Count)
                    throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: exact context source required.");
                source = entries[index].OriginalPayload?.ToBlobReference();
            }
            if (source == null) throw new InvalidOperationException("RESOURCE_SNAPSHOT_UNAVAILABLE: saved body unavailable.");
            var lease = _data.OpenDownload(session, Owner, Math.Max(1, source.ByteLength), cancellation =>
            {
                try
                {
                    return new ResourceDownloadContent {
                        VerifiedStream = _payloads.OpenVerifiedRead(source, Math.Max(1, source.ByteLength), cancellation),
                        VerifiedPayload = PayloadRef.FromBlob(source),
                        ContentType = source.ContentType ?? "application/json"
                    };
                }
                catch (Exception error) when (error is IOException || error is InvalidDataException ||
                    error is UnauthorizedAccessException || error is CryptographicException)
                { throw new InvalidOperationException("RESOURCE_SNAPSHOT_UNAVAILABLE: payload is missing, corrupt or inaccessible.", error); }
            }, token);
            try
            {
                token.ThrowIfCancellationRequested();
                return new ModelContextPayloadResponse { ChatId = session.Id, EventId = item.EventId, Data = lease };
            }
            catch { _data.Close(session.Id, Owner, lease.LeaseId); throw; }
        }

        private static bool IsModelEvent(string type)
        { return type == SessionEventTypes.LlmRequest || type == SessionEventTypes.LlmResponse ||
            type == SessionEventTypes.LlmFailure || type == SessionEventTypes.AssistantChunk ||
            type == SessionEventTypes.AgentResponseRejected || type == SessionEventTypes.ModelResponseAccepted; }
        private static LlmTraceRecord Trace(SessionEvent item)
        { return item.Data?.ToObject<LlmTraceRecord>() ?? new LlmTraceRecord(); }
        private static ModelContextEventDto Describe(SessionEvent item, bool detail, string alias)
        {
            var trace = Trace(item);
            if (!detail) trace.ContextMessages = null;
            return new ModelContextEventDto { EventId = item.EventId, Sequence = item.Sequence, CreatedUtc = item.CreatedUtc,
                Type = item.Type, HasPayload = item.Payload != null, Trace = trace, Alias = alias };
        }

        private static IReadOnlyList<ModelContextIdentityDto> DescribeIdentities(LlmTraceRecord trace,
            Func<ContextMessagePresentation, IEnumerable<string>> ids, Func<string, string> alias)
        {
            var values = new Dictionary<string, ModelContextIdentityDto>(StringComparer.Ordinal);
            foreach (var entry in (trace.ContextMessages ?? new List<ContextMessagePresentation>()).Where(item => item != null))
            {
                foreach (var id in ids(entry).Where(id => !string.IsNullOrWhiteSpace(id)))
                    if (!values.ContainsKey(id)) values.Add(id, new ModelContextIdentityDto { CanonicalId = id, Alias = alias(id) });
            }
            return values.Values.ToArray();
        }

        private IReadOnlyList<ModelContextResourceDto> DescribeResources(ChatSession session, LlmTraceRecord trace,
            AliasIndex aliases, string requestEventId)
        {
            var rows = new Dictionary<string, ModelContextResourceDto>(StringComparer.Ordinal);
            foreach (var entry in (trace.ContextMessages ?? new List<ContextMessagePresentation>()).Where(item => item != null))
                foreach (var link in entry.EvidenceLinks ?? new List<ContextEvidenceLink>())
                {
                    if (link?.Resource?.IsExact != true) continue;
                    var name = aliases.Resource(link.ScopeId, link.Resource);
                    if (name == null) continue;
                    ModelContextResourceDto row;
                    if (!rows.TryGetValue(name, out row))
                    {
                        row = new ModelContextResourceDto { Alias = name, Resource = link.Resource.Copy(), Scope = link.ScopeId,
                            Currentness = ModelContextCurrentness.Unknown,
                            Observations = new List<ModelContextResourceObservationDto>() };
                        rows.Add(name, row);
                    }
                    ((List<ModelContextResourceObservationDto>)row.Observations).Add(new ModelContextResourceObservationDto {
                        RequestAlias = aliases.Request(requestEventId), OperationAlias = aliases.Operation(entry.ToolCallId),
                        EvidenceAlias = aliases.Evidence(link.EvidenceId), View = link.View,
                        Coverage = link.ObservedCoverage, ObservationComplete = link.ObservationComplete,
                        ReadMetadata = link.ReadMetadata,
                        BodyIncludedInMessage = link.BodyIncludedInMessage,
                        EstimatedWireMessageTokens = entry.EvidenceLinks?.Count == 1
                            ? entry.EstimatedWireMessageTokens : null });
                }
            foreach (var row in rows.Values)
                DescribeAuthority(session, row);
            return rows.Values.OrderBy(row => row.Alias, StringComparer.Ordinal).ToArray();
        }

        private void DescribeAuthority(ChatSession session, ModelContextResourceDto row)
        {
            var scope = row.Scope;
            if (_authority == null || scope == null ||
                !(scope.Kind == "conversation" && scope.Id == session.Id ||
                  scope.Kind == "document" && scope.Id == session.DocumentAuthorityId ||
                  scope.Equals(CatalogPublicationService.ScopeId))) return;
            var revisions = _authority.Store as IResourceRevisionStore;
            if (revisions != null)
            {
                row.RevisionMetadata = revisions.GetRevision(scope, row.Resource);
                row.RevisionRecorded = row.RevisionMetadata != null;
                if (row.RevisionMetadata != null)
                {
                    row.PayloadAvailability = row.RevisionMetadata.Payload == null
                        ? ModelContextPayloadAvailability.NotRetained : ModelContextPayloadAvailability.Unchecked;
                    var names = row.Observations.SelectMany(item =>
                        new[] { item.View }.Concat(item.ReadMetadata?.DescriptorAtRead?.Representations ??
                            new string[0]).Concat(item.ReadMetadata?.DescriptorAtRead?.ViewCapabilities?
                            .Select(view => view.View) ?? new string[0]))
                        .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal).ToArray();
                    row.RetainedViews = names.Select(name => revisions.GetView(scope, row.Resource, name))
                        .Where(view => view != null)
                        .Select(DescribeRetainedView)
                        .ToArray();
                }
            }
            var head = _authority.Store.GetHead(scope, row.Resource.Identity);
            if (head == null) return;
            switch (head.Knowledge)
            {
                case HeadKnowledge.Known:
                    row.Currentness = head.Revision.Uri == row.Resource.Uri &&
                        head.Revision.Revision == row.Resource.Revision
                        ? ModelContextCurrentness.Current : ModelContextCurrentness.Superseded;
                    break;
                case HeadKnowledge.Removed: row.Currentness = ModelContextCurrentness.Removed; break;
                case HeadKnowledge.Unavailable: row.Currentness = ModelContextCurrentness.Unavailable; break;
            }
        }

        private ModelContextViewInventoryDto DescribeViewInventory(IReadOnlyList<ModelContextResourceDto> resources,
            ModelContextQuery query)
        {
            var resource = resources.SingleOrDefault(row => row.Alias == query.ViewInventoryAlias);
            var revisions = _authority?.Store as IResourceRevisionStore;
            if (resource?.RevisionMetadata == null || revisions == null)
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: view inventory is unavailable for this request and scope.");
            var offset = query.ViewInventoryOffset ?? 0;
            var page = revisions.ReadViews(resource.Scope, resource.Resource, offset, 20,
                query.ViewInventorySnapshotCount);
            return new ModelContextViewInventoryDto { ResourceAlias = resource.Alias,
                SnapshotCount = page.SnapshotCount, NextOffset = page.NextOffset,
                Items = page.Items.Select((view, index) => new ModelContextViewInventoryItemDto {
                    Ordinal = offset + index, View = view.View, Coverage = view.Coverage,
                    PayloadRegistered = view.Payload != null, PartsCount = view.Parts.Count,
                    EstimatedTextBodyTokens = EstimateTextBodyTokens(view) }).ToArray() };
        }

        private ModelContextViewVariantVerificationDto VerifyViewVariant(ModelContextResourceDto resource,
            int ordinal, int snapshotCount, CancellationToken token)
        {
            var revisions = _authority?.Store as IResourceRevisionStore;
            if (resource.RevisionMetadata == null || revisions == null)
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: retained view variant is unavailable for this request and scope.");
            var page = revisions.ReadViews(resource.Scope, resource.Resource, ordinal, 1, snapshotCount);
            if (page.Items.Count != 1)
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: retained view variant is absent from this inventory snapshot.");
            var variant = DescribeRetainedView(page.Items[0]);
            VerifyRetainedView(variant, token);
            return new ModelContextViewVariantVerificationDto { ResourceAlias = resource.Alias,
                Ordinal = ordinal, SnapshotCount = snapshotCount, Variant = variant };
        }

        private static ModelContextRetainedViewDto DescribeRetainedView(ResourceRevisionView view)
        {
            return new ModelContextRetainedViewDto { View = view.View, Coverage = view.Coverage,
                PayloadAvailability = view.Payload == null ? ModelContextPayloadAvailability.NotRetained :
                    ModelContextPayloadAvailability.Unchecked,
                PartsCount = view.Parts.Count,
                PartsAvailability = view.Parts.Count == 0 ? (ModelContextPayloadAvailability?)null :
                    ModelContextPayloadAvailability.Unchecked,
                EstimatedTextBodyTokens = EstimateTextBodyTokens(view), Retained = view };
        }

        // Same uncalibrated UTF-8 bytes/4 baseline as ModelContextBudget, for the
        // retained text or serialized tree body only. Indexes/parts and non-UTF-8
        // claims cannot represent a direct body or its eventual request cost.
        private static long? EstimateTextBodyTokens(ResourceRevisionView view)
        {
            var tree = view?.View == ResourceRepresentations.Tree;
            if (view?.Payload == null || view.Parts.Count != 0 ||
                (!tree && view.View != ResourceRepresentations.Text && view.View != ResourceRepresentations.Source) ||
                view.Payload.ByteLength < 0) return null;
            var mime = view.Payload.ContentType;
            if (string.IsNullOrWhiteSpace(mime)) return null;
            var fields = mime.Split(';');
            var mediaType = fields[0].Trim();
            if (tree ? !string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) :
                !mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)) return null;
            var utf8 = false;
            foreach (var field in fields.Skip(1))
            {
                var parameter = field.Trim();
                if (!parameter.StartsWith("charset=", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(parameter.Substring(8).Trim().Trim('"'), "utf-8",
                    StringComparison.OrdinalIgnoreCase)) return null;
                utf8 = true;
            }
            if (!utf8) return null;
            var bytes = view.Payload.ByteLength;
            return bytes / 4 + (bytes % 4 == 0 ? 0 : 1);
        }

        private void VerifyPayload(ModelContextResourceDto row, CancellationToken token)
        {
            // Only metadata authorized by DescribeAuthority can supply a payload.
            // No caller-provided CAS address, provider read or head publication.
            var payload = row.RevisionMetadata?.Payload;
            if (payload == null) return;
            row.PayloadAvailability = VerifyBlob(payload, token);
            token.ThrowIfCancellationRequested();
            row.PayloadCheckedUtc = DateTime.UtcNow;
        }

        private void VerifyView(ModelContextResourceDto row, string name, CancellationToken token)
        {
            var item = (row.RetainedViews ?? new ModelContextRetainedViewDto[0])
                .SingleOrDefault(view => view.View == name);
            if (item?.Retained == null)
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: retained view is absent from this request and scope.");
            VerifyRetainedView(item, token);
        }

        private void VerifyRetainedView(ModelContextRetainedViewDto item, CancellationToken token)
        {
            var retained = item.Retained;
            if (retained.Payload != null) item.PayloadAvailability = VerifyBlob(retained.Payload, token);
            if (retained.Parts.Count != 0)
            {
                item.PartsAvailability = ModelContextPayloadAvailability.Verified;
                foreach (var part in retained.Parts)
                    if (VerifyBlob(part, token) != ModelContextPayloadAvailability.Verified)
                    { item.PartsAvailability = ModelContextPayloadAvailability.Unavailable; break; }
            }
            token.ThrowIfCancellationRequested();
            if (retained.Payload != null || retained.Parts.Count != 0) item.CheckedUtc = DateTime.UtcNow;
        }

        private ModelContextPayloadAvailability VerifyBlob(PayloadRef payload, CancellationToken token)
        {
            try
            {
                // OpenVerifiedRead authenticates the full pinned file before returning;
                // disposing it does not materialize a body or create a download lease.
                using (_payloads.OpenVerifiedRead(payload.ToBlobReference(), Math.Max(1, payload.ByteLength), token)) { }
                return ModelContextPayloadAvailability.Verified;
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException ||
                error is UnauthorizedAccessException || error is CryptographicException)
            {
                return ModelContextPayloadAvailability.Unavailable;
            }
        }

        // Ordinals are derived from append-only request events in sequence order. No
        // new store, mutable alias counter or dependence on paginated UI results.
        private sealed class AliasIndex
        {
            private readonly Dictionary<string, string> _requests = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly Dictionary<string, string> _operations = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly Dictionary<string, string> _evidence = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly Dictionary<string, string> _resourceNames = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly Dictionary<string, string> _revisions = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly Dictionary<string, int> _revisionCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            private readonly Dictionary<string, string> _lastRequest = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly Dictionary<string, string> _resourceForEvidence = new Dictionary<string, string>(StringComparer.Ordinal);
            internal AliasIndex(IEnumerable<SessionEvent> events, bool includeLinks)
            {
                foreach (var request in events.Where(e => e.Type == SessionEventTypes.LlmRequest).OrderBy(e => e.Sequence))
                {
                    var q = Register(_requests, request.EventId, "Q");
                    _lastRequest[q] = request.EventId;
                    if (!includeLinks) continue;
                    foreach (var entry in (Trace(request).ContextMessages ?? new List<ContextMessagePresentation>()).Where(item => item != null))
                    {
                        var operation = Register(_operations, entry.ToolCallId, "O");
                        if (operation != null) _lastRequest[operation] = request.EventId;
                        foreach (var link in entry.EvidenceLinks ?? new List<ContextEvidenceLink>())
                        {
                            var resource = RegisterResource(link?.ScopeId, link?.Resource);
                            if (resource != null) _lastRequest[resource] = request.EventId;
                            var evidence = Register(_evidence, link?.EvidenceId, "E");
                            if (evidence != null)
                            {
                                _lastRequest[evidence] = request.EventId;
                                if (resource != null) _resourceForEvidence[evidence] = resource;
                            }
                        }
                    }
                }
            }
            private static string Register(Dictionary<string, string> values, string id, string prefix)
            {
                if (string.IsNullOrWhiteSpace(id)) return null;
                string alias;
                if (!values.TryGetValue(id, out alias)) values.Add(id, alias = prefix + (values.Count + 1));
                return alias;
            }
            private static string ResourceKey(ResourceAuthorityScopeId scope, ResourceRef reference)
            { return (scope == null ? "" : scope.Kind + "\0" + scope.Id) + "\0" + reference.Uri; }
            private string RegisterResource(ResourceAuthorityScopeId scope, ResourceRef reference)
            {
                if (reference?.IsExact != true) return null;
                var resourceKey = ResourceKey(scope, reference);
                var root = Register(_resourceNames, resourceKey, "R");
                var key = resourceKey + "\0" + reference.Revision;
                string alias;
                if (!_revisions.TryGetValue(key, out alias))
                {
                    int count;
                    _revisionCounts.TryGetValue(resourceKey, out count);
                    _revisionCounts[resourceKey] = ++count;
                    _revisions.Add(key, alias = root + "@v" + count);
                }
                return alias;
            }
            internal string Request(string id) { return Find(_requests, id); }
            internal string Operation(string id) { return Find(_operations, id); }
            internal string Evidence(string id) { return Find(_evidence, id); }
            internal string Resource(ResourceAuthorityScopeId scope, ResourceRef reference)
            { return reference?.IsExact == true ? Find(_revisions, ResourceKey(scope, reference) + "\0" + reference.Revision) : null; }
            internal bool TryResolve(string alias, out string requestEventId)
            { return _lastRequest.TryGetValue(alias, out requestEventId); }
            internal string ResourceFor(string alias)
            { string value; return _resourceForEvidence.TryGetValue(alias, out value) ? value :
                alias.StartsWith("R", StringComparison.Ordinal) && _lastRequest.ContainsKey(alias) ? alias : null; }
            private static string Find(Dictionary<string, string> values, string id)
            { string value; return id != null && values.TryGetValue(id, out value) ? value : null; }
        }
    }
}
