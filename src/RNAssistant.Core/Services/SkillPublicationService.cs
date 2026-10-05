using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Core.Services
{
    // Shared Office/workspace publication owner. SkillStore is authoring storage;
    // active packages and references are read only from committed CAS snapshots.
    public sealed class SkillPublicationService
    {
        public static readonly ResourceAuthorityScopeId ScopeId = new ResourceAuthorityScopeId("catalog", "local");
        private readonly IResourceAuthorityStore _authority;
        private readonly IResourceRevisionStore _revisions;
        private readonly ResourceMutationJournal _journal;
        private readonly ChatBlobStore _payloads;
        private readonly SkillStore _store;
        private readonly SkillCatalogSnapshot _builtIns;
        private readonly object _sync = new object();
        private string _generation;
        private SkillCatalogSnapshot _snapshot;
        public string BuiltInKind { get; private set; }

        public SkillPublicationService(IResourceAuthorityStore authority, IResourceRevisionStore revisions,
            ResourceMutationJournal journal, ChatBlobStore payloads, SkillStore store,
            string host, IEnumerable<SkillDefinition> builtIns)
        {
            _authority = authority; _revisions = revisions; _journal = journal;
            _payloads = payloads; _store = store;
            BuiltInKind = "builtin-skills-" + (host ?? "common").ToLowerInvariant();
            _builtIns = new SkillCatalogSnapshot(builtIns);
            PublishBuiltIns();
        }

        public bool Owns(string kind) { return kind == "skills" || kind == BuiltInKind; }

        public ResourceMutationReadBack CaptureReadBack(ResourceIdentity identity)
        {
            var address = ResourceUri.Parse(identity.Uri);
            if (address.Provider != "catalog" || address.Segments.Count != 1 || !Owns(address.Segments[0]))
                throw new InvalidOperationException("An exact skill catalog publication target is required.");
            var parts = new List<PayloadRef>();
            var skills = address.Segments[0] == BuiltInKind ? _builtIns.Skills : _store.Load();
            if (address.Segments[0] == "skills")
                foreach (var skill in skills)
                    foreach (var reference in skill.References ?? new List<SkillReferenceMetadata>())
                    {
                        string body, error; SkillReferenceMetadata verified;
                        if (!_store.TryReadReference(skill, reference.Path, out body, out verified, out error))
                            throw Error("Skill reference cannot be published: " + error);
                        reference.Payload = PayloadRef.FromBlob(_payloads.StoreText(body, "text/markdown"));
                        parts.Add(reference.Payload);
                    }
            var payload = PayloadRef.FromBlob(_payloads.StoreText(JsonConvert.SerializeObject(skills), "application/json"));
            return new ResourceMutationReadBack(identity, true, "catalog-state", payload.Sha256, payload, parts: parts);
        }

        public ResourceRef Current(string kind)
        {
            if (!Owns(kind)) throw new InvalidOperationException("Unsupported skill catalog kind.");
            var identity = new ResourceIdentity(ResourceUri.Create("catalog", kind));
            var head = CaptureReady().Get(ScopeId).GetHead(identity);
            if (head == null)
                using (_journal.AcquireScope(ScopeId))
                {
                    var snapshot = CaptureReady().Get(ScopeId);
                    head = snapshot.GetHead(identity);
                    if (head == null) return Publish(identity, snapshot, null, AuthorityCommitReason.InitialObservation);
                }
            if (head.Knowledge != HeadKnowledge.Known) throw Unknown();
            return head.Revision.Copy();
        }

        public ResourceAuthoritySnapshotSet CaptureAuthority(IEnumerable<ResourceAuthorityScopeId> additionalScopes = null)
        {
            Current("skills"); Current(BuiltInKind);
            return CaptureReady(additionalScopes);
        }

        private ResourceAuthoritySnapshotSet CaptureReady(IEnumerable<ResourceAuthorityScopeId> additionalScopes = null)
        {
            var scopes = (additionalScopes ?? Enumerable.Empty<ResourceAuthorityScopeId>()).Concat(new[] { ScopeId }).Distinct().ToArray();
            EnsureReady(scopes);
            var captured = _authority.CaptureMany(scopes);
            EnsureReady(scopes);
            return captured;
        }

        private void EnsureReady(ResourceAuthorityScopeId[] scopes)
        {
            if (_journal.Unresolved().Any(attempt => attempt.State == MutationAttemptState.DispatchMayHaveOccurred && scopes.Contains(attempt.ScopeId)))
                throw new ResourceRequestException("A resource mutation has not reached its authority publication barrier.", "RESOURCE_AUTHORITY_NOT_READY", true);
        }

        public SkillCatalogSnapshot Capture()
        { return Capture(CaptureAuthority().Get(ScopeId)); }

        public SkillCatalogSnapshot Capture(ResourceAuthoritySnapshot frozen)
        {
            var custom = frozen.GetHead(new ResourceIdentity("rna://catalog/skills"));
            var builtIn = frozen.GetHead(new ResourceIdentity(ResourceUri.Create("catalog", BuiltInKind)));
            if (custom?.Knowledge != HeadKnowledge.Known || builtIn?.Knowledge != HeadKnowledge.Known) throw Unknown();
            var generation = custom.Revision.Revision + ":" + builtIn.Revision.Revision;
            lock (_sync)
            {
                if (_generation == generation) return _snapshot;
                var entries = new[] { builtIn.Revision, custom.Revision }.SelectMany(root => ReadDefinitions(root, frozen))
                    .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase).Select(group => group.First());
                _snapshot = new SkillCatalogSnapshot(entries, generation); _generation = generation;
                return _snapshot;
            }
        }

        public string Read(ResourceRef root)
        { return Read(root, CaptureReady().Get(ScopeId)); }

        private string Read(ResourceRef root, ResourceAuthoritySnapshot frozen)
        {
            if (root?.IsExact != true) throw Error("An exact skill publication is required.");
            var address = ResourceUri.Parse(root.Uri);
            if (address.Provider != "catalog" || address.Segments.Count != 1 || !Owns(address.Segments[0]))
                throw Error("A skill publication root is required.");
            var metadata = ResourcePublicationReader.RequirePublished(_revisions, frozen, root);
            return ResourcePublicationReader.ReadPayload(_payloads, metadata.Payload, 8L * 1024 * 1024);
        }

        public SkillDefinition[] ReadDefinitions(ResourceRef root)
        { return ReadDefinitions(root, CaptureReady().Get(ScopeId)); }

        private SkillDefinition[] ReadDefinitions(ResourceRef root, ResourceAuthoritySnapshot frozen)
        {
            SkillDefinition[] entries;
            try { entries = JsonConvert.DeserializeObject<SkillDefinition[]>(Read(root, frozen)); }
            catch (JsonException) { throw Error("The exact skill publication is incompatible."); }
            if (entries == null || entries.Any(item => item == null || string.IsNullOrWhiteSpace(item.Id)))
                throw Error("The exact skill publication is incomplete.");
            foreach (var entry in entries) entry.Publication = root.Copy();
            return entries;
        }

        public ResourceReadObservation ReadSkill(ResourceRef exact)
        {
            var frozen = CaptureReady().Get(ScopeId);
            var descriptor = Describe(exact, frozen);
            var address = ResourceUri.Parse(exact.Uri);
            var root = new ResourceRef(ResourceUri.Create("catalog", address.Segments[0]), exact.Revision);
            var skill = FindSkill(root, address.Segments[1], frozen);
            var reference = address.Segments.Count == 4 ? FindReference(skill, address.Segments[3]) : null;
            var text = reference == null ? skill.BodyMarkdown ?? string.Empty :
                ResourcePublicationReader.ReadPayload(_payloads, reference.Payload, SkillStore.MaximumSkillReferenceBytes);
            var payload = PayloadRef.FromBlob(_payloads.StoreText(text, "text/markdown"));
            descriptor.Payload = payload; descriptor.ContentSha256 = payload.Sha256;
            descriptor.Coverage = ResourceCoverage.Whole(); descriptor.ByteLength = payload.ByteLength;
            var result = new ResourceReadResult { Resource = descriptor, Representation = "text", Text = text,
                ReturnedCharacters = text.Length, TotalCharacters = text.Length, Complete = true,
                RawContentIncluded = true, ContentSha256 = payload.Sha256, Coverage = ResourceCoverage.Whole(),
                CompleteViewPayload = payload, AuthorityGeneration = frozen.Generation };
            if (_revisions.GetRevision(ScopeId, exact) == null)
                _revisions.RegisterRevision(ScopeId, new ResourceRevisionMetadata(exact, payload.Sha256, payload,
                    dependencies: descriptor.Dependencies));
            _revisions.RegisterView(ScopeId, new ResourceRevisionView(exact, "text", payload.Sha256, payload, ResourceCoverage.Whole()));
            var evidence = new ResourceEvidence("e_" + Guid.NewGuid().ToString("N"), ScopeId, exact, "text",
                ResourceCoverage.Whole(), true, frozen.Generation, payload, descriptor.Dependencies,
                immutable: true, contentSha256: payload.Sha256);
            return new ResourceReadObservation(result, new[] { evidence });
        }

        public ResourceDescriptor Describe(ResourceRef exact)
        { return Describe(exact, CaptureReady().Get(ScopeId)); }

        private ResourceDescriptor Describe(ResourceRef exact, ResourceAuthoritySnapshot frozen)
        {
            if (exact?.IsExact != true) throw Error("An exact skill source is required.");
            var address = ResourceUri.Parse(exact.Uri);
            if (address.Provider != "catalog" || !Owns(address.Segments.FirstOrDefault()) ||
                !(address.Segments.Count == 3 && address.Segments[2] == "body" ||
                  address.Segments.Count == 4 && address.Segments[2] == "reference")) throw Error("Unknown skill source.");
            var root = new ResourceRef(ResourceUri.Create("catalog", address.Segments[0]), exact.Revision);
            var skill = FindSkill(root, address.Segments[1], frozen);
            return DescribeSkill(root, skill, address.Segments.Count == 4 ? FindReference(skill, address.Segments[3]) : null);
        }

        private SkillDefinition FindSkill(ResourceRef root, string id, ResourceAuthoritySnapshot frozen)
        {
            var matches = ReadDefinitions(root, frozen).Where(item => item.Id == id).Take(2).ToArray();
            if (matches.Length != 1) throw Error("The exact skill definition is unavailable or ambiguous.");
            return matches[0];
        }

        public static ResourceRef SkillResource(SkillDefinition skill, string referencePath = null)
        {
            if (skill?.Publication?.IsExact != true) throw Error("The skill has no active publication.");
            var root = ResourceUri.Parse(skill.Publication.Uri).Segments[0];
            return new ResourceRef(referencePath == null ? ResourceUri.Create("catalog", root, skill.Id, "body") :
                ResourceUri.Create("catalog", root, skill.Id, "reference", ReferenceName(referencePath)), skill.Publication.Revision);
        }

        public static ResourceDescriptor DescribeSkill(ResourceRef root, SkillDefinition skill, SkillReferenceMetadata reference)
        {
            skill.Publication = root.Copy();
            var descriptor = new ResourceDescriptor { Reference = SkillResource(skill, reference?.Path), Provider = "catalog",
                Title = skill.Id + (reference == null ? "" : " / " + reference.Path), Kind = reference == null ? "skill" : "skill-reference",
                MimeType = "text/markdown", Mutable = false, ByteLength = reference?.ByteLength, Tracking = "strongly-tracked" };
            descriptor.Representations.Add("text"); descriptor.Capabilities.Add("read");
            descriptor.Dependencies.Add(new ResourceDependency(root, "text", ResourceCoverage.Whole(), "catalog-publication"));
            return descriptor;
        }

        private static string ReferenceName(string path) { return (path ?? "").Replace('\\', '/').Split('/').Last(); }
        private static SkillReferenceMetadata FindReference(SkillDefinition skill, string name)
        {
            var matches = (skill.References ?? new List<SkillReferenceMetadata>()).Where(item => ReferenceName(item.Path) == name).Take(2).ToArray();
            if (matches.Length != 1) throw Error("The exact reference is unavailable or ambiguous.");
            return matches[0];
        }

        private void PublishBuiltIns()
        {
            using (_journal.AcquireScope(ScopeId))
            {
                var state = CaptureReady().Get(ScopeId);
                var identity = new ResourceIdentity(ResourceUri.Create("catalog", BuiltInKind));
                var before = state.GetHead(identity);
                if (before?.Knowledge == HeadKnowledge.Unknown) throw Unknown();
                var captured = CaptureReadBack(identity);
                if (before?.Knowledge == HeadKnowledge.Known && _revisions.GetRevision(ScopeId, before.Revision)?.Payload?.Sha256 == captured.Payload.Sha256) return;
                Publish(identity, state, before, AuthorityCommitReason.MetadataTransition, captured);
            }
        }

        private ResourceRef Publish(ResourceIdentity identity, ResourceAuthoritySnapshot snapshot, ResourceHeadState before,
            AuthorityCommitReason reason, ResourceMutationReadBack captured = null)
        {
            captured = captured ?? CaptureReadBack(identity);
            var exact = new ResourceRef(identity.Uri, "r_" + Guid.NewGuid().ToString("N"));
            _revisions.RegisterRevision(ScopeId, new ResourceRevisionMetadata(exact, captured.ContentSha256, captured.Payload, before?.Revision));
            _revisions.RegisterView(ScopeId, new ResourceRevisionView(exact, captured.View, captured.ContentSha256,
                captured.Payload, captured.Coverage, captured.Parts));
            _authority.Publish(ResourceAuthorityCommit.Create(ScopeId, snapshot.Generation, null,
                new[] { new ResourceHeadChange(identity, before, ResourceHeadState.Known(exact, snapshot.Generation + 1)) }, reason));
            return exact;
        }

        private static ResourceRequestException Error(string message)
        { return new ResourceRequestException(message, "RESOURCE_SNAPSHOT_UNAVAILABLE", false); }
        private static ResourceRequestException Unknown()
        { return new ResourceRequestException("Skill catalog publication is unresolved; explicitly reconcile it before activation.", "RESOURCE_HEAD_UNKNOWN", false); }
    }
}
