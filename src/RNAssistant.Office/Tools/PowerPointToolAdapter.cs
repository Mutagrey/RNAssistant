using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Office.Domains.PowerPoint;

namespace RNAssistant.Office.Tools
{
    internal sealed class PowerPointToolAdapter
    {
        private readonly PowerPointService _service;

        internal PowerPointToolAdapter(IPowerPointBackend backend)
        {
            _service = new PowerPointService(
                backend ?? throw new ArgumentNullException(nameof(backend)));
        }

        internal PowerPointOutcome Execute(
            string toolId,
            IDictionary<string, object> arguments,
            Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            arguments = arguments ??
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (string.Equals(
                toolId, PowerPointToolIds.ReadSlides, StringComparison.Ordinal))
                return _service.ReadSlides(new PowerPointReadSlidesRequest
                {
                    HasSlideIndex = arguments.ContainsKey("slideIndex"),
                    SlideIndex = ToolArgumentReader.Int32(
                        arguments, "slideIndex", 0),
                    MaxSlides = ToolArgumentReader.Int32(
                        arguments, "maxSlides", 20),
                    Content = ToolArgumentReader.String(
                        arguments, "content", "text")
                }, cancellationToken);
            if (string.Equals(
                toolId, PowerPointToolIds.ListObjects, StringComparison.Ordinal))
                return _service.List(new PowerPointListRequest
                {
                    Kind = ToolArgumentReader.String(
                        arguments, "kind", string.Empty),
                    HasSlideIndex = arguments.ContainsKey("slideIndex"),
                    SlideIndex = ToolArgumentReader.Int32(
                        arguments, "slideIndex", 0)
                }, cancellationToken);
            if (string.Equals(
                toolId, PowerPointToolIds.SearchText, StringComparison.Ordinal))
                return _service.Search(ReplaceRequest(arguments, "query"),
                    ToolArgumentReader.Int32(arguments, "maxResults", 50),
                    ToolArgumentReader.Int32(arguments, "contextChars", 80),
                    cancellationToken);
            if (string.Equals(
                toolId, PowerPointToolIds.AddSlide, StringComparison.Ordinal))
                return _service.AddSlide(new PowerPointAddSlideRequest
                {
                    Title = ToolArgumentReader.String(
                        arguments, "title", "AI slide"),
                    Body = ToolArgumentReader.String(
                        arguments, "body", string.Empty)
                }, markDispatchPossible, cancellationToken);
            if (string.Equals(
                toolId, PowerPointToolIds.SetText, StringComparison.Ordinal))
                return _service.SetText(new PowerPointSetTextRequest
                {
                    Target = ToolArgumentReader.String(
                        arguments, "target", string.Empty),
                    HasSlideIndex = arguments.ContainsKey("slideIndex"),
                    SlideIndex = ToolArgumentReader.Int32(
                        arguments, "slideIndex", 0),
                    ShapeName = ToolArgumentReader.String(
                        arguments, "shapeName", string.Empty),
                    Text = ToolArgumentReader.String(
                        arguments, "text", string.Empty)
                }, markDispatchPossible, cancellationToken);
            if (string.Equals(
                toolId, PowerPointToolIds.ReplaceText, StringComparison.Ordinal))
                return _service.Replace(ReplaceRequest(arguments, "find"),
                    markDispatchPossible, cancellationToken);
            if (string.Equals(
                toolId, PowerPointToolIds.AddObject, StringComparison.Ordinal))
            {
                PowerPointAddObjectRequest request;
                string error;
                if (!TryObjectRequest(arguments, out request, out error))
                    return Invalid(error);
                return _service.AddObject(
                    request, markDispatchPossible, cancellationToken);
            }
            if (string.Equals(
                toolId, PowerPointToolIds.DuplicateSlide,
                StringComparison.Ordinal))
                return _service.DuplicateSlide(
                    new PowerPointDuplicateSlideRequest
                    {
                        SlideIndex = ToolArgumentReader.Int32(
                            arguments, "slideIndex", 1)
                    }, markDispatchPossible, cancellationToken);
            if (string.Equals(
                toolId, PowerPointToolIds.MoveSlide, StringComparison.Ordinal))
                return _service.MoveSlide(new PowerPointMoveSlideRequest
                {
                    SlideIndex = ToolArgumentReader.Int32(
                        arguments, "slideIndex", 1),
                    ToIndex = ToolArgumentReader.Int32(
                        arguments, "toIndex", 1)
                }, markDispatchPossible, cancellationToken);
            return Invalid(
                "Unsupported PowerPoint tool: " + toolId, "unknown_tool");
        }

        private static PowerPointReplaceRequest ReplaceRequest(
            IDictionary<string, object> arguments, string findName)
        {
            return new PowerPointReplaceRequest
            {
                Find = ToolArgumentReader.String(
                    arguments, findName, string.Empty),
                Replacement = ToolArgumentReader.String(
                    arguments, "replace", string.Empty),
                Scope = ToolArgumentReader.String(arguments, "scope", "deck"),
                SlideIndex = ToolArgumentReader.Int32(
                    arguments, "slideIndex", 0),
                IncludeNotes = ToolArgumentReader.Boolean(
                    arguments, "includeNotes", true),
                Mode = ToolArgumentReader.String(arguments, "mode", "literal"),
                MatchCase = ToolArgumentReader.Boolean(
                    arguments, "matchCase", false),
                WholeWord = ToolArgumentReader.Boolean(
                    arguments, "wholeWord", false),
                ReplaceAll = ToolArgumentReader.Boolean(
                    arguments, "replaceAll", true),
                MaxReplacements = ToolArgumentReader.Int32(
                    arguments, "maxReplacements", 500)
            };
        }

        private static bool TryObjectRequest(
            IDictionary<string, object> arguments,
            out PowerPointAddObjectRequest request,
            out string error)
        {
            request = null;
            error = null;
            var kind = ToolArgumentReader.String(
                arguments, "kind", string.Empty);
            var normalized = string.IsNullOrWhiteSpace(kind)
                ? string.Empty : kind.Trim().ToLowerInvariant();
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
            var defaultWidth = normalized == "picture" ? 320 :
                normalized == "table" ? 520 : 480;
            var defaultHeight = normalized == "picture" ? 180 :
                normalized == "table" ? 160 : 120;
            request = new PowerPointAddObjectRequest
            {
                Kind = kind,
                HasSlideIndex = arguments.ContainsKey("slideIndex"),
                SlideIndex = ToolArgumentReader.Int32(
                    arguments, "slideIndex", 0),
                HasText = arguments.ContainsKey("text"),
                Text = ToolArgumentReader.String(arguments, "text", string.Empty),
                Path = ToolArgumentReader.String(arguments, "path", string.Empty),
                Rows = rows,
                Columns = columns,
                Values = values.Count == 0 ? null : values,
                Left = ToolArgumentReader.Int32(arguments, "left", 60),
                Top = ToolArgumentReader.Int32(arguments, "top", 120),
                Width = ToolArgumentReader.Int32(
                    arguments, "width", defaultWidth),
                Height = ToolArgumentReader.Int32(
                    arguments, "height", defaultHeight),
                HasFontSize = arguments.ContainsKey("fontSize"),
                FontSize = ToolArgumentReader.Int32(arguments, "fontSize", 0)
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

        private static PowerPointOutcome Invalid(
            string message, string code = "invalid_arguments")
        {
            return PowerPointOutcome.Error(
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
