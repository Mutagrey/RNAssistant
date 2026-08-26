using System;
using System.Collections.Generic;
using System.Text;

namespace RNAssistant.Core.Tools
{
    /// <summary>
    /// Incrementally projects the root conversation-envelope message string from raw JSON chunks.
    /// Nested message properties and the rest of the envelope are intentionally ignored.
    /// </summary>
    public sealed class ConversationMessageStreamExtractor
    {
        private enum ParseState
        {
            BeforeRoot,
            RootPropertyOrEnd,
            RootColon,
            RootValue,
            RootAfterValue,
            String,
            SkipComposite,
            SkipPrimitive,
            Done,
            Invalid
        }

        private enum StringTarget
        {
            None,
            Property,
            Message,
            Skip
        }

        private readonly StringBuilder _property = new StringBuilder();
        private readonly StringBuilder _output = new StringBuilder();
        private readonly Stack<char> _compositeClosers = new Stack<char>();
        private ParseState _state = ParseState.BeforeRoot;
        private StringTarget _stringTarget;
        private string _currentProperty;
        private bool _messageSeen;
        private bool _stringEscape;
        private int _unicodeDigitsRemaining;
        private int _unicodeValue;
        private char? _pendingMessageHighSurrogate;
        private bool _compositeInString;
        private bool _compositeEscape;

        public bool MessageCompleted { get; private set; }

        public string Add(string value)
        {
            if (!string.IsNullOrEmpty(value) && _state != ParseState.Invalid)
            {
                for (var index = 0; index < value.Length; index++)
                {
                    Process(value[index]);
                    if (_state == ParseState.Invalid) break;
                }
            }
            return DrainOutput();
        }

        public string Complete()
        {
            return DrainOutput();
        }

        private void Process(char value)
        {
            var reprocess = true;
            while (reprocess && _state != ParseState.Invalid)
            {
                reprocess = false;
                switch (_state)
                {
                    case ParseState.BeforeRoot:
                        if (IsLeadingWhitespace(value)) return;
                        if (value != '{')
                        {
                            Invalidate();
                            return;
                        }
                        _state = ParseState.RootPropertyOrEnd;
                        return;

                    case ParseState.RootPropertyOrEnd:
                        if (char.IsWhiteSpace(value)) return;
                        if (value == '}')
                        {
                            _state = ParseState.Done;
                            return;
                        }
                        if (value != '"')
                        {
                            Invalidate();
                            return;
                        }
                        BeginString(StringTarget.Property);
                        return;

                    case ParseState.RootColon:
                        if (char.IsWhiteSpace(value)) return;
                        if (value != ':')
                        {
                            Invalidate();
                            return;
                        }
                        _state = ParseState.RootValue;
                        return;

                    case ParseState.RootValue:
                        if (char.IsWhiteSpace(value)) return;
                        if (!_messageSeen && string.Equals(_currentProperty, "message", StringComparison.Ordinal))
                        {
                            if (value != '"')
                            {
                                Invalidate();
                                return;
                            }
                            BeginString(StringTarget.Message);
                            return;
                        }
                        BeginSkippedValue(value);
                        return;

                    case ParseState.RootAfterValue:
                        if (char.IsWhiteSpace(value)) return;
                        if (value == ',')
                        {
                            _currentProperty = null;
                            _state = ParseState.RootPropertyOrEnd;
                            return;
                        }
                        if (value == '}')
                        {
                            _state = ParseState.Done;
                            return;
                        }
                        Invalidate();
                        return;

                    case ParseState.String:
                        ProcessString(value);
                        return;

                    case ParseState.SkipComposite:
                        ProcessComposite(value);
                        return;

                    case ParseState.SkipPrimitive:
                        if (char.IsWhiteSpace(value))
                        {
                            _state = ParseState.RootAfterValue;
                            return;
                        }
                        if (value == ',' || value == '}')
                        {
                            _state = ParseState.RootAfterValue;
                            reprocess = true;
                            continue;
                        }
                        if (value == '"' || value == '{' || value == '[')
                        {
                            Invalidate();
                        }
                        return;

                    case ParseState.Done:
                        if (!char.IsWhiteSpace(value)) Invalidate();
                        return;

                    default:
                        return;
                }
            }
        }

        private void BeginSkippedValue(char value)
        {
            if (value == '"')
            {
                BeginString(StringTarget.Skip);
                return;
            }
            if (value == '{' || value == '[')
            {
                _compositeClosers.Clear();
                _compositeClosers.Push(value == '{' ? '}' : ']');
                _compositeInString = false;
                _compositeEscape = false;
                _state = ParseState.SkipComposite;
                return;
            }
            if (value == ',' || value == '}')
            {
                Invalidate();
                return;
            }
            _state = ParseState.SkipPrimitive;
        }

