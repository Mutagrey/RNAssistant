using System;
using System.Collections.Generic;
using System.IO;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    // Validates RFC 8259 JSON syntax as chars flow to JsonTextReader. Json.NET's
    // reader alone accepts comments, single quotes, unquoted keys and trailing
    // commas; none may become an exact JSON navigation observation.
    internal sealed class StrictJsonSourceReader : TextReader
    {
        private enum Expect { RootValue, RootDone, ObjectKeyOrEnd, ObjectKey,
            ObjectColon, ObjectValue, ObjectCommaOrEnd, ArrayValueOrEnd, ArrayValue,
            ArrayCommaOrEnd }
        private enum Mode { Normal, String, Escape, Unicode, Number, Literal }
        private enum NumberState { Sign, Zero, Integer, Dot, Fraction, Exponent,
            ExponentSign, ExponentDigits }
        private sealed class Frame { internal bool Object; internal Expect State; }

        private readonly TextReader _inner;
        private readonly List<Frame> _stack = new List<Frame>();
        private Mode _mode;
        private NumberState _number;
        private string _literal;
        private int _literalPosition;
        private int _unicodeRemaining;
        private bool _stringIsKey;
        private bool _rootDone;
        private bool _seenAny;
        private bool _ended;

        internal StrictJsonSourceReader(TextReader inner)
        { _inner = inner ?? throw new ArgumentNullException(nameof(inner)); }

        public override int Read(char[] buffer, int index, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (index < 0 || count < 0 || index > buffer.Length - count) throw new ArgumentOutOfRangeException();
            if (count == 0) return 0;
            var read = _inner.Read(buffer, index, count);
            for (var i = index; i < index + read; i++) Accept(buffer[i]);
            if (read == 0) Finish();
            return read;
        }

        private Expect Current { get { return _stack.Count == 0
            ? (_rootDone ? Expect.RootDone : Expect.RootValue)
            : _stack[_stack.Count - 1].State; } }

        private void Accept(char c)
        {
            if (!_seenAny && c == '\uFEFF') { _seenAny = true; return; }
            _seenAny = true;
            switch (_mode)
            {
                case Mode.String:
                    if (c == '"')
                    {
                        _mode = Mode.Normal;
                        if (_stringIsKey) _stack[_stack.Count - 1].State = Expect.ObjectColon;
                        else ValueComplete();
                    }
                    else if (c == '\\') _mode = Mode.Escape;
                    else if (c < 0x20) Invalid();
                    return;
                case Mode.Escape:
                    if (c == 'u') { _mode = Mode.Unicode; _unicodeRemaining = 4; return; }
                    if (c == '"' || c == '\\' || c == '/' || c == 'b' || c == 'f' ||
                        c == 'n' || c == 'r' || c == 't') { _mode = Mode.String; return; }
                    Invalid(); return;
                case Mode.Unicode:
                    if (!IsHex(c)) Invalid();
                    if (--_unicodeRemaining == 0) _mode = Mode.String;
                    return;
                case Mode.Literal:
                    if (c != _literal[_literalPosition++]) Invalid();
                    if (_literalPosition == _literal.Length) { _mode = Mode.Normal; ValueComplete(); }
                    return;
                case Mode.Number:
                    if (NumberCharacter(c)) return;
                    FinishNumber();
                    _mode = Mode.Normal;
                    ValueComplete();
                    break; // Process the delimiter below.
            }
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n') return;
            switch (c)
            {
                case '{':
                    RequireValue();
                    _stack.Add(new Frame { Object = true, State = Expect.ObjectKeyOrEnd });
                    return;
                case '[':
                    RequireValue();
                    _stack.Add(new Frame { Object = false, State = Expect.ArrayValueOrEnd });
                    return;
                case '}':
                    if (Current != Expect.ObjectKeyOrEnd && Current != Expect.ObjectCommaOrEnd) Invalid();
                    _stack.RemoveAt(_stack.Count - 1); ValueComplete(); return;
                case ']':
                    if (Current != Expect.ArrayValueOrEnd && Current != Expect.ArrayCommaOrEnd) Invalid();
                    _stack.RemoveAt(_stack.Count - 1); ValueComplete(); return;
                case ':':
                    if (Current != Expect.ObjectColon) Invalid();
                    _stack[_stack.Count - 1].State = Expect.ObjectValue; return;
                case ',':
                    if (Current == Expect.ObjectCommaOrEnd)
                        _stack[_stack.Count - 1].State = Expect.ObjectKey;
                    else if (Current == Expect.ArrayCommaOrEnd)
                        _stack[_stack.Count - 1].State = Expect.ArrayValue;
                    else Invalid();
                    return;
                case '"':
                    _stringIsKey = Current == Expect.ObjectKeyOrEnd || Current == Expect.ObjectKey;
                    if (!_stringIsKey) RequireValue();
                    _mode = Mode.String; return;
                case 't': StartLiteral("true"); return;
                case 'f': StartLiteral("false"); return;
                case 'n': StartLiteral("null"); return;
                case '-':
                    RequireValue(); _mode = Mode.Number; _number = NumberState.Sign; return;
                default:
                    if (c >= '0' && c <= '9')
                    {
                        RequireValue(); _mode = Mode.Number;
                        _number = c == '0' ? NumberState.Zero : NumberState.Integer;
                        return;
                    }
                    Invalid(); return;
            }
        }

        private void StartLiteral(string literal)
        {
            RequireValue(); _mode = Mode.Literal; _literal = literal; _literalPosition = 1;
        }

        private void RequireValue()
        {
            var state = Current;
            if (state != Expect.RootValue && state != Expect.ObjectValue &&
                state != Expect.ArrayValueOrEnd && state != Expect.ArrayValue) Invalid();
        }

        private void ValueComplete()
        {
            if (_stack.Count == 0) { if (_rootDone) Invalid(); _rootDone = true; return; }
            var frame = _stack[_stack.Count - 1];
            if (frame.Object && frame.State == Expect.ObjectValue) frame.State = Expect.ObjectCommaOrEnd;
            else if (!frame.Object && (frame.State == Expect.ArrayValueOrEnd || frame.State == Expect.ArrayValue))
                frame.State = Expect.ArrayCommaOrEnd;
            else Invalid();
        }

        private bool NumberCharacter(char c)
        {
            switch (_number)
            {
                case NumberState.Sign:
                    if (c >= '0' && c <= '9') { _number = c == '0' ? NumberState.Zero : NumberState.Integer; return true; }
                    Invalid(); return false;
                case NumberState.Zero:
                    if (c >= '0' && c <= '9') Invalid();
                    break;
                case NumberState.Integer:
                    if (c >= '0' && c <= '9') return true;
                    break;
                case NumberState.Dot:
                    if (c >= '0' && c <= '9') { _number = NumberState.Fraction; return true; }
                    Invalid(); return false;
                case NumberState.Fraction:
                    if (c >= '0' && c <= '9') return true;
                    break;
                case NumberState.Exponent:
                    if (c == '+' || c == '-') { _number = NumberState.ExponentSign; return true; }
                    if (c >= '0' && c <= '9') { _number = NumberState.ExponentDigits; return true; }
                    Invalid(); return false;
                case NumberState.ExponentSign:
                    if (c >= '0' && c <= '9') { _number = NumberState.ExponentDigits; return true; }
                    Invalid(); return false;
                case NumberState.ExponentDigits:
                    if (c >= '0' && c <= '9') return true;
                    break;
            }
            if (c == '.' && (_number == NumberState.Zero || _number == NumberState.Integer))
            { _number = NumberState.Dot; return true; }
            if ((c == 'e' || c == 'E') && (_number == NumberState.Zero ||
                _number == NumberState.Integer || _number == NumberState.Fraction))
            { _number = NumberState.Exponent; return true; }
            return false;
        }

        private void FinishNumber()
        {
            if (_number != NumberState.Zero && _number != NumberState.Integer &&
                _number != NumberState.Fraction && _number != NumberState.ExponentDigits) Invalid();
        }

        private void Finish()
        {
            if (_ended) return;
            if (_mode == Mode.Number) { FinishNumber(); _mode = Mode.Normal; ValueComplete(); }
            if (_mode != Mode.Normal || _stack.Count != 0 || !_rootDone) Invalid();
            _ended = true;
        }

        private static bool IsHex(char c)
        { return c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F'; }

        private static void Invalid()
        { throw new ResourceRequestException("JSON source is not strict RFC 8259 JSON.", "resource_json_invalid", false); }
    }
}
