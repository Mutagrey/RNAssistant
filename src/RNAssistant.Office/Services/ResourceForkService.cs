using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office.Services
{
    internal sealed class ResourceForkPlan
    {
        internal string SourceSessionId { get; private set; }
        internal string TargetSessionId { get; private set; }
        internal IReadOnlyList<ResourceRef> Heads { get; private set; }
        internal IReadOnlyList<ResourceMutationReadBack> ReadBack { get; private set; }
        internal ResourceForkPlan(ChatSession source, ChatSession target, IEnumerable<ResourceMutationReadBack> readBack)
        {
            SourceSessionId = source.Id; TargetSessionId = target.Id;
            ReadBack = Array.AsReadOnly(readBack.ToArray());
            Heads = Array.AsReadOnly(ReadBack.Select(item => item.Revision).ToArray());
        }
    }

    // Prepares an explicit copy graph, not another store or a cross-chat read alias.
    // Only the existing mutation observer publishes heads after the fork event saves.
    internal sealed class ResourceForkService
    {
        private readonly ResourceAuthorityService _authority;
        private readonly ChatBlobStore _payloads;
        internal ResourceForkService(ResourceAuthorityService authority, ChatBlobStore payloads)
        { _authority = authority; _payloads = payloads; }

        internal ResourceForkPlan Prepare(ChatSession source, ChatSession target, HtmlWorkspace currentWorkspace)
        { return new Preparation(_authority, _payloads, source, target).Build(currentWorkspace); }

        private sealed class Preparation
        {
            private readonly ResourceAuthorityService _authority;
            private readonly IResourceRevisionStore _revisions;
            private readonly ChatBlobStore _payloads;
            private readonly ChatSession _source, _target;
            private readonly ResourceAuthorityScopeId _sourceScope, _targetScope;
            private readonly ResourceAuthoritySnapshot _sourcePublication;
            private readonly Dictionary<string, long[]> _published = new Dictionary<string, long[]>(StringComparer.Ordinal);
            private readonly Dictionary<string, ResourceCopyLink> _links = new Dictionary<string, ResourceCopyLink>(StringComparer.Ordinal);
            private readonly Dictionary<string, ResourceRevisionMetadata> _copies = new Dictionary<string, ResourceRevisionMetadata>(StringComparer.Ordinal);
            private readonly HashSet<string> _visiting = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<string> _artifactIds = new HashSet<string>(StringComparer.Ordinal);
            private long _bytes;

            internal Preparation(ResourceAuthorityService authority, ChatBlobStore payloads, ChatSession source, ChatSession target)
            {
                if (source == null || target == null || target.ParentSessionId != source.Id || target.Id == source.Id ||
                    source.DocumentAuthorityId != target.DocumentAuthorityId)
                    throw Error("An explicit child of the same document authority is required.");
                _authority = authority; _payloads = payloads; _revisions = (IResourceRevisionStore)authority.Store;
                _source = source; _target = target;
                _sourceScope = authority.Scope(source, false); _targetScope = authority.Scope(target, false);
                var frozen = authority.CaptureMany(new[] { _sourceScope, _targetScope });
                _sourcePublication = frozen.Get(_sourceScope);
                if (frozen.Get(_targetScope).Generation != 0 || (target.ResourceCopies?.Count ?? 0) != 0)
                    throw Error("The target must be an unpublished fork.");
            }

            internal ResourceForkPlan Build(HtmlWorkspace currentWorkspace)
            {
                // Retained workspace bodies stay immutable. Copy links also support a
                // later explicit undo/restore of any workspace retained by this fork.
                foreach (var artifact in _target.Artifacts.Where(item => item.Kind == ChatArtifactKinds.HtmlWorkspace))
                {
                    if (string.IsNullOrWhiteSpace(artifact.InlineText)) throw Error("An exact workspace snapshot is unavailable.");
                    var snapshot = JsonConvert.DeserializeObject<HtmlWorkspaceSnapshot>(artifact.InlineText);
                    if (snapshot == null) throw Error("The exact workspace snapshot is invalid.");
                    MapBindings(snapshot.DataSources);
                }
                MapBindings(currentWorkspace?.DataSources);
                foreach (var reference in _target.Messages.SelectMany(message => message.ResourceRefs ?? new List<ResourceRef>()))
                    if (IsCopyable(reference)) Map(reference);
                foreach (var note in _target.Context?.Notes ?? new List<ContextNote>())
                {
                    if (note?.Role != ContextNoteRole.SuppliedData || note.Evidence == null) continue;
                    var evidence = note.Evidence;
                    var copy = Map(evidence.Resource);
                    var metadata = _copies.Values.Single(item => Key(item.Reference) == Key(copy));
                    note.Evidence = new ResourceEvidence("ev_" + Guid.NewGuid().ToString("N"), _targetScope, copy,
                        evidence.View, evidence.Coverage, evidence.Complete, 1, metadata.Payload, metadata.Dependencies,
                        immutable: true, contentSha256: metadata.ContentSha256);
                }
                var addedArtifacts = ChatCloneService.CloneArtifactsForMessages(_source.Artifacts, new ChatMessage[0], _artifactIds);
                foreach (var artifact in addedArtifacts.Where(item => !_target.Artifacts.Any(existing => existing.Id == item.Id)))
                    _target.Artifacts.Add(artifact);
                _target.ResourceCopies = _links.Values.ToList();
                // CAS and immutable revision retention may precede publication, but no
                // head is exposed until all copied resources and the chat are durable.
                foreach (var metadata in _copies.Values) _revisions.RegisterRevision(_targetScope, metadata);
                var selected = _copies.GroupBy(item => item.Value.Reference.Identity.Uri, StringComparer.Ordinal)
                    .Select(group => group.Aggregate((left, right) => CompareOrder(_published[left.Key], _published[right.Key]) >= 0 ? left : right).Value);
                return new ResourceForkPlan(_source, _target, selected.Select(item => new ResourceMutationReadBack(
                    item.Reference.Identity, true, "text", item.ContentSha256, item.Payload, ResourceCoverage.Whole(),
                    item.Dependencies, revision: item.Reference)));
            }

            private void MapBindings(IEnumerable<HtmlWorkspaceDataSource> sources)
            {
                foreach (var binding in (sources ?? new HtmlWorkspaceDataSource[0]).Select(item => item?.Binding).Where(item => item != null))
                { Map(binding.Resource); Map(binding.Schema); Map(binding.Mapping); }
            }

            private ResourceRef Map(ResourceRef reference)
            {
                if (reference == null) return null;
                var original = reference;
                var address = ResourceUri.Parse(reference.Uri);
                if (address.Provider == "chat")
                {
                    var owned = ChatResourceUri.RebaseArtifactRevision(reference, _source.Id);
                    string id;
                    if (!ChatResourceUri.TryGetCurrentArtifactId(_source, owned, out id))
                        throw Error("An exact source artifact dependency is unavailable.");
                    _artifactIds.Add(id);
                    return ChatResourceUri.RebaseArtifactRevision(owned, _target.Id);
                }
                if (address.Provider != "state" && address.Provider != "context") return reference.Copy();
                if (address.Segments.Count != 3 || address.Segments[0] != "conversation")
                    throw Error("The conversation dependency scope is invalid.");
                if (address.Segments[1] != _source.Id)
                    reference = HtmlWorkspaceArtifactService.ForkReference(_source, reference);
                if (!IsCopyable(reference) || !reference.IsExact) throw Error("An exact supported conversation definition is required.");
                var key = Key(reference);
                ResourceRevisionMetadata existing;
                if (_copies.TryGetValue(key, out existing))
                { Link(original, existing.Reference, _published[key]); return existing.Reference; }
                if (_visiting.Count >= 16 || !_visiting.Add(key)) throw Error("The copy graph is cyclic or exceeds its depth bound.");
                try
                {
                    if (_copies.Count + _visiting.Count > 128)
                        throw Error("The copy graph is too large.");
                    var publicationOrder = _authority.PublicationOrder(_sourcePublication, reference, _source);
                    if (publicationOrder == null)
                        throw Error("The copy graph contains an unpublished revision.");
                    _published[key] = publicationOrder;
                    var metadata = _revisions.GetRevision(_sourceScope, reference);
                    if (metadata?.Payload == null || metadata.Payload.ByteLength > 2000000 ||
                        (_bytes += metadata.Payload.ByteLength) > 16L * 1024 * 1024)
                        throw Error("The exact copy payload is unavailable or exceeds the bounded preparation.");
                    var body = _payloads.ReadText(metadata.Payload.ToBlobReference());
                    if (body == null) throw Error("The exact copy payload is missing; no newer revision was substituted.");
                    address = ResourceUri.Parse(reference.Uri);
                    var copy = new ResourceRef(ResourceUri.Create(address.Provider, "conversation", _target.Id, address.Segments[2]),
                        "r_" + Guid.NewGuid().ToString("N"));
                    var payload = CopyPayload(address.Segments[2], address.Provider, metadata.Payload, body);
                    var dependencies = MapDependencies(metadata.Dependencies);
                    dependencies.Add(new ResourceDependency(reference, "text", ResourceCoverage.Whole(), "immutable-snapshot"));
                    ResourceRevisionMetadata parent;
                    _copies.TryGetValue(metadata.Parent == null ? string.Empty : Key(metadata.Parent), out parent);
                    var copied = new ResourceRevisionMetadata(copy, payload.Sha256, payload, parent?.Reference, dependencies: dependencies);
                    _copies.Add(key, copied);
                    Link(reference, copy, _published[key]); Link(original, copy, _published[key]);
                    return copy;
                }
                finally { _visiting.Remove(key); }
            }

            private PayloadRef CopyPayload(string name, string provider, PayloadRef payload, string body)
            {
                if (provider == "context" || name.StartsWith("derived-", StringComparison.Ordinal) && payload.ContentType != ResourceDerivedViewService.VirtualContentType)
                    return payload; // Immutable data is copied by reference, never rewritten as a definition.
                if (payload.ByteLength > 128000) throw Error("The definition exceeds its bounded contract.");
                object definition;
                if (name.StartsWith("schema-", StringComparison.Ordinal))
                {
                    var schema = JsonConvert.DeserializeObject<SemanticSchemaDefinition>(body);
                    if (schema?.Contract != "resource-schema-v1") throw Error("The exact schema contract is invalid.");
                    schema.ValidationSource = Map(schema.ValidationSource); definition = schema;
                }
                else if (name.StartsWith("mapping-", StringComparison.Ordinal))
                {
                    var mapping = JsonConvert.DeserializeObject<ResourceMappingDefinition>(body);
                    if (mapping?.Contract != "resource-mapping-v1") throw Error("The exact mapping contract is invalid.");
                    mapping.Source = Map(mapping.Source); mapping.Schema = Map(mapping.Schema);
                    mapping.SourceDependencies = MapDependencies(mapping.SourceDependencies); definition = mapping;
                }
                else
                {
                    var derived = JsonConvert.DeserializeObject<ResourceDerivedDefinition>(body);
                    if (derived?.Contract != "resource-derived-v1" || derived.Mode != DerivedResourceMode.Virtual)
                        throw Error("The exact virtual definition contract is invalid.");
                    derived.Source = Map(derived.Source); derived.Schema = Map(derived.Schema); derived.Mapping = Map(derived.Mapping);
                    derived.SourceDependencies = MapDependencies(derived.SourceDependencies); definition = derived;
                }
                return PayloadRef.FromBlob(_payloads.StoreText(JsonConvert.SerializeObject(definition), payload.ContentType));
            }

            private List<ResourceDependency> MapDependencies(IEnumerable<ResourceDependency> dependencies)
            {
                return (dependencies ?? new ResourceDependency[0]).Select(item => {
                    var address = ResourceUri.Parse(item.Resource.Uri);
                    // A prior fork's immutable provenance is not a source access grant.
                    var provenance = item.Kind == "immutable-snapshot" && (address.Provider == "state" || address.Provider == "context") &&
                        address.Segments.Count == 3 && address.Segments[1] != _source.Id;
                    return new ResourceDependency(provenance ? item.Resource : Map(item.Resource), item.View, item.Coverage, item.Kind);
                }).ToList();
            }

            private void Link(ResourceRef source, ResourceRef copy, long[] sourcePublicationPath)
            {
                if (_links.Count >= 256 && !_links.ContainsKey(Key(source))) throw Error("The copy link bound was exceeded.");
                _links[Key(source)] = new ResourceCopyLink(source, copy, sourcePublicationPath);
            }

            private static bool IsCopyable(ResourceRef reference)
            {
                if (reference == null) return false;
                var address = ResourceUri.Parse(reference.Uri);
                if (address.Segments.Count != 3 || address.Segments[0] != "conversation") return false;
                return address.Provider == "context" || address.Provider == "state" &&
                    new[] { "schema-draft-", "schema-published-", "mapping-", "derived-" }.Any(prefix => address.Segments[2].StartsWith(prefix, StringComparison.Ordinal));
            }
            private static string Key(ResourceRef reference) { return reference.Uri + "\n" + reference.Revision; }
            private static int CompareOrder(long[] left, long[] right)
            {
                for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
                    if (left[index] != right[index]) return left[index].CompareTo(right[index]);
                return left.Length.CompareTo(right.Length);
            }
        }

        private static InvalidOperationException Error(string message)
        { return new InvalidOperationException("RESOURCE_FORK_DEPENDENCY_UNAVAILABLE: " + message); }
    }
}
