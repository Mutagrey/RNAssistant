using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office.Services
{
    internal sealed partial class ResourceGatewayService
    {
        private const int InternalReadCharacters = ResourceReadRequest.MaximumCharacters;
        private const int MaximumWholeReadCharacters = ChatArtifactLimits.MaximumTextCharacters;
        private const int MaximumWholeReadPages = 128;

        // A semantic line selection pins the same whole snapshot used by the
        // gateway. Only its exact excerpt becomes model observation evidence.
        internal ResourceReadSelection ReadLines(ChatSession session, ResourceRef reference,
            string representation, int startLine, int lineCount)
        {
            if ((representation != "source" && representation != "text") || startLine < 1 || lineCount < 1 || lineCount > 500)
                throw new ResourceRequestException("Use source/text with 1-based startLine and lineCount between 1 and 500.", "resource_lines_invalid", false);
            var selection = ReadWhole(session, reference, representation);
            var result = selection.Result;
            var text = result.Text;
            if (text == null) throw new ResourceRequestException("This representation has no line-addressable text.", "resource_lines_unsupported", false);
            var starts = new List<int> { 0 };
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] != '\r' && text[i] != '\n') continue;
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                starts.Add(i + 1);
            }
            if (startLine > starts.Count)
                throw new ResourceRequestException("startLine is past the end of this text (" + starts.Count + " lines).", "resource_lines_invalid", false);
            var start = starts[startLine - 1];
            var next = Math.Min((long)starts.Count, (long)startLine - 1 + lineCount);
            var end = next < starts.Count ? starts[(int)next] : text.Length;
            if (end - start > InternalReadCharacters)
                throw new ResourceRequestException("Selected lines exceed 32000 characters. Reduce lineCount or find a narrower snippet target.", "resource_lines_too_large", false);
            result.Text = text.Substring(start, end - start);
            result.Offset = start;
            result.ReturnedCharacters = end - start;
            result.Coverage = new ResourceCoverage(ResourceCoverageKinds.CharacterRange, start: start, end: end);
            result.Complete = false;
            result.Truncated = true;
            result.CompleteViewPayload = null;
            return selection;
        }

        internal ResourceReadSelection ReadWhole(
            ChatSession session, ResourceRef reference,
            string representation)
        {
            var cursor = string.Empty;
            var seenCursors = new HashSet<string>(StringComparer.Ordinal);
            var text = new StringBuilder();
            var references = new List<ResourceRef>();
            var attachments = new List<ChatAttachment>();
            var related = new List<ResourceRef>();
            ResourceReadResult first = null;
            ResourceReadResult last = null;
            var hydratedForNextModelStep = false;
            var rawContentIncluded = false;
            var pages = 0;
            while (true)
            {
                pages++;
                if (pages > MaximumWholeReadPages)
                {
                    throw new ResourceRequestException(
                        "The provider exceeded the bounded internal page count. No partial content was returned to the model.",
                        "resource_whole_read_incomplete",
                        false);
                }
                var page = Read(
                    session,
                    new ResourceReadRequest
                    {
                        Reference = reference,
                        Representation = first == null
                            ? representation
                            : first.Representation,
                        Cursor = cursor,
                        MaxChars = InternalReadCharacters
                    });
                if (page == null || page.Result == null)
                {
                    throw new InvalidOperationException(
                        "Resource provider returned no read result.");
                }
                var result = page.Result;
                if (first == null)
                {
                    first = result;
                    reference = result.Resource.Reference.Copy();
                }
                else if (!string.Equals(
                    first.Representation,
                    result.Representation,
                    StringComparison.Ordinal))
                {
                    throw WholeReadFailure(
                        "Resource provider changed representation during one whole read.");
                }
                if (first.Resource.Reference.Revision != result.Resource.Reference.Revision)
                    throw WholeReadFailure("Resource provider crossed exact revisions during one whole read.");
                var pageText = result.Text ?? string.Empty;
                if (result.Offset != text.Length ||
                    result.ReturnedCharacters != pageText.Length ||
                    result.TotalCharacters < 0 ||
                    first.TotalCharacters != result.TotalCharacters)
                {
                    throw WholeReadFailure(
                        "Resource provider returned a non-contiguous whole-read page.");
                }
                if (result.TotalCharacters > MaximumWholeReadCharacters ||
                    text.Length + pageText.Length > MaximumWholeReadCharacters)
                {
                    throw new ResourceRequestException(
                        "The complete resource representation exceeds the " +
                        MaximumWholeReadCharacters +
                        "-character whole-read safety bound. Use a narrower semantic resource or a domain-specific read.",
                        "resource_whole_read_too_large",
                        false);
                }
                text.Append(pageText);
                references.AddRange(page.ResourceRefs ?? new ResourceRef[0]);
                attachments.AddRange(page.ModelAttachments ?? new ChatAttachment[0]);
                related.AddRange(result.Related ?? new List<ResourceRef>());
                hydratedForNextModelStep = hydratedForNextModelStep ||
                    result.HydratedForNextModelStep;
                rawContentIncluded = rawContentIncluded || result.RawContentIncluded;
                last = result;
                if (result.Complete)
                {
                    if (result.Truncated ||
                        !string.IsNullOrWhiteSpace(result.NextCursor) ||
                        text.Length != result.TotalCharacters)
                    {
                        throw WholeReadFailure(
                            "Resource provider marked an incomplete representation as complete.");
                    }
                    break;
                }
                cursor = result.NextCursor;
                if (!result.Truncated || pageText.Length == 0 ||
                    text.Length >= result.TotalCharacters ||
                    string.IsNullOrWhiteSpace(cursor) ||
                    !seenCursors.Add(cursor))
                {
                    throw new ResourceRequestException(
                        "The provider could not materialize this representation completely. No partial content was returned to the model.",
                        "resource_whole_read_incomplete",
                        false);
                }
            }
            return new ResourceReadSelection
            {
                Result = new ResourceReadResult
                {
                    Resource = first.Resource,
                    Representation = first.Representation,
                    // Body-free metadata must not acquire an empty CAS text payload
                    // when the completed view is retained again as tool evidence.
                    Text = first.Text == null && text.Length == 0 ? null : text.ToString(),
                    ContentSha256 = last.ContentSha256,
                    AuthorityGeneration = first.AuthorityGeneration,
                    Offset = 0,
                    ReturnedCharacters = text.Length,
                    TotalCharacters = last.TotalCharacters,
                    Complete = true,
                    Truncated = false,
                    HydratedForNextModelStep = hydratedForNextModelStep,
                    RawContentIncluded = rawContentIncluded,
                    Related = related
                        .Where(item => item != null &&
                            !string.IsNullOrWhiteSpace(item.Uri))
                        .GroupBy(item => item.Uri + "\n" +
                            (item.Revision ?? string.Empty), StringComparer.Ordinal)
                        .Select(group => group.First())
                        .ToList()
                },
                ModelAttachments = attachments
                    .Where(item => item != null)
                    .GroupBy(item => item.Id ?? string.Empty, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .ToList(),
                ResourceRefs = references
                    .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Uri))
                    .GroupBy(item => item.Uri + "\n" + (item.Revision ?? string.Empty), StringComparer.Ordinal)
                    .Select(group => group.First())
                    .ToList()
            };
        }

        private static ResourceRequestException WholeReadFailure(string message)
        {
            return new ResourceRequestException(
                message + " No partial content was returned to the model.",
                "resource_whole_read_invalid",
                false);
        }

    }
}
