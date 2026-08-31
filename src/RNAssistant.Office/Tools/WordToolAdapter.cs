using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Domains.Word;

namespace RNAssistant.Office.Tools
{
    internal sealed class WordToolAdapter
    {
        private readonly WordService _service;

        internal WordToolAdapter(IWordBackend backend)
        {
            _service = new WordService(
                backend ?? throw new ArgumentNullException(nameof(backend)));
        }

        internal WordOutcome Execute(
            string toolId,
            IDictionary<string, object> arguments,
            Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            arguments = arguments ??
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (string.Equals(toolId, WordToolIds.ReadText, StringComparison.Ordinal))
                return _service.ReadText(new WordTextReadRequest
                {
                    Source = ToolArgumentReader.String(
                        arguments, "source", "document"),
                    Start = ToolArgumentReader.Int32(arguments, "start", 0),
                    HasEnd = arguments.ContainsKey("end"),
                    End = ToolArgumentReader.Int32(arguments, "end", 0),
                    MaxChars = ToolArgumentReader.Int32(
                        arguments, "maxChars", 12000)
                }, cancellationToken);
            if (string.Equals(toolId, WordToolIds.FindText, StringComparison.Ordinal))
                return _service.Find(ReplaceRequest(arguments, "query"),
                    ToolArgumentReader.Int32(arguments, "maxResults", 50),
                    ToolArgumentReader.Int32(arguments, "contextChars", 80),
                    cancellationToken);
            if (string.Equals(toolId, WordToolIds.Inspect, StringComparison.Ordinal))
                return _service.Inspect(new WordInspectRequest
                {
                    Kind = ToolArgumentReader.String(arguments, "kind", string.Empty),
                    MaxResults = ToolArgumentReader.Int32(
                        arguments, "maxResults", 100),
                    MaxTables = ToolArgumentReader.Int32(
                        arguments, "maxTables", 20),
                    MaxRows = ToolArgumentReader.Int32(arguments, "maxRows", 50)
                }, cancellationToken);
            if (string.Equals(toolId, WordToolIds.WriteText, StringComparison.Ordinal))
                return _service.Write(new WordWriteRequest
                {
                    Mode = ToolArgumentReader.String(arguments, "mode", string.Empty),
                    Text = ToolArgumentReader.String(arguments, "text", string.Empty),
                    Location = ToolArgumentReader.String(
                        arguments, "location", "selection")
                }, markDispatchPossible, cancellationToken);
            if (string.Equals(toolId, WordToolIds.ReplaceText, StringComparison.Ordinal))
                return _service.Replace(ReplaceRequest(arguments, "find"),
                    markDispatchPossible, cancellationToken);
            if (string.Equals(toolId, WordToolIds.FormatText, StringComparison.Ordinal))
                return _service.Format(new WordFormatRequest
                {
                    Kind = ToolArgumentReader.String(arguments, "kind", string.Empty),
                    Style = ToolArgumentReader.String(arguments, "style", string.Empty),
                    Target = ToolArgumentReader.String(
                        arguments, "target", "selection"),
                    HasBold = arguments.ContainsKey("bold"),
                    Bold = ToolArgumentReader.Boolean(arguments, "bold", false),
                    HasItalic = arguments.ContainsKey("italic"),
                    Italic = ToolArgumentReader.Boolean(arguments, "italic", false),
                    HasUnderline = arguments.ContainsKey("underline"),
                    Underline = ToolArgumentReader.Boolean(
                        arguments, "underline", false),
                    HasFontSize = arguments.ContainsKey("fontSize"),
                    FontSize = ToolArgumentReader.Int32(arguments, "fontSize", 0),
                    HasFontName = arguments.ContainsKey("fontName"),
                    FontName = ToolArgumentReader.String(
                        arguments, "fontName", string.Empty)
                }, markDispatchPossible, cancellationToken);
            if (string.Equals(toolId, WordToolIds.AddTable, StringComparison.Ordinal))
            {
                WordTableRequest table;
                string error;
                if (!TryTableRequest(arguments, out table, out error))
                    return Invalid(error);
                return _service.AddTable(
                    table, markDispatchPossible, cancellationToken);
            }
            if (string.Equals(
                toolId, WordToolIds.InsertPageBreak, StringComparison.Ordinal))
                return _service.InsertPageBreak(
                    markDispatchPossible, cancellationToken);
            if (string.Equals(toolId, WordToolIds.AddComment, StringComparison.Ordinal))
                return _service.AddComment(new WordCommentRequest
                {
                    Text = ToolArgumentReader.String(arguments, "text", string.Empty)
                }, markDispatchPossible, cancellationToken);
            return Invalid("Unsupported Word tool: " + toolId, "unknown_tool");
        }

