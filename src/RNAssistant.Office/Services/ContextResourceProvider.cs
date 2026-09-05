using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office.Services
{
    // Attached notes are discovery metadata only. Bodies, history and currentness
    // come from canonical scoped revisions; display text is never a read source.
    internal sealed class ContextResourceProvider : IResourceProvider
    {
        internal const string DataKind = "context-data";
        internal const string ObservationKind = "context-observation";
        private readonly ResourceAuthorityService _authority;
        private readonly ResourceSnapshotReadService _reads;
        public string Id { get { return "context"; } }
        internal ContextResourceProvider(ResourceAuthorityService authority, ChatBlobStore payloads)
        { _authority = authority; _reads = new ResourceSnapshotReadService(authority, payloads); }

        public ResourceListPage List(ChatSession session, string kind, string cursor, int limit)
        {
            var notes = (session?.Context?.Notes ?? new List<ContextNote>()).Where(IsResourceNote).ToArray();
            var scopes = notes.Select(note => Scope(session, note.Evidence.Resource.Uri)).Distinct().ToArray();
            var snapshot = _authority.CaptureMany(scopes);
            var items = notes.Select(note => {
                var scope = Scope(session, note.Evidence.Resource.Uri);
                if ((scope.Kind == "document") != (note.Role == ContextNoteRole.OfficeObservation) || !scope.Equals(note.Evidence.ScopeId))
                    throw Error("RESOURCE_ACCESS_DENIED", "The context role and resource scope do not agree.");
                var head = snapshot.Get(scope).GetHead(note.Evidence.Resource.Identity);
                return head == null || head.Knowledge == HeadKnowledge.Unavailable ? null : Describe(session, note.Evidence.Resource.Uri, head);
            }).Where(item => item != null && (string.IsNullOrEmpty(kind) || item.Kind == kind))
                .GroupBy(item => item.Reference.Identity.Uri, StringComparer.Ordinal).Select(group => group.First())
                .OrderBy(item => item.Reference.Uri, StringComparer.Ordinal).ToList();
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
            var scope = Scope(session, uri);
            return Describe(session, uri, _authority.CaptureMany(new[] { scope }).Get(scope).GetHead(new ResourceIdentity(uri)));
        }

        public ResourceSearchResult Search(ChatSession session, string query, string kind, int limit, int maxCharsPerMatch)
        {
            var matches = new List<ResourceSearchMatch>();
            string cursor = null;
            var maximum = Math.Max(1, Math.Min(20, limit));
            do
            {
                var page = List(session, kind, cursor, 50);
                matches.AddRange(page.Items.Where(item => item.Title.IndexOf(query ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(item => new ResourceSearchMatch { Reference = item.Reference, Title = item.Title, Kind = item.Kind }));
                cursor = page.NextCursor;
            } while (cursor != null && matches.Count < maximum);
            return new ResourceSearchResult { Query = query, Matches = matches.Take(maximum).ToList() };
        }

        public ResourceReadSelection Read(ChatSession session, ResourceReadRequest request)
        {
            var scope = Scope(session, request.Reference.Uri);
            return _reads.Read(session, scope, Resolve(session, request.Reference.Uri), request);
        }

        private ResourceAuthorityScopeId Scope(ChatSession session, string uri)
        {
            if (ResourceUri.Parse(uri).Provider != Id) throw Error("RESOURCE_ACCESS_DENIED", "A context resource is required.");
            return _authority.ScopeFor(session, new ResourceRef(uri), false);
        }

        private ResourceDescriptor Describe(ChatSession session, string uri, ResourceHeadState head)
        {
            var scope = Scope(session, uri);
            var note = (session.Context?.Notes ?? new List<ContextNote>()).FirstOrDefault(item => IsResourceNote(item) && item.Evidence.Resource.Uri == uri);
            var descriptor = new ResourceDescriptor { Provider = Id, Reference = head?.Revision?.Copy() ?? new ResourceRef(uri),
                Kind = scope.Kind == "document" ? ObservationKind : DataKind, Mutable = scope.Kind == "document",
                Title = ContextNormalizer.TrimForContext(note?.Title ?? (scope.Kind == "document" ? "Office observation" : "Context data"), 240),
                MimeType = "text/plain; charset=utf-8", Tracking = "strongly-tracked" };
            descriptor.Capabilities.Add("read"); descriptor.Representations.Add("text");
            return descriptor;
        }

        private static bool IsResourceNote(ContextNote note)
        { return note != null && (note.Role == ContextNoteRole.SuppliedData || note.Role == ContextNoteRole.OfficeObservation) && note.Evidence?.Resource?.IsExact == true; }
        private static ResourceRequestException Error(string code, string message)
        { return new ResourceRequestException(message, code, false); }
    }
}