        private void ProcessComposite(char value)
        {
            if (_compositeInString)
            {
                if (_compositeEscape)
                {
                    _compositeEscape = false;
                    return;
                }
                if (value == '\\')
                {
                    _compositeEscape = true;
                    return;
                }
                if (value == '"') _compositeInString = false;
                return;
            }

            if (value == '"')
            {
                _compositeInString = true;
                return;
            }
            if (value == '{' || value == '[')
            {
                _compositeClosers.Push(value == '{' ? '}' : ']');
                return;
            }
            if (value != '}' && value != ']') return;
            if (_compositeClosers.Count == 0 || _compositeClosers.Peek() != value)
            {
                Invalidate();
                return;
            }
            _compositeClosers.Pop();
            if (_compositeClosers.Count == 0) _state = ParseState.RootAfterValue;
        }

        private void BeginString(StringTarget target)
        {
            _stringTarget = target;
            _stringEscape = false;
            _unicodeDigitsRemaining = 0;
            _unicodeValue = 0;
            _pendingMessageHighSurrogate = null;
            if (target == StringTarget.Property) _property.Clear();
            _state = ParseState.String;
        }

        private void ProcessString(char value)
        {
            if (_unicodeDigitsRemaining > 0)
            {
                var digit = HexDigit(value);
                if (digit < 0)
                {
                    Invalidate();
                    return;
                }
                _unicodeValue = (_unicodeValue << 4) | digit;
                _unicodeDigitsRemaining -= 1;
                if (_unicodeDigitsRemaining == 0) AppendStringCharacter((char)_unicodeValue);
                return;
            }

            if (_stringEscape)
            {
                _stringEscape = false;
                switch (value)
                {
                    case '"': AppendStringCharacter('"'); return;
                    case '\\': AppendStringCharacter('\\'); return;
                    case '/': AppendStringCharacter('/'); return;
                    case 'b': AppendStringCharacter('\b'); return;
                    case 'f': AppendStringCharacter('\f'); return;
                    case 'n': AppendStringCharacter('\n'); return;
                    case 'r': AppendStringCharacter('\r'); return;
                    case 't': AppendStringCharacter('\t'); return;
                    case 'u':
                        _unicodeDigitsRemaining = 4;
                        _unicodeValue = 0;
                        return;
                    default:
                        Invalidate();
                        return;
                }
            }

            if (value == '\\')
            {
                _stringEscape = true;
                return;
            }
            if (value == '"')
            {
                EndString();
                return;
            }
            if (value < 0x20)
            {
                Invalidate();
                return;
            }
            AppendStringCharacter(value);
        }

        private void AppendStringCharacter(char value)
        {
            if (_stringTarget == StringTarget.Property)
            {
                _property.Append(value);
                return;
            }
            if (_stringTarget != StringTarget.Message) return;

            if (_pendingMessageHighSurrogate.HasValue)
            {
                var high = _pendingMessageHighSurrogate.Value;
                _pendingMessageHighSurrogate = null;
                _output.Append(high);
                if (char.IsLowSurrogate(value))
                {
                    _output.Append(value);
                    return;
                }
            }
            if (char.IsHighSurrogate(value))
            {
                _pendingMessageHighSurrogate = value;
                return;
            }
            _output.Append(value);
        }

        private void EndString()
        {
            if (_stringTarget == StringTarget.Property)
            {
                _currentProperty = _property.ToString();
                _state = ParseState.RootColon;
            }
            else
            {
                if (_stringTarget == StringTarget.Message)
                {
                    if (_pendingMessageHighSurrogate.HasValue)
                    {
                        _output.Append(_pendingMessageHighSurrogate.Value);
                    }
                    _messageSeen = true;
                    MessageCompleted = true;
                }
                _state = ParseState.RootAfterValue;
            }
            _pendingMessageHighSurrogate = null;
            _stringTarget = StringTarget.None;
            _stringEscape = false;
            _unicodeDigitsRemaining = 0;
        }

        private void Invalidate()
        {
            _state = ParseState.Invalid;
            _pendingMessageHighSurrogate = null;
        }

        private string DrainOutput()
        {
            if (_output.Length == 0) return string.Empty;
            var value = _output.ToString();
            _output.Clear();
            return value;
        }

        private static bool IsLeadingWhitespace(char value)
        {
            return value == '\uFEFF' || char.IsWhiteSpace(value);
        }

        private static int HexDigit(char value)
        {
            if (value >= '0' && value <= '9') return value - '0';
            if (value >= 'a' && value <= 'f') return value - 'a' + 10;
            if (value >= 'A' && value <= 'F') return value - 'A' + 10;
            return -1;
        }
    }
}
