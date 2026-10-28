using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    // Ephemeral continuation validation, scoped to one chat handler. No durable store,
    // write authority, index or inferred dependency traversal. Restart fails closed.
    public sealed class ResourceTextSearch
    {
        public const int MaximumLineCharacters = 65536;
        public const int MaximumScanCharacters = 8 * 1024 * 1024;
        private readonly object _sync = new object();
        private string _request, _membership;
        private readonly HashSet<string> _gaps = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _unavailable = new HashSet<string>(StringComparer.Ordinal);
        private bool _sourceComplete = true;
        private int _matches;
        private ResourceSearchPosition _next;
        private readonly Dictionary<string, ResourceRef> _pins = new Dictionary<string, ResourceRef>(StringComparer.Ordinal);

        public ResourceWithinSearchPage Search(ResourceTextSearchRequest request, ResourceSearchSources sources, CancellationToken token)
        {
            lock (_sync) return SearchLocked(request, sources, token);
        }

        private ResourceWithinSearchPage SearchLocked(ResourceTextSearchRequest request, ResourceSearchSources sources, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request == null || string.IsNullOrEmpty(request.Query) || request.Query.Length > 500 ||
                request.Limit < 1 || request.Limit > 50 || (request.Mode != "literal" && request.Mode != "regex") ||
                string.IsNullOrEmpty(request.Target) == string.IsNullOrEmpty(request.Scope) ||
                request.Query.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                throw Invalid("Select one target or scope and a nonempty single-line query, mode literal/regex, limit 1–50.");
            RNAssistant.Core.Tools.ResourceTextMatcher matcher;
            try { matcher = new RNAssistant.Core.Tools.ResourceTextMatcher(request.Query, request.Mode, request.MatchCase); }
            catch (RNAssistant.Core.Tools.TextPatternException ex) { throw Invalid(ex.Message); }
            var items = sources.Items.OrderBy(x => x.Target, StringComparer.Ordinal).ToList();
            if (items.Select(x => x.Target).Distinct(StringComparer.Ordinal).Count() != items.Count)
                throw Invalid("Search targets are ambiguous.");
            var fingerprint = JsonConvert.SerializeObject(new { request.Target, request.Scope, request.Directory,
                request.Recursive, request.IncludeGlobs, request.ExcludeGlobs, request.Query, request.Mode, request.MatchCase, request.Limit });
            var membership = JsonConvert.SerializeObject(items.Select(x => x.Target));
            if (request.Continuation != null)
            {
                if (_next == null || fingerprint != _request || membership != _membership ||
                    JsonConvert.SerializeObject(request.Continuation) != JsonConvert.SerializeObject(_next)) throw Stale();
                foreach (var source in items.Where(x => _pins.ContainsKey(x.Target)))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        using (var current = source.Open(token))
                            if (!Same(_pins[source.Target], current.Reference)) throw Stale();
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ResourceRequestException)
                    { throw Stale(); }
                }
            }
            else { _pins.Clear(); _gaps.Clear(); _unavailable.Clear(); _sourceComplete = true; _matches = 0; }
            _next = null;
            _request = fingerprint; _membership = membership;
            var page = new ResourceWithinSearchPage { Target = request.Target, Scope = request.Scope, Query = request.Query,
                Mode = request.Mode, ScanComplete = sources.Complete && sources.Unavailable.Count == 0 && _gaps.Count == 0,
                SourceComplete = sources.Complete && sources.Unavailable.Count == 0 && _sourceComplete,
                MatchesComplete = sources.Complete, UnavailableScopes = sources.Unavailable.Concat(_unavailable).Distinct().ToList(),
                IncompleteReasons = _gaps.ToList() };
            if (!sources.Complete) page.IncompleteReasons.Add("source_discovery_incomplete");
            var visited = 0; var scanned = 0;
            foreach (var source in items)
            {
                var after = request.Continuation;
                var compare = after == null ? 1 : string.CompareOrdinal(source.Target, after.AfterTarget);
                if (compare < 0 || compare == 0 && after.AfterLine == 0) continue;
                token.ThrowIfCancellationRequested();
                try
                {
                    using (var text = source.Open(token))
                    {
                        if (text.Reference?.IsExact != true) throw Invalid("Search requires an exact source observation.");
                        ResourceRef previous;
                        if (request.Continuation != null && _pins.TryGetValue(source.Target, out previous) && !Same(previous, text.Reference))
                            throw Stale();
                        _pins[source.Target] = text.Reference.Copy();
                        page.ExactReferences.Add(text.Reference.Copy());
                        if (request.Target != null) page.ExactReference = text.Reference.Copy();
                        if (!text.SourceComplete) { page.SourceComplete = false; page.IncompleteReasons.Add("source_text_incomplete"); }
                        var lineNumber = 0;
                        while (true)
                        {
                            token.ThrowIfCancellationRequested();
                            bool tooLong;
                            var line = ReadLine(text.Reader, token, ref scanned, matcher.IsRegex ? MaximumLineCharacters : MaximumScanCharacters, out tooLong);
                            if (line == null) break;
                            lineNumber++;
                            if (scanned > MaximumScanCharacters)
                            { Incomplete(page, "scan_character_budget"); return Retain(page); }
                            if (tooLong) { Incomplete(page, "line_too_long"); continue; }
                            if (compare == 0 && lineNumber < after.AfterLine) continue;
                            var start = compare == 0 && lineNumber == after.AfterLine ? after.AfterColumn : 0;
                            while (start <= line.Length)
                            {
                                token.ThrowIfCancellationRequested();
                                var match = matcher.Next(line, start);
                                var hit = match == null ? -1 : match.Index;
                                var next = hit + (match == null ? 1 : Math.Max(1, match.Length));
                                if (hit < 0) break;
                                // Coordinates do not split Unicode scalar values.
                                if (hit < line.Length && char.IsLowSurrogate(line[hit])) { start = hit + 1; continue; }
                                var begin = Math.Max(0, hit - 120);
                                if (begin > 0 && char.IsLowSurrogate(line[begin])) begin--;
                                var count = Math.Min(600, line.Length - begin);
                                if (count > 0 && char.IsHighSurrogate(line[begin + count - 1])) count--;
                                page.Matches.Add(new ResourceWithinSearchItem { Target = source.Target,
                                    StartLine = lineNumber, StartColumn = hit + 1, Snippet = line.Substring(begin, count) });
                                _matches++;
                                if (page.Matches.Count == request.Limit)
                                {
                                    Continue(page, source.Target, lineNumber, next);
                                    return Retain(page);
                                }
                                start = next;
                            }
                        }
                    }
                }
                catch (RegexMatchTimeoutException) { Incomplete(page, "regex_timeout"); return Retain(page); }
                catch (ResourceRequestException ex) when (ex.ErrorCode == "resource_search_stale") { throw; }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ResourceRequestException || ex is DecoderFallbackException)
                {
                    page.UnavailableScopes.Add(source.Target); page.SourceComplete = false;
                    Incomplete(page, "source_unavailable");
                }
                if (++visited == 100 && source != items.Last())
                { Continue(page, source.Target, 0, 0); return Retain(page); }
            }
            page.MatchesComplete = page.ScanComplete && page.SourceComplete;
            return Retain(page);
        }

        private ResourceWithinSearchPage Retain(ResourceWithinSearchPage page)
        {
            page.TotalMatches = _matches;
            page.TotalMatchesExact = page.ScanComplete && page.SourceComplete;
            _sourceComplete &= page.SourceComplete;
            foreach (var reason in page.IncompleteReasons.Where(x => x != "page_limit")) _gaps.Add(reason);
            foreach (var target in page.UnavailableScopes) _unavailable.Add(target);
            return page;
        }

        private void Continue(ResourceWithinSearchPage page, string target, int line, int column)
        {
            Incomplete(page, "page_limit");
            _next = page.Continuation = new ResourceSearchPosition { AfterTarget = target, AfterLine = line, AfterColumn = column };
        }
        private static void Incomplete(ResourceWithinSearchPage page, string reason)
        { page.ScanComplete = false; page.MatchesComplete = false; if (!page.IncompleteReasons.Contains(reason)) page.IncompleteReasons.Add(reason); }
        private static bool Same(ResourceRef a, ResourceRef b) { return b != null && a.Uri == b.Uri && a.Revision == b.Revision; }
        private static ResourceRequestException Invalid(string message) { return new ResourceRequestException(message, "resource_search_invalid", false); }
        private static ResourceRequestException Stale() { return new ResourceRequestException("Search continuation is unavailable or stale. Start a fresh search without continuation.", "resource_search_stale", false); }
        private static string ReadLine(TextReader reader, CancellationToken token, ref int scanned, int maximumLine, out bool tooLong)
        {
            var line = new StringBuilder(); tooLong = false;
            while (true)
            {
                if ((scanned & 4095) == 0) token.ThrowIfCancellationRequested();
                var c = reader.Read();
                if (c < 0) return line.Length == 0 && !tooLong ? null : line.ToString();
                scanned++;
                if (scanned > MaximumScanCharacters) return line.ToString();
                if (c == '\r' || c == '\n')
                { if (c == '\r' && reader.Peek() == '\n') { reader.Read(); scanned++; } return line.ToString(); }
                if (line.Length < maximumLine) line.Append((char)c); else tooLong = true;
            }
        }
    }
}
