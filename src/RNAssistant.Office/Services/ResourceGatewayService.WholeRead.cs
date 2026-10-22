using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Services
{
    internal sealed partial class ResourceGatewayService
    {
        private const int InternalReadCharacters = ResourceReadRequest.MaximumCharacters;

        // A model's unqualified text read returns a complete representation when
        // available; an oversized live source returns one exact selected window.
        internal ResourceReadSelection ReadAdaptiveText(ChatSession session, ResourceRef reference,
            ResourceDescriptor descriptor, string representation, int maximumCharacters,
            System.Threading.CancellationToken cancellationToken)
        {
            if (maximumCharacters < 1) throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
            var provider = ProviderFor(reference.Uri);
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            var view = representation == "auto" || string.IsNullOrWhiteSpace(representation)
                ? descriptor.Representations.Contains(ResourceRepresentations.Text)
                    ? ResourceRepresentations.Text : ResourceRepresentations.Source
                : representation;
            if (view != ResourceRepresentations.Text && view != ResourceRepresentations.Source)
                return ReadWithinBudget(session, reference, representation, maximumCharacters, cancellationToken);
            if ((view == ResourceRepresentations.Text &&
                 !(provider is IResourceCharacterWindowProvider)) ||
                (view == ResourceRepresentations.Source &&
                 !(provider is IResourceLineWindowProvider)))
                return ReadWithinBudget(session, reference, representation, maximumCharacters, cancellationToken);
            var firstCharacter = 0;
            if (view == ResourceRepresentations.Text &&
                provider is IResourceTextBoundsProvider boundedText)
            {
                var bounds = WithProvider(provider, session, () =>
                    boundedText.ReadTextBounds(session, reference, view, cancellationToken));
                if (bounds != null)
                {
                    if (bounds.StartChar < 0 || bounds.EndChar < bounds.StartChar)
                        throw new ResourceRequestException("Provider returned invalid text bounds.",
                            "RESOURCE_SNAPSHOT_UNAVAILABLE", false);
                    firstCharacter = bounds.StartChar;
                    if ((long)bounds.EndChar - bounds.StartChar > maximumCharacters)
                    {
                        var firstWindow = ReadInitialCharacterWindow(provider, session, reference,
                            view, firstCharacter, maximumCharacters, cancellationToken);
                        if (firstWindow != null) return firstWindow;
                    }
                }
            }
            // VBA must select bounded lines before attempting a whole capture:
            // CodeModule.Lines for an entire large module materializes the source.
            if (view == ResourceRepresentations.Source &&
                provider is IResourceLineWindowProvider lines)
            {
                for (var count = 500; count >= 1; count /= 2)
                {
                    try
                    {
                        var selected = WithProvider(provider, session, () =>
                            lines.ReadLineWindow(session, reference, view, 1, count,
                                cancellationToken));
                        if (selected == null) break;
                        if (selected.Result.Text.Length > maximumCharacters) continue;
                        if (LineSourceFits(provider, lines, session, reference, view,
                            selected.Result, maximumCharacters, cancellationToken))
                        {
                            try
                            {
                                var whole = ReadWhole(session, reference, representation, cancellationToken);
                                if (whole.Result.Text == null || whole.Result.Text.Length <= maximumCharacters)
                                    return whole;
                            }
                            catch (ResourceRequestException error) when (
                                error.ErrorCode == "RESOURCE_BATCH_TOO_LARGE" ||
                                error.ErrorCode == "resource_whole_read_too_large" ||
                                error.ErrorCode == "resource_whole_read_incomplete") { }
                        }
                        return _authority == null ? selected : _authority.PublishRead(session,
                            selected, null, provider is ILiveOfficeResourceProvider);
                    }
                    catch (ResourceRequestException tooWide) when (
                        tooWide.ErrorCode == "resource_lines_too_large") { }
                }
            }
            ResourceReadSelection materialized = null;
            try
            {
                materialized = ReadWithinBudget(session, reference, representation, maximumCharacters, cancellationToken);
                if (materialized.Result.Text == null ||
                    materialized.Result.Text.Length <= maximumCharacters)
                    return materialized;
            }
            catch (ResourceRequestException error) when (
                view == ResourceRepresentations.Text &&
                provider is IResourceCharacterWindowProvider &&
                (error.ErrorCode == "RESOURCE_SNAPSHOT_TOO_LARGE" ||
                 error.ErrorCode == "RESOURCE_BATCH_TOO_LARGE" ||
                 error.ErrorCode == "resource_whole_read_too_large" ||
                 error.ErrorCode == "resource_whole_read_incomplete")) { }
            if (view == ResourceRepresentations.Text &&
                provider is IResourceCharacterWindowProvider)
            {
                var selected = ReadInitialCharacterWindow(provider, session, reference,
                    view, firstCharacter, maximumCharacters, cancellationToken);
                if (selected != null)
                    return selected;
            }
            if (materialized != null) return materialized;
            throw new ResourceRequestException("Choose a bounded text target or selector for this source.",
                "RESOURCE_SNAPSHOT_TOO_LARGE", false);
        }

        private ResourceReadSelection ReadWithinBudget(ChatSession session, ResourceRef reference,
            string representation, int maximumCharacters, System.Threading.CancellationToken cancellationToken)
        {
            var first = Read(session, new ResourceReadRequest { Reference = reference,
                Representation = representation, MaxChars = Math.Min(InternalReadCharacters, maximumCharacters) }, cancellationToken);
            var result = first.Result;
            if (result.Text == null || result.Complete && !result.Truncated) return first;
            // Runtime metadata must remain parseable for its semantic projection.
            // The compiler budgets that projection; only source text is excerpted here.
            if (result.Representation != ResourceRepresentations.Text && result.Representation != ResourceRepresentations.Source ||
                result.Resource.Kind == ChatHtmlResourceCatalog.DataKind || result.TotalCharacters <= maximumCharacters)
                return ReadWhole(session, result.Resource.Reference, representation, cancellationToken);
            var length = Math.Min(result.Text.Length, maximumCharacters);
            if (length > 0 && (long)result.Offset + length < result.TotalCharacters &&
                char.IsHighSurrogate(result.Text[length - 1])) length--;
            result.Text = result.Text.Substring(0, length);
            result.ReturnedCharacters = length;
            result.Coverage = result.Resource.Coverage = new ResourceCoverage(ResourceCoverageKinds.CharacterRange,
                start: result.Offset, end: (long)result.Offset + length);
            result.SourceComplete = false;
            result.SourceEndChar = result.TotalCharacters;
            result.Complete = false;
            result.Truncated = true;
            result.CompleteViewPayload = null;
            result.Payload = null;
            result.NextCursor = null;
            first.RetainedText = null;
            return first;
        }

        // Probe only bounded line windows until the context budget is exceeded
        // or EOF is reached. A fitting source still uses the authoritative whole
        // read; independently captured windows never become a stitched observation.
        private bool LineSourceFits(IResourceProvider provider, IResourceLineWindowProvider lines,
            ChatSession session, ResourceRef reference, string representation,
            ResourceReadResult first, int maximumCharacters,
            System.Threading.CancellationToken cancellationToken)
        {
            if (!first.SourceTotalLines.HasValue || first.SourceCoverage?.End == null) return false;
            var totalLines = first.SourceTotalLines.Value;
            long next = first.SourceCoverage.End.Value;
            long characters = first.Text.Length;
            while (next <= totalLines && characters <= maximumCharacters)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ResourceReadSelection selected = null;
                for (var count = (int)Math.Min(500L, (long)totalLines - next + 1); count >= 1; count /= 2)
                {
                    try
                    {
                        selected = WithProvider(provider, session, () => lines.ReadLineWindow(
                            session, reference, representation, (int)next, count, cancellationToken));
                        break;
                    }
                    catch (ResourceRequestException error) when (error.ErrorCode == "resource_lines_too_large") { }
                }
                var result = selected?.Result;
                if (result?.Text == null || result.SourceTotalLines != totalLines ||
                    result.SourceCoverage?.Start != next || result.SourceCoverage.End == null ||
                    result.SourceCoverage.End <= next)
                    return false;
                // VBIDE may omit the separator at a window boundary.
                characters += result.Text.Length + 2L;
                next = result.SourceCoverage.End.Value;
            }
            return next == (long)totalLines + 1 && characters <= maximumCharacters;
        }

        private ResourceReadSelection ReadInitialCharacterWindow(IResourceProvider provider,
            ChatSession session, ResourceRef reference, string representation, int startChar,
            int maximumCharacters, System.Threading.CancellationToken cancellationToken)
        {
            var characters = provider as IResourceCharacterWindowProvider;
            if (characters == null) return null;
            var count = Math.Min(maximumCharacters, InternalReadCharacters);
            var selected = WithProvider(provider, session, () =>
            {
                try
                {
                    return characters.ReadCharacterWindow(session, reference, representation,
                        startChar, count, cancellationToken);
                }
                catch (ResourceRequestException error) when (
                    error.ErrorCode == "resource_chars_invalid" && count > 1)
                {
                    // Only an implicit first window may shorten its boundary to
                    // avoid splitting a UTF-16 pair. Explicit selectors still fail.
                    return characters.ReadCharacterWindow(session, reference, representation,
                        startChar, count - 1, cancellationToken);
                }
            });
            return selected == null || _authority == null ? selected :
                _authority.PublishRead(session, selected, null,
                    provider is ILiveOfficeResourceProvider);
        }

        // A semantic line selection pins the same whole snapshot used by the
        // gateway. Only its exact excerpt becomes model observation evidence.
        internal ResourceReadSelection ReadLines(ChatSession session, ResourceRef reference,
            string representation, int startLine, int lineCount, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken),
            int maximumCharacters = InternalReadCharacters)
        {
            if ((representation != "source" && representation != "text") || startLine < 1 || lineCount < 1 || maximumCharacters < 1)
                throw new ResourceRequestException("Use source/text with positive startLine and lineCount.", "resource_lines_invalid", false);
            var lineProvider = ProviderFor(reference.Uri);
            if (lineProvider is IResourceLineWindowProvider boundedLines)
            {
                if (reference.IsExact)
                    throw new ResourceRequestException("Live line windows require a current semantic target.", "RESOURCE_SNAPSHOT_UNAVAILABLE", false);
                var pushed = WithProvider(lineProvider, session, () =>
                {
                    var selected = boundedLines.ReadLineWindow(session, reference, representation,
                        startLine, lineCount, cancellationToken, maximumCharacters);
                    return selected == null || _authority == null ? selected : _authority.PublishRead(session, selected, null,
                        lineProvider is ILiveOfficeResourceProvider);
                });
                if (pushed != null) return pushed;
            }
            var selection = ReadWhole(session, reference, representation, cancellationToken);
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
            if (end - start > maximumCharacters)
                throw new ResourceRequestException("Selected lines exceed the model read budget. Reduce lineCount or use startChar/charCount for a long line.", "resource_lines_too_large", false);
            result.Text = text.Substring(start, end - start);
            result.Offset = start;
            result.ReturnedCharacters = end - start;
            result.Coverage = result.Resource.Coverage = new ResourceCoverage(ResourceCoverageKinds.CharacterRange, start: start, end: end);
            result.Complete = false;
            result.Truncated = true;
            result.CompleteViewPayload = null;
            result.Payload = null;
            return selection;
        }

        // UTF-16 offsets match character-range evidence. The whole snapshot is
        // pinned before slicing; no selected excerpt grants whole-source authority.
        internal ResourceReadSelection ReadCharacters(ChatSession session, ResourceRef reference,
            string representation, int startChar, int charCount,
            System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken),
            int maximumCharacters = InternalReadCharacters)
        {
            if ((representation != "source" && representation != "text") ||
                startChar < 0 || charCount < 1 || charCount > maximumCharacters)
                throw new ResourceRequestException("Use source/text with zero-based startChar and positive charCount within the model read budget.",
                    "resource_chars_invalid", false);
            var characterProvider = ProviderFor(reference.Uri);
            if (characterProvider is IResourceCharacterWindowProvider boundedCharacters)
            {
                if (reference.IsExact)
                    throw new ResourceRequestException("Live character windows require a current semantic target.", "RESOURCE_SNAPSHOT_UNAVAILABLE", false);
                var pushed = WithProvider(characterProvider, session, () =>
                {
                    var selected = boundedCharacters.ReadCharacterWindow(session, reference,
                        representation, startChar, charCount, cancellationToken);
                    return selected == null || _authority == null ? selected : _authority.PublishRead(session, selected, null,
                        characterProvider is ILiveOfficeResourceProvider);
                });
                if (pushed != null) return pushed;
            }
            var selection = ReadWhole(session, reference, representation, cancellationToken);
            var result = selection.Result;
            var text = result.Text;
            if (text == null)
                throw new ResourceRequestException("This representation has no character-addressable text.",
                    "resource_chars_unsupported", false);
            if (startChar > text.Length)
                throw new ResourceRequestException("startChar is past the end of this text (" + text.Length + " characters).",
                    "resource_chars_invalid", false);
            var end = (int)System.Math.Min((long)text.Length, (long)startChar + charCount);
            if ((startChar > 0 && startChar < text.Length &&
                char.IsLowSurrogate(text[startChar]) && char.IsHighSurrogate(text[startChar - 1])) ||
                (end > 0 && end < text.Length &&
                char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end])))
                throw new ResourceRequestException("The selected character range splits a UTF-16 surrogate pair. Adjust startChar or charCount by one.",
                    "resource_chars_invalid", false);
            result.Text = text.Substring(startChar, end - startChar);
            result.Offset = startChar;
            result.ReturnedCharacters = end - startChar;
            result.Coverage = result.Resource.Coverage = new ResourceCoverage(ResourceCoverageKinds.CharacterRange,
                start: startChar, end: end);
            result.Complete = false;
            result.Truncated = true;
            result.CompleteViewPayload = null;
            result.Payload = null;
            return selection;
        }

        internal ResourceReadSelection ReadProcedure(ChatSession session, ResourceRef reference,
            string name, string kind, int maximumCharacters,
            System.Threading.CancellationToken cancellationToken)
        {
            VbaProcedureSelector.Validate(name, kind);
            // Capture once under the provider gate, then parse/slice the exact
            // retained revision. A runtime whole capture is not a model whole read.
            var selection = ReadWhole(session, reference, ResourceRepresentations.Source, cancellationToken);
            var result = selection.Result;
            if (result.Representation != ResourceRepresentations.Source || result.Text == null ||
                result.SourceComplete == false || result.SourceCoverage != null)
                throw new ResourceRequestException("Procedure selection requires complete VBA source; use line/character selectors for this view.",
                    "resource_procedure_unsupported", false);
            ResourceProcedureSelection procedure;
            using (var reader = new System.IO.StringReader(result.Text))
                procedure = VbaProcedureSelector.Select(reader, name, kind, cancellationToken);
            VbaProcedureSelector.RequireFits(procedure, maximumCharacters);
            result.Text = result.Text.Substring(procedure.StartChar, procedure.EndChar - procedure.StartChar);
            result.Offset = procedure.StartChar;
            result.ReturnedCharacters = result.Text.Length;
            result.Coverage = result.Resource.Coverage = new ResourceCoverage(ResourceCoverageKinds.CharacterRange,
                start: procedure.StartChar, end: procedure.EndChar);
            result.Procedure = procedure;
            result.Complete = false;
            result.SourceComplete = false;
            result.Truncated = true;
            result.Payload = result.CompleteViewPayload = null;
            result.CompleteViewParts = null;
            result.NextCursor = null;
            selection.RetainedText = null;
            return selection;
        }

        internal ResourceReadSelection ReadWhole(
            ChatSession session, ResourceRef reference,
            string representation, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
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
                    }, cancellationToken);
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
                if (pages == 1 && page.RetainedText != null)
                {
                    if (result.CompleteViewPayload == null || !result.Resource.Reference.IsExact ||
                        page.RetainedText.Length != result.TotalCharacters ||
                        !page.RetainedText.StartsWith(pageText, StringComparison.Ordinal))
                        throw WholeReadFailure("The retained body does not match its exact first page.");
                    cancellationToken.ThrowIfCancellationRequested();
                    result.Text = page.RetainedText;
                    page.RetainedText = null;
                    result.ReturnedCharacters = result.TotalCharacters;
                    result.Payload = result.CompleteViewPayload;
                    result.Coverage = result.Resource.Coverage = ResourceCoverage.Whole();
                    result.Complete = true;
                    result.Truncated = false;
                    result.NextCursor = null;
                    return page;
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
                    SourceComplete = first.SourceComplete,
                    SourceCoverage = first.SourceCoverage,
                    SourceEndChar = first.SourceEndChar,
                    SourceTotalLines = first.SourceTotalLines,
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
