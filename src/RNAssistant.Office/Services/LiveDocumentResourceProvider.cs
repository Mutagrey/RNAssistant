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
        private readonly RNAssistant.Core.Storage.ChatBlobStore _payloads;
        private bool IsExactSource(string target) { return IsOutlook || IsWord || (IsPowerPoint && target != "selection"); }

        public LiveDocumentResourceProvider(IOfficeApplicationAdapter adapter, RNAssistant.Core.Storage.ChatBlobStore payloads = null)
        {
            _adapter = adapter ?? throw new ArgumentNullException("adapter");
            _scope = new LiveOfficeResourceScope(adapter);
            _payloads = payloads;
            if (IsOutlook) _outlook = (adapter as RNAssistant.Office.Domains.Outlook.IOutlookBackendProvider)?.OutlookBackend;
            if (IsPowerPoint) _powerPoint = (adapter as RNAssistant.Office.Domains.PowerPoint.IPowerPointBackendProvider)?.PowerPointBackend;
            if (IsWord) _word = (adapter as RNAssistant.Office.Domains.Word.IWordBackendProvider)?.WordBackend;
        }

        public string Id { get { return ProviderName; } }

        public ResourceListPage List(ChatSession session, string kind, string cursor, int limit)
        {
            return _scope.Read(session, delegate
            {
                limit = Math.Max(1, Math.Min(MaximumItems, limit <= 0 ? 20 : limit));
                if (IsOutlook && kind == OutlookMailKind) return ListOutlookMail(session, cursor, limit);
                var items = new List<ResourceDescriptor>();
                if (IsPowerPoint && kind == PowerPointSearchKind)
                    items.AddRange(new[] { "deck", "deck+notes" }.Select(scope => Describe(session, "search-" + scope)));
                if (IsWord && kind == WordSearchKind)
                    items.AddRange(new[] { "main", "selection", "all" }.Select(scope => Describe(session, "stories-" + scope)));
                if (IsOutlook && (string.IsNullOrWhiteSpace(kind) || kind == OutlookCollectionKind))
                    items.Add(DescribeOutlookCollection(session));
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
            if (IsOutlook && target == OutlookCollectionKey) return DescribeOutlookCollection(session);
            if (IsPowerPoint && IsPowerPointSearch(target))
            {
                var search = new ResourceDescriptor { Reference = new ResourceRef(CreateUri(session, target)),
                    Provider = ProviderName, Kind = PowerPointSearchKind, Title = target.Substring(7).Replace("slide-", "slide:"),
                    Mutable = true, MimeType = "application/json", Tracking = "externally-observed" };
                search.Representations.AddRange(new[] { "metadata", "text" });
                search.Metadata["host"] = "PowerPoint";
                return search;
            }
            if (IsWord && IsWordSearch(target))
            {
                var search = new ResourceDescriptor { Reference = new ResourceRef(CreateUri(session, target)),
                    Provider = ProviderName, Kind = WordSearchKind, Title = target.Substring(8),
                    Mutable = true, MimeType = "application/json", Tracking = "externally-observed" };
                search.Representations.AddRange(new[] { "metadata", "text" });
                search.Metadata["host"] = "Word";
                return search;
            }
            string outlookEntryId;
            if (IsOutlook && TryOutlookMailKey(target, out outlookEntryId))
            {
                var mail = CaptureOutlookMail(target, false).Mail;
                return DescribeOutlookMail(session, target, new RNAssistant.Office.Domains.Outlook.OutlookMailSummarySnapshot {
                    EntryId = mail.EntryId, Subject = mail.Subject, Sender = mail.Sender, Received = mail.Received });
            }
            var selection = string.Equals(target, "selection", StringComparison.Ordinal);
            var powerPointSlide = IsPowerPoint && IsPowerPointSlide(target);
            var wordRange = IsWord && target.StartsWith("range-", StringComparison.Ordinal);
            var descriptor = new ResourceDescriptor
            {
                Reference = new ResourceRef(CreateUri(session, target)),
                Provider = ProviderName,
                Kind = powerPointSlide ? PowerPointSlideKind : wordRange ? WordRangeKind : selection ? SelectionKind : DocumentKind,
                Title = IsOutlook ? (selection ? "Current Office selection" : "Current Outlook mail") : powerPointSlide ? target.Substring(6) : wordRange ? target.Substring(6).Replace('-', ':') : selection ? "Current Office selection" : _adapter.DocumentTitle ?? "Office document",
                MimeType = "text/plain; charset=utf-8",
                Mutable = true
            };
            descriptor.Representations.Add(ResourceRepresentations.Metadata);
            descriptor.Representations.Add(ResourceRepresentations.Structure);
            descriptor.Representations.Add(ResourceRepresentations.Text);
            if (IsOutlook || (IsPowerPoint && !selection)) descriptor.Representations.Add(ResourceRepresentations.Source);
            if (IsOutlook) descriptor.Metadata["sourceTarget"] = "Current selected/open mail; use outlook-mail children for durable mail identity.";
            if (IsPowerPoint) descriptor.Metadata["slideTargetFormat"] = "PowerPoint slide: N (one-based); source includes text and notes";
            descriptor.Metadata["host"] = _adapter.HostName ?? string.Empty;
            descriptor.Metadata["live"] = "true";
            descriptor.Metadata["target"] = target;
            if (IsWord) descriptor.Metadata["rangeTargetFormat"] = "Word range: start:end (zero-based, end exclusive; main story)";
            if (!selection)
            {
                descriptor.Metadata["childKinds"] = IsOutlook ? SelectionKind + "," + OutlookMailKind : SelectionKind;
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
            string outlookEntryId;
            if (!string.Equals(address.Segments[1], "root", StringComparison.Ordinal) &&
                !string.Equals(address.Segments[1], "selection", StringComparison.Ordinal) &&
                !(IsWord && IsWordRange(address.Segments[1])) &&
                !(IsWord && IsWordSearch(address.Segments[1])) &&
                !(IsPowerPoint && IsPowerPointSlide(address.Segments[1])) &&
                !(IsPowerPoint && IsPowerPointSearch(address.Segments[1])) &&
                !(IsOutlook && address.Segments[1] == OutlookCollectionKey) &&
                !(IsOutlook && TryOutlookMailKey(address.Segments[1], out outlookEntryId)))
            {
                return false;
            }
            target = address.Segments[1];
            return true;
        }

    }
}
