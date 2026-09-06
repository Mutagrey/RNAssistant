using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office.Services
{
    // Catalog files are authoring storage. Only a committed exact CAS snapshot is active.
    internal sealed class CatalogPublicationService
    {
        internal static readonly ResourceAuthorityScopeId ScopeId = new ResourceAuthorityScopeId("catalog", "local");
        private readonly ResourceAuthorityService _authority;
        private readonly ResourceMutationJournal _journal;
        private readonly ToolStore _tools;
        private readonly SkillStore _skills;
        private readonly Func<string> _prompts;
        private readonly object _sync = new object();
        private string _skillRevision;
        private SkillCatalogSnapshot _skillSnapshot;
        private readonly SkillCatalogSnapshot _builtIns;
        internal string BuiltInKind { get; private set; }
        private BuiltInToolPublication[] _builtInTools;
        internal string BuiltInToolsKind { get; private set; }
        internal bool HasBuiltInTools { get { return _builtInTools != null; } }

        internal CatalogPublicationService(ResourceAuthorityService authority, ResourceMutationJournal journal,
            ToolStore tools, SkillStore skills, Func<string> prompts, IOfficeApplicationAdapter adapter = null)
        {
            _authority = authority; _journal = journal; _tools = tools; _skills = skills; _prompts = prompts;
            BuiltInKind = "builtin-skills-" + (adapter?.HostName ?? "common").ToLowerInvariant();
            BuiltInToolsKind = "builtin-tools-" + (adapter?.HostName ?? "common").ToLowerInvariant();
            _builtIns = new SkillCatalogSnapshot(BuiltInSkillProvider.GetSkills(adapter));
            // Registration is a publication boundary, never a provider/COM read during compile.
            PublishBuiltIns(BuiltInKind);
        }

        internal void RegisterBuiltInTools(IEnumerable<RNAssistant.Core.Tools.ToolCatalogEntry> tools)
        {
            if (_builtInTools != null) throw new InvalidOperationException("Built-in tools are already registered.");
            _builtInTools = tools.Select(tool =>
            {
                var definition = tool.Clone();
                // Generate while the source-owned runtime policy exists. Deserialized
                // definitions are projections and must never reconstruct that authority.
                var markdown = ToolLibraryDocumentationService.Build(definition);
                if (System.Text.Encoding.UTF8.GetByteCount(markdown) > ToolLibraryDocumentationService.MaximumBytes)
                    throw Unavailable("Built-in tool documentation exceeds its publication bound.");
                return new BuiltInToolPublication { Type = BuiltInToolPublication.ContractType, Definition = definition,
                    Documentation = PayloadRef.FromBlob(_authority.Payloads.StoreText(markdown, "text/markdown")) };
            }).ToArray();
            PublishBuiltIns(BuiltInToolsKind);
        }

        internal ResourceMutationReadBack CaptureReadBack(ResourceIdentity identity)
        {
            var address = ResourceUri.Parse(identity.Uri);
            if (address.Provider != "catalog" || address.Segments.Count != 1)
                throw new InvalidOperationException("An exact catalog publication target is required.");
            string json;
            var parts = new List<PayloadRef>();
            switch (address.Segments[0])
            {
                case "tools": json = JsonConvert.SerializeObject(_tools?.Load() ?? new List<RNAssistant.Core.Tools.ToolCatalogEntry>()); break;
                case "skills":
                    var skills = _skills.Load();
                    foreach (var skill in skills)
                        foreach (var reference in skill.References ?? new List<SkillReferenceMetadata>())
                        {
                            string body, error; SkillReferenceMetadata verified;
                            if (!_skills.TryReadReference(skill, reference.Path, out body, out verified, out error))
                                throw new ResourceRequestException("Skill reference cannot be published: " + error, "RESOURCE_SNAPSHOT_UNAVAILABLE", false);
                            reference.Payload = PayloadRef.FromBlob(_authority.Payloads.StoreText(body, "text/markdown"));
                            parts.Add(reference.Payload);
                        }
                    json = JsonConvert.SerializeObject(skills); break;
                case "prompts": json = _prompts(); break;
                default:
                    if (address.Segments[0] == BuiltInKind) json = JsonConvert.SerializeObject(_builtIns.Skills);
                    else if (HasBuiltInTools && address.Segments[0] == BuiltInToolsKind)
                    { json = JsonConvert.SerializeObject(_builtInTools); parts.AddRange(_builtInTools.Select(item => item.Documentation)); }
                    else throw new InvalidOperationException("Unsupported catalog publication.");
                    break;
            }
            var payload = PayloadRef.FromBlob(_authority.Payloads.StoreText(json, "application/json"));
            return new ResourceMutationReadBack(identity, true, "catalog-state", payload.Sha256, payload, parts: parts);
        }

        internal ResourceRef Current(string kind)
        {
            if (kind != "skills" && kind != "tools" && kind != "prompts" && kind != BuiltInKind &&
                !(HasBuiltInTools && kind == BuiltInToolsKind)) throw new InvalidOperationException("Unsupported catalog kind.");
            var identity = new ResourceIdentity(ResourceUri.Create("catalog", kind));
            var head = _authority.CaptureMany(new[] { ScopeId }).Get(ScopeId).GetHead(identity);
            if (head == null)
            {
                using (_journal.AcquireScope(ScopeId))
                {
                    var snapshot = _authority.CaptureMany(new[] { ScopeId }).Get(ScopeId);
                    head = snapshot.GetHead(identity);
                    if (head == null)
                    {
                        var captured = CaptureReadBack(identity);
                        var exact = new ResourceRef(identity.Uri, "r_" + Guid.NewGuid().ToString("N"));
                        ((IResourceRevisionStore)_authority.Store).RegisterRevision(ScopeId,
                            new ResourceRevisionMetadata(exact, captured.ContentSha256, captured.Payload));
                        ((IResourceRevisionStore)_authority.Store).RegisterView(ScopeId,
                            new ResourceRevisionView(exact, captured.View, captured.ContentSha256, captured.Payload, captured.Coverage, captured.Parts));
                        _authority.Store.Publish(ResourceAuthorityCommit.Create(ScopeId, snapshot.Generation, null,
                            new[] { new ResourceHeadChange(identity, null, ResourceHeadState.Known(exact, snapshot.Generation + 1)) }, AuthorityCommitReason.InitialObservation));
                        return exact;
                    }
                }
            }
            if (head.Knowledge != HeadKnowledge.Known)
                throw new ResourceRequestException("Catalog publication is unresolved; reconcile or explicitly republish it before activation.", "RESOURCE_HEAD_UNKNOWN", false);
            return head.Revision.Copy();
        }

        internal PublishedCatalogSnapshot Capture()
        {
            Current("tools"); Current("skills"); Current("prompts"); Current(BuiltInKind);
            var frozen = _authority.CaptureMany(new[] { ScopeId }).Get(ScopeId);
            return new PublishedCatalogSnapshot(frozen, CaptureSkills(frozen),
                JsonConvert.DeserializeObject<RNAssistant.Core.Tools.ToolCatalogEntry[]>(Read(Known(frozen, "tools"))),
                Read(Known(frozen, "prompts")));
        }

        internal SkillCatalogSnapshot CaptureSkills()
        {
            Current("skills"); Current(BuiltInKind);
            return CaptureSkills(_authority.CaptureMany(new[] { ScopeId }).Get(ScopeId));
        }

        private static ResourceRef Known(ResourceAuthoritySnapshot snapshot, string kind)
        {
            var head = snapshot.GetHead(new ResourceIdentity(ResourceUri.Create("catalog", kind)));
            if (head?.Knowledge != HeadKnowledge.Known)
                throw new ResourceRequestException("Catalog publication is unresolved.", "RESOURCE_HEAD_UNKNOWN", false);
            return head.Revision;
        }

        private SkillCatalogSnapshot CaptureSkills(ResourceAuthoritySnapshot frozen)
        {
            var exact = frozen.GetHead(new ResourceIdentity("rna://catalog/skills"));
            var builtin = frozen.GetHead(new ResourceIdentity("rna://catalog/" + BuiltInKind));
            if (exact.Knowledge != HeadKnowledge.Known || builtin.Knowledge != HeadKnowledge.Known)
                throw new ResourceRequestException("Catalog publication is unresolved.", "RESOURCE_HEAD_UNKNOWN", false);
            var generation = exact.Revision.Revision + ":" + builtin.Revision.Revision;
            lock (_sync)
            {
                if (_skillRevision == generation) return _skillSnapshot;
                var entries = new[] { builtin.Revision, exact.Revision }.SelectMany(reference => {
                    var values = JsonConvert.DeserializeObject<SkillDefinition[]>(Read(reference));
                    foreach (var entry in values) entry.Publication = reference.Copy();
                    return values;
                }).GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase).Select(group => group.First());
                var snapshot = new SkillCatalogSnapshot(entries, generation);
                _skillSnapshot = snapshot; _skillRevision = generation;
                return snapshot;
            }
        }

        internal IReadOnlyList<RNAssistant.Core.Tools.ToolCatalogEntry> CaptureTools()
        { return JsonConvert.DeserializeObject<RNAssistant.Core.Tools.ToolCatalogEntry[]>(Read(Current("tools"))); }

        internal long CaptureGeneration()
        {
            Current("tools"); Current("skills"); Current("prompts"); Current(BuiltInKind);
            return _authority.CaptureMany(new[] { ScopeId }).Get(ScopeId).Generation;
        }

        internal string Read(ResourceRef exact)
        {
            if (exact?.IsExact != true) throw Unavailable("An exact catalog publication is required.");
            var address = ResourceUri.Parse(exact.Uri);
            if (address.Provider != "catalog" || address.Segments.Count != 1)
                throw Unavailable("A catalog publication root is required.");
            var snapshot = _authority.Store.Capture(ScopeId);
            var metadata = _authority.RequirePublished(snapshot, exact);
            return ReadPayload(metadata?.Payload, 8L * 1024 * 1024);
        }

        internal string ReadReference(SkillReferenceMetadata reference)
        { return ReadPayload(reference?.Payload, SkillStore.MaximumSkillReferenceBytes); }

        internal BuiltInToolPublication[] ReadBuiltInTools(ResourceRef root)
        { return ParseBuiltInTools(Read(root)); }

        private static BuiltInToolPublication[] ParseBuiltInTools(string json)
        {
            BuiltInToolPublication[] entries;
            try { entries = JsonConvert.DeserializeObject<BuiltInToolPublication[]>(json); }
            catch (JsonException) { throw Unavailable("The exact built-in tool publication is incompatible."); }
            if (entries == null || entries.Any(item => item == null || item.Type != BuiltInToolPublication.ContractType ||
                item.Definition?.BuiltIn != true || string.IsNullOrWhiteSpace(item.Definition.Id) || item.Documentation?.ContentType != "text/markdown"))
                throw Unavailable("The exact built-in tool publication is incompatible or incomplete.");
            return entries;
        }

        internal string ReadDocumentation(PayloadRef payload)
        { return ReadPayload(payload, ToolLibraryDocumentationService.MaximumBytes); }

        private string ReadPayload(PayloadRef payload, long maximumBytes)
        {
            if (payload == null || payload.ByteLength > maximumBytes)
                throw Unavailable("The exact catalog payload is unavailable or exceeds its bound.");
            return ResourceSnapshotReadService.ReadPayload(_authority.Payloads, payload);
        }

        private static ResourceRequestException Unavailable(string message)
        { return new ResourceRequestException(message, "RESOURCE_SNAPSHOT_UNAVAILABLE", false); }

        internal string ReadPublic(ResourceRef exact)
        {
            var text = Read(exact);
            var kind = ResourceUri.Parse(exact.Uri).Segments[0];
            if (kind == "skills" || kind == BuiltInKind)
            {
                var entries = JsonConvert.DeserializeObject<SkillDefinition[]>(text);
                foreach (var entry in entries) { entry.StoragePath = null; entry.BodyMarkdown = null; }
                return JsonConvert.SerializeObject(entries, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
            }
            if (kind == BuiltInToolsKind)
            {
                // Public catalog metadata never injects the generated human docs.
                var entries = ParseBuiltInTools(text);
                return JsonConvert.SerializeObject(entries.Select(item => item.Definition));
            }
            if (kind == "tools")
            {
                var entries = JsonConvert.DeserializeObject<RNAssistant.Core.Tools.ToolCatalogEntry[]>(text);
                foreach (var entry in entries) { entry.StoragePath = null; entry.Binding = null; }
                return JsonConvert.SerializeObject(entries, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
            }
            return text;
        }

        private void PublishBuiltIns(string kind)
        {
            using (_journal.AcquireScope(ScopeId))
            {
                var state = _authority.CaptureMany(new[] { ScopeId }).Get(ScopeId);
                var identity = new ResourceIdentity(ResourceUri.Create("catalog", kind));
                var before = state.GetHead(identity);
                if (before?.Knowledge == HeadKnowledge.Unknown)
                    throw new ResourceRequestException("Built-in catalog publication is unresolved.", "RESOURCE_HEAD_UNKNOWN", false);
                var captured = CaptureReadBack(identity);
                var revisions = (IResourceRevisionStore)_authority.Store;
                if (before?.Knowledge == HeadKnowledge.Known && revisions.GetRevision(ScopeId, before.Revision)?.Payload?.Sha256 == captured.Payload.Sha256) return;
                var exact = new ResourceRef(identity.Uri, "r_" + Guid.NewGuid().ToString("N"));
                revisions.RegisterRevision(ScopeId, new ResourceRevisionMetadata(exact, captured.ContentSha256, captured.Payload, before?.Revision));
                revisions.RegisterView(ScopeId, new ResourceRevisionView(exact, captured.View, captured.ContentSha256,
                    captured.Payload, captured.Coverage, captured.Parts));
                _authority.Store.Publish(ResourceAuthorityCommit.Create(ScopeId, state.Generation, null,
                    new[] { new ResourceHeadChange(identity, before, ResourceHeadState.Known(exact, state.Generation + 1)) }, AuthorityCommitReason.MetadataTransition));
            }
        }
    }

    internal sealed class BuiltInToolPublication
    {
        internal const string ContractType = "rnassistant.builtInToolPublication.v1";
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("definition")] public RNAssistant.Core.Tools.ToolCatalogEntry Definition { get; set; }
        [JsonProperty("documentation")] public PayloadRef Documentation { get; set; }
    }

    // Request-local frozen publication, never another durable catalog or activation store.
    internal sealed class PublishedCatalogSnapshot
    {
        internal ResourceAuthoritySnapshot Authority { get; private set; }
        internal SkillCatalogSnapshot Skills { get; private set; }
        internal IReadOnlyList<RNAssistant.Core.Tools.ToolCatalogEntry> Tools { get; private set; }
        internal string PromptsJson { get; private set; }
        internal PublishedCatalogSnapshot(ResourceAuthoritySnapshot authority, SkillCatalogSnapshot skills,
            IReadOnlyList<RNAssistant.Core.Tools.ToolCatalogEntry> tools, string promptsJson)
        { Authority = authority; Skills = skills; Tools = tools; PromptsJson = promptsJson; }
    }
}
