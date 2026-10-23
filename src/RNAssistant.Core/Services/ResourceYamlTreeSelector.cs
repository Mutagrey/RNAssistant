using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace RNAssistant.Core.Services
{
    // YAML navigation uses literal decoded scalar keys. Unselected aliases can
    // be skipped; selected aliases are never expanded into exact tree evidence.
    public sealed class ResourceYamlTreeSelector
    {
        private readonly IParser _parser;
        private readonly CancellationToken _token;
        private readonly ResourceTree _tree;
        private readonly int _budget;
        private ParsingEvent _current;
        private long _used;

        private ResourceYamlTreeSelector(TextReader source, string pointer, int depth, int offset,
            int limit, int maximumCharacters, CancellationToken token)
        {
            _parser = new Parser(source);
            _token = token;
            _tree = new ResourceTree { Format = "yaml", Pointer = pointer, Depth = depth,
                Offset = offset, Limit = limit };
            _budget = maximumCharacters;
        }

        public static ResourceTree Select(TextReader source, string pointer, int depth, int offset,
            int limit, int maximumCharacters, CancellationToken token = default(CancellationToken))
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            List<string> tokens;
            try { tokens = ResourceTreePointer.ParseTokens(pointer); }
            catch (ResourceRequestException error) when (error.ErrorCode == "resource_tree_pointer_invalid")
            { throw Error("Use an RFC 6901-style pointer with valid ~0/~1 escapes.", "resource_yaml_pointer_invalid"); }
            if (depth < 0 || offset < 0 || limit < 1 || maximumCharacters < 1)
                throw Error("Use nonnegative depth/offset, positive limit and a model read budget.", "resource_yaml_selector_invalid");
            var selector = new ResourceYamlTreeSelector(source, pointer, depth, offset, limit,
                maximumCharacters, token);
            try
            {
                if (!selector.Next() || !(selector._current is StreamStart) ||
                    !selector.Next() || !(selector._current is DocumentStart) ||
                    !selector.Next() || selector._current is DocumentEnd)
                    throw Error("YAML source must contain one nonempty document.", "resource_yaml_invalid");
                var found = selector.Find(tokens, 0);
                if (!(selector._current is DocumentEnd))
                    throw Error("YAML document did not end after its root value.", "resource_yaml_invalid");
                if (!selector.Next() || !(selector._current is StreamEnd))
                    throw Error("YAML tree requires exactly one document.", "resource_yaml_multiple_documents");
                if (selector.Next()) throw Error("YAML source has trailing content.", "resource_yaml_invalid");
                if (!found) throw Error("Pointer does not identify a YAML value.", "resource_yaml_pointer_not_found");
                var tree = selector._tree;
                if (offset > tree.TotalChildren)
                    throw Error("Tree offset is beyond the selected node's children.", "resource_yaml_offset_invalid");
                tree.NextOffset = depth > 0 && (long)offset + tree.ReturnedChildren < tree.TotalChildren
                    ? (long?)offset + tree.ReturnedChildren : null;
                if (JsonConvert.SerializeObject(tree).Length > maximumCharacters)
                    throw Error("Selected YAML tree exceeds the model read budget; narrow pointer, depth or limit.",
                        "resource_yaml_view_too_large");
                return tree;
            }
            catch (YamlException error)
            { throw Error("YAML source is invalid: " + error.Message, "resource_yaml_invalid"); }
        }

        private bool Find(IReadOnlyList<string> tokens, int index)
        {
            _token.ThrowIfCancellationRequested();
            if (index == tokens.Count) { Walk(_tree.Pointer, _tree.Depth, true); return true; }
            if (_current is MappingStart)
            {
                NextRequired();
                var found = false;
                var matched = false;
                while (!(_current is MappingEnd))
                {
                    var name = Key();
                    if (string.Equals(name, tokens[index], StringComparison.Ordinal))
                    {
                        if (matched) throw Error("Pointer refers to a duplicate YAML key.", "resource_yaml_pointer_ambiguous");
                        matched = true;
                        found = Find(tokens, index + 1);
                    }
                    else Skip();
                }
                NextRequired();
                return found;
            }
            if (_current is SequenceStart)
            {
                var wanted = ResourceTreePointer.ArrayIndex(tokens[index]);
                long position = 0;
                var found = false;
                NextRequired();
                while (!(_current is SequenceEnd))
                {
                    if (position == wanted) found = Find(tokens, index + 1);
                    else Skip();
                    position++;
                }
                NextRequired();
                return found;
            }
            if (_current is AnchorAlias)
                throw Error("Selected YAML aliases cannot be expanded into exact tree pointers.",
                    "resource_yaml_alias_unsupported");
            Skip();
            return false;
        }

        private void Walk(string pointer, int remainingDepth, bool root)
        {
            _token.ThrowIfCancellationRequested();
            var node = Node(pointer);
            Add(node);
            if (root) _tree.NodeType = node.Type;
            if (_current is MappingStart)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                long position = 0;
                NextRequired();
                while (!(_current is MappingEnd))
                {
                    var name = Key();
                    if (!names.Add(name))
                        throw Error("Selected YAML mapping contains duplicate keys.", "resource_yaml_pointer_ambiguous");
                    if (remainingDepth > 0 && (!root || position >= _tree.Offset && position < (long)_tree.Offset + _tree.Limit))
                    {
                        Walk(pointer + "/" + ResourceTreePointer.Escape(name), remainingDepth - 1, false);
                        if (root) _tree.ReturnedChildren++;
                    }
                    else Skip();
                    position++;
                }
                node.ChildCount = position;
                NextRequired();
            }
            else if (_current is SequenceStart)
            {
                long position = 0;
                NextRequired();
                while (!(_current is SequenceEnd))
                {
                    if (remainingDepth > 0 && (!root || position >= _tree.Offset && position < (long)_tree.Offset + _tree.Limit))
                    {
                        Walk(pointer + "/" + position.ToString(CultureInfo.InvariantCulture), remainingDepth - 1, false);
                        if (root) _tree.ReturnedChildren++;
                    }
                    else Skip();
                    position++;
                }
                node.ChildCount = position;
                NextRequired();
            }
            else NextRequired();
            if (root) _tree.TotalChildren = node.ChildCount ?? 0;
        }

        private string Key()
        {
            if (!(_current is Scalar scalar))
                throw Error("Complex YAML mapping keys have no unambiguous pointer.", "resource_yaml_key_unsupported");
            var name = scalar.Value ?? string.Empty;
            if (name == "<<" && scalar.Style == ScalarStyle.Plain)
                throw Error("YAML merge keys are unsupported in exact tree navigation.", "resource_yaml_merge_unsupported");
            NextRequired();
            return name;
        }

        private ResourceTreeNode Node(string pointer)
        {
            if (_current is MappingStart) return new ResourceTreeNode { Pointer = pointer, Type = "object" };
            if (_current is SequenceStart) return new ResourceTreeNode { Pointer = pointer, Type = "array" };
            if (_current is Scalar scalar)
                return new ResourceTreeNode { Pointer = pointer, Type = "scalar", Value = scalar.Value,
                    ValueExact = false };
            if (_current is AnchorAlias)
                throw Error("Selected YAML aliases cannot be expanded into exact tree pointers.",
                    "resource_yaml_alias_unsupported");
            throw Error("Unsupported YAML node.", "resource_yaml_invalid");
        }

        private void Add(ResourceTreeNode node)
        {
            if (node.Value != null && node.Value.Length > _budget ||
                (_used += JsonConvert.SerializeObject(node).Length + 1) > _budget)
                throw Error("Selected YAML tree exceeds the model read budget; narrow pointer, depth or limit.",
                    "resource_yaml_view_too_large");
            _tree.Nodes.Add(node);
        }

        private void Skip()
        {
            if (!(_current is MappingStart) && !(_current is SequenceStart)) { NextRequired(); return; }
            var depth = 1;
            NextRequired();
            while (depth > 0)
            {
                if (_current is MappingStart || _current is SequenceStart) depth++;
                else if (_current is MappingEnd || _current is SequenceEnd) depth--;
                NextRequired();
            }
        }

        private bool Next()
        {
            _token.ThrowIfCancellationRequested();
            var next = _parser.MoveNext();
            _current = next ? _parser.Current : null;
            return next;
        }

        private void NextRequired()
        { if (!Next()) throw Error("YAML source ended inside a value.", "resource_yaml_invalid"); }

        private static ResourceRequestException Error(string message, string code)
        { return new ResourceRequestException(message, code, false); }
    }
}
