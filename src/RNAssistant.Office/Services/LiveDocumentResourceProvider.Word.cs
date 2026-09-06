using System;
using System.Globalization;
using System.Threading;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Domains.Word;

namespace RNAssistant.Office.Services
{
    internal sealed partial class LiveDocumentResourceProvider
    {
        internal const string WordRangeKind = "word-text-range";
        private readonly IWordBackend _word;
        private readonly ChatBlobStore _payloads;
        private bool IsWord { get { return string.Equals(_adapter.HostName, "Word", StringComparison.OrdinalIgnoreCase); } }

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

        private static WordTextReadRequest WordRange(string target)
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
    }
}
