using System;
using System.IO;
using System.Text;
using System.Threading;
using RNAssistant.Core.Storage;

namespace RNAssistant.Core.Services
{
    // Bounded UTF-16 window over a verified UTF-8 source. Consume the full stream
    // for integrity and coordinates while retaining only the selected text.
    public sealed class ResourceTextWindow
    {
        private readonly int _requestedStart, _requestedCount;
        private readonly bool _lines;
        private readonly int _maximumCharacters;
        private readonly bool _trimSplitEnd;
        private readonly StringBuilder _selected = new StringBuilder();
        private int _line = 1;
        private bool _pendingCr;
        private char _previous;
        public int Start { get; private set; }
        public int End { get; private set; }
        public int TotalCharacters { get; private set; }
        public string Text { get { return _selected.ToString(); } }

        public ResourceTextWindow(int requestedStart, int requestedCount, bool lines, int maximumCharacters,
            bool trimSplitEnd = false)
        {
            _requestedStart = requestedStart; _requestedCount = requestedCount; _lines = lines;
            _maximumCharacters = maximumCharacters; _trimSplitEnd = trimSplitEnd;
            Start = lines ? requestedStart == 1 ? 0 : -1 : requestedStart;
            End = -1;
        }

        public void Read(Stream input, CancellationToken token = default(CancellationToken))
        {
            var decoder = new UTF8Encoding(false, true).GetDecoder();
            var bytes = new byte[81920];
            var chars = new char[81922];
            try
            {
                int count;
                while ((count = input.Read(bytes, 0, bytes.Length)) != 0)
                {
                    token.ThrowIfCancellationRequested();
                    var length = decoder.GetChars(bytes, 0, count, chars, 0, false);
                    for (var i = 0; i < length; i++) Accept(chars[i]);
                }
                var tail = decoder.GetChars(Array.Empty<byte>(), 0, 0, chars, 0, true);
                for (var i = 0; i < tail; i++) Accept(chars[i]);
            }
            catch (DecoderFallbackException)
            { throw new WorkspaceFileException("encoding_ambiguous", "Only strict UTF-8 text is supported."); }
            Finish();
        }

        private void Accept(char value)
        {
            if (TotalCharacters == int.MaxValue)
                throw new WorkspaceFileException("file_too_large",
                    "Text exceeds the current UTF-16 coordinate range.");
            var offset = TotalCharacters;
            if (_lines && _pendingCr && value != '\n') AdvanceLine(offset);
            if (!_lines && (offset == Start || offset == (long)Start + _requestedCount) &&
                char.IsLowSurrogate(value) && char.IsHighSurrogate(_previous))
            {
                if (offset == Start || !_trimSplitEnd)
                    throw new WorkspaceFileException("resource_chars_invalid",
                        "The selected character range splits a UTF-16 surrogate pair. Adjust startChar or charCount by one.");
                _selected.Length--;
                End = offset - 1;
            }
            var selected = _lines
                ? _line >= _requestedStart && (long)_line < (long)_requestedStart + _requestedCount
                : offset >= Start && (long)offset < (long)Start + _requestedCount;
            if (selected)
            {
                if (_selected.Length == _maximumCharacters)
                    throw new WorkspaceFileException("read_too_large",
                        "Selected lines exceed " + _maximumCharacters + " characters; reduce lineCount or use startChar/charCount for a long line.");
                _selected.Append(value);
            }
            TotalCharacters++;
            if (_lines)
            {
                if (value == '\n') { _pendingCr = false; AdvanceLine(TotalCharacters); }
                else _pendingCr = value == '\r';
            }
            _previous = value;
        }

        private void AdvanceLine(int offset)
        {
            _line++;
            if (_line == _requestedStart) Start = offset;
            if ((long)_line == (long)_requestedStart + _requestedCount) End = offset;
        }

        private void Finish()
        {
            if (_lines && _pendingCr) AdvanceLine(TotalCharacters);
            if (_lines && Start < 0)
                throw new WorkspaceFileException("resource_lines_invalid",
                    "startLine exceeds the current source's " + _line + " lines.");
            if (!_lines && Start > TotalCharacters)
                throw new WorkspaceFileException("resource_chars_invalid",
                    "startChar exceeds the current source's " + TotalCharacters + " characters.");
            if (End < 0) End = _lines ? TotalCharacters : (int)Math.Min((long)TotalCharacters, (long)Start + _requestedCount);
        }
    }

}