        internal ToolResult ExecuteDataSource(
            ToolCommand command, CancellationToken cancellationToken)
        {
            if (command == null)
                return ToolResult.Fail(
                    "Word read command is empty.", null,
                    "word_read_command_missing", false);
            var outcome = Execute(
                command.ToolId, command.Arguments, null, cancellationToken);
            return outcome.Status == WordOutcomeStatus.Ok
                ? ToolResult.Ok(outcome.Message, outcome.DataJson)
                : ToolResult.Fail(
                    outcome.Message, outcome.DataJson,
                    outcome.ErrorCode, outcome.Retryable);
        }

        private static WordReplaceRequest ReplaceRequest(
            IDictionary<string, object> arguments, string findName)
        {
            return new WordReplaceRequest
            {
                Find = ToolArgumentReader.String(
                    arguments, findName, string.Empty),
                Replacement = ToolArgumentReader.String(
                    arguments, "replace", string.Empty),
                Scope = ToolArgumentReader.String(arguments, "scope", "main"),
                Mode = ToolArgumentReader.String(arguments, "mode", "literal"),
                ReplaceAll = ToolArgumentReader.Boolean(
                    arguments, "replaceAll", true),
                MatchCase = ToolArgumentReader.Boolean(
                    arguments, "matchCase", false),
                WholeWord = ToolArgumentReader.Boolean(
                    arguments, "wholeWord", false),
                MaxReplacements = ToolArgumentReader.Int32(
                    arguments, "maxReplacements", 500)
            };
        }

        private static bool TryTableRequest(
            IDictionary<string, object> arguments,
            out WordTableRequest request,
            out string error)
        {
            request = null;
            error = null;
            var values = new List<IReadOnlyList<object>>();
            var valuesJson = ToolArgumentReader.String(
                arguments, "values", string.Empty);
            if (!string.IsNullOrWhiteSpace(valuesJson))
            {
                JArray array;
                try { array = JArray.Parse(valuesJson); }
                catch (JsonException ex)
                {
                    error = "values must be a native two-dimensional JSON array: " +
                        ex.Message;
                    return false;
                }
                foreach (var token in array)
                {
                    var row = token as JArray;
                    if (row == null)
                    {
                        error = "values must contain only row arrays.";
                        return false;
                    }
                    values.Add(row.Select(Value).ToArray());
                }
            }
            var valueRows = values.Count;
            var valueColumns = values.Count == 0
                ? 0 : values.Max(row => row == null ? 0 : row.Count);
            var rows = arguments.ContainsKey("rows")
                ? ToolArgumentReader.Int32(arguments, "rows", 2)
                : valueRows > 0 ? valueRows : 2;
            var columns = arguments.ContainsKey("columns")
                ? ToolArgumentReader.Int32(arguments, "columns", 2)
                : valueColumns > 0 ? valueColumns : 2;
            request = new WordTableRequest
            {
                Rows = rows,
                Columns = columns,
                Values = values.Count == 0 ? null : values,
                Location = ToolArgumentReader.String(
                    arguments, "location", "selection")
            };
            return true;
        }

        private static object Value(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null ||
                token.Type == JTokenType.Undefined) return null;
            var value = token as JValue;
            return value == null
                ? token.ToString(Formatting.None)
                : Convert.ToString(value.Value, CultureInfo.InvariantCulture);
        }

        private static WordOutcome Invalid(
            string message, string code = "invalid_arguments")
        {
            return WordOutcome.Error(
                message,
                new JObject
                {
                    ["code"] = code,
                    ["retryable"] = false
                }.ToString(Formatting.None),
                code, false);
        }
    }
}
