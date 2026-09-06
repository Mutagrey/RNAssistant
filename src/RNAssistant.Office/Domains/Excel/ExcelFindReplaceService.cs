using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Domains.Excel
{
    public sealed class ExcelFindReplaceService
    {
        public const int MaxResults = 500;
        public const int MaxContextChars = 1000;
        public const int MaxReplacements = 10000;
        public const int MaximumSearchCharacters = 1000000;
        public const int MaximumSearchCells = 100000;

        private readonly IExcelFindReplaceBackend _backend;

        public ExcelFindReplaceService(IExcelFindReplaceBackend backend)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        public ExcelSearchSnapshot CaptureSearch(ExcelCellScopeRequest request, CancellationToken cancellationToken)
        {
            var cells = new List<ExcelCellSnapshot>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long characters = 0;
            request.MaxCells = MaximumSearchCells;
            cancellationToken.ThrowIfCancellationRequested();
            _backend.ReadScope(request, cell => {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateCell(cell);
                if (cell.Value == null || cell.Formula == null || cell.Sheet.Length > 128 || cell.Address.Length > 128 ||
                    !seen.Add(CellKey(cell.Sheet, cell.Address)))
                    throw new ExcelFindReplaceBackendException("Invalid or duplicate search cell.", "excel_scope_snapshot_invalid", false);
                characters += cell.Sheet.Length + cell.Address.Length + cell.Value.Length + cell.Formula.Length;
                if (cells.Count >= MaximumSearchCells || characters > MaximumSearchCharacters)
                    throw new ExcelFindReplaceBackendException("Choose a smaller Excel search scope.", "RESOURCE_SNAPSHOT_TOO_LARGE", false);
                cells.Add(new ExcelCellSnapshot { Sheet = cell.Sheet, Address = cell.Address, Value = cell.Value,
                    Formula = cell.Formula, HasFormula = cell.HasFormula });
            });
            cancellationToken.ThrowIfCancellationRequested();
            return new ExcelSearchSnapshot { Scope = request.Scope, Sheet = request.Sheet, Address = request.Address, Cells = cells };
        }

        internal static ExcelFindOutcome Find(ExcelSearchSnapshot snapshot, ExcelFindRequest request, CancellationToken cancellationToken)
        {
            request = request ?? new ExcelFindRequest();
            if (string.IsNullOrWhiteSpace(request.Query))
                return FindFailure("query is required.", "invalid_pattern", false);
            var mode = NormalizeMode(request.Mode);
            if (mode == null)
                return FindFailure("mode must be literal or regex.", "invalid_arguments", false);
            var lookIn = NormalizeFindLookIn(request.LookIn);
            if (lookIn == null)
                return FindFailure("lookIn must be values, formulas, or both.", "invalid_arguments", false);
            var scope = NormalizeScope(request.Scope, request.Sheet, request.Address, "workbook");
            if (scope == null)
                return FindFailure("scope must be workbook, sheet, range, or selection.", "invalid_arguments", false);

            var maxResults = Math.Max(1, Math.Min(MaxResults,
                request.MaxResults < 1 ? 50 : request.MaxResults));
            var contextChars = Math.Max(0, Math.Min(MaxContextChars,
                request.ContextChars < 0 ? 0 : request.ContextChars));
            var options = new TextPatternOptions
            {
                Mode = mode,
                MatchCase = request.MatchCase,
                WholeWord = request.WholeWord
            };
            var matches = new JArray();
            var total = 0;
            try
            {
                if (snapshot == null || snapshot.Cells == null || snapshot.Scope != scope)
                    return FindFailure("The exact search snapshot is unavailable.", "RESOURCE_SNAPSHOT_UNAVAILABLE", false);
                foreach (var cell in snapshot.Cells)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ValidateCell(cell);
                    foreach (var field in SearchFields(cell, lookIn))
                    {
                        var found = TextPatternEngine.Find(
                            field.Text,
                            request.Query,
                            options,
                            Math.Max(1, maxResults - matches.Count),
                            contextChars);
                        total += found.MatchCount;
                        foreach (var match in found.Matches)
                        {
                            if (matches.Count >= maxResults) break;
                            matches.Add(new JObject
                            {
                                ["sheet"] = cell.Sheet,
                                ["address"] = cell.Address,
                                ["field"] = field.Name,
                                ["start"] = match.Index,
                                ["end"] = match.Index + match.Length,
                                ["preview"] = match.Preview ?? string.Empty
                            });
                        }
                    }
                }
                var data = new JObject
                {
                    ["query"] = request.Query,
                    ["mode"] = mode,
                    ["scope"] = scope,
                    ["matchCount"] = total,
                    ["returnedCount"] = matches.Count,
                    ["truncated"] = total > matches.Count,
                    ["matches"] = matches
                };
                return ExcelFindOutcome.Ok("Cells found: " + total,
                    data.ToString(Formatting.None));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (TextPatternException ex)
            {
                return FindFailure(ex.Message, ex.ErrorCode, false);
            }
            catch (ExcelFindReplaceBackendException ex)
            {
                return FindFailure(ex.Message, ex.ErrorCode, ex.Retryable, ex.DetailsJson);
            }
            catch (Exception ex)
            {
                return FindFailure(ex.Message, "office_tool_error", true);
            }
        }

        public ExcelReplaceOutcome Replace(
            ExcelReplaceRequest request,
            Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            request = request ?? new ExcelReplaceRequest();
            if (markDispatchPossible == null)
                throw new ArgumentNullException(nameof(markDispatchPossible));
            if (string.IsNullOrEmpty(request.Find))
                return ReplaceFailure("Pattern is required.", "invalid_pattern", false);
            var mode = NormalizeMode(request.Mode);
            if (mode == null)
                return ReplaceFailure("mode must be literal or regex.", "invalid_arguments", false);
            var lookIn = NormalizeReplaceLookIn(request.LookIn);
            if (lookIn == null)
                return ReplaceFailure("replace_cells lookIn must be values or formulas.", "invalid_arguments", false);
            var scope = NormalizeScope(request.Scope, request.Sheet, request.Address, "selection");
            if (scope == null)
                return ReplaceFailure("scope must be workbook, sheet, range, or selection.", "invalid_arguments", false);

            var maxReplacements = Math.Max(1, Math.Min(MaxReplacements,
                request.MaxReplacements < 1 ? 500 : request.MaxReplacements));
            var options = new TextPatternOptions
            {
                Mode = mode,
                MatchCase = request.MatchCase,
                WholeWord = request.WholeWord
            };
            var plans = new List<ReplacementPlan>();
            var plannedCount = 0;
            var limitExceeded = false;
            var replacementPlanned = false;
            var dispatched = false;
            try
            {
                var scopeRequest = ScopeRequest(scope, request.Sheet, request.Address);
                _backend.ReadScope(scopeRequest, cell =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ValidateCell(cell);
                    if (limitExceeded) return;
                    if (lookIn == "values" && cell.HasFormula) return;
                    if (lookIn == "formulas" && !cell.HasFormula) return;
                    var current = cell.HasFormula ? cell.Formula ?? string.Empty : cell.Value ?? string.Empty;
                    var found = TextPatternEngine.Find(current, request.Find, options, 1, 0);
                    if (found.MatchCount < 1 || (!request.ReplaceAll && replacementPlanned)) return;
                    var replaced = TextPatternEngine.Replace(
                        current,
                        request.Find,
                        request.Replacement ?? string.Empty,
                        options,
                        request.ReplaceAll,
                        maxReplacements);
                    if (replaced.MatchCount < 1) return;
                    if (plannedCount > maxReplacements - replaced.MatchCount)
                    {
                        limitExceeded = true;
                        return;
                    }
                    plans.Add(new ReplacementPlan
                    {
                        Count = replaced.MatchCount,
                        CurrentText = current,
                        Request = new ExcelCellReplacementRequest
                        {
                            Sheet = cell.Sheet,
                            Address = cell.Address,
                            ExpectedValue = cell.Value ?? string.Empty,
                            ExpectedFormula = cell.Formula ?? string.Empty,
                            ExpectedHasFormula = cell.HasFormula,
                            Formula = cell.HasFormula,
                            Text = replaced.Text
                        }
                    });
                    plannedCount += replaced.MatchCount;
                    replacementPlanned = true;
                });

                if (limitExceeded)
                    return ReplaceFailure(
                        "Replacement count exceeds maxReplacements=" + maxReplacements + ".",
                        "replacement_limit_exceeded", false);
                var total = plannedCount;

                var changes = plans
                    .Where(plan => !string.Equals(
                        plan.CurrentText, plan.Request.Text, StringComparison.Ordinal))
                    .Select(plan => plan.Request)
                    .ToArray();
                if (changes.Length > 0)
                {
                    _backend.Apply(new ExcelReplaceApplyRequest { Replacements = changes }, delegate
                    {
                        dispatched = true;
                        markDispatchPossible();
                    });
                }

                var expectedKeys = new HashSet<string>(
                    changes.Select(change => CellKey(change.Sheet, change.Address)),
                    StringComparer.OrdinalIgnoreCase);
                var post = new Dictionary<string, ExcelCellSnapshot>(
                    StringComparer.OrdinalIgnoreCase);
                var postHash = new StringBuilder();
                _backend.ReadScope(scopeRequest, cell =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ValidateCell(cell);
                    var key = CellKey(cell.Sheet, cell.Address);
                    if (expectedKeys.Contains(key)) post[key] = cell;
                    AppendHash(postHash, cell);
                });
                if (changes.Any(change => !Matches(post, change)))
                    return ReplaceUnknown(
                        "Excel replacements may have been applied, but exact read-back diverged.",
                        "excel_replace_verification_failed");

                var data = new JObject
                {
                    ["replacements"] = total,
                    ["scopeSha256"] = TextPatternEngine.Sha256(postHash.ToString())
                };
                return ExcelReplaceOutcome.Ok(
                    "Excel replacements completed: " + total + ".",
                    data.ToString(Formatting.None),
                    changes.Length == 0
                        ? ExcelReplaceEffect.VerifiedNoChange
                        : ExcelReplaceEffect.VerifiedChange);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (TextPatternException ex)
            {
                return dispatched
                    ? ReplaceUnknown(ex.Message, "excel_replace_effect_unknown")
                    : ReplaceFailure(ex.Message, ex.ErrorCode, false);
            }
            catch (ExcelFindReplaceBackendException ex)
            {
                return dispatched
                    ? ReplaceUnknown(ex.Message, "excel_replace_effect_unknown", ex.DetailsJson)
                    : ReplaceFailure(ex.Message, ex.ErrorCode, ex.Retryable, ex.DetailsJson);
            }
            catch (Exception ex)
            {
                return dispatched
                    ? ReplaceUnknown(ex.Message, "excel_replace_effect_unknown")
                    : ReplaceFailure(ex.Message, "office_tool_error", true);
            }
        }

        internal static string NormalizeScope(
            string scope, string sheet, string address, string defaultScope)
        {
            if (string.IsNullOrWhiteSpace(scope))
                return !string.IsNullOrWhiteSpace(address)
                    ? "range"
                    : !string.IsNullOrWhiteSpace(sheet) ? "sheet" : defaultScope;
            scope = scope.Trim().ToLowerInvariant();
            return scope == "workbook" || scope == "sheet" ||
                scope == "range" || scope == "selection" ? scope : null;
        }

        internal static string NormalizeMode(string value)
        {
            value = string.IsNullOrWhiteSpace(value) ? "literal" : value.Trim().ToLowerInvariant();
            return value == "literal" || value == "regex" ? value : null;
        }

        internal static string NormalizeFindLookIn(string value)
        {
            value = string.IsNullOrWhiteSpace(value) ? "values" : value.Trim().ToLowerInvariant();
            return value == "values" || value == "formulas" || value == "both" ? value : null;
        }

        private static string NormalizeReplaceLookIn(string value)
        {
            value = string.IsNullOrWhiteSpace(value) ? "values" : value.Trim().ToLowerInvariant();
            return value == "values" || value == "formulas" ? value : null;
        }

        private static ExcelCellScopeRequest ScopeRequest(
            string scope, string sheet, string address)
        {
            return new ExcelCellScopeRequest
            {
                Scope = scope,
                Sheet = sheet ?? string.Empty,
                Address = address ?? string.Empty
            };
        }

        private static IEnumerable<SearchField> SearchFields(
            ExcelCellSnapshot cell, string lookIn)
        {
            if (!cell.HasFormula && lookIn != "formulas")
                yield return new SearchField("value", cell.Value);
            if (cell.HasFormula && lookIn != "values")
            {
                if (lookIn == "both")
                    yield return new SearchField("value", cell.Value);
                yield return new SearchField("formula", cell.Formula);
            }
        }

        private static void ValidateCell(ExcelCellSnapshot cell)
        {
            if (cell == null || string.IsNullOrWhiteSpace(cell.Sheet) ||
                string.IsNullOrWhiteSpace(cell.Address))
                throw new ExcelFindReplaceBackendException(
                    "Excel scope returned an invalid cell.",
                    "excel_scope_snapshot_invalid", false);
        }

        private static void AppendHash(StringBuilder builder, ExcelCellSnapshot cell)
        {
            builder.Append(cell.Sheet).Append('!').Append(cell.Address).Append('\n')
                .Append(cell.Value ?? string.Empty).Append('\n')
                .Append(cell.Formula ?? string.Empty).Append('\n');
        }

        private static string CellKey(string sheet, string address)
        {
            return (sheet ?? string.Empty) + "\n" + (address ?? string.Empty);
        }

        private static bool Matches(
            IDictionary<string, ExcelCellSnapshot> post,
            ExcelCellReplacementRequest expected)
        {
            ExcelCellSnapshot cell;
            if (!post.TryGetValue(CellKey(expected.Sheet, expected.Address), out cell)) return false;
            return cell.HasFormula == expected.Formula && string.Equals(
                expected.Formula ? cell.Formula : cell.Value,
                expected.Text,
                StringComparison.Ordinal);
        }

        private static ExcelFindOutcome FindFailure(
            string message, string code, bool retryable, string detailsJson = null)
        {
            return ExcelFindOutcome.Error(message,
                ErrorData(code, retryable, detailsJson).ToString(Formatting.None),
                code, retryable);
        }

        private static ExcelReplaceOutcome ReplaceFailure(
            string message, string code, bool retryable, string detailsJson = null)
        {
            return ExcelReplaceOutcome.Error(message,
                ErrorData(code, retryable, detailsJson).ToString(Formatting.None),
                code, retryable);
        }

        private static ExcelReplaceOutcome ReplaceUnknown(
            string message, string code, string detailsJson = null)
        {
            return ExcelReplaceOutcome.Unknown(message,
                ErrorData(code, false, detailsJson).ToString(Formatting.None), code);
        }

        private static JObject ErrorData(
            string code, bool retryable, string detailsJson)
        {
            var data = new JObject
            {
                ["code"] = code,
                ["retryable"] = retryable
            };
            if (!string.IsNullOrWhiteSpace(detailsJson))
            {
                try { data["details"] = JToken.Parse(detailsJson); }
                catch (JsonException) { data["details"] = detailsJson; }
            }
            return data;
        }

        private sealed class SearchField
        {
            internal SearchField(string name, string text)
            {
                Name = name;
                Text = text ?? string.Empty;
            }

            internal string Name { get; private set; }
            internal string Text { get; private set; }
        }

        private sealed class ReplacementPlan
        {
            internal int Count { get; set; }
            internal string CurrentText { get; set; }
            internal ExcelCellReplacementRequest Request { get; set; }
        }
    }
}
