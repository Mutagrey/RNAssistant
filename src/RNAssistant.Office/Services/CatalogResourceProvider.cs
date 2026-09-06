using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    // Reading a stored publication never activates a tool, skill or schema.
    internal sealed class CatalogResourceProvider : IResourceProvider
    {
        private readonly CatalogPublicationService _catalogs;
        private readonly RNAssistant.Core.Storage.ChatBlobStore _payloads;
        public string Id { get { return "catalog"; } }
        internal CatalogResourceProvider(CatalogPublicationService catalogs, ResourceAuthorityService authority)
        { _catalogs = catalogs; _payloads = authority.Payloads; }

        private IEnumerable<string> Kinds { get { return new[] { "tools", "skills", "prompts", _catalogs.BuiltInKind }
            .Concat(_catalogs.HasBuiltInTools ? new[] { _catalogs.BuiltInToolsKind } : new string[0]); } }
        private bool SkillKind(string kind) { return kind == "skills" || kind == _catalogs.BuiltInKind; }
        private bool ToolKind(string kind) { return kind == "tools" || _catalogs.HasBuiltInTools && kind == _catalogs.BuiltInToolsKind; }

        public ResourceListPage List(ChatSession session, string kind, string cursor, int limit)
        {
            var items = new List<ResourceDescriptor>();
            foreach (var name in Kinds)
            {
                var root = _catalogs.Current(name);
                items.Add(DescribeRoot(root));
                if (ToolKind(name) && (string.IsNullOrEmpty(kind) || kind == "tool-source"))
                    foreach (var tool in Tools(root).Where(tool =>
                        string.Equals(tool.Host, session.Host, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(tool.Host, "Common", StringComparison.OrdinalIgnoreCase)))
                        items.Add(DescribeTool(root, tool));
                if (!SkillKind(name)) continue;
                foreach (var skill in Skills(root))
                {
                    items.Add(DescribeSkill(root, skill, null));
                    foreach (var reference in skill.References ?? new List<SkillReferenceMetadata>())
                        items.Add(DescribeSkill(root, skill, reference));
                }
            }
            items = items.Where(item => string.IsNullOrEmpty(kind) || item.Kind == kind).ToList();
            var binding = ResourceReadCursor.ListBinding(Id, kind);
            var position = ResourceReadCursor.ParseRevisionBound(cursor, binding);
            var revision = ResourceReadCursor.CollectionRevision(items);
            ResourceReadCursor.ValidateContinuation(position, revision);
            ResourceReadCursor.ValidateCollectionOffset(position, items.Count);
            var selected = items.Skip(position.Offset).Take(Math.Max(1, Math.Min(50, limit <= 0 ? 20 : limit))).ToList();
            var next = position.Offset + selected.Count;
            return new ResourceListPage { Items = selected, Total = items.Count, Truncated = next < items.Count,
                NextCursor = next < items.Count ? ResourceReadCursor.CreateRevisionBound(next, revision, binding) : null };
        }

        public ResourceDescriptor Resolve(ChatSession session, string uri)
        {
            var address = Address(uri);
            return Describe(new ResourceRef(uri, _catalogs.Current(address.Segments[0]).Revision));
        }

        public ResourceSearchResult Search(ChatSession session, string query, string kind, int limit, int maxCharsPerMatch)
        {
            var results = new List<ResourceSearchMatch>();
            var cursor = (string)null;
            do
            {
                var page = List(session, kind, cursor, 50);
                results.AddRange(page.Items.Where(item => item.Title.IndexOf(query ?? "", StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(item => new ResourceSearchMatch { Reference = item.Reference, Title = item.Title, Kind = item.Kind }));
                cursor = page.NextCursor;
            } while (cursor != null && results.Count < Math.Max(1, Math.Min(20, limit)));
            return new ResourceSearchResult { Query = query, Matches = results.Take(Math.Max(1, Math.Min(20, limit))).ToList() };
        }

        public ResourceReadSelection Read(ChatSession session, ResourceReadRequest request)
        {
            var address = Address(request.Reference.Uri);
            if (!string.IsNullOrEmpty(request.Representation) && request.Representation != "auto" && request.Representation != "text")
                throw Error("Catalog definitions expose a bounded text view.", "RESOURCE_VIEW_UNAVAILABLE");
            var binding = ResourceReadCursor.ReadBinding(request.Reference.Uri, "text");
            var position = ResourceReadCursor.ParseExact(request, binding);
            var exact = request.Reference.IsExact ? request.Reference :
                new ResourceRef(request.Reference.Uri, _catalogs.Current(address.Segments[0]).Revision);
            ResourceDescriptor descriptor;
            string text;
            if (address.Segments.Count == 1)
            { descriptor = DescribeRoot(exact); text = _catalogs.ReadPublic(exact); }
            else if (ToolKind(address.Segments[0]))
            {
                var root = new ResourceRef(ResourceUri.Create("catalog", address.Segments[0]), exact.Revision);
                if (address.Segments[2] == "documentation")
                {
                    var publication = FindBuiltIn(root, address.Segments[1]);
                    descriptor = DescribeTool(root, publication.Definition, true);
                    text = _catalogs.ReadDocumentation(publication.Documentation);
                }
                else
                {
                    var tool = FindTool(root, address.Segments[1]);
                    descriptor = DescribeTool(root, tool);
                    text = JsonConvert.SerializeObject(ToolSourceBodyDto.From(tool));
                }
            }
            else
            {
                var root = new ResourceRef(ResourceUri.Create("catalog", address.Segments[0]), exact.Revision);
                var skill = FindSkill(root, address.Segments[1]);
                var reference = address.Segments.Count == 4 ? FindReference(skill, address.Segments[3]) : null;
                descriptor = DescribeSkill(root, skill, reference);
                text = reference == null ? skill.BodyMarkdown ?? string.Empty : _catalogs.ReadReference(reference);
            }
            var payload = PayloadRef.FromBlob(_payloads.StoreText(text, descriptor.MimeType));
            if (position.Offset > text.Length) throw Error("Catalog cursor exceeds the exact snapshot.", "RESOURCE_CURSOR_INVALID");
            var count = Math.Min(text.Length - position.Offset, Math.Max(1, Math.Min(32000, request.MaxChars <= 0 ? 32000 : request.MaxChars)));
            var next = position.Offset + count;
            descriptor.Payload = payload;
            return new ResourceReadSelection { Result = new ResourceReadResult {
                Resource = descriptor, Representation = "text", Text = text.Substring(position.Offset, count),
                Offset = position.Offset, ReturnedCharacters = count, TotalCharacters = text.Length,
                Complete = next == text.Length, Truncated = next < text.Length, ContentSha256 = payload.Sha256,
                RawContentIncluded = true, CompleteViewPayload = payload,
                NextCursor = next < text.Length ? ResourceReadCursor.CreateRevisionBound(next, exact.Revision, binding) : null
            }, ResourceRefs = new[] { exact.Copy() } };
        }

        internal static ResourceRef SkillResource(SkillDefinition skill, string referencePath = null)
        {
            if (skill?.Publication == null || !skill.Publication.IsExact)
                throw Error("The skill has no active publication.", "RESOURCE_SNAPSHOT_UNAVAILABLE");
            var root = ResourceUri.Parse(skill.Publication.Uri).Segments[0];
            return new ResourceRef(referencePath == null ? ResourceUri.Create("catalog", root, skill.Id, "body") :
                ResourceUri.Create("catalog", root, skill.Id, "reference", ReferenceName(referencePath)), skill.Publication.Revision);
        }

        private ResourceAddress Address(string uri)
        {
            var parsed = ResourceUri.Parse(uri);
            if (parsed.Provider != Id || parsed.Segments.Count < 1 || !Kinds.Contains(parsed.Segments[0]) ||
                parsed.Segments.Count != 1 &&
                !(ToolKind(parsed.Segments[0]) && parsed.Segments.Count == 3 && (parsed.Segments[2] == "source" ||
                    parsed.Segments[0] == _catalogs.BuiltInToolsKind && parsed.Segments[2] == "documentation") ||
                  SkillKind(parsed.Segments[0]) && (parsed.Segments.Count == 3 && parsed.Segments[2] == "body" ||
                  parsed.Segments.Count == 4 && parsed.Segments[2] == "reference")))
                throw Error("Unknown catalog resource.", "RESOURCE_NOT_FOUND");
            return parsed;
        }

        private ResourceDescriptor Describe(ResourceRef exact)
        {
            var address = Address(exact.Uri);
            if (address.Segments.Count == 1) return DescribeRoot(exact);
            var root = new ResourceRef(ResourceUri.Create("catalog", address.Segments[0]), exact.Revision);
            if (ToolKind(address.Segments[0])) return DescribeTool(root, FindTool(root, address.Segments[1]), address.Segments[2] == "documentation");
            var skill = FindSkill(root, address.Segments[1]);
            return DescribeSkill(root, skill, address.Segments.Count == 4 ? FindReference(skill, address.Segments[3]) : null);
        }

        private SkillDefinition[] Skills(ResourceRef root)
        { return JsonConvert.DeserializeObject<SkillDefinition[]>(_catalogs.Read(root)); }
        private IEnumerable<ToolCatalogEntry> Tools(ResourceRef root)
        {
            return ResourceUri.Parse(root.Uri).Segments[0] == _catalogs.BuiltInToolsKind
                ? _catalogs.ReadBuiltInTools(root).Select(item => item.Definition)
                : JsonConvert.DeserializeObject<ToolCatalogEntry[]>(_catalogs.Read(root));
        }
        private ToolCatalogEntry FindTool(ResourceRef root, string id)
        {
            var values = Tools(root).Where(item => item.Id == id).Take(2).ToArray();
            if (values.Length != 1) throw Error("The exact tool source is unavailable or ambiguous.", "RESOURCE_SNAPSHOT_UNAVAILABLE");
            return values[0];
        }
        private BuiltInToolPublication FindBuiltIn(ResourceRef root, string id)
        {
            var values = _catalogs.ReadBuiltInTools(root).Where(item => item.Definition.Id == id).Take(2).ToArray();
            if (values.Length != 1) throw Error("The exact built-in tool is unavailable or ambiguous.", "RESOURCE_SNAPSHOT_UNAVAILABLE");
            return values[0];
        }
        private static ResourceDescriptor DescribeTool(ResourceRef root, ToolCatalogEntry tool, bool documentation = false)
        {
            var descriptor = new ResourceDescriptor { Reference = new ResourceRef(ResourceUri.Create("catalog",
                    ResourceUri.Parse(root.Uri).Segments[0], tool.Id, documentation ? "documentation" : "source"), root.Revision),
                Provider = "catalog", Title = tool.Id, Kind = documentation ? "tool-documentation" : "tool-source",
                MimeType = documentation ? "text/markdown" : "application/json", Mutable = false, Tracking = "strongly-tracked" };
            descriptor.Metadata["host"] = tool.Host;
            if (documentation) descriptor.Metadata["libraryRevision"] = ToolAuthoringService.LibraryRevision(tool);
            descriptor.Representations.Add("text"); descriptor.Capabilities.Add("read");
            descriptor.Dependencies.Add(new ResourceDependency(root, "text", ResourceCoverage.Whole(), "catalog-publication"));
            return descriptor;
        }
        private SkillDefinition FindSkill(ResourceRef root, string id)
        {
            var values = Skills(root).Where(item => item.Id == id).Take(2).ToArray();
            if (values.Length != 1) throw Error("The exact skill definition is unavailable or ambiguous.", "RESOURCE_SNAPSHOT_UNAVAILABLE");
            return values[0];
        }
        private static string ReferenceName(string path) { return (path ?? "").Replace('\\', '/').Split('/').Last(); }
        private static SkillReferenceMetadata FindReference(SkillDefinition skill, string name)
        {
            var matches = (skill.References ?? new List<SkillReferenceMetadata>()).Where(item => ReferenceName(item.Path) == name).Take(2).ToArray();
            if (matches.Length != 1) throw Error("The exact reference is unavailable or ambiguous.", "RESOURCE_SNAPSHOT_UNAVAILABLE");
            return matches[0];
        }
        private static ResourceDescriptor DescribeSkill(ResourceRef root, SkillDefinition skill, SkillReferenceMetadata reference)
        {
            skill.Publication = root.Copy();
            var descriptor = new ResourceDescriptor { Reference = SkillResource(skill, reference?.Path), Provider = "catalog",
                Title = skill.Id + (reference == null ? "" : " / " + reference.Path),
                Kind = reference == null ? "skill" : "skill-reference", MimeType = "text/markdown", Mutable = false,
                ByteLength = reference?.ByteLength, Tracking = "strongly-tracked" };
            descriptor.Representations.Add("text"); descriptor.Capabilities.Add("read");
            descriptor.Dependencies.Add(new ResourceDependency(root, "text", ResourceCoverage.Whole(), "catalog-publication"));
            return descriptor;
        }
        private static ResourceDescriptor DescribeRoot(ResourceRef exact)
        {
            var name = ResourceUri.Parse(exact.Uri).Segments[0];
            var result = new ResourceDescriptor { Reference = exact.Copy(), Provider = "catalog", Title = name,
                Kind = "catalog", MimeType = "application/json", Mutable = true, Tracking = "strongly-tracked" };
            result.Representations.Add("text"); result.Capabilities.Add("read");
            return result;
        }
        private static ResourceRequestException Error(string message, string code)
        { return new ResourceRequestException(message, code, false); }
    }
}
