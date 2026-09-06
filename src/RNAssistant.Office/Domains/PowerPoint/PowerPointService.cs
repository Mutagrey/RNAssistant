using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Domains.PowerPoint
{
    public sealed class PowerPointService
    {
        public const int MaxSlides = 500;
        public const int MaxShapesPerSlide = 1000;
        public const int MaxTextTargets = 5000;
        public const int MaxTableCells = 10000;

        private readonly IPowerPointBackend _backend;

        public PowerPointService(IPowerPointBackend backend)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        public const int MaximumTextCharacters = 1000000;

        public PowerPointSlideReadSnapshot CaptureSlides(
            PowerPointReadSlidesRequest request, CancellationToken cancellationToken)
        {
            if (request == null || (request.HasSlideIndex && request.SlideIndex < 1) ||
                request.MaxSlides < 1 || request.MaxSlides > MaxSlides ||
                request.MaxCharacters < 1 || request.MaxCharacters > MaximumTextCharacters ||
                request.MaxShapesPerSlide < 1 || request.MaxShapesPerSlide > MaxShapesPerSlide)
                throw new PowerPointBackendException("Invalid exact PowerPoint capture bounds.", "invalid_arguments", false);
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = _backend.ReadSlides(request);
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot == null || snapshot.Slides == null || snapshot.TotalSlides < 0 ||
                snapshot.Slides.Count != (request.HasSlideIndex ? 1 : snapshot.TotalSlides) ||
                snapshot.Slides.Count > request.MaxSlides)
                throw new PowerPointBackendException("PowerPoint backend returned an incomplete capture.", "powerpoint_read_snapshot_invalid", false);
            long characters = 0;
            var ids = new HashSet<int>();
            for (var i = 0; i < snapshot.Slides.Count; i++)
            {
                var slide = snapshot.Slides[i];
                if (slide == null || slide.Text == null || slide.Notes == null ||
                    slide.Index != (request.HasSlideIndex ? request.SlideIndex : i + 1) ||
                    slide.Index > snapshot.TotalSlides || slide.SlideId <= 0 || !ids.Add(slide.SlideId))
                    throw new PowerPointBackendException("PowerPoint backend returned an invalid slide.", "powerpoint_read_snapshot_invalid", false);
                characters += (long)slide.Text.Length + slide.Notes.Length;
            }
            if (characters > request.MaxCharacters)
                throw new PowerPointBackendException("Choose a smaller PowerPoint source.", "RESOURCE_SNAPSHOT_TOO_LARGE", false);
            return snapshot;
        }

        public PowerPointOutcome List(
            PowerPointListRequest request,
            CancellationToken cancellationToken)
        {
            request = request ?? new PowerPointListRequest();
            request.Kind = Normalize(request.Kind, string.Empty);
            request.MaxSlides = MaxSlides;
            request.MaxShapes = MaxShapesPerSlide;
            if (request.Kind != "slides" && request.Kind != "shapes")
                return Failure(
                    "kind must be slides or shapes.",
                    "powerpoint_list_kind_invalid", false);
            if (request.HasSlideIndex && request.SlideIndex < 1)
                return Failure(
                    "slideIndex must be a positive integer.",
                    "invalid_arguments", false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = _backend.List(request);
                if (snapshot == null)
                    return Failure(
                        "PowerPoint list backend returned no snapshot.",
                        "powerpoint_list_snapshot_missing", true);
                if (request.Kind == "slides")
                {
                    var slides = snapshot.Slides ??
                        new PowerPointSlideSummarySnapshot[0];
                    return PowerPointOutcome.Ok(
                        "Slides listed: " + slides.Count,
                        SlideSummariesJson(slides), PowerPointEffect.None);
                }
                var shapes = snapshot.Shapes ?? new PowerPointShapeSnapshot[0];
                return PowerPointOutcome.Ok(
                    "Shapes listed: " + shapes.Count,
                    ShapesJson(shapes), PowerPointEffect.None);
            }
            catch (OperationCanceledException) { throw; }
            catch (PowerPointBackendException ex)
            {
                return Failure(ex.Message, ex.ErrorCode, ex.Retryable, ex.DetailsJson);
            }
            catch (Exception ex)
            {
                return Failure(
                    "PowerPoint object list failed: " + ex.Message,
                    "powerpoint_list_failed", true);
            }
        }

        public PowerPointSearchSnapshot CaptureSearch(int slideIndex, bool includeNotes, CancellationToken cancellationToken)
        {
            if (slideIndex < 0)
                throw new PowerPointBackendException("slideIndex cannot be negative.", "invalid_arguments", false);
            cancellationToken.ThrowIfCancellationRequested();
            var targets = _backend.ReadTextTargets(new PowerPointTextScopeRequest {
                Scope = slideIndex == 0 ? "deck" : "slide", SlideIndex = slideIndex, IncludeNotes = includeNotes,
                MaxSlides = MaxSlides, MaxShapesPerSlide = MaxShapesPerSlide, MaxTargets = MaxTextTargets,
                MaxCharacters = MaximumTextCharacters });
            cancellationToken.ThrowIfCancellationRequested();
            if (targets == null || targets.Count > MaxTextTargets)
                throw new PowerPointBackendException("Invalid PowerPoint search capture.", "powerpoint_read_snapshot_invalid", false);
            long characters = 0;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var target in targets)
            {
                if (target == null || target.Text == null || target.ShapeName == null || target.ShapeName.Length > 4096 ||
                    target.SlideIndex < 1 || (slideIndex > 0 && target.SlideIndex != slideIndex) ||
                    (target.Kind != "shape" && target.Kind != "notes") || (!includeNotes && target.Kind == "notes") ||
                    string.IsNullOrWhiteSpace(target.TargetId) || !ids.Add(target.TargetId))
                    throw new PowerPointBackendException("Invalid PowerPoint search target.", "powerpoint_read_snapshot_invalid", false);
                characters += target.Text.Length;
            }
            if (characters > MaximumTextCharacters)
                throw new PowerPointBackendException("Choose a smaller PowerPoint search scope.", "RESOURCE_SNAPSHOT_TOO_LARGE", false);
            return new PowerPointSearchSnapshot { SlideIndex = slideIndex, IncludeNotes = includeNotes, Targets = targets };
        }

        internal static PowerPointOutcome Search(
            PowerPointSearchSnapshot snapshot,
            PowerPointReplaceRequest request,
            int maxResults,
            int contextChars,
            CancellationToken cancellationToken)
        {
            request = request ?? new PowerPointReplaceRequest();
            if (string.IsNullOrWhiteSpace(request.Find))
                return Failure("query is required.", "invalid_arguments", false);
            PowerPointOutcome scopeFailure;
            var scope = Scope(request.Scope, request.SlideIndex, out scopeFailure);
            if (scopeFailure != null) return scopeFailure;
            request.Scope = scope;
            maxResults = Math.Max(1, Math.Min(500, maxResults));
            contextChars = Math.Max(0, Math.Min(1000, contextChars));
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (snapshot == null || snapshot.Targets == null || snapshot.SlideIndex != request.SlideIndex || snapshot.IncludeNotes != request.IncludeNotes)
                    return Failure("The search capture does not match the requested scope.", "RESOURCE_SNAPSHOT_UNAVAILABLE", false);
                var targets = snapshot.Targets;
                var matches = new JArray();
                var total = 0;
                var options = Options(request);
                foreach (var target in targets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var found = TextPatternEngine.Find(
                        target.Text, request.Find, options,
                        Math.Max(1, maxResults - matches.Count), contextChars);
                    total += found.MatchCount;
                    foreach (var match in found.Matches)
                    {
                        if (matches.Count >= maxResults) break;
                        matches.Add(new JObject
                        {
                            ["slideIndex"] = target.SlideIndex,
                            ["shapeName"] = target.ShapeName ?? string.Empty,
                            ["kind"] = target.Kind ?? string.Empty,
                            ["start"] = match.Index,
                            ["end"] = match.Index + match.Length,
                            ["preview"] = match.Preview ?? string.Empty
                        });
                    }
                }
                return PowerPointOutcome.Ok(
                    "PowerPoint text matches found: " + total,
                    new JObject
                    {
                        ["matchCount"] = total,
                        ["returnedCount"] = matches.Count,
                        ["truncated"] = total > matches.Count,
                        ["matches"] = matches
                    }.ToString(Formatting.None),
                    PowerPointEffect.None);
            }
            catch (OperationCanceledException) { throw; }
            catch (TextPatternException ex)
            {
                return Failure(ex.Message, ex.ErrorCode, false);
            }
            catch (PowerPointBackendException ex)
            {
                return Failure(ex.Message, ex.ErrorCode, ex.Retryable, ex.DetailsJson);
            }
            catch (Exception ex)
            {
                return Failure(
                    "PowerPoint text search failed: " + ex.Message,
                    "powerpoint_search_failed", true);
            }
        }

        public PowerPointOutcome AddSlide(
            PowerPointAddSlideRequest request,
            Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            request = request ?? new PowerPointAddSlideRequest();
            request.Title = request.Title ?? "AI slide";
            request.Body = request.Body ?? string.Empty;
            return Mutate(
                delegate(Action mark) { return _backend.AddSlide(request, mark); },
                "Slide added: " + request.Title, null,
                markDispatchPossible, cancellationToken);
        }

        public PowerPointOutcome SetText(
            PowerPointSetTextRequest request,
            Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            request = request ?? new PowerPointSetTextRequest();
            request.Target = Normalize(request.Target, string.Empty);
            request.ShapeName = request.ShapeName ?? string.Empty;
            request.Text = request.Text ?? string.Empty;
            if (request.Target != "notes" && request.Target != "shape")
                return Failure(
                    "target must be notes or shape.",
                    "powerpoint_text_target_invalid", false);
            if (request.HasSlideIndex && request.SlideIndex < 1)
                return Failure(
                    "slideIndex must be a positive integer.",
                    "invalid_arguments", false);
            return Mutate(
                delegate(Action mark) { return _backend.SetText(request, mark); },
                request.Target == "notes"
                    ? "Speaker notes set."
                    : "Shape text set.",
                null, markDispatchPossible, cancellationToken,
                delegate(PowerPointMutationBackendResult result)
                {
                    return request.Target == "notes"
                        ? "Speaker notes set for slide " + result.SlideIndex
                        : "Shape text set: " + (result.ShapeName ?? string.Empty);
                });
        }

        public PowerPointOutcome Replace(
            PowerPointReplaceRequest request,
            Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            request = request ?? new PowerPointReplaceRequest();
            if (string.IsNullOrWhiteSpace(request.Find))
                return Failure("find is required.", "invalid_arguments", false);
            PowerPointOutcome scopeFailure;
            var scope = Scope(request.Scope, request.SlideIndex, out scopeFailure);
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
                var before = ReadTargets(request);
                var plans = ReplacementPlans(request, before);
                var replacements = plans.Sum(plan => plan.Replacements.Count);
                if (replacements == 0 || plans.All(plan => string.Equals(
                    plan.ExpectedText, plan.ResultText, StringComparison.Ordinal)))
                    return PowerPointOutcome.Ok(
                        "PowerPoint replacements completed: " + replacements + ".",
                        ReplaceData(replacements, TargetHash(before)),
                        PowerPointEffect.VerifiedNoChange);
                var after = _backend.ApplyReplacement(
                    new PowerPointReplaceApplyRequest
                    {
                        Scope = TextScope(request),
                        Targets = plans
                    }, mark);
                if (!dispatched)
                    return PowerPointOutcome.Unknown(
                        "PowerPoint replacement backend returned without a dispatch boundary.",
                        ReplaceData(replacements, TargetHash(before)),
                        "powerpoint_replace_dispatch_boundary_missing");
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReplacementVerified(before, after, plans))
                    return PowerPointOutcome.Unknown(
                        "PowerPoint text may have been replaced, but exact read-back diverged.",
                        ReplaceData(replacements, TargetHash(after)),
                        "powerpoint_replace_verification_failed");
                return PowerPointOutcome.Ok(
                    "PowerPoint replacements completed: " + replacements + ".",
                    ReplaceData(replacements, TargetHash(after)),
                    PowerPointEffect.VerifiedChange);
            }
            catch (OperationCanceledException)
            {
                if (!dispatched) throw;
                return PowerPointOutcome.Unknown(
                    "Cancellation was observed after the PowerPoint replacement dispatch boundary; inspect the target before retrying.",
                    null, "powerpoint_effect_unknown");
            }
            catch (TextPatternException ex)
            {
                return dispatched
                    ? PowerPointOutcome.Unknown(
                        "PowerPoint replacement final state is unknown. " + ex.Message,
                        null, "powerpoint_effect_unknown")
                    : Failure(ex.Message, ex.ErrorCode, false);
            }
            catch (PowerPointBackendException ex)
            {
                return dispatched
                    ? PowerPointOutcome.Unknown(
                        "PowerPoint replacement final state is unknown. " + ex.Message,
                        ex.DetailsJson, "powerpoint_effect_unknown")
                    : Failure(ex.Message, ex.ErrorCode, ex.Retryable, ex.DetailsJson);
            }
            catch (Exception ex)
            {
                return dispatched
                    ? PowerPointOutcome.Unknown(
                        "PowerPoint replacement final state is unknown. " + ex.Message,
                        null, "powerpoint_effect_unknown")
                    : Failure(
                        "PowerPoint replacement failed before dispatch: " + ex.Message,
                        "powerpoint_replace_failed", true);
            }
        }

        public PowerPointOutcome AddObject(
            PowerPointAddObjectRequest request,
            Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            request = request ?? new PowerPointAddObjectRequest();
            request.Kind = Normalize(request.Kind, string.Empty);
            request.Text = request.Text ?? string.Empty;
            request.Path = request.Path ?? string.Empty;
            if (request.Kind != "textbox" && request.Kind != "picture" &&
                request.Kind != "table")
                return Failure(
                    "kind must be textBox, picture, or table.",
                    "powerpoint_object_kind_invalid", false);
            if (request.HasSlideIndex && request.SlideIndex < 1)
                return Failure(
                    "slideIndex must be a positive integer.",
                    "invalid_arguments", false);
            if (request.Kind == "textbox" && !request.HasText)
                return Failure(
                    "text is required for kind=textBox.",
                    "invalid_arguments", false);
            if (request.Kind == "picture" && string.IsNullOrWhiteSpace(request.Path))
                return Failure("path is required.", "invalid_arguments", false);
            if (request.Kind == "table")
            {
                if (request.Rows < 1 || request.Columns < 1)
                    return Failure(
                        "rows and columns must be positive integers.",
                        "invalid_arguments", false);
                if ((long)request.Rows * request.Columns > MaxTableCells)
                    return Failure(
                        "PowerPoint table exceeds the " + MaxTableCells +
                        "-cell safety limit.",
                        "powerpoint_table_too_large", false);
                if (request.Values != null &&
                    (request.Values.Count > request.Rows ||
                     request.Values.Any(row => row != null &&
                         row.Count > request.Columns)))
                    return Failure(
                        "Explicit rows/columns are smaller than the supplied values; omit them to infer the table size.",
                        "invalid_arguments", false);
            }
            if (request.HasFontSize && request.FontSize < 1)
                return Failure(
                    "fontSize must be a positive integer.",
                    "invalid_arguments", false);
            return Mutate(
                delegate(Action mark) { return _backend.AddObject(request, mark); },
                request.Kind == "textbox" ? "Text box added." :
                request.Kind == "picture" ? "Picture added." : "Table added.",
                delegate(PowerPointMutationBackendResult result)
                {
                    var data = new JObject
                    {
                        ["slide"] = result.SlideIndex,
                        ["shape"] = result.ShapeName ?? string.Empty
                    };
                    if (request.Kind == "table")
                    {
                        data["rows"] = result.Rows;
                        data["columns"] = result.Columns;
                    }
                    return data.ToString(Formatting.None);
                },
                markDispatchPossible, cancellationToken);
        }

        public PowerPointOutcome DuplicateSlide(
            PowerPointDuplicateSlideRequest request,
            Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            request = request ?? new PowerPointDuplicateSlideRequest();
            if (request.SlideIndex < 1)
                return Failure(
                    "slideIndex must be a positive integer.",
                    "invalid_arguments", false);
            return Mutate(
                delegate(Action mark)
                {
                    return _backend.DuplicateSlide(request, mark);
                },
                "Slide duplicated.",
                delegate(PowerPointMutationBackendResult result)
                {
                    return new JObject
                    {
                        ["sourceIndex"] = result.SourceIndex,
                        ["duplicateIndex"] = result.DuplicateIndex
                    }.ToString(Formatting.None);
                },
                markDispatchPossible, cancellationToken);
        }

        public PowerPointOutcome MoveSlide(
            PowerPointMoveSlideRequest request,
            Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            request = request ?? new PowerPointMoveSlideRequest();
            if (request.SlideIndex < 1 || request.ToIndex < 1)
                return Failure(
                    "slideIndex and toIndex must be positive integers.",
                    "invalid_arguments", false);
            return Mutate(
                delegate(Action mark) { return _backend.MoveSlide(request, mark); },
                "Slide moved to " + request.ToIndex,
                null, markDispatchPossible, cancellationToken);
        }

        private PowerPointOutcome Mutate(
            Func<Action, PowerPointMutationBackendResult> operation,
            string successMessage,
            Func<PowerPointMutationBackendResult, string> data,
            Action markDispatchPossible,
            CancellationToken cancellationToken,
            Func<PowerPointMutationBackendResult, string> message = null)
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
                        ? PowerPointOutcome.Unknown(
                            "PowerPoint backend returned no mutation result.",
                            null, "powerpoint_effect_unknown")
                        : Failure(
                            "PowerPoint backend returned no mutation result.",
                            "powerpoint_mutation_result_missing", true);
                if (result.Changed && !dispatched)
                    return PowerPointOutcome.Unknown(
                        "PowerPoint backend reported a change without a dispatch boundary.",
                        null, "powerpoint_dispatch_boundary_missing");
                cancellationToken.ThrowIfCancellationRequested();
                if (!result.Verified)
                    return PowerPointOutcome.Unknown(
                        "PowerPoint presentation may have changed, but exact read-back diverged.",
                        null, "powerpoint_verification_failed");
                return PowerPointOutcome.Ok(
                    message == null ? successMessage : message(result),
                    data == null ? null : data(result),
                    result.Changed
                        ? PowerPointEffect.VerifiedChange
                        : PowerPointEffect.VerifiedNoChange);
            }
            catch (OperationCanceledException)
            {
                if (!dispatched) throw;
                return PowerPointOutcome.Unknown(
                    "Cancellation was observed after the PowerPoint dispatch boundary; inspect the target before retrying.",
                    null, "powerpoint_effect_unknown");
            }
            catch (PowerPointBackendException ex)
            {
                return dispatched
                    ? PowerPointOutcome.Unknown(
                        "PowerPoint presentation final state is unknown. " + ex.Message,
                        ex.DetailsJson, "powerpoint_effect_unknown")
                    : Failure(ex.Message, ex.ErrorCode, ex.Retryable, ex.DetailsJson);
            }
            catch (Exception ex)
            {
                return dispatched
                    ? PowerPointOutcome.Unknown(
                        "PowerPoint presentation final state is unknown. " + ex.Message,
                        null, "powerpoint_effect_unknown")
                    : Failure(
                        "PowerPoint operation failed before dispatch: " + ex.Message,
                        "powerpoint_tool_failed", true);
            }
        }

        private IReadOnlyList<PowerPointTextTargetSnapshot> ReadTargets(
            PowerPointReplaceRequest request)
        {
            var targets = _backend.ReadTextTargets(TextScope(request));
            if (targets == null)
                throw new PowerPointBackendException(
                    "PowerPoint text backend returned no snapshot.",
                    "powerpoint_text_snapshot_missing", true);
            return targets;
        }

        private static PowerPointTextScopeRequest TextScope(
            PowerPointReplaceRequest request)
        {
            return new PowerPointTextScopeRequest
            {
                Scope = request.Scope,
                SlideIndex = request.SlideIndex,
                IncludeNotes = request.IncludeNotes,
                MaxSlides = MaxSlides,
                MaxShapesPerSlide = MaxShapesPerSlide,
                MaxTargets = MaxTextTargets
            };
        }

        private static List<PowerPointTextReplacementPlan> ReplacementPlans(
            PowerPointReplaceRequest request,
            IReadOnlyList<PowerPointTextTargetSnapshot> targets)
        {
            var plans = new List<PowerPointTextReplacementPlan>();
            var options = Options(request);
            var replacementPlanned = false;
            var total = 0;
            foreach (var target in targets)
            {
                var text = target.Text ?? string.Empty;
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
                plans.Add(new PowerPointTextReplacementPlan
                {
                    TargetId = target.TargetId,
                    SlideId = target.SlideId,
                    SlideIndex = target.SlideIndex,
                    ShapeId = target.ShapeId,
                    ShapeName = target.ShapeName,
                    Kind = target.Kind,
                    ExpectedText = text,
                    ResultText = Apply(text, edits),
                    Replacements = edits.Select(edit =>
                        new PowerPointTextReplacement
                        {
                            Index = edit.Index,
                            Length = edit.Length,
                            Text = edit.Text
                        }).ToArray()
                });
            }
            return plans;
        }

        private static bool ReplacementVerified(
            IReadOnlyList<PowerPointTextTargetSnapshot> before,
            IReadOnlyList<PowerPointTextTargetSnapshot> after,
            IReadOnlyList<PowerPointTextReplacementPlan> plans)
        {
            if (before == null || after == null || before.Count != after.Count)
                return false;
            var expected = plans.ToDictionary(
                plan => plan.TargetId, plan => plan.ResultText,
                StringComparer.Ordinal);
            for (var index = 0; index < before.Count; index++)
            {
                var left = before[index];
                var right = after[index];
                if (!string.Equals(left.TargetId, right.TargetId,
                        StringComparison.Ordinal) ||
                    left.SlideId != right.SlideId ||
                    left.ShapeId != right.ShapeId ||
                    left.SlideIndex != right.SlideIndex ||
                    !string.Equals(left.ShapeName, right.ShapeName,
                        StringComparison.Ordinal) ||
                    !string.Equals(left.Kind, right.Kind,
                        StringComparison.Ordinal))
                    return false;
                string text;
                if (!expected.TryGetValue(left.TargetId, out text))
                    text = left.Text ?? string.Empty;
                if (!string.Equals(text, right.Text ?? string.Empty,
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

        private static TextPatternOptions Options(PowerPointReplaceRequest request)
        {
            return new TextPatternOptions
            {
                Mode = Normalize(request.Mode, "literal"),
                MatchCase = request.MatchCase,
                WholeWord = request.WholeWord
            };
        }

        private static string Scope(
            string value, int slideIndex, out PowerPointOutcome failure)
        {
            failure = null;
            var scope = Normalize(value, "deck");
            if (scope != "deck" && scope != "slide")
            {
                failure = Failure(
                    "scope must be deck or slide.",
                    "powerpoint_scope_invalid", false);
                return null;
            }
            if (slideIndex < 0)
            {
                failure = Failure(
                    "slideIndex cannot be negative.",
                    "invalid_arguments", false);
                return null;
            }
            return scope;
        }

        private static string TargetHash(
            IEnumerable<PowerPointTextTargetSnapshot> targets)
        {
            var builder = new StringBuilder();
            foreach (var target in targets ??
                new PowerPointTextTargetSnapshot[0])
                builder.Append(target.SlideIndex).Append(':')
                    .Append(target.Kind).Append(':')
                    .Append(target.ShapeName).Append('\n')
                    .Append(target.Text ?? string.Empty).Append('\n');
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

        private static string SlideSummariesJson(
            IEnumerable<PowerPointSlideSummarySnapshot> slides)
        {
            return new JArray((slides ??
                new PowerPointSlideSummarySnapshot[0]).Select(slide =>
                    new JObject
                    {
                        ["index"] = slide.Index,
                        ["title"] = slide.Title ?? string.Empty,
                        ["text"] = Trim(slide.Text, 1000)
                    })).ToString(Formatting.None);
        }

        private static string ShapesJson(
            IEnumerable<PowerPointShapeSnapshot> shapes)
        {
            return new JArray((shapes ?? new PowerPointShapeSnapshot[0]).Select(
                shape => new JObject
                {
                    ["name"] = shape.Name ?? string.Empty,
                    ["type"] = shape.Type ?? string.Empty,
                    ["text"] = shape.Text ?? string.Empty,
                    ["left"] = shape.Left,
                    ["top"] = shape.Top,
                    ["width"] = shape.Width,
                    ["height"] = shape.Height
                })).ToString(Formatting.None);
        }

        private static string Trim(string text, int maxChars)
        {
            maxChars = Math.Max(0, maxChars);
            if (maxChars == 0) return string.Empty;
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars) return text;
            return text.Substring(0, maxChars) + "\n...[truncated]";
        }

        private static string Normalize(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value)
                ? fallback : value.Trim().ToLowerInvariant();
        }

        private static PowerPointOutcome Failure(
            string message, string code, bool retryable,
            string detailsJson = null)
        {
            JObject data;
            try
            {
                data = string.IsNullOrWhiteSpace(detailsJson)
                    ? new JObject() : JObject.Parse(detailsJson);
            }
            catch (JsonException)
            {
                data = new JObject { ["details"] = detailsJson };
            }
            data["code"] = code;
            data["retryable"] = retryable;
            return PowerPointOutcome.Error(
                message, data.ToString(Formatting.None), code, retryable);
        }
    }
}
