using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Services
{
    internal sealed partial class LiveDocumentResourceProvider
    {
        private const int MaximumMaterializedCharacters = 1000000;
        private const int MaximumSearchResults = 20;
        private const int MaximumSnippetCharacters = 2000;

        public ResourceReadSelection Read(ChatSession session, ResourceReadRequest request)
        {
            return _scope.Read(session, delegate
            {
                var resourceUri = request == null || request.Reference == null
                    ? string.Empty
                    : request.Reference.Uri;
                string target;
                if (!TryParseUri(session, resourceUri, out target))
                {
                    throw new KeyNotFoundException("Live Office resource was not found: " + resourceUri);
                }
                var representation = NormalizeRepresentation(request == null ? null : request.Representation, target);
                if (representation == ResourceRepresentations.Metadata)
                {
                    ResourceReadCursor.RejectCursor(request);
                    return new ResourceReadSelection
                    {
                        Result = new ResourceReadResult
                        {
                            Resource = Describe(session, target),
                            Representation = ResourceRepresentations.Metadata,
                            Complete = true
                        },
                        ResourceRefs = new[] { new ResourceRef(resourceUri) }
                    };
                }

                var content = IsOutlook ? ReadOutlookSource(target, representation)
                    : representation == ResourceRepresentations.Source
                    ? ReadPowerPointSource(target, true)
                    : representation == ResourceRepresentations.Structure
                    ? ReadStructure(target)
                    : ReadText(target);
                var sourceTruncated = !IsExactSource(target) && content.Length >= MaximumMaterializedCharacters;
                var cursorBinding = ResourceReadCursor.ReadBinding(resourceUri, representation);
                var position = ResourceReadCursor.ParseRevisionBound(request, cursorBinding);
                return SelectText(
                    session,
                    target,
                    representation,
                    content,
                    sourceTruncated,
                    request,
                    position,
                    cursorBinding);
            });
        }

        public ResourceSearchResult Search(
            ChatSession session,
            string query,
            string kind,
            int limit,
            int maxCharsPerMatch)
        {
            return _scope.Read(session, delegate
            {
                query = (query ?? string.Empty).Trim();
                if (query.Length == 0)
                {
                    throw new ResourceRequestException(
                        "Resource search query is required.",
                        "resource_query_required",
                        true);
                }
                limit = Math.Max(1, Math.Min(MaximumSearchResults, limit <= 0 ? 10 : limit));
                maxCharsPerMatch = Math.Max(128, Math.Min(
                    MaximumSnippetCharacters,
                    maxCharsPerMatch <= 0 ? 600 : maxCharsPerMatch));
                var result = new ResourceSearchResult { Query = query };
                string target;
                string resultKind;
                if (string.IsNullOrWhiteSpace(kind) ||
                    string.Equals(kind, DocumentKind, StringComparison.OrdinalIgnoreCase))
                {
                    target = "root";
                    resultKind = DocumentKind;
                }
                else if (string.Equals(kind, SelectionKind, StringComparison.OrdinalIgnoreCase))
                {
                    target = "selection";
                    resultKind = SelectionKind;
                }
                else
                {
                    return result;
                }
                var content = ReadText(target);
                var contentSha256 = TextPatternEngine.Sha256(content);
                result.ScannedCharacters = content.Length;
                result.ScanTruncated = !IsExactSource(target) && content.Length >= MaximumMaterializedCharacters;
                var index = 0;
                while (result.Matches.Count < limit &&
                    (index = content.IndexOf(query, index, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    var start = Math.Max(0, index - maxCharsPerMatch / 3);
                    result.Matches.Add(new ResourceSearchMatch
                    {
                        Reference = new ResourceRef(CreateUri(session, target), contentSha256),
                        Kind = resultKind,
                        Title = target == "selection"
                            ? "Current Office selection"
                            : _adapter.DocumentTitle ?? "Office document",
                        Representation = ResourceRepresentations.Text,
                        MatchOffset = index,
                        MatchLength = query.Length,
                        SnippetOffset = start,
                        Snippet = content.Substring(start, Math.Min(maxCharsPerMatch, content.Length - start))
                    });
                    index += Math.Max(1, query.Length);
                }
                if (result.Matches.Count >= limit &&
                    content.IndexOf(query, index, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    result.ScanTruncated = true;
                }
                return result;
            });
        }

        private string ReadText(string target)
        {
            if (IsOutlook) return ReadOutlookSource(target, ResourceRepresentations.Text);
            if (IsWord) return ReadWordText(target);
            if (IsPowerPoint && target != "selection") return ReadPowerPointSource(target, false);
            if (string.Equals(target, "selection", StringComparison.Ordinal))
            {
                var selection = _adapter.CaptureSelectionContext("selection", MaximumMaterializedCharacters);
                if (selection == null)
                {
                    throw new ResourceRequestException(
                        "The current Office selection is unavailable.",
                        "office_selection_unavailable",
                        true);
                }
                return selection.Text ?? string.Empty;
            }
            return _adapter.GetDocumentSnapshot(MaximumMaterializedCharacters) ?? string.Empty;
        }

        private string ReadStructure(string target)
        {
            if (IsPowerPoint && IsPowerPointSlide(target))
                return JsonConvert.SerializeObject(new { slideIndex = int.Parse(target.Substring(6), System.Globalization.CultureInfo.InvariantCulture) });
            if (IsWord && target.StartsWith("range-", StringComparison.Ordinal))
            {
                var range = WordRange(target);
                return JsonConvert.SerializeObject(new { source = "range", start = range.Start, end = range.End });
            }
            if (string.Equals(target, "selection", StringComparison.Ordinal))
            {
                var selection = _adapter.CaptureSelectionContext("reference", MaximumMaterializedCharacters);
                if (selection == null)
                {
                    throw new ResourceRequestException(
                        "The current Office selection is unavailable.",
                        "office_selection_unavailable",
                        true);
                }
                JToken details;
                try
                {
                    details = JToken.Parse(string.IsNullOrWhiteSpace(selection.DetailsJson)
                        ? "{}"
                        : selection.DetailsJson);
                }
                catch (JsonException)
                {
                    details = new JObject();
                }
                return JsonConvert.SerializeObject(new
                {
                    type = "rnassistant.officeSelection",
                    host = _adapter.HostName,
                    selection.Kind,
                    selection.Title,
                    selection.Reference,
                    selection.Source,
                    details
                });
            }
            var contextProvider = _adapter as IOfficeContextProvider;
            var context = contextProvider == null ? null : contextProvider.GetOfficeContext();
            return JsonConvert.SerializeObject(new
            {
                type = "rnassistant.officeDocument",
                host = _adapter.HostName,
                title = _adapter.DocumentTitle,
                container = context == null ? null : context.ContainerName,
                selectionAddress = context == null ? null : context.SelectionAddress
            });
        }

        private ResourceReadSelection SelectText(
            ChatSession session,
            string target,
            string representation,
            string content,
            bool sourceTruncated,
            ResourceReadRequest request,
            ResourceReadPosition position,
            string cursorBinding)
        {
            content = content ?? string.Empty;
            var offset = position == null ? 0 : position.Offset;
            var maxChars = request == null ? 0 : request.MaxChars;
            maxChars = Math.Max(ResourceReadRequest.MinimumCharacters, Math.Min(
                ResourceReadRequest.MaximumCharacters,
                maxChars <= 0 ? ResourceReadRequest.DefaultCharacters : maxChars));
            var contentSha256 = TextPatternEngine.Sha256(content);
            ResourceReadCursor.ValidateLive(request, position, contentSha256);
            if (offset > content.Length)
            {
                throw new ResourceRequestException(
                    "Resource read cursor exceeds the available live representation. Omit cursor and restart this exact resource from the first chunk.",
                    "resource_cursor_invalid",
                    false);
            }
            var length = Math.Min(maxChars, content.Length - offset);
            var next = offset + length;
            var uri = CreateUri(session, target);
            var complete = next >= content.Length && !sourceTruncated;
            var descriptor = Describe(session, target);
            descriptor.ContentSha256 = contentSha256;
            descriptor.Reference = new ResourceRef(descriptor.Reference.Uri, contentSha256);
            return new ResourceReadSelection
            {
                Result = new ResourceReadResult
                {
                    Resource = descriptor,
                    Representation = representation,
                    Text = content.Substring(offset, length),
                    ContentSha256 = contentSha256,
                    CompleteViewPayload = IsExactSource(target) && !sourceTruncated && _payloads != null
                        ? PayloadRef.FromBlob(_payloads.StoreText(content, descriptor.MimeType)) : null,
                    Offset = offset,
                    ReturnedCharacters = length,
                    TotalCharacters = content.Length,
                    NextCursor = next < content.Length
                        ? ResourceReadCursor.CreateRevisionBound(next, contentSha256, cursorBinding)
                        : null,
                    Complete = complete,
                    Truncated = !complete,
                    RawContentIncluded = true
                },
                ResourceRefs = new[] { new ResourceRef(uri, contentSha256) }
            };
        }

        private string NormalizeRepresentation(string value, string target)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (value.Length == 0 || value == "auto") return ResourceRepresentations.Text;
            if (IsOutlook && target == OutlookCollectionKey && (value == ResourceRepresentations.Structure || value == ResourceRepresentations.Source))
                throw new ResourceRequestException("Read collection text metadata or records at $.messages.", "RESOURCE_VIEW_UNSUPPORTED", false);
            if (value == ResourceRepresentations.Source && (IsOutlook || (IsPowerPoint && target != "selection"))) return value;
            if (value == ResourceRepresentations.Metadata ||
                value == ResourceRepresentations.Structure ||
                value == ResourceRepresentations.Text) return value;
            throw new ResourceRequestException(
                "Live Office representation is unavailable: " + value + ".",
                "resource_representation_unavailable",
                true);
        }
    }
}
