using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Services
{
    internal sealed partial class LiveDocumentResourceProvider : ILiveOfficeResourceProvider
    {
        public const string ProviderName = "document";
        public const string DocumentKind = "office-document";
        public const string SelectionKind = "office-selection";
        private const int MaximumItems = 50;

        private readonly IOfficeApplicationAdapter _adapter;
        private readonly LiveOfficeResourceScope _scope;

        public LiveDocumentResourceProvider(IOfficeApplicationAdapter adapter)
        {
            _adapter = adapter ?? throw new ArgumentNullException("adapter");
            _scope = new LiveOfficeResourceScope(adapter);
        }

        public string Id { get { return ProviderName; } }

        public ResourceListPage List(ChatSession session, string kind, string cursor, int limit)
        {
            return _scope.Read(session, delegate
            {
                limit = Math.Max(1, Math.Min(MaximumItems, limit <= 0 ? 20 : limit));
                var items = new List<ResourceDescriptor>();
                if (string.IsNullOrWhiteSpace(kind) ||
                    string.Equals(kind, DocumentKind, StringComparison.OrdinalIgnoreCase))
                {
                    items.Add(Describe(session, "root"));
                }
                if (string.Equals(kind, SelectionKind, StringComparison.OrdinalIgnoreCase))
                {
                    items.Add(Describe(session, "selection"));
                }
                var cursorBinding = ResourceReadCursor.ListBinding(ProviderName, kind);
                var position = ResourceReadCursor.ParseRevisionBound(cursor, cursorBinding);
                var collectionRevision = ResourceReadCursor.CollectionRevision(items);
                ResourceReadCursor.ValidateContinuation(position, collectionRevision);
                ResourceReadCursor.ValidateCollectionOffset(position, items.Count);
                var offset = position.Offset;
                var selected = items.Skip(offset).Take(limit).ToList();
                var next = offset + selected.Count;
                return new ResourceListPage
                {
                    Items = selected,
                    Total = items.Count,
                    Cursor = ResourceReadCursor.CreateRevisionBound(offset, collectionRevision, cursorBinding),
                    NextCursor = next < items.Count
                        ? ResourceReadCursor.CreateRevisionBound(next, collectionRevision, cursorBinding)
                        : null,
                    Truncated = next < items.Count
                };
            });
        }

        public ResourceDescriptor Resolve(ChatSession session, string resourceUri)
        {
            return _scope.Read(session, delegate
            {
                string target;
                if (!TryParseUri(session, resourceUri, out target))
                {
                    throw new KeyNotFoundException("Live Office resource was not found: " + resourceUri);
                }
                return Describe(session, target);
            });
        }

        private ResourceDescriptor Describe(ChatSession session, string target)
        {
            var selection = string.Equals(target, "selection", StringComparison.Ordinal);
            var descriptor = new ResourceDescriptor
            {
                Reference = new ResourceRef(CreateUri(session, target)),
                Provider = ProviderName,
                Kind = selection ? SelectionKind : DocumentKind,
                Title = selection ? "Current Office selection" : _adapter.DocumentTitle ?? "Office document",
                MimeType = "text/plain; charset=utf-8",
                Mutable = true
            };
            descriptor.Representations.Add(ResourceRepresentations.Metadata);
            descriptor.Representations.Add(ResourceRepresentations.Structure);
            descriptor.Representations.Add(ResourceRepresentations.Text);
            descriptor.Metadata["host"] = _adapter.HostName ?? string.Empty;
            descriptor.Metadata["live"] = "true";
            descriptor.Metadata["target"] = target;
            if (!selection)
            {
                descriptor.Metadata["childKinds"] = SelectionKind;
                descriptor.Metadata["selectionDiscovery"] =
                    "List this provider with exact kind " + SelectionKind + ".";
            }
            return descriptor;
        }

        private string CreateUri(ChatSession session, string target)
        {
            return ResourceUri.Create(ProviderName, _scope.DocumentToken(session), target);
        }

        private bool TryParseUri(ChatSession session, string resourceUri, out string target)
        {
            target = null;
            ResourceAddress address;
            if (!ResourceUri.TryParse(resourceUri, out address) ||
                !string.Equals(address.Provider, ProviderName, StringComparison.Ordinal) ||
                address.Segments.Count != 2 ||
                !_scope.MatchesDocumentToken(session, address.Segments[0]))
            {
                return false;
            }
            if (!string.Equals(address.Segments[1], "root", StringComparison.Ordinal) &&
                !string.Equals(address.Segments[1], "selection", StringComparison.Ordinal))
            {
                return false;
            }
            target = address.Segments[1];
            return true;
        }

    }
}
