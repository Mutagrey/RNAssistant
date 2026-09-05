using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office.Services
{
    // Provider for logical resource revisions already materialized in the canonical
    // authority/revision journal and CAS. It is not another snapshot store.
    internal sealed class ResourceStateProvider : IResourceProvider
    {
        private readonly ResourceAuthorityService _authority;
        private readonly IResourceRevisionStore _revisions;
        private readonly ChatBlobStore _payloads;
        public string Id { get { return "state"; } }
        internal ResourceStateProvider(ResourceAuthorityService authority, ChatBlobStore payloads)
        { _authority = authority; _revisions = (IResourceRevisionStore)authority.Store; _payloads = payloads; }

        internal static ResourceIdentity Identity(ResourceAuthorityScopeId scope, string name)
        { return new ResourceIdentity(ResourceUri.Create("state", scope.Kind, scope.Id, name)); }

        internal static SchemaRegistrySnapshot CaptureSchemas(ResourceAuthoritySnapshotSet authority)
        {
            return new SchemaRegistrySnapshot(authority.Snapshots.Values.SelectMany(snapshot => snapshot.Heads.Values)
                .Where(head => head.Knowledge == HeadKnowledge.Known && head.Identity.Uri.StartsWith("rna://state/conversation/", StringComparison.Ordinal))
                .Where(head => { var name = ResourceUri.Parse(head.Identity.Uri).Segments.Last();
                    return name.StartsWith("schema-published-", StringComparison.Ordinal); })
                .Select(head => head.Revision));
        }

        public ResourceListPage List(ChatSession session, string kind, string cursor, int limit)
        {
            var scope = _authority.Scope(session, false);
            var snapshot = _authority.CaptureMany(new[] { scope }).Get(scope);
            var items = snapshot.Heads.Values.Where(head => head.Knowledge != HeadKnowledge.Unavailable &&
                head.Identity.Uri.StartsWith("rna://state/", StringComparison.Ordinal))
                .Select(head => Describe(head.Identity.Uri, head)).Where(item => string.IsNullOrWhiteSpace(kind) || item.Kind == kind).ToList();
            var binding = ResourceReadCursor.ListBinding(Id, kind);
            var position = ResourceReadCursor.ParseRevisionBound(cursor, binding);
            var revision = ResourceReadCursor.CollectionRevision(items);
            ResourceReadCursor.ValidateContinuation(position, revision);
            var selected = items.Skip(position.Offset).Take(Math.Max(1, Math.Min(50, limit))).ToList();
            return new ResourceListPage { Items = selected, Total = items.Count, Truncated = position.Offset + selected.Count < items.Count,
                NextCursor = position.Offset + selected.Count < items.Count ? ResourceReadCursor.CreateRevisionBound(position.Offset + selected.Count, revision, binding) : null };
        }
        public ResourceDescriptor Resolve(ChatSession session, string uri)
        {
            var scope = Scope(session, uri);
            return Describe(uri, _authority.Store.GetHead(scope, new ResourceIdentity(uri)));
        }
        public ResourceSearchResult Search(ChatSession session, string query, string kind, int limit, int maxCharsPerMatch)
        {
            return new ResourceSearchResult { Query = query, Matches = List(session, kind, null, 50).Items
                .Where(item => item.Title.IndexOf(query ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0).Take(Math.Min(20, limit))
                .Select(item => new ResourceSearchMatch { Reference = item.Reference, Title = item.Title, Kind = item.Kind }).ToList() };
        }
        public ResourceReadSelection Read(ChatSession session, ResourceReadRequest request)
        {
            var scope = Scope(session, request.Reference.Uri);
            var descriptor = Resolve(session, request.Reference.Uri);
            var exact = request.Reference.IsExact ? request.Reference : descriptor.Reference;
            if (exact == null || !exact.IsExact) throw Error("RESOURCE_HEAD_UNKNOWN", "The current logical resource has no captured revision.");
            var metadata = _revisions.GetRevision(scope, exact);
            var view = string.IsNullOrWhiteSpace(request.Representation) || request.Representation == "auto" ? "text" : request.Representation;
            var captured = _revisions.GetView(scope, exact, view);
            var payload = captured?.Payload ?? (view == "text" ? metadata?.Payload : null);
            if (payload == null) throw Error("RESOURCE_VIEW_UNAVAILABLE", "This exact revision does not have the requested view.");
            if (payload.ByteLength > 8L * 1024 * 1024) throw Error("RESOURCE_BATCH_TOO_LARGE", "The snapshot exceeds this view's materialization bound.");
            var text = _payloads.ReadText(payload.ToBlobReference());
            if (text == null) throw Error("RESOURCE_SNAPSHOT_UNAVAILABLE", "The exact snapshot payload is unavailable.");
            var binding = ResourceReadCursor.ReadBinding(exact.Uri, view);
            var position = ResourceReadCursor.ParseRevisionBound(request, binding);
            var hash = captured?.ContentSha256 ?? metadata.ContentSha256;
            ResourceReadCursor.ValidateContinuation(position, hash);
            if (position.Offset > text.Length) throw Error("RESOURCE_CURSOR_INVALID", "The cursor is outside this exact view.");
            var count = Math.Min(text.Length - position.Offset, Math.Max(1, Math.Min(32000, request.MaxChars <= 0 ? 32000 : request.MaxChars)));
            var next = position.Offset + count;
            descriptor.Reference = exact.Copy(); descriptor.Payload = payload; descriptor.MimeType = payload.ContentType;
            descriptor.Dependencies = metadata.Dependencies.ToList();
            return new ResourceReadSelection { Result = new ResourceReadResult { Resource = descriptor, Representation = view,
                Text = text.Substring(position.Offset, count), ContentSha256 = hash, Offset = position.Offset,
                ReturnedCharacters = count, TotalCharacters = text.Length, Complete = next == text.Length, Truncated = next < text.Length,
                NextCursor = next < text.Length ? ResourceReadCursor.CreateRevisionBound(next, hash, binding) : null }, ResourceRefs = new[] { exact.Copy() } };
        }
        private ResourceAuthorityScopeId Scope(ChatSession session, string uri)
        {
            var address = ResourceUri.Parse(uri);
            if (address.Provider != Id || address.Segments.Count != 3 || address.Segments[0] != "conversation" || address.Segments[1] != session?.Id)
                throw Error("RESOURCE_ACCESS_DENIED", "The logical resource belongs to another scope.");
            return _authority.Scope(session, false);
        }
        private ResourceDescriptor Describe(string uri, ResourceHeadState head)
        {
            var name = ResourceUri.Parse(uri).Segments.Last();
            var descriptor = new ResourceDescriptor { Reference = head?.Revision?.Copy() ?? new ResourceRef(uri), Provider = Id,
                Kind = name, Title = name.Replace('-', ' '), Mutable = true, Tracking = "strongly-tracked" };
            descriptor.Representations.Add("text");
            descriptor.Capabilities.Add("read");
            return descriptor;
        }
        private static ResourceRequestException Error(string code, string message) { return new ResourceRequestException(message, code, false); }
    }
}
