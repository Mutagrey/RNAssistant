using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    // Catalog files are authoring storage. Only a committed exact CAS snapshot is active.
    internal sealed class CatalogPublicationService
    {
        internal static readonly ResourceAuthorityScopeId ScopeId = SkillPublicationService.ScopeId;
        internal const string PromptDefaultsKind = "prompt-defaults";
        private readonly ResourceAuthorityService _authority;
        private readonly ResourceMutationJournal _journal;
        private readonly ToolStore _tools;
        private readonly Func<string> _prompts;
        private readonly string _promptDefaults = PromptSettingsService.CaptureTemplates(new AppSettings());
        internal SkillPublicationService Skills { get; private set; }
        internal string BuiltInKind { get { return Skills.BuiltInKind; } }
        private BuiltInToolPublication[] _builtInTools;
        internal string BuiltInToolsKind { get; private set; }
        internal bool HasBuiltInTools { get { return _builtInTools != null; } }

        internal CatalogPublicationService(ResourceAuthorityService authority, ResourceMutationJournal journal,
            ToolStore tools, SkillStore skills, Func<string> prompts, IOfficeApplicationAdapter adapter = null)
        {
            _authority = authority; _journal = journal; _tools = tools; _prompts = prompts;
            BuiltInToolsKind = "builtin-tools-" + (adapter?.HostName ?? "common").ToLowerInvariant();
            Skills = new SkillPublicationService(authority.Store, authority.Revisions, journal, authority.Payloads,
                skills, adapter?.HostName, BuiltInSkillProvider.GetSkills(adapter));
            // Registration is a publication boundary, never a provider/COM read during compile.
            PublishBuiltIns(PromptDefaultsKind);
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
            if (Skills.Owns(address.Segments[0])) return Skills.CaptureReadBack(identity);
            string json;
            var parts = new List<PayloadRef>();
            switch (address.Segments[0])
            {
                case "tools": json = JsonConvert.SerializeObject(_tools?.Load() ?? new List<RNAssistant.Core.Tools.ToolCatalogEntry>()); break;
                case "prompts": json = _prompts(); break;
                case PromptDefaultsKind: json = _promptDefaults; break;
                default:
                    if (HasBuiltInTools && address.Segments[0] == BuiltInToolsKind)
                    { json = JsonConvert.SerializeObject(_builtInTools); parts.AddRange(_builtInTools.Select(item => item.Documentation)); }
                    else throw new InvalidOperationException("Unsupported catalog publication.");
                    break;
            }
            var payload = PayloadRef.FromBlob(_authority.Payloads.StoreText(json, "application/json"));
            return new ResourceMutationReadBack(identity, true, "catalog-state", payload.Sha256, payload, parts: parts);
        }

        internal ResourceRef Current(string kind)
        {
            if (Skills.Owns(kind)) return Skills.Current(kind);
            if (kind != "skills" && kind != "tools" && kind != "prompts" && kind != PromptDefaultsKind && kind != BuiltInKind &&
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
            var frozen = CaptureReady();
            return new PublishedCatalogSnapshot(frozen, Skills.Capture(frozen),
                JsonConvert.DeserializeObject<RNAssistant.Core.Tools.ToolCatalogEntry[]>(Read(Known(frozen, "tools"))),
                Read(Known(frozen, "prompts")));
        }

        internal SkillCatalogSnapshot CaptureSkills()
        { return Skills.Capture(); }

        private static ResourceRef Known(ResourceAuthoritySnapshot snapshot, string kind)
        {
            var head = snapshot.GetHead(new ResourceIdentity(ResourceUri.Create("catalog", kind)));
            if (head?.Knowledge != HeadKnowledge.Known)
                throw new ResourceRequestException("Catalog publication is unresolved.", "RESOURCE_HEAD_UNKNOWN", false);
            return head.Revision;
        }

        internal IReadOnlyList<RNAssistant.Core.Tools.ToolCatalogEntry> CaptureTools()
        { return JsonConvert.DeserializeObject<RNAssistant.Core.Tools.ToolCatalogEntry[]>(Read(Current("tools"))); }

        internal long CaptureGeneration()
        {
            return CaptureReady().Generation;
        }

        private ResourceAuthoritySnapshot CaptureReady()
        {
            var frozen = _authority.CaptureMany(new[] { ScopeId }).Get(ScopeId);
            var initialized = false;
            var kinds = HasBuiltInTools
                ? new[] { "tools", "skills", "prompts", PromptDefaultsKind, BuiltInKind, BuiltInToolsKind }
                : new[] { "tools", "skills", "prompts", PromptDefaultsKind, BuiltInKind };
            foreach (var kind in kinds)
            {
                if (frozen.GetHead(new ResourceIdentity(ResourceUri.Create("catalog", kind)))?.Knowledge == HeadKnowledge.Known)
                    continue;
                Current(kind);
                initialized = true;
            }
            return initialized ? _authority.CaptureMany(new[] { ScopeId }).Get(ScopeId) : frozen;
        }

        internal string Read(ResourceRef exact)
        {
            if (exact?.IsExact != true) throw Unavailable("An exact catalog publication is required.");
            var address = ResourceUri.Parse(exact.Uri);
            if (address.Provider != "catalog" || address.Segments.Count != 1)
                throw Unavailable("A catalog publication root is required.");
            if (Skills.Owns(address.Segments[0])) return Skills.Read(exact);
            var snapshot = _authority.Store.Capture(ScopeId);
            var metadata = _authority.RequirePublished(snapshot, exact);
            return ReadPayload(metadata?.Payload, 8L * 1024 * 1024);
        }

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
