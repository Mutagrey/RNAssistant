using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Storage
{
    public sealed partial class DocumentArtifactStore
    {
        private const string TextIndexView = "artifact-text-index-v1";
        private const string ExtractedTextIndexView = "artifact-extracted-text-index-v1";
        private const int TextPartCharacters = 32000;
        private const int MaximumIndexedCharacters = 2000000;

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
            if (body == null || body.ByteLength > MaximumIndexedCharacters * 4L)
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
            if (string.IsNullOrEmpty(query) || characterBudget < 1 || characterBudget > 1000000 ||
                snippetCharacters < 1 || snippetCharacters > 2000)
                throw new ArgumentException("A bounded text search is required.");
            var artifact = Read(session, reference, false);
            var original = artifact.OriginalAttachment;
            if (original == null && artifact.Kind != ChatArtifactKinds.Markdown && artifact.Kind != ChatArtifactKinds.PlanDocument)
                throw new InvalidDataException("This index requires a retained original text, Markdown or Plan snapshot.");
            var scope = Scope(session);
            var body = _revisions.GetRevision(scope, reference)?.Payload;
            if (body == null || body.Sha256 != artifact.ContentSha256 || body.ByteLength != artifact.ContentByteLength)
                throw new InvalidDataException("The exact source revision is unavailable.");
            if (original != null)
            {
                if (string.IsNullOrWhiteSpace(original.ExtractedTextSha256) || !original.ExtractedTextByteLength.HasValue ||
                    original.ExtractedCharCount < 0 || original.ExtractedCharCount > MaximumIndexedCharacters)
                    throw new InvalidDataException("The exact original text extraction is unavailable.");
                body = new PayloadRef(original.ExtractedTextSha256, original.ExtractedTextByteLength.Value, "text/plain; charset=utf-8");
            }
            if (!_payloads.HasStoredReference(body.ToBlobReference()))
                throw new InvalidDataException("The exact text source is unavailable.");
            var sourceIncomplete = original != null && (original.TextTruncated ||
                original.Kind == "pdf" && original.PageCount > (original.PageTextLengths?.Count ?? 0));
            var coverage = sourceIncomplete ? new ResourceCoverage(ResourceCoverageKinds.CharacterRange, start: 0, end: original.ExtractedCharCount) : ResourceCoverage.Whole();
            var view = original == null ? TextIndexView : ExtractedTextIndexView;
            var markdown = original == null || IsMarkdownOriginal(original);
            var result = SearchTextView(scope, reference, artifact, body, view, coverage, markdown, original?.ExtractedCharCount,
                query, characterBudget, snippetCharacters);
            result.ScanTruncated |= sourceIncomplete;
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
            if (!_payloads.HasStoredReference(_revisions.GetRevision(Scope(session), member.Parent).Payload.ToBlobReference()))
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
            string query, int characterBudget, int snippetCharacters, string derivedText = null)
        {
            var captured = _revisions.GetView(scope, reference, view);
            if (captured != null && (captured.ContentSha256 != body.Sha256 || JsonConvert.SerializeObject(captured.Coverage) != JsonConvert.SerializeObject(coverage)))
                throw new InvalidDataException("The text index does not match its exact source.");
            if (captured == null) captured = MaterializeTextIndex(scope, reference, body, view, coverage, markdown, expectedCharacters, derivedText);
            ResourceSearchResult result;
            try { result = SearchTextIndex(captured, artifact, query, characterBudget, snippetCharacters); }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is JsonException)
            {
                // Recreate only this deterministic view from the SAME exact body.
                // Missing derived bytes must not hide a healthy document. Immutable
                // view registration rejects any conflicting derivation.
                captured = MaterializeTextIndex(scope, reference, body, view, coverage, markdown, expectedCharacters, derivedText);
                result = SearchTextIndex(captured, artifact, query, characterBudget, snippetCharacters);
            }
            return result;
        }

        private ResourceRevisionView MaterializeTextIndex(ResourceAuthorityScopeId scope, ResourceRef reference, PayloadRef body,
            string view, ResourceCoverage coverage, bool markdown, int? expectedCharacters, string derivedText)
        {
            if (body.ByteLength > MaximumIndexedCharacters * 4L)
                throw new InvalidDataException("The text source exceeds the bounded index size.");
            var text = derivedText ?? _payloads.ReadText(body.ToBlobReference());
            if (text == null || text.Length > MaximumIndexedCharacters)
                throw new InvalidDataException("The exact text source is unavailable or exceeds the bounded index size.");
            if (expectedCharacters.HasValue && expectedCharacters.Value != text.Length)
                throw new InvalidDataException("The retained extraction length does not match its exact text.");
            if (derivedText != null) body = PayloadRef.FromBlob(_payloads.StoreText(derivedText, body.ContentType));
            var index = new TextIndex { Length = text.Length, SectionsThrough = text.Length };
            for (var start = 0; start < text.Length;)
            {
                var length = Math.Min(TextPartCharacters, text.Length - start);
                // Keep UTF-16 pairs together when storing UTF-8 parts.
                if (start + length < text.Length && char.IsHighSurrogate(text[start + length - 1]) && char.IsLowSurrogate(text[start + length])) length--;
                index.Parts.Add(new TextPart { Start = start, Length = length,
                    Payload = PayloadRef.FromBlob(_payloads.StoreText(text.Substring(start, length), "text/plain; charset=utf-8")) });
                start += length;
            }
            if (markdown) foreach (var heading in MarkdownHeadingScanner.Scan(text))
            {
                if (index.Sections.Count == 4096) { index.SectionsThrough = heading.Start; break; }
                var after = heading.ContentStart; var end = heading.LineEnd;
                // Preserve the v1 discovery label and manifest bytes.
                var title = text.Substring(after, Math.Min(201, end - after)).Trim();
                if (title.Length > 200 || end - after > 201) title = title.Substring(0, Math.Min(200, title.Length)) + "…";
                index.Sections.Add(new TextSection { Start = heading.Start, Title = title });
            }
            var payload = PayloadRef.FromBlob(_payloads.StoreText(JsonConvert.SerializeObject(index), "application/vnd.rnassistant.text-index+json"));
            var captured = new ResourceRevisionView(reference, view, body.Sha256, payload,
                coverage, index.Parts.Select(item => item.Payload).Concat(new[] { body }));
            _revisions.RegisterView(scope, captured);
            return captured;
        }

        private ResourceSearchResult SearchTextIndex(ResourceRevisionView captured, ChatArtifact artifact,
            string query, int budget, int snippetCharacters)
        {
            if (captured.Payload == null || captured.Payload.ByteLength > 8 * 1024 * 1024)
                throw new InvalidDataException("The text index is unavailable.");
            var json = _payloads.ReadText(captured.Payload.ToBlobReference());
            var index = json == null ? null : JsonConvert.DeserializeObject<TextIndex>(json);
            if (index == null || index.Length < 0 || index.Length > MaximumIndexedCharacters || index.Parts == null || index.Sections == null ||
                index.Parts.Count > 64 || index.Sections.Count > 4096 || index.SectionsThrough < 0 || index.SectionsThrough > index.Length)
                throw new InvalidDataException("The exact text index is incomplete.");
            var expected = 0;
            foreach (var part in index.Parts)
            {
                if (part == null || part.Start != expected || part.Length < 1 || part.Length > TextPartCharacters ||
                    part.Payload == null || part.Payload.ByteLength > TextPartCharacters * 4L)
                    throw new InvalidDataException("The text index has invalid part coverage.");
                expected += part.Length;
            }
            if (expected != index.Length || index.Sections.Any(item => item == null || item.Start < 0 || item.Start >= index.SectionsThrough ||
                item.Title == null || item.Title.Length > 201) || !index.Sections.Select(item => item.Start).SequenceEqual(index.Sections.Select(item => item.Start).Distinct().OrderBy(value => value)))
                throw new InvalidDataException("The text index does not cover its source.");
            var result = new ResourceSearchResult { Query = query, Matches = new List<ResourceSearchMatch>() };
            if (query.Length > index.Length) return result;
            if (query.Length > budget) { result.ScanTruncated = true; return result; }
            var tail = string.Empty;
            foreach (var part in index.Parts)
            {
                if (result.ScannedCharacters == budget) { result.ScanTruncated = true; break; }
                var text = _payloads.ReadText(part.Payload.ToBlobReference());
                if (text == null || text.Length != part.Length) throw new InvalidDataException("An exact text index part is unavailable.");
                var count = Math.Min(text.Length, budget - result.ScannedCharacters);
                var window = tail + text.Substring(0, count);
                var windowStart = part.Start - tail.Length;
                result.ScannedCharacters += count;
                var hit = window.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                if (hit >= 0)
                {
                    var offset = windowStart + hit;
                    var snippetStart = Math.Max(0, hit - snippetCharacters / 3);
                    var section = offset < index.SectionsThrough ? index.Sections.LastOrDefault(item => item.Start <= offset) : null;
                    result.Matches.Add(new ResourceSearchMatch { Reference = captured.Reference.Copy(),
                        CreatedUtc = artifact.CreatedUtc, DocumentScoped = true, Kind = artifact.Kind, Title = artifact.Title,
                        Representation = "text", MatchOffset = offset, MatchLength = query.Length,
                        SnippetOffset = windowStart + snippetStart,
                        Snippet = window.Substring(snippetStart, Math.Min(snippetCharacters, window.Length - snippetStart)),
                        SectionTitle = section?.Title });
                    // One occurrence per resource is the discovery contract. A hit
                    // resolves that resource; it does not assert whole-read coverage.
                    return result;
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
            public int SectionsThrough { get; set; }
            public List<TextPart> Parts { get; set; } = new List<TextPart>();
            public List<TextSection> Sections { get; set; } = new List<TextSection>();
        }
        private sealed class TextPart
        {
            public int Start { get; set; }
            public int Length { get; set; }
            public PayloadRef Payload { get; set; }
        }
        private sealed class TextSection
        {
            public int Start { get; set; }
            public string Title { get; set; }
        }
    }
}
