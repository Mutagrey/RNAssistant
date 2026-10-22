using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Domains.Word;

namespace RNAssistant.Office.Services
{
    internal sealed partial class LiveDocumentResourceProvider
    {
        internal const string WordRangeKind = "word-text-range";
        internal const string WordSearchKind = "word-search-scope";
        private readonly IWordBackend _word;
        internal bool IsWord { get { return string.Equals(_adapter.HostName, "Word", StringComparison.OrdinalIgnoreCase); } }

        internal ResourceDescriptor ResolveWordSearch(ChatSession session, string scope)
        {
            if (!IsWord || _word == null)
                throw new ResourceRequestException("The bound Word reader is unavailable.", "RESOURCE_PROVIDER_UNAVAILABLE", false);
            if (scope != "main" && scope != "selection" && scope != "all")
                throw new ResourceRequestException("scope must be main, selection, or all.", "word_scope_invalid", false);
            return _scope.Read(session, () => Describe(session, "stories-" + scope));
        }

        private static bool IsWordSearch(string target)
        { return target == "stories-main" || target == "stories-selection" || target == "stories-all"; }

        private string ReadWordSearch(string target)
        {
            if (_word == null)
                throw new ResourceRequestException("The bound Word reader is unavailable.", "RESOURCE_PROVIDER_UNAVAILABLE", false);
            WordSearchSnapshot snapshot;
            try { snapshot = new WordService(_word).CaptureSearch(target.Substring(8), CancellationToken.None); }
            catch (WordBackendException error)
            { throw new ResourceRequestException(error.Message, error.ErrorCode, error.Retryable); }
            var json = new JObject { ["scope"] = snapshot.Scope,
                ["stories"] = new JArray(snapshot.Stories.Select(story => new JObject {
                    ["kind"] = story.Kind, ["start"] = story.Start, ["end"] = story.End, ["text"] = story.Text }))
            }.ToString(Formatting.None);
            if (json.Length > MaximumMaterializedCharacters)
                throw new ResourceRequestException("The Word search snapshot exceeds the serialized bound.", "RESOURCE_SNAPSHOT_TOO_LARGE", false);
            return json;
        }

        internal ResourceDescriptor ResolveWordRange(ChatSession session, string target)
        {
            if (!IsWord || _word == null)
                throw new ResourceRequestException("The bound Word reader is unavailable.", "RESOURCE_PROVIDER_UNAVAILABLE", false);
            var key = "range-" + target.Substring("Word range: ".Length).Replace(':', '-');
            WordRange(key);
            if (target != "Word range: " + key.Substring(6).Replace('-', ':'))
                throw new ResourceRequestException("Use Word range: start:end.", "RESOURCE_TARGET_INVALID", false);
            return _scope.Read(session, () => Describe(session, key));
        }

        private static bool IsWordRange(string target)
        {
            try { WordRange(target); return true; }
            catch (ResourceRequestException) { return false; }
        }

        internal static WordTextReadRequest WordRange(string target)
        {
            var parts = (target ?? string.Empty).Split('-');
            int start, end;
            if (parts.Length != 3 || parts[0] != "range" ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out start) ||
                !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out end) ||
                start < 0 || end < start ||
                target != "range-" + start.ToString(CultureInfo.InvariantCulture) + "-" + end.ToString(CultureInfo.InvariantCulture))
                throw new ResourceRequestException("Use Word range: start:end with zero-based inclusive start and exclusive end.", "RESOURCE_TARGET_INVALID", false);
            if ((long)end - start > WordService.MaximumTextCharacters)
                throw new ResourceRequestException("Choose a narrower Word character range.", "RESOURCE_SNAPSHOT_TOO_LARGE", false);
            return new WordTextReadRequest { Source = "range", Start = start, End = end, HasEnd = true,
                MaxChars = WordService.MaximumTextCharacters };
        }

        private string ReadWordText(string target)
        {
            if (_word == null)
                throw new ResourceRequestException("The bound Word reader is unavailable.", "RESOURCE_PROVIDER_UNAVAILABLE", false);
            var request = target.StartsWith("range-", StringComparison.Ordinal) ? WordRange(target) :
                new WordTextReadRequest { Source = target == "selection" ? "selection" : "document",
                    MaxChars = WordService.MaximumTextCharacters };
            try { return new WordService(_word).CaptureText(request, CancellationToken.None).Text; }
            catch (WordBackendException error)
            { throw new ResourceRequestException(error.Message, error.ErrorCode, error.Retryable); }
        }

        private ResourceReadSelection ReadWordCharacterWindow(ChatSession session, ResourceRef reference,
            string representation, int startChar, int charCount, CancellationToken cancellationToken)
        {
            if (!IsWord || representation != ResourceRepresentations.Text) return null;
            if (_word == null)
                throw new ResourceRequestException("The bound Word reader is unavailable.", "RESOURCE_PROVIDER_UNAVAILABLE", false);
            cancellationToken.ThrowIfCancellationRequested();
            return _scope.Read(session, () =>
            {
                string target;
                if (!TryParseUri(session, reference?.Uri, out target) || target != "root") return null;
                WordTextBounds bounds;
                try { bounds = new WordService(_word).CaptureMainStoryBounds(cancellationToken); }
                catch (WordBackendException error)
                { throw new ResourceRequestException(error.Message, error.ErrorCode, error.Retryable); }
                if (startChar < bounds.Start || startChar > bounds.End)
                    throw new ResourceRequestException("startChar is outside the current Word main story.",
                        "resource_chars_invalid", false);
                var end = (int)Math.Min((long)bounds.End, (long)startChar + charCount);
                var selectedTarget = "range-" + startChar.ToString(CultureInfo.InvariantCulture) +
                    "-" + end.ToString(CultureInfo.InvariantCulture);
                var selected = Read(session, new ResourceReadRequest {
                    Reference = new ResourceRef(CreateUri(session, selectedTarget)),
                    Representation = ResourceRepresentations.Text, MaxChars = charCount
                }, cancellationToken);
                var text = selected.Result.Text;
                selected.Result.SourceEndChar = bounds.End;
                if (text.Length > 0 &&
                    (char.IsLowSurrogate(text[0]) || char.IsHighSurrogate(text[text.Length - 1])))
                    throw new ResourceRequestException("The selected Word range splits a UTF-16 character.",
                        "resource_chars_invalid", false);
                return selected;
            });
        }

        private string ReadWordStructure()
        {
            if (_word == null)
                throw new ResourceRequestException("The bound Word reader is unavailable.", "RESOURCE_PROVIDER_UNAVAILABLE", false);
            try
            {
                var bounds = new WordService(_word).CaptureMainStoryBounds(CancellationToken.None);
                return new JObject
                {
                    ["type"] = "rnassistant.wordMainStory",
                    ["start"] = bounds.Start,
                    ["end"] = bounds.End,
                    ["rangeTargetFormat"] = "Word range: start:end",
                    ["maxRangeCharacters"] = WordService.MaximumTextCharacters
                }.ToString(Formatting.None);
            }
            catch (WordBackendException error)
            { throw new ResourceRequestException(error.Message, error.ErrorCode, error.Retryable); }
        }
    }
}
