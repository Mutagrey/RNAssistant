using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Domains.Word
{
    public sealed class WordService
    {
        public const int MaxTableCells = 10000;

        private readonly IWordBackend _backend;

        public WordService(IWordBackend backend)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        public const int MaximumTextCharacters = 1000000;

        internal WordTextSnapshot CaptureText(WordTextReadRequest request, CancellationToken cancellationToken)
        {
            if (request == null || (request.Source != "document" && request.Source != "selection" && request.Source != "range") ||
                request.MaxChars < 1 || request.MaxChars > MaximumTextCharacters ||
                request.Source == "range" && (!request.HasEnd || request.Start < 0 || request.End < request.Start))
                throw new WordBackendException("An explicit bounded Word text request is required.", "RESOURCE_TARGET_INVALID", false);
            if (request.Source == "range" && (long)request.End - request.Start > request.MaxChars)
                throw new WordBackendException("Choose a narrower Word character range.", "RESOURCE_SNAPSHOT_TOO_LARGE", false);
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = _backend.ReadText(request);
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot == null || snapshot.Text == null || snapshot.Source != request.Source ||
                snapshot.Start < 0 || snapshot.End < snapshot.Start || snapshot.Text.Length > request.MaxChars ||
                (long)snapshot.End - snapshot.Start > request.MaxChars ||
                request.Source == "range" && (snapshot.Start != request.Start || snapshot.End != request.End))
                throw new WordBackendException("Word text backend returned an incomplete or mismatched snapshot.", "word_text_snapshot_invalid", false);
            return snapshot;
        }

        internal WordSearchSnapshot CaptureSearch(string scope, CancellationToken cancellationToken)
        {
            WordOutcome failure;
            scope = Scope(scope, out failure);
            if (failure != null) throw new WordBackendException(failure.Message, "word_scope_invalid", false);
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = new WordSearchSnapshot { Scope = scope, Stories = _backend.ReadStories(
                new WordStoryReadRequest { Scope = scope, MaxCharacters = MaximumTextCharacters, MaxStories = 256 }) };
            cancellationToken.ThrowIfCancellationRequested();
            ValidateSearchSnapshot(snapshot);
            return snapshot;
        }

        internal static void ValidateSearchSnapshot(WordSearchSnapshot snapshot)
        {
            if (snapshot == null || (snapshot.Scope != "main" && snapshot.Scope != "selection" && snapshot.Scope != "all") ||
                snapshot.Stories == null || snapshot.Stories.Count == 0 || snapshot.Stories.Count > 256)
                throw new WordBackendException("Invalid Word search snapshot.", "word_story_snapshot_invalid", false);
            long characters = 0, extent = 0;
            foreach (var story in snapshot.Stories)
            {
                if (story == null || string.IsNullOrWhiteSpace(story.Kind) || story.Kind.Length > 128 ||
                    story.Text == null || story.Start < 0 || story.End < story.Start)
                    throw new WordBackendException("Incomplete Word search story.", "word_story_snapshot_invalid", false);
                characters += story.Text.Length; extent += (long)story.End - story.Start;
                if (characters > MaximumTextCharacters || extent > MaximumTextCharacters)
                    throw new WordBackendException("Choose a narrower search scope.", "RESOURCE_SNAPSHOT_TOO_LARGE", false);
            }
        }

        internal static WordOutcome Find(
            WordSearchSnapshot snapshot,
            WordReplaceRequest request,
            int maxResults,
            int contextChars,
            CancellationToken cancellationToken)
        {
            request = request ?? new WordReplaceRequest();
            if (string.IsNullOrWhiteSpace(request.Find))
                return Failure("query is required.", "invalid_arguments", false);
            WordOutcome scopeFailure;
            var scope = Scope(request.Scope, out scopeFailure);
            if (scopeFailure != null) return scopeFailure;
            request.Scope = scope;
            maxResults = Math.Max(1, Math.Min(500, maxResults));
            contextChars = Math.Max(0, Math.Min(1000, contextChars));
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateSearchSnapshot(snapshot);
                if (snapshot.Scope != scope)
                    return Failure("Word search scope does not match its snapshot.", "word_story_snapshot_invalid", false);
                var stories = snapshot.Stories;
                var matches = new JArray();
                var total = 0;
                var options = Options(request);
                foreach (var story in stories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var found = TextPatternEngine.Find(
                        story.Text, request.Find, options,
                        Math.Max(1, maxResults - matches.Count), contextChars);
                    total += found.MatchCount;
                    foreach (var match in found.Matches)
                    {
                        if (matches.Count >= maxResults) break;
                        matches.Add(new JObject
                        {
                            ["story"] = story.Kind,
                            ["start"] = story.Start + match.Index,
                            ["end"] = story.Start + match.Index + match.Length,
                            ["preview"] = match.Preview ?? string.Empty
                        });
                    }
                }
                return WordOutcome.Ok(
                    "Text matches found: " + total,
                    new JObject
                    {
                        ["query"] = request.Find,
                        ["scope"] = scope,
                        ["matchCount"] = total,
                        ["returnedCount"] = matches.Count,
                        ["truncated"] = total > matches.Count,
                        ["matches"] = matches
                    }.ToString(Formatting.None),
                    WordEffect.None);
            }
            catch (OperationCanceledException) { throw; }
            catch (TextPatternException ex)
            {
                return Failure(ex.Message, ex.ErrorCode, false);
            }
            catch (WordBackendException ex)
            {
                return Failure(ex.Message, ex.ErrorCode, ex.Retryable, ex.DetailsJson);
            }
            catch (Exception ex)
            {
                return Failure(
                    "Word text search failed: " + ex.Message,
                    "word_find_failed", true);
            }
        }

        public WordOutcome Inspect(
            WordInspectRequest request, CancellationToken cancellationToken)
        {
            request = request ?? new WordInspectRequest();
            var kind = Normalize(request.Kind, string.Empty);
            if (kind != "headings" && kind != "tables" &&
                kind != "comments" && kind != "stats")
                return Failure(
                    "kind must be headings, tables, comments, or stats.",
                    "word_inspect_kind_invalid", false);
            request.Kind = kind;
            request.MaxResults = Math.Max(1, Math.Min(500, request.MaxResults));
            request.MaxTables = Math.Max(1, Math.Min(50, request.MaxTables));
            request.MaxRows = Math.Max(1, Math.Min(500, request.MaxRows));
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = _backend.Inspect(request);
                if (snapshot == null)
                    return Failure(
                        "Word inspection backend returned no snapshot.",
                        "word_inspect_snapshot_missing", true);
                if (kind == "headings")
                    return WordOutcome.Ok(
                        "Headings read: " + Count(snapshot.Headings),
                        HeadingJson(snapshot.Headings), WordEffect.None);
                if (kind == "tables")
                    return WordOutcome.Ok(
                        "Tables read: " + Count(snapshot.Tables),
                        TableJson(snapshot.Tables), WordEffect.None);
                if (kind == "comments")
                    return WordOutcome.Ok(
                        "Comments listed: " + Count(snapshot.Comments),
                        CommentJson(snapshot.Comments), WordEffect.None);
                return WordOutcome.Ok(
                    "Document stats collected.",
                    StatisticsJson(snapshot.Statistics), WordEffect.None);
            }
            catch (OperationCanceledException) { throw; }
            catch (WordBackendException ex)
            {
                return Failure(ex.Message, ex.ErrorCode, ex.Retryable, ex.DetailsJson);
            }
            catch (Exception ex)
            {
                return Failure(
                    "Word inspection failed: " + ex.Message,
                    "word_inspect_failed", true);
            }
        }

        public WordOutcome Write(
            WordWriteRequest request, Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            request = request ?? new WordWriteRequest();
            request.Mode = Normalize(request.Mode, string.Empty);
            request.Location = Normalize(request.Location, "selection");
            request.Text = request.Text ?? string.Empty;
            if (request.Mode != "insert" && request.Mode != "paragraph" &&
                request.Mode != "replaceselection")
                return Failure(
                    "mode must be insert, paragraph, or replaceSelection.",
                    "word_write_mode_invalid", false);
            if (request.Mode == "paragraph" && request.Location != "selection" &&
                request.Location != "start" && request.Location != "end")
                return Failure(
                    "location must be selection, start, or end.",
                    "word_write_location_invalid", false);
            return Mutate(
                delegate(Action mark) { return _backend.Write(request, mark); },
                request.Mode == "paragraph" ? "Paragraph inserted." :
                request.Mode == "replaceselection" ? "Selection replaced." :
                "Text inserted.",
                null,
                markDispatchPossible, cancellationToken);
        }

        public WordOutcome Replace(
            WordReplaceRequest request, Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            request = request ?? new WordReplaceRequest();
            if (string.IsNullOrWhiteSpace(request.Find))
                return Failure("find is required.", "invalid_arguments", false);
            WordOutcome scopeFailure;
            var scope = Scope(request.Scope, out scopeFailure);
            if (scopeFailure != null) return scopeFailure;
            request.Scope = scope;
            request.Replacement = request.Replacement ?? string.Empty;
            request.MaxReplacements = Math.Max(
                1, Math.Min(500, request.MaxReplacements));
            var dispatched = false;
            Action mark = delegate
            {
                if (dispatched) return;
                dispatched = true;
                markDispatchPossible();
            };
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var before = ReadStories(scope);
                var plans = ReplacementPlans(request, before);
                var replacements = plans.Sum(plan => plan.Replacements.Count);
                if (replacements == 0 || plans.All(plan =>
                    string.Equals(plan.ExpectedText, plan.ResultText,
                        StringComparison.Ordinal)))
                    return WordOutcome.Ok(
                        "Word replacements completed: " + replacements + ".",
                        ReplaceData(replacements, ScopeHash(before)),
                        WordEffect.VerifiedNoChange);

                _backend.ApplyReplacement(new WordReplaceApplyRequest
                {
                    Scope = scope,
                    Stories = plans
                }, mark);
                if (!dispatched)
                    return WordOutcome.Unknown(
                        "Word replacement backend returned without a dispatch boundary.",
                        ReplaceData(replacements, ScopeHash(before)),
                        "word_replace_dispatch_boundary_missing");
                cancellationToken.ThrowIfCancellationRequested();
                var after = ReadStories(scope);
                if (!ReplacementVerified(before, after, plans))
                    return WordOutcome.Unknown(
                        "Word text may have been replaced, but exact read-back diverged.",
                        ReplaceData(replacements, ScopeHash(after)),
                        "word_replace_verification_failed");
                return WordOutcome.Ok(
                    "Word replacements completed: " + replacements + ".",
                    ReplaceData(replacements, ScopeHash(after)),
                    WordEffect.VerifiedChange);
            }
            catch (OperationCanceledException)
            {
                if (!dispatched) throw;
                return WordOutcome.Unknown(
                    "Cancellation was observed after the Word replacement dispatch boundary; inspect the target before retrying.",
                    null, "word_effect_unknown");
            }
            catch (TextPatternException ex)
            {
                return dispatched
                    ? WordOutcome.Unknown(
                        "Word replacement final state is unknown. " + ex.Message,
                        null, "word_effect_unknown")
                    : Failure(ex.Message, ex.ErrorCode, false);
            }
            catch (WordBackendException ex)
            {
                return dispatched
                    ? WordOutcome.Unknown(
                        "Word replacement final state is unknown. " + ex.Message,
                        ex.DetailsJson, "word_effect_unknown")
                    : Failure(ex.Message, ex.ErrorCode, ex.Retryable, ex.DetailsJson);
            }
            catch (Exception ex)
            {
                return dispatched
                    ? WordOutcome.Unknown(
                        "Word replacement final state is unknown. " + ex.Message,
                        null, "word_effect_unknown")
                    : Failure(
                        "Word replacement failed before dispatch: " + ex.Message,
                        "word_replace_failed", true);
            }
        }

        public WordOutcome Format(
            WordFormatRequest request, Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            request = request ?? new WordFormatRequest();
            request.Kind = Normalize(request.Kind, string.Empty);
            request.Target = Normalize(request.Target, "selection");
            request.Style = request.Style ?? string.Empty;
            request.FontName = request.FontName ?? string.Empty;
            if (request.Kind == "style")
            {
                if (string.IsNullOrWhiteSpace(request.Style))
                    return Failure("style is required.", "invalid_arguments", false);
                if (request.Target != "selection" && request.Target != "document")
                    return Failure(
                        "target must be selection or document.",
                        "word_format_target_invalid", false);
            }
            else if (request.Kind == "font")
            {
                if (!request.HasBold && !request.HasItalic &&
                    !request.HasUnderline && !request.HasFontSize &&
                    !request.HasFontName)
                    return Failure(
                        "kind=font requires at least one font formatting field.",
                        "invalid_arguments", false);
                if (request.HasFontSize && request.FontSize < 1)
                    return Failure(
                        "fontSize must be a positive integer.",
                        "invalid_arguments", false);
            }
            else return Failure(
                "kind must be style or font.",
                "word_format_kind_invalid", false);
            return Mutate(
                delegate(Action mark) { return _backend.Format(request, mark); },
                request.Kind == "style"
                    ? "Style applied: " + request.Style
                    : "Selection formatted.",
                null,
                markDispatchPossible, cancellationToken);
        }

        public WordOutcome AddTable(
            WordTableRequest request, Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            request = request ?? new WordTableRequest();
            request.Location = Normalize(request.Location, "selection");
            if (request.Rows < 1 || request.Columns < 1)
                return Failure(
                    "rows and columns must be positive integers.",
                    "invalid_arguments", false);
            if ((long)request.Rows * request.Columns > MaxTableCells)
                return Failure(
                    "Word table exceeds the " + MaxTableCells + "-cell safety limit.",
                    "word_table_too_large", false);
            if (request.Location != "selection" && request.Location != "start" &&
                request.Location != "end")
                return Failure(
                    "location must be selection, start, or end.",
                    "word_table_location_invalid", false);
            if (request.Values != null &&
                (request.Values.Count > request.Rows ||
                 request.Values.Any(row => row != null &&
                     row.Count > request.Columns)))
                return Failure(
                    "Explicit rows/columns are smaller than the supplied values; omit them to infer the table size.",
                    "invalid_arguments", false);
            return Mutate(
                delegate(Action mark) { return _backend.AddTable(request, mark); },
                "Table inserted.",
                delegate(WordMutationBackendResult result)
                {
                    return new JObject
                    {
                        ["rows"] = result.Rows,
                        ["columns"] = result.Columns
                    }.ToString(Formatting.None);
                },
                markDispatchPossible, cancellationToken);
        }

        public WordOutcome InsertPageBreak(
            Action markDispatchPossible, CancellationToken cancellationToken)
        {
            return Mutate(
                delegate(Action mark)
                {
                    return _backend.InsertPageBreak(
                        new WordPageBreakRequest(), mark);
                },
                "Page break inserted.", null,
                markDispatchPossible, cancellationToken);
        }

        public WordOutcome AddComment(
            WordCommentRequest request, Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            request = request ?? new WordCommentRequest();
            request.Text = request.Text ?? string.Empty;
            return Mutate(
                delegate(Action mark) { return _backend.AddComment(request, mark); },
                "Comment added.", null,
                markDispatchPossible, cancellationToken);
        }

        private WordOutcome Mutate(
            Func<Action, WordMutationBackendResult> operation,
            string successMessage,
            Func<WordMutationBackendResult, string> data,
            Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            if (markDispatchPossible == null)
                throw new ArgumentNullException(nameof(markDispatchPossible));
            var dispatched = false;
            Action mark = delegate
            {
                if (dispatched) return;
                dispatched = true;
                markDispatchPossible();
            };
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = operation(mark);
                if (result == null)
                    return dispatched
                        ? WordOutcome.Unknown(
                            "Word backend returned no mutation result.",
                            null, "word_effect_unknown")
                        : Failure(
                            "Word backend returned no mutation result.",
                            "word_mutation_result_missing", true);
                if (result.Changed && !dispatched)
                    return WordOutcome.Unknown(
                        "Word backend reported a change without a dispatch boundary.",
                        null, "word_dispatch_boundary_missing");
                cancellationToken.ThrowIfCancellationRequested();
                if (!result.Verified)
                    return WordOutcome.Unknown(
                        "Word document may have changed, but exact read-back diverged.",
                        null, "word_verification_failed");
                return WordOutcome.Ok(
                    successMessage,
                    data == null ? null : data(result),
                    result.Changed
                        ? WordEffect.VerifiedChange
                        : WordEffect.VerifiedNoChange);
            }
            catch (OperationCanceledException)
            {
                if (!dispatched) throw;
                return WordOutcome.Unknown(
                    "Cancellation was observed after the Word dispatch boundary; inspect the target before retrying.",
                    null, "word_effect_unknown");
            }
            catch (WordBackendException ex)
            {
                return dispatched
                    ? WordOutcome.Unknown(
                        "Word document final state is unknown. " + ex.Message,
                        ex.DetailsJson, "word_effect_unknown")
                    : Failure(ex.Message, ex.ErrorCode, ex.Retryable, ex.DetailsJson);
            }
            catch (Exception ex)
            {
                return dispatched
                    ? WordOutcome.Unknown(
                        "Word document final state is unknown. " + ex.Message,
                        null, "word_effect_unknown")
                    : Failure(
                        "Word operation failed before dispatch: " + ex.Message,
                        "word_tool_failed", true);
            }
        }

        private IReadOnlyList<WordStorySnapshot> ReadStories(string scope)
        {
            var stories = _backend.ReadStories(
                new WordStoryReadRequest { Scope = scope });
            if (stories == null)
                throw new WordBackendException(
                    "Word story backend returned no snapshot.",
                    "word_story_snapshot_missing", true);
            return stories;
        }

        private static List<WordStoryReplacementPlan> ReplacementPlans(
            WordReplaceRequest request,
            IReadOnlyList<WordStorySnapshot> stories)
        {
            var plans = new List<WordStoryReplacementPlan>();
            var options = Options(request);
            var replacementPlanned = false;
            var total = 0;
            foreach (var story in stories)
            {
                var text = story.Text ?? string.Empty;
                var found = TextPatternEngine.Find(
                    text, request.Find, options, 1, 0);
                var edits = new List<TextPatternReplacement>();
                if (found.MatchCount > 0 &&
                    (request.ReplaceAll || !replacementPlanned))
                {
                    edits = TextPatternEngine.PlanReplacements(
                        text, request.Find, request.Replacement, options,
                        request.ReplaceAll, request.MaxReplacements);
                    total += edits.Count;
                    if (total > request.MaxReplacements)
                        throw new TextPatternException(
                            "replacement_limit_exceeded",
                            "Replacement count exceeds maxReplacements=" +
                            request.MaxReplacements + ".");
                    if (edits.Count > 0) replacementPlanned = true;
                }
                plans.Add(new WordStoryReplacementPlan
                {
                    StoryId = story.Id,
                    Start = story.Start,
                    End = story.End,
                    ExpectedText = text,
                    ResultText = Apply(text, edits),
                    Replacements = edits.Select(edit => new WordTextReplacement
                    {
                        Index = edit.Index,
                        Length = edit.Length,
                        Text = edit.Text
                    }).ToArray()
                });
            }
            return plans;
        }

        private static string HeadingJson(
            IEnumerable<WordHeadingSnapshot> headings)
        {
            return new JArray((headings ?? new WordHeadingSnapshot[0]).Select(
                item => new JObject
                {
                    ["style"] = item.Style ?? string.Empty,
                    ["start"] = item.Start,
                    ["end"] = item.End,
                    ["text"] = item.Text ?? string.Empty
                })).ToString(Formatting.None);
        }

        private static string TableJson(
            IEnumerable<WordTableSnapshot> tables)
        {
            return new JArray((tables ?? new WordTableSnapshot[0]).Select(
                item => new JObject
                {
                    ["index"] = item.Index,
                    ["rows"] = item.Rows,
                    ["columns"] = item.Columns,
                    ["values"] = JArray.FromObject(item.Values ??
                        new IReadOnlyList<string>[0])
                })).ToString(Formatting.None);
        }

        private static string CommentJson(
            IEnumerable<WordCommentSnapshot> comments)
        {
            return new JArray((comments ?? new WordCommentSnapshot[0]).Select(
                item => new JObject
                {
                    ["index"] = item.Index,
                    ["author"] = item.Author ?? string.Empty,
                    ["text"] = item.Text ?? string.Empty,
                    ["scope"] = item.Scope ?? string.Empty
                })).ToString(Formatting.None);
        }

        private static string StatisticsJson(WordStatisticsSnapshot statistics)
        {
            statistics = statistics ?? new WordStatisticsSnapshot();
            return new JObject
            {
                ["characters"] = statistics.Characters,
                ["words"] = statistics.Words,
                ["paragraphs"] = statistics.Paragraphs,
                ["tables"] = statistics.Tables,
                ["comments"] = statistics.Comments
            }.ToString(Formatting.None);
        }

        private static bool ReplacementVerified(
            IReadOnlyList<WordStorySnapshot> before,
            IReadOnlyList<WordStorySnapshot> after,
            IReadOnlyList<WordStoryReplacementPlan> plans)
        {
            if (before == null || after == null || before.Count != after.Count)
                return false;
            var expected = plans.ToDictionary(
                plan => plan.StoryId, plan => plan.ResultText,
                StringComparer.Ordinal);
            for (var index = 0; index < before.Count; index++)
            {
                if (!string.Equals(before[index].Id, after[index].Id,
                    StringComparison.Ordinal) ||
                    before[index].Start != after[index].Start)
                    return false;
                string text;
                if (!expected.TryGetValue(before[index].Id, out text))
                    text = before[index].Text ?? string.Empty;
                if (!string.Equals(text, after[index].Text ?? string.Empty,
                    StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        private static string Apply(
            string text, IReadOnlyList<TextPatternReplacement> edits)
        {
            var builder = new StringBuilder(text ?? string.Empty);
            for (var index = edits.Count - 1; index >= 0; index--)
            {
                var edit = edits[index];
                builder.Remove(edit.Index, edit.Length);
                builder.Insert(edit.Index, edit.Text ?? string.Empty);
            }
            return builder.ToString();
        }

        private static TextPatternOptions Options(WordReplaceRequest request)
        {
            return new TextPatternOptions
            {
                Mode = Normalize(request.Mode, "literal"),
                MatchCase = request.MatchCase,
                WholeWord = request.WholeWord
            };
        }

        private static string ScopeHash(
            IEnumerable<WordStorySnapshot> stories)
        {
            var builder = new StringBuilder();
            foreach (var story in stories ?? new WordStorySnapshot[0])
                builder.Append(story.Kind).Append('\n')
                    .Append(story.Start).Append(':').Append(story.End).Append('\n')
                    .Append(story.Text ?? string.Empty).Append('\n');
            return TextPatternEngine.Sha256(builder.ToString());
        }

        private static string ReplaceData(int replacements, string scopeHash)
        {
            return new JObject
            {
                ["replacements"] = replacements,
                ["scopeSha256"] = scopeHash ?? string.Empty
            }.ToString(Formatting.None);
        }

        private static string Scope(string value, out WordOutcome failure)
        {
            failure = null;
            var scope = Normalize(value, "main");
            if (scope == "main" || scope == "selection" || scope == "all")
                return scope;
            failure = Failure(
                "scope must be main, selection, or all.",
                "word_scope_invalid", false);
            return null;
        }

        private static int Count<T>(IReadOnlyList<T> values)
        {
            return values == null ? 0 : values.Count;
        }

        private static string Normalize(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value)
                ? fallback
                : value.Trim().ToLowerInvariant();
        }

        private static WordOutcome Failure(
            string message, string code, bool retryable,
            string detailsJson = null)
        {
            JObject data;
            try
            {
                data = string.IsNullOrWhiteSpace(detailsJson)
                    ? new JObject()
                    : JObject.Parse(detailsJson);
            }
            catch (JsonException)
            {
                data = new JObject { ["details"] = detailsJson };
            }
            data["code"] = code;
            data["retryable"] = retryable;
            return WordOutcome.Error(
                message, data.ToString(Formatting.None), code, retryable);
        }
    }
}
