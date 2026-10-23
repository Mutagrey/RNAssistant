using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Services
{
    // One scanner for retained Office text and immutable workspace UTF-8 bytes.
    // Only returned nodes consume model output budget; source length does not.
    public sealed class ResourceJsonTreeSelector
    {
        private readonly JsonTextReader _reader;
        private readonly CancellationToken _token;
        private readonly ResourceTree _tree;
        private readonly int _budget;
        private long _used;

        private ResourceJsonTreeSelector(TextReader source, string pointer, int depth, int offset,
            int limit, int maximumCharacters, CancellationToken token)
        {
            _reader = new JsonTextReader(source) { CloseInput = false, DateParseHandling = DateParseHandling.None,
                MaxDepth = int.MaxValue };
            _token = token;
            _tree = new ResourceTree { Format = "json", Pointer = pointer, Depth = depth, Offset = offset, Limit = limit };
            _budget = maximumCharacters;
        }

        public static ResourceTree Select(TextReader source, string pointer, int depth, int offset,
            int limit, int maximumCharacters, CancellationToken token = default(CancellationToken))
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            List<string> tokens;
            try { tokens = ResourceTreePointer.ParseTokens(pointer); }
            catch (ResourceRequestException error) when (error.ErrorCode == "resource_tree_pointer_invalid")
            { throw Error(error.Message, "resource_json_pointer_invalid"); }
            if (depth < 0 || offset < 0 || limit < 1 || maximumCharacters < 1)
                throw Error("Use nonnegative depth/offset, positive limit and a model read budget.", "resource_json_selector_invalid");
            var scanner = new ResourceJsonTreeSelector(new StrictJsonSourceReader(source), pointer,
                depth, offset, limit, maximumCharacters, token);
            try
            {
                if (!scanner.ReadNext()) throw Error("JSON source is empty.", "resource_json_invalid");
                if (!scanner.Find(tokens, 0))
                    throw Error("JSON Pointer does not identify a value.", "resource_json_pointer_not_found");
                if (scanner.ReadNext()) throw Error("JSON source has trailing content.", "resource_json_invalid");
                var tree = scanner._tree;
                if (offset > tree.TotalChildren)
                    throw Error("Tree offset is beyond the selected node's children.", "resource_json_offset_invalid");
                tree.NextOffset = depth > 0 && (long)offset + tree.ReturnedChildren < tree.TotalChildren
                    ? (long?)offset + tree.ReturnedChildren : null;
                if (JsonConvert.SerializeObject(tree).Length > maximumCharacters)
                    throw Error("Selected JSON tree exceeds the model read budget; narrow pointer, depth or limit.", "resource_json_view_too_large");
                return tree;
            }
            catch (JsonReaderException error)
            { throw Error("JSON source is invalid: " + error.Message, "resource_json_invalid"); }
            finally { scanner._reader.Close(); }
        }

        private bool Find(IReadOnlyList<string> tokens, int index)
        {
            _token.ThrowIfCancellationRequested();
            if (index == tokens.Count) { Walk(_tree.Pointer, _tree.Depth, true); return true; }
            if (_reader.TokenType == JsonToken.StartObject)
            {
                var found = false;
                var matched = false;
                while (true)
                {
                    if (!ReadNext()) throw Error("Incomplete JSON object.", "resource_json_invalid");
                    if (_reader.TokenType == JsonToken.EndObject) break;
                    if (_reader.TokenType != JsonToken.PropertyName) throw Error("Invalid JSON object.", "resource_json_invalid");
                    var name = (string)_reader.Value;
                    if (!ReadNext()) throw Error("Incomplete JSON object.", "resource_json_invalid");
                    if (string.Equals(name, tokens[index], StringComparison.Ordinal))
                    {
                        if (matched) throw Error("JSON Pointer refers to a duplicate object key.", "resource_json_pointer_ambiguous");
                        matched = true;
                        found = Find(tokens, index + 1);
                    }
                    else SkipValue();
                }
                return found;
            }
            if (_reader.TokenType == JsonToken.StartArray)
            {
                var wanted = ResourceTreePointer.ArrayIndex(tokens[index]);
                long position = 0;
                var found = false;
                while (true)
                {
                    if (!ReadNext()) throw Error("Incomplete JSON array.", "resource_json_invalid");
                    if (_reader.TokenType == JsonToken.EndArray) break;
                    if (position == wanted) found = Find(tokens, index + 1);
                    else SkipValue();
                    position++;
                }
                return found;
            }
            SkipValue();
            return false;
        }

        private void Walk(string pointer, int remainingDepth, bool root)
        {
            _token.ThrowIfCancellationRequested();
            var node = Node(pointer);
            Add(node);
            if (root) _tree.NodeType = node.Type;
            if (_reader.TokenType == JsonToken.StartObject)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                long position = 0;
                while (true)
                {
                    if (!ReadNext()) throw Error("Incomplete JSON object.", "resource_json_invalid");
                    if (_reader.TokenType == JsonToken.EndObject) break;
                    if (_reader.TokenType != JsonToken.PropertyName) throw Error("Invalid JSON object.", "resource_json_invalid");
                    var name = (string)_reader.Value;
                    if (!names.Add(name)) throw Error("Selected JSON object contains duplicate keys.", "resource_json_pointer_ambiguous");
                    if (!ReadNext()) throw Error("Incomplete JSON object.", "resource_json_invalid");
                    if (remainingDepth > 0 && (!root || position >= _tree.Offset && position < (long)_tree.Offset + _tree.Limit))
                    {
                        Walk(pointer + "/" + ResourceTreePointer.Escape(name), remainingDepth - 1, false);
                        if (root) _tree.ReturnedChildren++;
                    }
                    else SkipValue();
                    position++;
                }
                node.ChildCount = position;
            }
            else if (_reader.TokenType == JsonToken.StartArray)
            {
                long position = 0;
                while (true)
                {
                    if (!ReadNext()) throw Error("Incomplete JSON array.", "resource_json_invalid");
                    if (_reader.TokenType == JsonToken.EndArray) break;
                    if (remainingDepth > 0 && (!root || position >= _tree.Offset && position < (long)_tree.Offset + _tree.Limit))
                    {
                        Walk(pointer + "/" + position.ToString(CultureInfo.InvariantCulture), remainingDepth - 1, false);
                        if (root) _tree.ReturnedChildren++;
                    }
                    else SkipValue();
                    position++;
                }
                node.ChildCount = position;
            }
            if (root) _tree.TotalChildren = node.ChildCount ?? 0;
        }

        private void Add(ResourceTreeNode node)
        {
            if (node.Value != null && node.Value.Length > _budget ||
                (_used += JsonConvert.SerializeObject(node).Length + 1) > _budget)
                throw Error("Selected JSON tree exceeds the model read budget; narrow pointer, depth or limit.", "resource_json_view_too_large");
            _tree.Nodes.Add(node);
        }

        private ResourceTreeNode Node(string pointer)
        {
            var type = _reader.TokenType;
            var node = new ResourceTreeNode { Pointer = pointer };
            switch (type)
            {
                case JsonToken.StartObject: node.Type = "object"; break;
                case JsonToken.StartArray: node.Type = "array"; break;
                case JsonToken.String: node.Type = "string"; node.Value = (string)_reader.Value; node.ValueExact = true; break;
                case JsonToken.Integer:
                case JsonToken.Float:
                    if (_reader.Value is double number && (double.IsNaN(number) || double.IsInfinity(number)))
                        throw Error("Non-finite numbers are not JSON.", "resource_json_invalid");
                    node.Type = "number";
                    node.Value = Convert.ToString(_reader.Value, CultureInfo.InvariantCulture);
                    node.ValueExact = false;
                    break;
                case JsonToken.Boolean: node.Type = "boolean"; node.Value = (bool)_reader.Value ? "true" : "false"; node.ValueExact = true; break;
                case JsonToken.Null: node.Type = "null"; node.ValueExact = true; break;
                default: throw Error("Unsupported JSON value.", "resource_json_invalid");
            }
            return node;
        }

        private bool ReadNext()
        {
            _token.ThrowIfCancellationRequested();
            var read = _reader.Read();
            if (read && _reader.TokenType == JsonToken.Comment)
                throw Error("JSON comments are unsupported.", "resource_json_invalid");
            return read;
        }

        private void SkipValue()
        {
            if (_reader.TokenType != JsonToken.StartObject && _reader.TokenType != JsonToken.StartArray) return;
            var depth = 1;
            while (depth > 0)
            {
                if (!ReadNext()) throw Error("Incomplete JSON value.", "resource_json_invalid");
                if (_reader.TokenType == JsonToken.StartObject || _reader.TokenType == JsonToken.StartArray) depth++;
                if (_reader.TokenType == JsonToken.EndObject || _reader.TokenType == JsonToken.EndArray) depth--;
            }
        }

        private static ResourceRequestException Error(string message, string code)
        { return new ResourceRequestException(message, code, false); }
    }
}
