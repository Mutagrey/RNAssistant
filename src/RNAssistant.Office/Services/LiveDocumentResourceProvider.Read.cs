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
        private const int MaximumReadCharacters = 32000;
        private const int MaximumMaterializedCharacters = 1000000;
        private const int MaximumSearchResults = 20;
        private const int MaximumSnippetCharacters = 2000;

        public ResourceReadSelection Read(
            ChatSession session,
            string resourceUri,
            string representation,
            int offset,
            int maxChars)
        {
            return _scope.Read(session, delegate
            {
                string target;
                if (!TryParseUri(session, resourceUri, out target))
                {
                    throw new KeyNotFoundException("Live Office resource was not found: " + resourceUri);
                }
                representation = NormalizeRepresentation(representation);
                if (representation == ResourceRepresentations.Metadata)
                {
                    return new ResourceReadSelection
                    {
                        Result = new ResourceReadResult
                        {
                            Resource = Describe(session, target),
                            Representation = ResourceRepresentations.Metadata,
                            Complete = true
                        },
                        ResourceUris = new[] { resourceUri }
                    };
                }

                var content = representation == ResourceRepresentations.Structure
                    ? ReadStructure(target)
                    : ReadText(target);
                var sourceTruncated = content.Length >= MaximumMaterializedCharacters;
                return SelectText(
                    session,
                    target,
                    representation,
                    content,
                    sourceTruncated,
                    offset,
                    maxChars);
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
                result.ScannedCharacters = content.Length;
                result.ScanTruncated = content.Length >= MaximumMaterializedCharacters;
                var index = 0;
                while (result.Matches.Count < limit &&
                    (index = content.IndexOf(query, index, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    var start = Math.Max(0, index - maxCharsPerMatch / 3);
                    result.Matches.Add(new ResourceSearchMatch
                    {
                        Reference = new ResourceRef(CreateUri(session, target)),
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
            int offset,
            int maxChars)
        {
            content = content ?? string.Empty;
            offset = Math.Max(0, offset);
            maxChars = Math.Max(128, Math.Min(
                MaximumReadCharacters,
                maxChars <= 0 ? 8000 : maxChars));
            if (offset > content.Length)
            {
                throw new ResourceRequestException(
                    "Resource read offset exceeds the available live representation.",
                    "resource_cursor_invalid",
                    true);
            }
            var length = Math.Min(maxChars, content.Length - offset);
            var next = offset + length;
            var uri = CreateUri(session, target);
            var complete = next >= content.Length && !sourceTruncated;
            var contentSha256 = TextPatternEngine.Sha256(content);
            var descriptor = Describe(session, target);
            descriptor.ContentSha256 = contentSha256;
            descriptor.Reference.Revision = contentSha256;
            return new ResourceReadSelection
            {
                Result = new ResourceReadResult
                {
                    Resource = descriptor,
                    Representation = representation,
                    Text = content.Substring(offset, length),
                    ContentSha256 = contentSha256,
                    Offset = offset,
                    ReturnedCharacters = length,
                    TotalCharacters = content.Length,
                    NextCursor = next < content.Length ? next.ToString() : null,
                    Complete = complete,
                    Truncated = !complete,
                    RawContentIncluded = true
                },
                ResourceUris = new[] { uri }
            };
        }

        private static string NormalizeRepresentation(string value)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (value.Length == 0 || value == "auto") return ResourceRepresentations.Text;
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
