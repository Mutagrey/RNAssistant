using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Storage
{
    public sealed partial class DocumentArtifactStore
    {
        private const string TextIndexView = "artifact-text-index-v3";
        private const string ExtractedTextIndexView = "artifact-extracted-text-index-v3";
        private const int TextPartCharacters = 32000;
        private const int MaximumIndexedCharacters = 20 * 1024 * 1024;
        private const int MaximumIndexedBytes = 20 * 1024 * 1024;

        public ResourceReadResult ReadMarkdownSection(ChatSession session, ResourceRef reference, string section, int maxCharacters)
        {
            if (string.IsNullOrWhiteSpace(section) || section.Length > 200 || maxCharacters < 1 || maxCharacters > ResourceReadRequest.MaximumCharacters)
                throw new MarkdownSectionException("Use a complete heading title of at most 200 characters.", "resource_section_invalid");
            var artifact = Read(session, reference, false);
            var original = artifact.OriginalAttachment;
            if (original == null ? artifact.Kind != ChatArtifactKinds.Markdown && artifact.Kind != ChatArtifactKinds.PlanDocument : !IsMarkdownOriginal(original))
                throw new MarkdownSectionException("Section reads require document Markdown or a Plan. Read this resource's advertised representation instead.", "resource_section_unsupported");
            if (original?.TextTruncated == true)
                throw new MarkdownSectionException("The extraction is incomplete; a unique complete section cannot be established.", "resource_section_incomplete");
            var body = original == null ? _revisions.GetRevision(Scope(session), reference)?.Payload :
                string.IsNullOrWhiteSpace(original.ExtractedTextSha256) || !original.ExtractedTextByteLength.HasValue ? null :
                new PayloadRef(original.ExtractedTextSha256, original.ExtractedTextByteLength.Value, "text/plain; charset=utf-8");
            if (body == null || body.ByteLength > MaximumIndexedBytes)
                throw new MarkdownSectionException("The exact Markdown source is unavailable or exceeds the section scan bound.", "RESOURCE_SNAPSHOT_UNAVAILABLE");
            var text = _payloads.ReadText(body.ToBlobReference());
            if (text == null || text.Length > MaximumIndexedCharacters || original != null && original.ExtractedCharCount != text.Length)
                throw new MarkdownSectionException("The exact Markdown source is unavailable or incomplete.", "RESOURCE_SNAPSHOT_UNAVAILABLE");
            var headings = MarkdownHeadingScanner.Scan(text).Take(4097).ToList();
            if (headings.Count > 4096)
                throw new MarkdownSectionException("The heading scan is incomplete; read the whole resource instead.", "resource_section_incomplete");
            var matches = headings.Where(item => string.Equals(text.Substring(item.ContentStart, item.LineEnd - item.ContentStart).Trim(), section.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1)
                throw new MarkdownSectionException(matches.Count == 0 ? "Heading not found. Rediscover or read the source; do not guess a section title." :
                    "The heading is repeated. Choose a unique nested heading or read the whole resource.",
                    matches.Count == 0 ? "resource_section_not_found" : "resource_section_ambiguous");
            var selected = matches[0];
            var end = headings.FirstOrDefault(item => item.Start > selected.Start && item.Level <= selected.Level)?.Start ?? text.Length;
            if (end - selected.Start > maxCharacters)
                throw new MarkdownSectionException("This section exceeds the bounded read size. Choose a narrower nested heading or read the whole resource.", "resource_section_too_large");
            return new ResourceReadResult { Representation = ResourceRepresentations.Text, Text = text.Substring(selected.Start, end - selected.Start),
                ContentSha256 = body.Sha256, Offset = selected.Start, ReturnedCharacters = end - selected.Start, TotalCharacters = text.Length,
                Coverage = new ResourceCoverage(ResourceCoverageKinds.CharacterRange, start: selected.Start, end: end),
                Complete = true, Truncated = false, RawContentIncluded = true };
        }

        public static bool IsMarkdownOriginal(ChatAttachment original)
        {
            return original != null && (string.Equals(original.ContentType, "text/markdown", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetExtension(original.FileName), ".md", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetExtension(original.FileName), ".markdown", StringComparison.OrdinalIgnoreCase));
        }

        // A derived view of an exact, published text representation in the existing
        // revision journal/CAS. This is neither an inventory nor read evidence.
        public ResourceSearchResult SearchText(ChatSession session, ResourceRef reference,
            string query, int characterBudget, int snippetCharacters)
        {
            return SearchTextCore(session, reference, query, characterBudget, snippetCharacters, 1, false, false);
        }

        private ResourceSearchResult SearchTextCore(ChatSession session, ResourceRef reference,
            string query, int characterBudget, int snippetCharacters, int maximumMatches, bool scanAll, bool matchCase)
        {
            if (string.IsNullOrEmpty(query) || characterBudget < 1 || characterBudget > MaximumIndexedCharacters ||
                snippetCharacters < 1 || snippetCharacters > 2000)
                throw new ArgumentException("A bounded text search is required.");
            var artifact = Read(session, reference, false);
            var original = artifact.OriginalAttachment;
            if (original == null && artifact.Kind != ChatArtifactKinds.Markdown && artifact.Kind != ChatArtifactKinds.PlanDocument &&
                !(artifact.Kind == ChatArtifactKinds.File && FileEncoding(artifact) != null))
                throw new InvalidDataException("This index requires a retained original text, Markdown or Plan snapshot.");
            var scope = Scope(session);
            var body = _revisions.GetRevision(scope, reference)?.Payload;
            if (body == null || body.Sha256 != artifact.ContentSha256 || body.ByteLength != artifact.ContentByteLength)
                throw new InvalidDataException("The exact source revision is unavailable.");
            if (original != null)
            {
                if (string.IsNullOrWhiteSpace(original.ExtractedTextSha256) || !original.ExtractedTextByteLength.HasValue ||
                    original.ExtractedTextByteLength.Value > MaximumIndexedBytes ||
                    original.ExtractedCharCount < 0 || original.ExtractedCharCount > MaximumIndexedCharacters)
                    throw new InvalidDataException("The exact original text extraction is unavailable.");
                body = new PayloadRef(original.ExtractedTextSha256, original.ExtractedTextByteLength.Value, "text/plain; charset=utf-8");
            }
            else if (artifact.Kind == ChatArtifactKinds.File)
                body = FileTextPayload(session, reference, artifact, body);
            if (!_payloads.HasVerifiedReference(body.ToBlobReference()))
                throw new InvalidDataException("The exact text source is unavailable.");
            var sourceIncomplete = original != null && (original.TextTruncated ||
                original.Kind == "pdf" && original.PageCount > (original.PageTextLengths?.Count ?? 0));
            var coverage = sourceIncomplete ? new ResourceCoverage(ResourceCoverageKinds.CharacterRange, start: 0, end: original.ExtractedCharCount) : ResourceCoverage.Whole();
            var view = original == null ? TextIndexView : ExtractedTextIndexView;
            var markdown = original == null ? artifact.Kind != ChatArtifactKinds.File ||
                string.Equals(Path.GetExtension(artifact.Title), ".md", StringComparison.OrdinalIgnoreCase) : IsMarkdownOriginal(original);
            var result = SearchTextView(scope, reference, artifact, body, view, coverage, markdown, original?.ExtractedCharCount,
                query, characterBudget, snippetCharacters, null, maximumMatches, scanAll, matchCase);
            result.ScanTruncated |= sourceIncomplete;
            return result;
        }

        public ResourceSearchResult SearchTextOccurrences(ChatSession session, ResourceRef reference,
            string query, int limit, bool matchCase)
        {
            if (limit < 1 || limit > 50 || string.IsNullOrEmpty(query) || query.Length > 500)
                throw new ArgumentException("A query and result limit are required.");
            return SearchTextCore(session, reference, query, MaximumIndexedCharacters, 600,
                limit, true, matchCase);
        }

        public ResourceReadResult ReadIndexedTextPart(ChatSession session, ResourceRef reference, int partNumber)
        {
            if (partNumber < 1) throw new ArgumentOutOfRangeException(nameof(partNumber));
            var artifact = Read(session, reference, false);
            var original = artifact.OriginalAttachment;
            if (original == null && artifact.Kind != ChatArtifactKinds.Markdown && artifact.Kind != ChatArtifactKinds.PlanDocument &&
                !(artifact.Kind == ChatArtifactKinds.File && FileEncoding(artifact) != null))
                throw new InvalidDataException("This resource has no indexed text representation.");
            var scope = Scope(session);
            var source = _revisions.GetRevision(scope, reference)?.Payload;
            if (source == null || source.Sha256 != artifact.ContentSha256 || source.ByteLength != artifact.ContentByteLength)
                throw new InvalidDataException("The exact source revision is unavailable.");
            if (original != null)
            {
                if (string.IsNullOrWhiteSpace(original.ExtractedTextSha256) || !original.ExtractedTextByteLength.HasValue ||
                    original.ExtractedTextByteLength.Value > MaximumIndexedBytes || original.ExtractedCharCount < 0)
                    throw new InvalidDataException("The exact extraction is unavailable.");
                source = new PayloadRef(original.ExtractedTextSha256, original.ExtractedTextByteLength.Value, "text/plain; charset=utf-8");
            }
            else if (artifact.Kind == ChatArtifactKinds.File)
                source = FileTextPayload(session, reference, artifact, source);
            if (source.ByteLength > MaximumIndexedBytes || !_payloads.HasVerifiedReference(source.ToBlobReference()))
                throw new InvalidDataException("The exact text source is unavailable or exceeds 20 MiB.");
            var sourceComplete = original == null || !(original.TextTruncated ||
                original.Kind == "pdf" && original.PageCount > (original.PageTextLengths?.Count ?? 0));
            var coverage = sourceComplete ? ResourceCoverage.Whole() :
                new ResourceCoverage(ResourceCoverageKinds.CharacterRange, start: 0, end: original.ExtractedCharCount);
            var view = original == null ? TextIndexView : ExtractedTextIndexView;
            var captured = _revisions.GetView(scope, reference, view);
            if (captured != null && (captured.ContentSha256 != source.Sha256 ||
                JsonConvert.SerializeObject(captured.Coverage) != JsonConvert.SerializeObject(coverage)))
                throw new InvalidDataException("The text index does not match its source.");
            if (captured == null)
                captured = MaterializeTextIndex(scope, reference, source, view, coverage,
                    original == null ? artifact.Kind != ChatArtifactKinds.File ||
                        string.Equals(Path.GetExtension(artifact.Title), ".md", StringComparison.OrdinalIgnoreCase) : IsMarkdownOriginal(original),
                    original?.ExtractedCharCount, null, artifact.Title);
            try { return ReadIndexedTextPartFromView(captured, source, sourceComplete, partNumber); }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is JsonException)
            {
                // A derived manifest or part may disappear independently of its
                // verified source. Rebuild only this exact view once, then read
                // from the repaired CAS part; never switch to a live head.
                captured = MaterializeTextIndex(scope, reference, source, view, coverage,
                    original == null ? artifact.Kind != ChatArtifactKinds.File ||
                        string.Equals(Path.GetExtension(artifact.Title), ".md", StringComparison.OrdinalIgnoreCase) : IsMarkdownOriginal(original),
                    original?.ExtractedCharCount, null, artifact.Title);
                return ReadIndexedTextPartFromView(captured, source, sourceComplete, partNumber);
            }
        }

        public ResourceReadResult ReadIndexedTextWindow(ChatSession session, ResourceRef reference, int offset, int maxCharacters)
        {
            if (offset < 0 || maxCharacters < 1 || maxCharacters > ResourceReadRequest.MaximumCharacters)
                throw new ArgumentOutOfRangeException(nameof(offset));
            var first = ReadIndexedTextPart(session, reference, 1);
            if (offset > first.TotalCharacters)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if (offset == first.TotalCharacters)
                return new ResourceReadResult { Representation = ResourceRepresentations.Text,
                    Text = string.Empty, ContentSha256 = first.ContentSha256, Offset = offset,
                    ReturnedCharacters = 0, TotalCharacters = first.TotalCharacters,
                    SourceComplete = first.SourceComplete, Complete = true, RawContentIncluded = true,
                    Coverage = new ResourceCoverage(ResourceCoverageKinds.CharacterRange, start: offset, end: offset) };
            var number = Math.Min(first.PartCount.Value, Math.Max(1, offset / TextPartCharacters + 1));
            var part = number == 1 ? first : ReadIndexedTextPart(session, reference, number);
            while (offset < part.Offset && number > 1)
                part = ReadIndexedTextPart(session, reference, --number);
            while (offset >= part.Offset + part.ReturnedCharacters && number < first.PartCount.Value)
                part = ReadIndexedTextPart(session, reference, ++number);
            if (offset < part.Offset || offset >= part.Offset + part.ReturnedCharacters)
                throw new InvalidDataException("The indexed file window is unavailable.");
            var local = offset - part.Offset;
            if (local > 0 && char.IsLowSurrogate(part.Text[local]) && char.IsHighSurrogate(part.Text[local - 1]))
                throw new InvalidDataException("The requested window splits a UTF-16 character.");
            var length = Math.Min(maxCharacters, part.Text.Length - local);
            if (local + length < part.Text.Length && char.IsHighSurrogate(part.Text[local + length - 1]) &&
                char.IsLowSurrogate(part.Text[local + length])) length--;
            if (length == 0) throw new InvalidDataException("The requested window splits a UTF-16 character.");
            var selected = part.Text.Substring(local, length);
            var end = offset + selected.Length;
            return new ResourceReadResult { Representation = ResourceRepresentations.Text,
                Text = selected, ContentSha256 = part.ContentSha256, Offset = offset,
                ReturnedCharacters = selected.Length, TotalCharacters = part.TotalCharacters,
                SourceComplete = part.SourceComplete, Complete = end == part.TotalCharacters,
                Truncated = end < part.TotalCharacters, RawContentIncluded = true,
                Coverage = new ResourceCoverage(ResourceCoverageKinds.CharacterRange, start: offset, end: end) };
        }

        private ResourceReadResult ReadIndexedTextPartFromView(ResourceRevisionView captured,
            PayloadRef source, bool sourceComplete, int partNumber)
        {
            if (captured?.Payload == null || captured.Payload.ByteLength > 8 * 1024 * 1024)
                throw new InvalidDataException("The exact text index is unavailable.");
            var manifest = _payloads.ReadText(captured.Payload.ToBlobReference());
            var index = manifest == null ? null : JsonConvert.DeserializeObject<TextIndex>(manifest);
            if (index == null || index.Parts == null || index.Length < 0 || index.Length > MaximumIndexedCharacters ||
                index.Parts.Count > (MaximumIndexedCharacters / TextPartCharacters) + 1)
                throw new InvalidDataException("The exact text index is unavailable.");
            var expected = 0;
            var expectedLine = 1;
            foreach (var part in index.Parts)
            {
                if (part == null || part.Start != expected || part.Length < 1 || part.Length > TextPartCharacters ||
                    part.Payload == null || part.Payload.ByteLength > TextPartCharacters * 4L)
                    throw new InvalidDataException("The text index has invalid part coverage.");
                if (part.FirstLine != expectedLine || part.LastLine < part.FirstLine)
                    throw new InvalidDataException("The text index has invalid line coverage.");
                expected += part.Length;
                expectedLine = part.LastLine;
            }
            if (expected != index.Length || index.TotalLines != expectedLine ||
                index.Parts.Count > 0 && index.Parts[index.Parts.Count - 1].LastLine != index.TotalLines)
                throw new InvalidDataException("The selected text part is unavailable.");
            if (partNumber > Math.Max(1, index.Parts.Count)) throw new ArgumentOutOfRangeException(nameof(partNumber));
            var selected = index.Parts.Count == 0 ? null : index.Parts[partNumber - 1];
            var text = selected == null ? string.Empty : _payloads.ReadText(selected.Payload.ToBlobReference());
            if (text == null || selected != null && text.Length != selected.Length)
                throw new InvalidDataException("The selected text part failed CAS verification.");
            var start = selected?.Start ?? 0;
            return new ResourceReadResult
            {
                Representation = ResourceRepresentations.Text, Text = text, ContentSha256 = source.Sha256,
                Offset = start, ReturnedCharacters = text.Length, TotalCharacters = index.Length,
                PartNumber = partNumber, PartCount = Math.Max(1, index.Parts.Count), SourceComplete = sourceComplete,
                Complete = false, Truncated = true, RawContentIncluded = true,
                Coverage = new ResourceCoverage(ResourceCoverageKinds.CharacterRange, start: start, end: start + text.Length)
            };
        }

        public ResourceTextOutline OutlineText(ChatSession session, ResourceRef reference, int fromPart, int limit)
        {
            if (fromPart < 1 || limit < 1 || limit > 50)
                throw new ArgumentException("Select a positive part and at most 50 outline entries.");
            var verified = ReadIndexedTextPart(session, reference, 1);
            var artifact = Read(session, reference, false);
            var view = artifact.OriginalAttachment == null ? TextIndexView : ExtractedTextIndexView;
            var captured = _revisions.GetView(Scope(session), reference, view);
            var manifest = captured?.Payload == null ? null : _payloads.ReadText(captured.Payload.ToBlobReference());
            var index = manifest == null ? null : JsonConvert.DeserializeObject<TextIndex>(manifest);
            if (index?.Parts == null || index.Symbols == null || index.Sections == null || index.TotalLines < 1)
                throw new InvalidDataException("The exact text outline is unavailable.");
            var count = Math.Max(1, index.Parts.Count);
            if (fromPart > count) throw new ArgumentOutOfRangeException(nameof(fromPart));
            var markdown = artifact.OriginalAttachment == null ? artifact.Kind != ChatArtifactKinds.File ||
                string.Equals(Path.GetExtension(artifact.Title), ".md", StringComparison.OrdinalIgnoreCase) : IsMarkdownOriginal(artifact.OriginalAttachment);
            var result = new ResourceTextOutline {
                ExactReference = reference.Copy(), PartCount = count, TotalLines = index.TotalLines,
                SourceComplete = verified.SourceComplete != false,
                SymbolLanguage = markdown ? "markdown" : index.SymbolLanguage,
                SymbolsComplete = markdown ? index.SectionsThrough == index.Length :
                    index.SymbolLanguage != null && index.SymbolsComplete,
                NextPart = fromPart + limit < count + 1 ? (int?)(fromPart + limit) : null
            };
            for (var number = fromPart; number <= count && number < fromPart + limit; number++)
            {
                var part = index.Parts.Count == 0 ? null : index.Parts[number - 1];
                var projected = new ResourceTextOutlinePart {
                    PartNumber = number, FirstLine = part?.FirstLine ?? 1,
                    LastLine = part?.LastLine ?? 1, Characters = part?.Length ?? 0
                };
                if (part != null && markdown)
                    projected.Symbols.AddRange(index.Sections
                        .Where(item => item.Start >= part.Start && item.Start < part.Start + part.Length)
                        .Select(item => new ResourceTextOutlineSymbol { Name = item.Title, Kind = "heading" }));
                else if (part != null)
                    projected.Symbols.AddRange(index.Symbols
                        .Where(item => item.PartNumber == number)
                        .Select(item => new ResourceTextOutlineSymbol {
                            Name = item.Name, Kind = item.Kind, Line = item.Line }));
                result.Parts.Add(projected);
            }
            return result;
        }

        // The HTML catalog owns member resolution/serialization. Retain its derived
        // view beneath the exact published parent; do not invent member heads.
        public ResourceSearchResult SearchMemberText(ChatSession session, ResourceDescriptor member, string text,
            string query, int characterBudget, int snippetCharacters)
        {
            if (member?.Reference == null || member.Parent == null || text == null || string.IsNullOrEmpty(query) ||
                characterBudget < 1 || characterBudget > 1000000 || snippetCharacters < 1 || snippetCharacters > 2000)
                throw new ArgumentException("An exact member and bounded search are required.");
            var artifact = Read(session, member.Parent, false);
            var parent = ResourceUri.Parse(member.Parent.Uri);
            var address = ResourceUri.Parse(member.Reference.Uri);
            if (artifact.Kind != ChatArtifactKinds.HtmlWorkspace || parent.Segments.Count != 5 || address.Provider != parent.Provider ||
                address.Segments.Count != 8 || !address.Segments.Take(5).SequenceEqual(parent.Segments) ||
                address.Segments[5] != "member" || address.Segments[6] != "file" && address.Segments[6] != "data" ||
                member.Reference.Revision != member.Parent.Revision || text.Length > MaximumIndexedCharacters)
                throw new InvalidDataException("The text member does not belong to this bounded HTML snapshot.");
            if (!_payloads.HasVerifiedReference(_revisions.GetRevision(Scope(session), member.Parent).Payload.ToBlobReference()))
                throw new InvalidDataException("The exact HTML source is unavailable.");
            var body = new PayloadRef(TextPatternEngine.Sha256(text), Encoding.UTF8.GetByteCount(text), member.MimeType);
            var path = "member/" + address.Segments[6] + "/" + address.Segments[7];
            var result = SearchTextView(Scope(session), member.Parent, artifact, body, "artifact-member-text-index-v1:" + path,
                new ResourceCoverage(ResourceCoverageKinds.CharacterRange, start: 0, end: text.Length, path: path), false, text.Length,
                query, characterBudget, snippetCharacters, text);
            foreach (var match in result.Matches)
            {
                match.Reference = member.Reference.Copy(); match.Kind = member.Kind; match.Title = member.Title;
                match.CreatedUtc = member.CreatedUtc;
                // Model semantic scope remains HTML, regardless of document ownership.
                match.DocumentScoped = false;
                match.Representation = address.Segments[6] == "file" ? ResourceRepresentations.Source : ResourceRepresentations.Text;
            }
            return result;
        }

        private ResourceSearchResult SearchTextView(ResourceAuthorityScopeId scope, ResourceRef reference, ChatArtifact artifact,
            PayloadRef body, string view, ResourceCoverage coverage, bool markdown, int? expectedCharacters,
            string query, int characterBudget, int snippetCharacters, string derivedText = null,
            int maximumMatches = 1, bool scanAll = false, bool matchCase = false)
        {
            var captured = _revisions.GetView(scope, reference, view);
            if (captured != null && (captured.ContentSha256 != body.Sha256 || JsonConvert.SerializeObject(captured.Coverage) != JsonConvert.SerializeObject(coverage)))
                throw new InvalidDataException("The text index does not match its exact source.");
            if (captured == null) captured = MaterializeTextIndex(scope, reference, body, view, coverage, markdown, expectedCharacters, derivedText, artifact.Title);
            ResourceSearchResult result;
            try { result = SearchTextIndex(captured, artifact, query, characterBudget, snippetCharacters,
                maximumMatches, scanAll, matchCase); }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is JsonException)
            {
                // Recreate only this deterministic view from the SAME exact body.
                // Missing derived bytes must not hide a healthy document. Immutable
                // view registration rejects any conflicting derivation.
                captured = MaterializeTextIndex(scope, reference, body, view, coverage, markdown, expectedCharacters, derivedText, artifact.Title);
                result = SearchTextIndex(captured, artifact, query, characterBudget, snippetCharacters,
                    maximumMatches, scanAll, matchCase);
            }
            return result;
        }

        private ResourceRevisionView MaterializeTextIndex(ResourceAuthorityScopeId scope, ResourceRef reference, PayloadRef body,
            string view, ResourceCoverage coverage, bool markdown, int? expectedCharacters, string derivedText, string fileName)
        {
            if (body.ByteLength > MaximumIndexedBytes)
                throw new InvalidDataException("The text source exceeds the bounded index size.");
            TextIndex index;
            if (derivedText == null)
                index = BuildTextIndexFromCas(body, markdown, expectedCharacters, fileName);
            else
            {
                if (derivedText.Length > MaximumIndexedCharacters)
                    throw new InvalidDataException("The text member exceeds the index bound.");
                using (var reader = new StringReader(derivedText))
                    index = BuildTextIndex(reader, markdown, expectedCharacters, fileName);
                body = PayloadRef.FromBlob(_payloads.StoreText(derivedText, body.ContentType));
            }
            var payload = PayloadRef.FromBlob(_payloads.StoreText(JsonConvert.SerializeObject(index), "application/vnd.rnassistant.text-index+json"));
            var captured = new ResourceRevisionView(reference, view, body.Sha256, payload,
                coverage, index.Parts.Select(item => item.Payload).Concat(new[] { body }));
            _revisions.RegisterView(scope, captured);
            return captured;
        }

        private TextIndex BuildTextIndex(TextReader reader, bool markdown, int? expectedCharacters, string fileName)
        {
            var builder = new TextIndexAccumulator(this, markdown, fileName);
            var buffer = new char[81920];
            try
            {
                int length;
                while ((length = reader.Read(buffer, 0, buffer.Length)) > 0) builder.Add(buffer, length);
            }
            catch (DecoderFallbackException error)
            {
                throw new InvalidDataException("The exact source is not valid UTF-8.", error);
            }
            return builder.Finish(expectedCharacters);
        }

        private TextIndex BuildTextIndexFromCas(PayloadRef body, bool markdown, int? expectedCharacters, string fileName)
        {
            var builder = new TextIndexAccumulator(this, markdown, fileName);
            Decoder decoder = new UTF8Encoding(false, true).GetDecoder();
            var characters = new char[81920];
            try
            {
                _payloads.VisitVerifiedTextBytes(body.ToBlobReference(), (bytes, length) =>
                {
                    var decoded = decoder.GetChars(bytes, 0, length, characters, 0, false);
                    builder.Add(characters, decoded);
                }, () =>
                {
                    // A malformed compressed envelope may be a legacy raw blob.
                    // Discard provisional parts from that pass before the raw retry.
                    builder = new TextIndexAccumulator(this, markdown, fileName);
                    decoder = new UTF8Encoding(false, true).GetDecoder();
                });
                builder.Add(characters, decoder.GetChars(new byte[0], 0, 0, characters, 0, true));
                return builder.Finish(expectedCharacters);
            }
            catch (DecoderFallbackException error)
            {
                throw new InvalidDataException("The exact source is not valid UTF-8.", error);
            }
        }

        private sealed class TextIndexAccumulator
        {
            private readonly DocumentArtifactStore _store;
            private readonly TextIndex _index = new TextIndex();
            private readonly TextHeadingCollector _headings;
            private readonly TextSymbolCollector _symbols;
            private readonly StringBuilder _pending = new StringBuilder(TextPartCharacters + 81920);
            private int _line = 1;
            private bool _previousCr;

            internal TextIndexAccumulator(DocumentArtifactStore store, bool markdown, string fileName)
            {
                _store = store;
                _headings = markdown ? new TextHeadingCollector(_index) : null;
                _index.SymbolLanguage = markdown ? null : CodeLanguage(fileName);
                _symbols = _index.SymbolLanguage == null ? null : new TextSymbolCollector(_index);
                if (_symbols != null) _index.SymbolsComplete = false; // advisory grammar only
            }

            internal void Add(char[] characters, int length)
            {
                if (length == 0) return;
                if (_index.Length > MaximumIndexedCharacters - _pending.Length - length)
                    throw new InvalidDataException("The text source exceeds the index bound.");
                _pending.Append(characters, 0, length);
                while (_pending.Length >= TextPartCharacters)
                {
                    var count = TextPartCharacters;
                    if (char.IsHighSurrogate(_pending[count - 1]))
                    {
                        if (_pending.Length == count) break;
                        if (!char.IsLowSurrogate(_pending[count]))
                            throw new InvalidDataException("The text source contains an invalid UTF-16 pair.");
                        count--;
                    }
                    Emit(count);
                }
            }

            internal TextIndex Finish(int? expectedCharacters)
            {
                if (_pending.Length > 0)
                {
                    if (char.IsHighSurrogate(_pending[_pending.Length - 1]))
                        throw new InvalidDataException("The text source ends with an incomplete UTF-16 pair.");
                    Emit(_pending.Length);
                }
                _headings?.Finish();
                _symbols?.Finish();
                _index.TotalLines = _line;
                if (_headings == null) _index.SectionsThrough = _index.Length;
                if (expectedCharacters.HasValue && expectedCharacters.Value != _index.Length)
                    throw new InvalidDataException("The retained extraction length does not match its exact text.");
                return _index;
            }

            private void Emit(int length)
            {
                var text = _pending.ToString(0, length);
                _pending.Remove(0, length);
                var firstLine = _line;
                foreach (var character in text)
                {
                    if (character == '\r') { _line++; _previousCr = true; }
                    else if (character == '\n') { if (!_previousCr) _line++; _previousCr = false; }
                    else _previousCr = false;
                }
                _index.Parts.Add(new TextPart { Start = _index.Length, Length = length,
                    FirstLine = firstLine, LastLine = _line,
                    Payload = PayloadRef.FromBlob(_store._payloads.StoreText(text, "text/plain; charset=utf-8")) });
                _headings?.Add(text);
                _symbols?.Add(text, _index.Parts.Count);
                _index.Length += length;
            }
        }

        // Incremental counterpart of MarkdownHeadingScanner's bounded ATX grammar.
        // Even a 20 MiB line retains only 201 title characters.
        private sealed class TextHeadingCollector
        {
            private readonly TextIndex _index;
            private readonly StringBuilder _title = new StringBuilder(201);
            private int _position, _lineStart, _leading, _markers, _afterLength, _fenceLength;
            private char _marker, _fence, _firstAfter;
            private bool _afterWhitespace = true, _invalid, _afterStarted, _sectionsOmitted;

            internal TextHeadingCollector(TextIndex index) { _index = index; }

            internal void Add(string text)
            {
                foreach (var value in text)
                {
                    if (value == '\n')
                    {
                        FinishLine();
                        _lineStart = _position + 1;
                    }
                    else if (!_invalid)
                    {
                        if (_marker == '\0')
                        {
                            if (value == ' ' && _leading < 4) _leading++;
                            else if (_leading < 4 && (value == '#' || value == '`' || value == '~'))
                            { _marker = value; _markers = 1; }
                            else _invalid = true;
                        }
                        else if (!_afterStarted && value == _marker) _markers++;
                        else
                        {
                            if (!_afterStarted) { _firstAfter = value; _afterStarted = true; }
                            _afterLength++;
                            if (!char.IsWhiteSpace(value)) _afterWhitespace = false;
                            if (_title.Length < 201) _title.Append(value);
                        }
                    }
                    _position++;
                }
            }

            internal void Finish()
            {
                if (_lineStart < _position) FinishLine();
                if (!_sectionsOmitted) _index.SectionsThrough = _position;
            }

            private void FinishLine()
            {
                if (!_invalid && (_marker == '`' || _marker == '~') && _markers >= 3)
                {
                    if (_fence == '\0') { _fence = _marker; _fenceLength = _markers; }
                    else if (_fence == _marker && _markers >= _fenceLength && _afterWhitespace) _fence = '\0';
                }
                else if (!_invalid && _fence == '\0' && _marker == '#' && _markers <= 6 &&
                    (_afterLength == 0 || char.IsWhiteSpace(_firstAfter)))
                {
                    if (_index.Sections.Count == 4096)
                    {
                        if (!_sectionsOmitted) { _index.SectionsThrough = _lineStart; _sectionsOmitted = true; }
                    }
                    else
                    {
                        var title = _title.ToString().Trim();
                        if (title.Length > 200 || _afterLength > 201)
                            title = title.Substring(0, Math.Min(200, title.Length)) + "…";
                        _index.Sections.Add(new TextSection { Start = _lineStart, Title = title });
                    }
                }
                _leading = _markers = _afterLength = 0;
                _marker = _firstAfter = '\0';
                _afterWhitespace = true;
                _afterStarted = false;
                _invalid = false;
                _title.Clear();
            }
        }

        private ResourceSearchResult SearchTextIndex(ResourceRevisionView captured, ChatArtifact artifact,
            string query, int budget, int snippetCharacters, int maximumMatches = 1,
            bool scanAll = false, bool matchCase = false)
        {
            if (captured.Payload == null || captured.Payload.ByteLength > 8 * 1024 * 1024)
                throw new InvalidDataException("The text index is unavailable.");
            var json = _payloads.ReadText(captured.Payload.ToBlobReference());
            var index = json == null ? null : JsonConvert.DeserializeObject<TextIndex>(json);
            if (index == null || index.Length < 0 || index.Length > MaximumIndexedCharacters || index.Parts == null || index.Sections == null ||
                index.Symbols == null || index.Symbols.Count > 4096 || index.TotalLines < 1 ||
                index.Parts.Count > (MaximumIndexedCharacters / TextPartCharacters) + 1 || index.Sections.Count > 4096 || index.SectionsThrough < 0 || index.SectionsThrough > index.Length)
                throw new InvalidDataException("The exact text index is incomplete.");
            var expected = 0;
            var expectedLine = 1;
            foreach (var part in index.Parts)
            {
                if (part == null || part.Start != expected || part.Length < 1 || part.Length > TextPartCharacters ||
                    part.Payload == null || part.Payload.ByteLength > TextPartCharacters * 4L ||
                    part.FirstLine != expectedLine || part.LastLine < part.FirstLine)
                    throw new InvalidDataException("The text index has invalid part coverage.");
                expected += part.Length;
                expectedLine = part.LastLine;
            }
            if (expected != index.Length || expectedLine != index.TotalLines ||
                index.Symbols.Any(item => item == null || item.Line < 1 || item.Line > index.TotalLines ||
                    item.PartNumber < 1 || item.PartNumber > index.Parts.Count || string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 500) ||
                index.Sections.Any(item => item == null || item.Start < 0 || item.Start >= index.SectionsThrough ||
                item.Title == null || item.Title.Length > 201) || !index.Sections.Select(item => item.Start).SequenceEqual(index.Sections.Select(item => item.Start).Distinct().OrderBy(value => value)))
                throw new InvalidDataException("The text index does not cover its source.");
            var result = new ResourceSearchResult { Query = query, Matches = new List<ResourceSearchMatch>() };
            if (query.Length > index.Length) return result;
            if (query.Length > budget) { result.ScanTruncated = true; return result; }
            var tail = string.Empty;
            for (var partIndex = 0; partIndex < index.Parts.Count; partIndex++)
            {
                var part = index.Parts[partIndex];
                if (result.ScannedCharacters == budget) { result.ScanTruncated = true; break; }
                var text = _payloads.ReadText(part.Payload.ToBlobReference());
                if (text == null || text.Length != part.Length) throw new InvalidDataException("An exact text index part is unavailable.");
                var count = Math.Min(text.Length, budget - result.ScannedCharacters);
                var window = tail + text.Substring(0, count);
                var windowStart = part.Start - tail.Length;
                result.ScannedCharacters += count;
                for (var searchFrom = 0; searchFrom <= window.Length - query.Length;)
                {
                    var hit = window.IndexOf(query, searchFrom, matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
                    if (hit < 0) break;
                    searchFrom = hit + 1;
                    var offset = windowStart + hit;
                    // Text fully inside the overlap was counted on the prior part.
                    if (offset + query.Length <= part.Start) continue;
                    result.TotalMatches++;
                    if (result.Matches.Count >= maximumMatches) continue;
                    var snippetStart = Math.Max(0, hit - snippetCharacters / 3);
                    var section = offset < index.SectionsThrough ? index.Sections.LastOrDefault(item => item.Start <= offset) : null;
                    result.Matches.Add(new ResourceSearchMatch { Reference = captured.Reference.Copy(),
                        CreatedUtc = artifact.CreatedUtc, DocumentScoped = true, Kind = artifact.Kind, Title = artifact.Title,
                        Representation = "text", MatchOffset = offset, MatchLength = query.Length,
                        PartNumber = offset < part.Start ? partIndex : partIndex + 1,
                        SnippetOffset = windowStart + snippetStart,
                        Snippet = window.Substring(snippetStart, Math.Min(snippetCharacters, window.Length - snippetStart)),
                        SectionTitle = section?.Title });
                    // One occurrence per resource is the discovery contract. A hit
                    // resolves that resource; it does not assert whole-read coverage.
                    if (!scanAll) return result;
                }
                var retained = Math.Min(window.Length, query.Length - 1 + snippetCharacters / 3);
                tail = window.Substring(window.Length - retained);
                if (count < text.Length) result.ScanTruncated = true;
            }
            return result;
        }

        private sealed class TextIndex
        {
            public int Length { get; set; }
            public int TotalLines { get; set; } = 1;
            public int SectionsThrough { get; set; }
            public string SymbolLanguage { get; set; }
            public bool SymbolsComplete { get; set; } = true;
            public List<TextPart> Parts { get; set; } = new List<TextPart>();
            public List<TextSection> Sections { get; set; } = new List<TextSection>();
            public List<TextSymbol> Symbols { get; set; } = new List<TextSymbol>();
        }
        private sealed class TextPart
        {
            public int Start { get; set; }
            public int Length { get; set; }
            public int FirstLine { get; set; }
            public int LastLine { get; set; }
            public PayloadRef Payload { get; set; }
        }
        private sealed class TextSection
        {
            public int Start { get; set; }
            public string Title { get; set; }
        }
        private sealed class TextSymbol
        {
            public int Line { get; set; }
            public int PartNumber { get; set; }
            public string Name { get; set; }
            public string Kind { get; set; }
        }

        private static string CodeLanguage(string fileName)
        {
            switch (Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant())
            {
                case ".cs": return "csharp";
                case ".bas": case ".cls": case ".frm": case ".vba": return "vba";
                case ".js": case ".jsx": case ".mjs": case ".cjs": return "javascript";
                case ".ts": case ".tsx": return "typescript";
                default: return null;
            }
        }

        // Advisory declarations, not a compiler or mutation authority. Lines
        // longer than the prefix bound make the symbol map explicitly partial.
        private sealed class TextSymbolCollector
        {
            private static readonly Regex CSharpType = new Regex(
                @"^(?:(?:public|private|protected|internal|static|partial|abstract|sealed|readonly|file|unsafe|new)\s+)*(class|interface|struct|record|enum)\s+([A-Za-z_][A-Za-z0-9_]*)\b",
                RegexOptions.CultureInvariant);
            private static readonly Regex CSharpMethod = new Regex(
                @"^(?:(?:public|private|protected|internal|static|virtual|override|async|sealed|partial|extern|new)\s+)+[A-Za-z_][A-Za-z0-9_<>,?.\[\]\s]*\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(",
                RegexOptions.CultureInvariant);
            private static readonly Regex JsDeclaration = new Regex(
                @"^(?:(?:export|default|declare|async)\s+)*(function|class|interface|type|enum|const|let|var)\s+([A-Za-z_$][A-Za-z0-9_$]*)\b",
                RegexOptions.CultureInvariant);
            private static readonly Regex VbaDeclaration = new Regex(
                @"^(?:(?:Public|Private|Friend|Static)\s+)?(Sub|Function|Property(?:\s+(?:Get|Let|Set)))\s+([A-Za-z_][A-Za-z0-9_]*)\b",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            private readonly TextIndex _index;
            private readonly StringBuilder _line = new StringBuilder(500);
            private int _lineNumber = 1, _linePart = 1;
            private bool _longLine, _previousCr, _insideBlock;

            internal TextSymbolCollector(TextIndex index) { _index = index; }

            internal void Add(string text, int partNumber)
            {
                foreach (var character in text)
                {
                    if (character == '\r' || character == '\n')
                    {
                        if (character == '\n' && _previousCr) { _previousCr = false; continue; }
                        FinishLine();
                        _lineNumber++;
                        _previousCr = character == '\r';
                        continue;
                    }
                    _previousCr = false;
                    if (_line.Length == 0 && !_longLine) _linePart = partNumber;
                    if (_line.Length < 500) _line.Append(character);
                    else { _longLine = true; _index.SymbolsComplete = false; }
                }
            }

            internal void Finish() { if (_line.Length > 0 || _longLine) FinishLine(); }

            private void FinishLine()
            {
                var source = _line.ToString().TrimStart();
                _line.Clear();
                _longLine = false;
                if (source.Length == 0 || _index.Symbols.Count == 4096)
                {
                    if (_index.Symbols.Count == 4096) _index.SymbolsComplete = false;
                    return;
                }
                if (_index.SymbolLanguage != "vba")
                {
                    if (_insideBlock)
                    {
                        var end = source.IndexOf("*/", StringComparison.Ordinal);
                        if (end < 0) return;
                        source = source.Substring(end + 2).TrimStart();
                        _insideBlock = false;
                    }
                    while (source.StartsWith("/*", StringComparison.Ordinal))
                    {
                        var end = source.IndexOf("*/", 2, StringComparison.Ordinal);
                        if (end < 0) { _insideBlock = true; return; }
                        source = source.Substring(end + 2).TrimStart();
                    }
                    if (source.StartsWith("//", StringComparison.Ordinal) ||
                        source.StartsWith("*", StringComparison.Ordinal) ||
                        source.StartsWith("\"", StringComparison.Ordinal) ||
                        source.StartsWith("'", StringComparison.Ordinal) ||
                        source.StartsWith("`", StringComparison.Ordinal)) return;
                }
                else if (source.StartsWith("'", StringComparison.Ordinal) ||
                    source.StartsWith("Rem ", StringComparison.OrdinalIgnoreCase)) return;
                Match match;
                string kind, name;
                if (_index.SymbolLanguage == "csharp")
                {
                    match = CSharpType.Match(source);
                    if (match.Success) { kind = match.Groups[1].Value; name = match.Groups[2].Value; }
                    else
                    {
                        match = CSharpMethod.Match(source);
                        if (!match.Success) return;
                        kind = "method"; name = match.Groups[1].Value;
                    }
                }
                else if (_index.SymbolLanguage == "vba")
                {
                    match = VbaDeclaration.Match(source);
                    if (!match.Success) return;
                    kind = match.Groups[1].Value.ToLowerInvariant(); name = match.Groups[2].Value;
                }
                else
                {
                    match = JsDeclaration.Match(source);
                    if (!match.Success) return;
                    kind = match.Groups[1].Value; name = match.Groups[2].Value;
                }
                _index.Symbols.Add(new TextSymbol { Line = _lineNumber, PartNumber = _linePart, Kind = kind, Name = name });
            }
        }
    }
}
