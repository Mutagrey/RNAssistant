using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    public sealed partial class DocumentArtifactStore
    {
        private const string TextIndexView = "artifact-text-index-v1";
        private const int TextPartCharacters = 32000;
        private const int MaximumIndexedCharacters = 2000000;

        // A derived view of an exact, published Markdown/Plan body in the existing
        // revision journal/CAS. This is neither an inventory nor read evidence.
        public ResourceSearchResult SearchText(ChatSession session, ResourceRef reference,
            string query, int characterBudget, int snippetCharacters)
        {
            if (string.IsNullOrEmpty(query) || characterBudget < 1 || characterBudget > 1000000 ||
                snippetCharacters < 1 || snippetCharacters > 2000)
                throw new ArgumentException("A bounded text search is required.");
            var artifact = Read(session, reference, false);
            if (artifact.Kind != ChatArtifactKinds.Markdown && artifact.Kind != ChatArtifactKinds.PlanDocument)
                throw new InvalidDataException("This index requires an authored Markdown or Plan snapshot.");
            var scope = Scope(session);
            var body = _revisions.GetRevision(scope, reference)?.Payload;
            if (body == null || body.Sha256 != artifact.ContentSha256 || body.ByteLength != artifact.ContentByteLength ||
                !_payloads.HasStoredReference(body.ToBlobReference()))
                throw new InvalidDataException("The exact text source is unavailable.");
            var captured = _revisions.GetView(scope, reference, TextIndexView);
            if (captured != null && captured.ContentSha256 != body.Sha256)
                throw new InvalidDataException("The text index does not match its exact source.");
            if (captured == null) captured = MaterializeTextIndex(scope, reference, body);
            try { return SearchTextIndex(captured, artifact, query, characterBudget, snippetCharacters); }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is JsonException)
            {
                // Recreate only this deterministic view from the SAME exact body.
                // Missing derived bytes must not hide a healthy document. Immutable
                // view registration rejects any conflicting derivation.
                captured = MaterializeTextIndex(scope, reference, body);
                return SearchTextIndex(captured, artifact, query, characterBudget, snippetCharacters);
            }
        }

        private ResourceRevisionView MaterializeTextIndex(ResourceAuthorityScopeId scope, ResourceRef reference, PayloadRef body)
        {
            if (body.ByteLength > MaximumIndexedCharacters * 4L)
                throw new InvalidDataException("The text source exceeds the bounded index size.");
            var text = _payloads.ReadText(body.ToBlobReference());
            if (text == null || text.Length > MaximumIndexedCharacters)
                throw new InvalidDataException("The exact text source is unavailable or exceeds the bounded index size.");
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
            char fence = '\0'; int fenceLength = 0;
            for (var start = 0; start < text.Length;)
            {
                var end = text.IndexOf('\n', start); if (end < 0) end = text.Length;
                var position = start;
                while (position < end && position - start < 4 && text[position] == ' ') position++;
                if (position - start < 4 && position < end)
                {
                    var marker = text[position]; var after = position;
                    while (after < end && text[after] == marker) after++;
                    var count = after - position;
                    if ((marker == '`' || marker == '~') && count >= 3)
                    {
                        if (fence == '\0') { fence = marker; fenceLength = count; }
                        else if (fence == marker && count >= fenceLength && string.IsNullOrWhiteSpace(text.Substring(after, end - after))) fence = '\0';
                    }
                    else if (fence == '\0' && marker == '#' && count <= 6 && (after == end || char.IsWhiteSpace(text[after])))
                    {
                        if (index.Sections.Count == 4096) { index.SectionsThrough = start; break; }
                        var title = text.Substring(after, Math.Min(201, end - after)).Trim();
                        if (title.Length > 200 || end - after > 201) title = title.Substring(0, Math.Min(200, title.Length)) + "…";
                        index.Sections.Add(new TextSection { Start = start, Title = title });
                    }
                }
                start = end + 1;
            }
            var payload = PayloadRef.FromBlob(_payloads.StoreText(JsonConvert.SerializeObject(index), "application/vnd.rnassistant.text-index+json"));
            var captured = new ResourceRevisionView(reference, TextIndexView, body.Sha256, payload,
                ResourceCoverage.Whole(), index.Parts.Select(item => item.Payload).Concat(new[] { body }));
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
