using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Tools
{
    internal sealed partial class HtmlWorkspaceToolService
    {
        private static readonly TimeSpan HtmlInspectionTimeout = TimeSpan.FromMilliseconds(250);
        private static readonly Regex HtmlCommentPattern = InspectionRegex("<!--.*?-->", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        private static readonly Regex HtmlRawBodyPattern = InspectionRegex("<(?<tag>script|style)\\b[^>]*>(?<body>.*?)</\\k<tag>\\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        private static readonly Regex HtmlIdPattern = InspectionRegex("<[a-z][^>]*?\\sid\\s*=\\s*(?:\"(?<value>[^\"]*)\"|'(?<value>[^']*)'|(?<value>[^\\s>]+))", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        private static readonly Regex HtmlRelevantTagPattern = InspectionRegex("<(?<tag>script|link|iframe|frame|object|embed|base|img|source|audio|video|form)\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        private static readonly Regex HtmlSrcAttributePattern = AttributeRegex("src");
        private static readonly Regex HtmlHrefAttributePattern = AttributeRegex("href");
        private static readonly Regex HtmlRelAttributePattern = AttributeRegex("rel");
        private static readonly Regex HtmlTypeAttributePattern = AttributeRegex("type");
        private static readonly Regex HtmlActionAttributePattern = AttributeRegex("action");
        private static readonly Regex CssCommentPattern = InspectionRegex("/\\*.*?\\*/", RegexOptions.Singleline);
        private static readonly Regex CssImportPattern = InspectionRegex("^\\s*@import\\b", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        private static readonly Regex CssUrlPattern = InspectionRegex("url\\(\\s*(?:\"(?<value>[^\"]*)\"|'(?<value>[^']*)'|(?<value>[^)\\s]+))\\s*\\)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        private static readonly Regex ModuleSyntaxPattern = InspectionRegex("^\\s*(?:import(?=\\s|\\{|\\*)|export(?=\\s|\\{|\\*))", RegexOptions.Multiline);
        private static readonly Regex GetElementByIdPattern = InspectionRegex("\\bgetElementById\\s*\\(\\s*(?:\"(?<value>[^\"]+)\"|'(?<value>[^']+)')", RegexOptions.None);
        private static readonly Regex QuerySelectorIdPattern = InspectionRegex("\\bquerySelector(?:All)?\\s*\\(\\s*(?:\"#(?<value>[A-Za-z0-9_-]+)\"|'#(?<value>[A-Za-z0-9_-]+)')", RegexOptions.None);
        private static readonly Regex DataPropertyPattern = InspectionRegex("\\bRNAssistantData\\s*\\.\\s*(?<value>[A-Za-z_$][A-Za-z0-9_$]*)", RegexOptions.None);
        private static readonly Regex DataIndexPattern = InspectionRegex("\\bRNAssistantData\\s*\\[\\s*(?:\"(?<value>[^\"]+)\"|'(?<value>[^']+)')\\s*\\]", RegexOptions.None);
        private static readonly Regex DataAccessorPattern = InspectionRegex("\\bRNAssistant\\s*\\.\\s*data\\s*\\.\\s*(?:get|meta)\\s*\\(\\s*(?:\"(?<value>[^\"]+)\"|'(?<value>[^']+)')", RegexOptions.None);

        internal static HtmlWorkspaceToolOutcome InspectForPreview(
            ChatSession session, CancellationToken cancellationToken)
        {
            if (session == null)
                return HtmlWorkspaceToolOutcome.Error(
                    "HTML workspace requires an active chat session.", null,
                    "html_workspace_session_required", false);
            return InspectWorkspace(session,
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase),
                cancellationToken);
        }

        private static HtmlWorkspaceToolOutcome WithAutomaticPreflight(
            ChatSession session,
            HtmlWorkspaceToolOutcome outcome,
            CancellationToken cancellationToken)
        {
            if (outcome == null ||
                outcome.Status == HtmlWorkspaceOutcomeStatus.Error)
                return outcome;

            var inspection = InspectWorkspace(session,
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    { "includeWarnings", true },
                    { "maxIssues", 25 }
                }, cancellationToken);
            JObject data;
            try
            {
                data = string.IsNullOrWhiteSpace(outcome.DataJson)
                    ? new JObject()
                    : JObject.Parse(outcome.DataJson);
            }
            catch (JsonException)
            {
                data = new JObject { ["result"] = outcome.DataJson };
            }

            if (inspection.Status == HtmlWorkspaceOutcomeStatus.Ok)
            {
                data["preflight"] = JObject.Parse(inspection.DataJson);
            }
            else
            {
                data["preflight"] = new JObject
                {
                    ["passed"] = false,
                    ["status"] = "error",
                    ["code"] = inspection.ErrorCode,
                    ["message"] = inspection.Message
                };
            }

            var json = data.ToString(Formatting.None);
            return outcome.Status == HtmlWorkspaceOutcomeStatus.Unknown
                ? HtmlWorkspaceToolOutcome.Unknown(
                    outcome.Message, json, outcome.ErrorCode)
                : HtmlWorkspaceToolOutcome.Ok(
                    outcome.Message, json, outcome.Effect);
        }

        private static HtmlWorkspaceToolOutcome InspectWorkspace(
            ChatSession session,
            IDictionary<string, object> arguments,
            CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var workspace = NormalizedWorkspaceCopy(session.HtmlWorkspace);
                var requestedEntry = ToolArgumentReader.String(
                    arguments, "entryName", string.Empty).Trim();
                var includeWarnings = ToolArgumentReader.Boolean(
                    arguments, "includeWarnings", true);
                var maxIssues = Math.Max(1, Math.Min(500,
                    ToolArgumentReader.Int32(arguments, "maxIssues", 100)));
                var entry = ResolveInspectionEntry(workspace, requestedEntry);
                var collector = new HtmlInspectionCollector(maxIssues, includeWarnings);
                var ids = new HashSet<string>(StringComparer.Ordinal);
                var dataNames = new HashSet<string>(
                    workspace.DataSources.Where(item => item != null).Select(item => item.Name ?? string.Empty),
                    StringComparer.Ordinal);

                if (entry == null)
                {
                    collector.Add("error", "workspace.entry_missing", "The workspace has no active HTML entry file.", string.Empty, "html", null, -1);
                }
                else
                {
                    InspectEntryHtml(entry, ids, collector, cancellationToken);
                }

                foreach (var file in workspace.Files.Where(item => item != null && string.Equals(item.Kind, "css", StringComparison.OrdinalIgnoreCase)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    InspectCss(file, collector);
                }
                foreach (var file in workspace.Files.Where(item => item != null && string.Equals(item.Kind, "script", StringComparison.OrdinalIgnoreCase)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    InspectScript(file.Path, file.Kind, file.Content ?? string.Empty, file.Content ?? string.Empty, 0, ids, dataNames, collector);
                }
                if (entry != null)
                {
                    InspectInlineScripts(entry, ids, dataNames, collector);
                }
                foreach (var data in workspace.DataSources.Where(item => item != null))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    InspectDataSource(data, collector);
                }

                var errors = collector.ErrorCount;
                var warnings = collector.WarningCount;
                var message = "HTML workspace static inspection found " + errors + " error(s) and " + warnings + " warning(s).";
                return HtmlWorkspaceToolOutcome.Ok(message, new JObject
                {
                    ["type"] = "rnassistant.htmlWorkspaceInspection",
                    ["version"] = 1,
                    ["scope"] = "static_preflight",
                    ["runtimeExecuted"] = false,
                    ["revisionArtifactId"] = session.ActiveHtmlArtifactId,
                    ["passed"] = errors == 0,
                    ["entryName"] = entry == null ? null : entry.Path,
                    ["fileCount"] = workspace.Files.Count(item => item != null),
                    ["htmlFileCount"] = workspace.Files.Count(item => item != null && string.Equals(item.Kind, "html", StringComparison.OrdinalIgnoreCase)),
                    ["injectedCss"] = new JArray(workspace.Files.Where(item => item != null && string.Equals(item.Kind, "css", StringComparison.OrdinalIgnoreCase)).Select(item => item.Path)),
                    ["injectedScripts"] = new JArray(workspace.Files.Where(item => item != null && string.Equals(item.Kind, "script", StringComparison.OrdinalIgnoreCase)).Select(item => item.Path)),
                    ["dataSources"] = new JArray(workspace.DataSources.Where(item => item != null).Select(item => item.Name)),
                    ["errorCount"] = errors,
                    ["warningCount"] = warnings,
                    ["issueCount"] = errors + warnings,
                    ["returnedCount"] = collector.Returned.Count,
                    ["truncated"] = errors + (includeWarnings ? warnings : 0) > collector.Returned.Count,
                    ["warningsIncluded"] = includeWarnings,
                    ["issues"] = new JArray(collector.Returned)
                }.ToString(Formatting.None), HtmlWorkspaceEffect.None);
            }
            catch (RegexMatchTimeoutException)
            {
                return HtmlWorkspaceToolOutcome.Error(
                    "HTML workspace inspection exceeded its regex time limit.",
                    null, "html_workspace_inspection_timeout", true);
            }
        }

        private static HtmlWorkspaceFile ResolveInspectionEntry(HtmlWorkspace workspace, string requestedEntry)
        {
            if (!string.IsNullOrWhiteSpace(requestedEntry))
            {
                return FindFile(workspace, requestedEntry, true);
            }
            return (workspace.Files ?? new List<HtmlWorkspaceFile>()).FirstOrDefault(item => item != null &&
                string.Equals(item.Id, workspace.ActiveFileId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Kind, "html", StringComparison.OrdinalIgnoreCase));
        }

        private static void InspectEntryHtml(
            HtmlWorkspaceFile entry,
            ISet<string> ids,
            HtmlInspectionCollector collector,
            CancellationToken cancellationToken)
        {
            var source = entry.Content ?? string.Empty;
            var lineStarts = SourceLineStarts(source);
            var masked = MaskHtmlRawText(source);
            var firstIdOffsets = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Match match in HtmlIdPattern.Matches(masked))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = MatchValue(match).Trim();
                if (string.IsNullOrWhiteSpace(id))
                {
                    collector.Add("warning", "html.empty_id", "An empty id attribute cannot be referenced reliably.", entry.Path, entry.Kind, lineStarts, match.Index);
                    continue;
                }
                int firstOffset;
                if (firstIdOffsets.TryGetValue(id, out firstOffset))
                {
                    int firstLine;
                    int ignoredColumn;
                    SourcePosition(lineStarts, firstOffset, out firstLine, out ignoredColumn);
                    collector.Add("error", "html.duplicate_id", "Duplicate id '" + id + "'; first declared on line " + firstLine + ".", entry.Path, entry.Kind, lineStarts, match.Index);
                }
                else
                {
                    firstIdOffsets[id] = match.Index;
                    ids.Add(id);
                }
            }

            foreach (Match match in HtmlRelevantTagPattern.Matches(masked))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var tag = (match.Groups["tag"].Value ?? string.Empty).ToLowerInvariant();
                var tagText = match.Value;
                if (tag == "script")
                {
                    var src = AttributeValue(HtmlSrcAttributePattern, tagText);
                    if (!string.IsNullOrWhiteSpace(src))
                    {
                        collector.Add("error", "html.script_src_unsupported", "Script src is not assembled by the workspace; keep JavaScript in workspace script files.", entry.Path, entry.Kind, lineStarts, match.Index);
                    }
                    var type = AttributeValue(HtmlTypeAttributePattern, tagText);
                    if (string.Equals(type, "module", StringComparison.OrdinalIgnoreCase))
                    {
                        collector.Add("error", "html.module_unsupported", "ES module scripts are unsupported because workspace scripts are injected as classic scripts.", entry.Path, entry.Kind, lineStarts, match.Index);
                    }
                    continue;
                }
                if (tag == "link")
                {
                    var rel = AttributeValue(HtmlRelAttributePattern, tagText);
                    var href = AttributeValue(HtmlHrefAttributePattern, tagText);
                    if (ContainsToken(rel, "stylesheet") && !string.IsNullOrWhiteSpace(href))
                    {
                        collector.Add("error", "html.stylesheet_link_unsupported", "Stylesheet links are not assembled by the workspace; keep CSS in workspace CSS files.", entry.Path, entry.Kind, lineStarts, match.Index);
                    }
                    continue;
                }
                if (tag == "iframe" || tag == "frame" || tag == "object" || tag == "embed")
                {
                    collector.Add("error", "html.frame_blocked", "Embedded frames and objects are blocked by the preview sandbox and CSP.", entry.Path, entry.Kind, lineStarts, match.Index);
                    continue;
                }
                if (tag == "base")
                {
                    collector.Add("error", "html.base_blocked", "The base element is blocked by the preview CSP.", entry.Path, entry.Kind, lineStarts, match.Index);
                    continue;
                }
                if (tag == "form")
                {
                    var action = AttributeValue(HtmlActionAttributePattern, tagText);
                    if (!string.IsNullOrWhiteSpace(action))
                    {
                        collector.Add("error", "html.form_action_blocked", "Form navigation is blocked by the preview CSP.", entry.Path, entry.Kind, lineStarts, match.Index);
                    }
                    continue;
                }

                var resource = AttributeValue(HtmlSrcAttributePattern, tagText);
                if (!string.IsNullOrWhiteSpace(resource) && !IsInlinePreviewResource(resource))
                {
                    collector.Add("error", "html.resource_url_blocked", "Resource URL '" + ShortValue(resource) + "' is blocked; preview media must use data: or blob: URLs.", entry.Path, entry.Kind, lineStarts, match.Index);
                }
            }
        }

        private static void InspectInlineScripts(
            HtmlWorkspaceFile entry,
            ISet<string> ids,
            ISet<string> dataNames,
            HtmlInspectionCollector collector)
        {
            var source = entry.Content ?? string.Empty;
            foreach (Match match in HtmlRawBodyPattern.Matches(source))
            {
                if (!string.Equals(match.Groups["tag"].Value, "script", StringComparison.OrdinalIgnoreCase)) continue;
                var body = match.Groups["body"];
                InspectScript(entry.Path, entry.Kind, body.Value, source, body.Index, ids, dataNames, collector);
            }
        }

        private static void InspectCss(HtmlWorkspaceFile file, HtmlInspectionCollector collector)
        {
            var source = file.Content ?? string.Empty;
            var searchable = MaskPatternMatches(source, CssCommentPattern);
            var lineStarts = SourceLineStarts(source);
            foreach (Match match in CssImportPattern.Matches(searchable))
            {
                collector.Add("error", "css.import_unsupported", "CSS @import is blocked; keep styles in workspace CSS files.", file.Path, file.Kind, lineStarts, match.Index);
            }
            foreach (Match match in CssUrlPattern.Matches(searchable))
            {
                var value = MatchValue(match).Trim();
                if (!string.IsNullOrWhiteSpace(value) && !IsInlinePreviewResource(value) && !value.StartsWith("#", StringComparison.Ordinal))
                {
                    collector.Add("error", "css.resource_url_blocked", "CSS resource URL '" + ShortValue(value) + "' is blocked; use data: or blob: assets.", file.Path, file.Kind, lineStarts, match.Index);
                }
            }
        }

        private static void InspectScript(
            string name,
            string kind,
            string script,
            string locationSource,
            int baseOffset,
            ISet<string> ids,
            ISet<string> dataNames,
            HtmlInspectionCollector collector)
        {
            script = script ?? string.Empty;
            locationSource = locationSource ?? script;
            var searchable = MaskJavaScriptComments(script);
            var lineStarts = SourceLineStarts(locationSource);
            foreach (Match match in ModuleSyntaxPattern.Matches(searchable))
            {
                collector.Add("error", "script.module_syntax_unsupported", "Static import/export syntax is unsupported in classic workspace scripts.", name, kind, lineStarts, baseOffset + match.Index);
            }

            var seenDomIds = new HashSet<string>(StringComparer.Ordinal);
            InspectMissingReferences(GetElementByIdPattern, searchable, "script.dom_id_missing", "DOM id", name, kind, locationSource, baseOffset, ids, seenDomIds, collector);
            InspectMissingReferences(QuerySelectorIdPattern, searchable, "script.dom_id_missing", "DOM id", name, kind, locationSource, baseOffset, ids, seenDomIds, collector);

            var seenDataNames = new HashSet<string>(StringComparer.Ordinal);
            InspectMissingReferences(DataPropertyPattern, searchable, "script.data_source_missing", "Data source", name, kind, locationSource, baseOffset, dataNames, seenDataNames, collector);
            InspectMissingReferences(DataIndexPattern, searchable, "script.data_source_missing", "Data source", name, kind, locationSource, baseOffset, dataNames, seenDataNames, collector);
            InspectMissingReferences(DataAccessorPattern, searchable, "script.data_source_missing", "Data source", name, kind, locationSource, baseOffset, dataNames, seenDataNames, collector);
        }

        private static void InspectMissingReferences(
            Regex pattern,
            string script,
            string code,
            string label,
            string name,
            string kind,
            string locationSource,
            int baseOffset,
            ISet<string> known,
            ISet<string> seen,
            HtmlInspectionCollector collector)
        {
            var lineStarts = SourceLineStarts(locationSource);
            foreach (Match match in pattern.Matches(script))
            {
                var value = MatchValue(match);
                if (known.Contains(value) || !seen.Add(value)) continue;
                collector.Add("warning", code, label + " '" + value + "' is not declared in the selected entry/workspace; it may be created dynamically.", name, kind, lineStarts, baseOffset + match.Index);
            }
        }

        private static void InspectDataSource(HtmlWorkspaceDataSource data, HtmlInspectionCollector collector)
        {
            try
            {
                JToken.Parse(data.Json ?? string.Empty);
            }
            catch (JsonException ex)
            {
                collector.Add("error", "data.invalid_json", "Data source is not valid JSON: " + ShortValue(ex.Message), data.Name, "data", null, -1);
            }
            if (data.Binding != null && string.Equals(data.Binding.Status, "error", StringComparison.OrdinalIgnoreCase))
            {
                var detail = string.IsNullOrWhiteSpace(data.Binding.LastError) ? "Bound data refresh failed." : data.Binding.LastError;
                collector.Add("error", "data.binding_error", ShortValue(detail), data.Name, "data", null, -1);
            }
        }

        private static string MaskHtmlRawText(string source)
        {
            source = source ?? string.Empty;
            var chars = source.ToCharArray();
            foreach (Match match in HtmlCommentPattern.Matches(source)) MaskInspectionRange(chars, match.Index, match.Length);
            foreach (Match match in HtmlRawBodyPattern.Matches(source))
            {
                var body = match.Groups["body"];
                MaskInspectionRange(chars, body.Index, body.Length);
            }
            return new string(chars);
        }

        private static string MaskPatternMatches(string source, Regex pattern)
        {
            source = source ?? string.Empty;
            var chars = source.ToCharArray();
            foreach (Match match in pattern.Matches(source)) MaskInspectionRange(chars, match.Index, match.Length);
            return new string(chars);
        }

        private static string MaskJavaScriptComments(string source)
        {
            source = source ?? string.Empty;
            var chars = source.ToCharArray();
            var quote = '\0';
            var escaped = false;
            for (var index = 0; index < chars.Length; index++)
            {
                var current = chars[index];
                if (quote != '\0')
                {
                    if (escaped) escaped = false;
                    else if (current == '\\') escaped = true;
                    else if (current == quote) quote = '\0';
                    continue;
                }
                if (current == '\'' || current == '"' || current == '`')
                {
                    quote = current;
                    continue;
                }
                if (current != '/' || index + 1 >= chars.Length) continue;
                if (chars[index + 1] == '/')
                {
                    var end = index + 2;
                    while (end < chars.Length && chars[end] != '\r' && chars[end] != '\n') end++;
                    MaskInspectionRange(chars, index, end - index);
                    index = end - 1;
                }
                else if (chars[index + 1] == '*')
                {
                    var end = index + 2;
                    while (end + 1 < chars.Length && !(chars[end] == '*' && chars[end + 1] == '/')) end++;
                    end = Math.Min(chars.Length, end + 2);
                    MaskInspectionRange(chars, index, end - index);
                    index = end - 1;
                }
            }
            return new string(chars);
        }

        private static void MaskInspectionRange(char[] chars, int index, int length)
        {
            for (var offset = Math.Max(0, index); offset < chars.Length && offset < index + length; offset++)
            {
                if (chars[offset] != '\r' && chars[offset] != '\n') chars[offset] = ' ';
            }
        }

        private static Regex InspectionRegex(string pattern, RegexOptions options)
        {
            return new Regex(pattern, options | RegexOptions.CultureInvariant, HtmlInspectionTimeout);
        }

        private static Regex AttributeRegex(string name)
        {
            return InspectionRegex("(?:^|\\s)" + Regex.Escape(name) + "\\s*=\\s*(?:\"(?<value>[^\"]*)\"|'(?<value>[^']*)'|(?<value>[^\\s>]+))", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }

        private static string AttributeValue(Regex pattern, string tag)
        {
            var match = pattern.Match(tag ?? string.Empty);
            return match.Success ? MatchValue(match).Trim() : string.Empty;
        }

        private static string MatchValue(Match match)
        {
            return match == null ? string.Empty : match.Groups["value"].Value ?? string.Empty;
        }

        private static bool ContainsToken(string value, string token)
        {
            return (value ?? string.Empty).Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
                .Any(item => string.Equals(item, token, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsInlinePreviewResource(string value)
        {
            value = (value ?? string.Empty).Trim();
            return value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("blob:", StringComparison.OrdinalIgnoreCase);
        }

        private static string ShortValue(string value)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= 180 ? value : value.Substring(0, 180) + "...";
        }

        private sealed class HtmlInspectionCollector
        {
            private readonly int _maxIssues;
            private readonly bool _includeWarnings;
            private readonly List<JObject> _errors = new List<JObject>();
            private readonly List<JObject> _warnings = new List<JObject>();

            public HtmlInspectionCollector(int maxIssues, bool includeWarnings)
            {
                _maxIssues = maxIssues;
                _includeWarnings = includeWarnings;
            }

            public int ErrorCount { get; private set; }
            public int WarningCount { get; private set; }

            public IReadOnlyList<JObject> Returned
            {
                get
                {
                    var rows = new List<JObject>(_errors.Take(_maxIssues));
                    if (_includeWarnings && rows.Count < _maxIssues)
                    {
                        rows.AddRange(_warnings.Take(_maxIssues - rows.Count));
                    }
                    return rows;
                }
            }

            public void Add(string severity, string code, string message, string name, string kind, List<int> lineStarts, int index)
            {
                var error = string.Equals(severity, "error", StringComparison.OrdinalIgnoreCase);
                if (error) ErrorCount++;
                else WarningCount++;
                var target = error ? _errors : _warnings;
                if (target.Count >= _maxIssues) return;

                var row = new JObject
                {
                    ["severity"] = error ? "error" : "warning",
                    ["code"] = code,
                    ["message"] = message,
                    ["name"] = name,
                    ["kind"] = kind
                };
                if (lineStarts != null && index >= 0)
                {
                    int line;
                    int column;
                    SourcePosition(lineStarts, index, out line, out column);
                    row["line"] = line;
                    row["column"] = column;
                }
                target.Add(row);
            }
        }
    }
}
