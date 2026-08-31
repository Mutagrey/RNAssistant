using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Office.Domains.Excel
{
    public sealed class ExcelChartService
    {
        public const int MaxChatChartCells = 10000;
        public const int MaxWorkbookCharts = 200;
        public const int MaxChartSeries = 100;

        private readonly IExcelChartBackend _backend;

        public ExcelChartService(IExcelChartBackend backend)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        public ExcelChartOutcome CreateChatChart(
            ExcelChatChartRequest request,
            CancellationToken cancellationToken)
        {
            request = request ?? new ExcelChatChartRequest();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = _backend.ReadChatSource(
                    new ExcelChatChartSourceRequest
                    {
                        Sheet = request.Sheet ?? string.Empty,
                        Address = request.Address ?? string.Empty,
                        MaxCells = MaxChatChartCells
                    });
                ValidateChatSource(source);
                cancellationToken.ThrowIfCancellationRequested();
                var rows = source.Values.Select(row =>
                    (IList<object>)row.ToList()).ToList();
                var artifact = new ChartArtifactBuilder().Build(
                    rows,
                    new ChartArtifactSource
                    {
                        Host = "Excel",
                        Workbook = source.Workbook,
                        Sheet = source.Sheet,
                        Address = source.Address,
                        SourceMode = source.SourceMode
                    },
                    request.Title,
                    request.ChartType);
                return ExcelChartOutcome.Ok(
                    "Chat chart artifact created: " + artifact.Title,
                    JsonConvert.SerializeObject(artifact),
                    ExcelChartEffect.None);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ExcelChartBackendException ex)
            {
                return Failure(
                    ex.Message, ex.ErrorCode, ex.Retryable, ex.DetailsJson);
            }
            catch (Exception ex)
            {
                return Failure(
                    "Excel chat chart failed: " + ex.Message,
                    "excel_chart_read_failed", true);
            }
        }

        public ExcelChartOutcome Mutate(
            ExcelChartMutationRequest request,
            Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            request = request ?? new ExcelChartMutationRequest();
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
                var discovery = ReadSnapshot(null);
                ExcelChartOutcome planningFailure;
                var plan = Plan(request, discovery, out planningFailure);
                if (planningFailure != null) return planningFailure;

                cancellationToken.ThrowIfCancellationRequested();
                var before = ReadSnapshot(plan);
                var target = ExactTarget(before.Charts, plan.Sheet, plan.ChartName);
                if (plan.Kind == ExcelChartMutationKind.Create)
                {
                    if (target != null)
                        return Failure(
                            "Chart already exists: " + plan.ChartName,
                            "chart_already_exists", false);
                }
                else if (target == null)
                    return Failure(
                        "Chart not found: " + plan.ChartName,
                        "chart_not_found", false);

                if (plan.Kind == ExcelChartMutationKind.Update &&
                    MatchesPlan(target, plan))
                    return Success(
                        "Chart already matches: " + target.Name,
                        target, ExcelChartEffect.VerifiedNoChange);

                _backend.Apply(new ExcelChartApplyRequest
                {
                    Plan = plan,
                    MaxCharts = MaxWorkbookCharts,
                    MaxSeries = MaxChartSeries,
                    MaxSourceCells = MaxChatChartCells,
                    ExpectedStateToken = before.StateToken
                }, mark);
                if (!dispatched)
                {
                    mark();
                    return Unknown(
                        "Excel chart backend returned without a dispatch boundary.",
                        "excel_chart_dispatch_boundary_missing", before);
                }

                cancellationToken.ThrowIfCancellationRequested();
                var after = ReadSnapshot(plan);
                if (plan.Kind == ExcelChartMutationKind.Delete)
                {
                    if (ExactTarget(after.Charts, plan.Sheet, plan.ChartName) != null ||
                        !OtherChartsUnchanged(
                            before.Charts, after.Charts,
                            plan.Sheet, plan.ChartName, true))
                        return Unknown(
                            "Excel chart may have been deleted, but exact read-back diverged.",
                            "excel_chart_verification_failed", after);
                    return ExcelChartOutcome.Ok(
                        "Chart deleted: " + plan.ChartName,
                        new JObject
                        {
                            ["sheet"] = plan.Sheet,
                            ["chartName"] = plan.ChartName,
                            ["verification"] = "changed"
                        }.ToString(Formatting.None),
                        ExcelChartEffect.VerifiedChange);
                }

                ExcelChartState changed;
                if (plan.Kind == ExcelChartMutationKind.Create)
                {
                    changed = FindCreated(before.Charts, after.Charts);
                    if (changed == null ||
                        !MatchesPlan(changed, plan) ||
                        !string.Equals(
                            changed.Sheet, plan.Sheet,
                            StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(plan.ChartName) &&
                            !string.Equals(changed.Name, plan.ChartName,
                                StringComparison.Ordinal)))
                        return Unknown(
                            "Excel chart may have been created, but exact read-back diverged.",
                            "excel_chart_verification_failed", after);
                }
                else
                {
                    changed = ExactTarget(
                        after.Charts, plan.Sheet, plan.ChartName);
                    if (changed == null || !MatchesPlan(changed, plan) ||
                        !OtherChartsUnchanged(
                            before.Charts, after.Charts,
                            plan.Sheet, plan.ChartName, false) ||
                        !UnrequestedStatePreserved(target, changed, plan))
                        return Unknown(
                            "Excel chart may have been updated, but exact read-back diverged.",
                            "excel_chart_verification_failed", after);
                }
                return Success(
                    plan.Kind == ExcelChartMutationKind.Create
                        ? "Chart added: " + changed.Name
                        : "Chart updated: " + changed.Name,
                    changed, ExcelChartEffect.VerifiedChange);
            }
            catch (OperationCanceledException)
            {
                if (!dispatched) throw;
                return Unknown(
                    "Cancellation was observed after the Excel chart dispatch boundary; inspect the target before retrying.",
                    "excel_chart_effect_unknown", null);
            }
            catch (ExcelChartBackendException ex)
            {
                return dispatched
                    ? Unknown(
                        "Excel chart final state is unknown. " + ex.Message,
                        "excel_chart_effect_unknown", null, ex.DetailsJson)
                    : Failure(
                        ex.Message, ex.ErrorCode, ex.Retryable, ex.DetailsJson);
            }
            catch (Exception ex)
            {
                return dispatched
                    ? Unknown(
                        "Excel chart final state is unknown. " + ex.Message,
                        "excel_chart_effect_unknown", null)
                    : Failure(
                        "Excel chart operation failed before dispatch: " + ex.Message,
                        "excel_chart_failed", true);
            }
        }

        private ExcelChartCollectionSnapshot ReadSnapshot(
            ExcelChartMutationPlan plan)
        {
            var snapshot = _backend.Read(new ExcelChartReadRequest
            {
                Plan = plan,
                MaxCharts = MaxWorkbookCharts,
                MaxSeries = MaxChartSeries,
                MaxSourceCells = MaxChatChartCells
            });
            ValidateSnapshot(snapshot);
            return snapshot;
        }

        private static ExcelChartMutationPlan Plan(
            ExcelChartMutationRequest request,
            ExcelChartCollectionSnapshot snapshot,
            out ExcelChartOutcome failure)
        {
            failure = null;
            var delete = string.Equals(
                request.ToolId, "excel.delete_chart", StringComparison.Ordinal);
            var mode = string.IsNullOrWhiteSpace(request.Mode)
                ? "upsert" : request.Mode.Trim().ToLowerInvariant();
            if (!delete && mode != "upsert" && mode != "createonly" &&
                mode != "updateonly")
            {
                failure = Failure(
                    "mode must be upsert, createOnly, or updateOnly.",
                    "excel_chart_mode_invalid", false);
                return null;
            }
            var candidates = Targets(
                snapshot.Charts, request.Sheet, request.ChartName);
            if (candidates.Count > 1)
            {
                failure = Failure(
                    "Chart name is ambiguous across worksheets: " +
                    request.ChartName + ". Provide sheet.",
                    "excel_chart_ambiguous", false);
                return null;
            }
            var existing = candidates.FirstOrDefault();
            if (delete)
            {
                if (string.IsNullOrWhiteSpace(request.ChartName))
                {
                    failure = Failure(
                        "chartName is required.", "invalid_arguments", false);
                    return null;
                }
                if (existing == null)
                {
                    failure = Failure(
                        "Chart not found: " + request.ChartName,
                        "chart_not_found", false);
                    return null;
                }
                return new ExcelChartMutationPlan
                {
                    Kind = ExcelChartMutationKind.Delete,
                    Sheet = existing.Sheet,
                    ChartName = existing.Name
                };
            }
            if (existing != null && mode == "createonly")
            {
                failure = Failure(
                    "Chart already exists: " + request.ChartName +
                    ". Use mode=upsert or updateOnly.",
                    "chart_already_exists", false);
                return null;
            }
            if (existing == null && mode == "updateonly")
            {
                failure = Failure(
                    string.IsNullOrWhiteSpace(request.ChartName)
                        ? "chartName is required for mode=updateOnly."
                        : "Chart not found: " + request.ChartName +
                            ". Use mode=upsert or createOnly.",
                    "chart_not_found", false);
                return null;
            }
            return existing == null
                ? CreatePlan(request, snapshot.ActiveSheet)
                : UpdatePlan(request, existing);
        }

        private static ExcelChartMutationPlan CreatePlan(
            ExcelChartMutationRequest request, string activeSheet)
        {
            var title = request.HasTitle ? request.Title ?? string.Empty : "Chart";
            var xTitle = request.XAxisTitle ?? string.Empty;
            var yTitle = request.YAxisTitle ?? string.Empty;
            return new ExcelChartMutationPlan
            {
                Kind = ExcelChartMutationKind.Create,
                Sheet = string.IsNullOrWhiteSpace(request.Sheet)
                    ? activeSheet : request.Sheet,
                ChartName = request.ChartName ?? string.Empty,
                HasSourceRange = true,
                SourceRange = request.HasSourceRange
                    ? request.SourceRange ?? string.Empty : "A1:B6",
                HasChartType = true,
                ChartType = NormalizeChartType(request.HasChartType
                    ? request.ChartType : "line"),
                HasTitle = true,
                Title = title,
                ExpectedHasTitle = true,
                HasCategoryLabelsRange = request.HasCategoryLabelsRange &&
                    !string.IsNullOrWhiteSpace(request.CategoryLabelsRange),
                CategoryLabelsRange = request.CategoryLabelsRange ?? string.Empty,
                HasXAxisTitle = request.HasXAxisTitle,
                XAxisTitle = xTitle,
                ExpectedHasXAxisTitle = request.HasXAxisTitle &&
                    !string.IsNullOrWhiteSpace(xTitle),
                HasYAxisTitle = request.HasYAxisTitle,
                YAxisTitle = yTitle,
                ExpectedHasYAxisTitle = request.HasYAxisTitle &&
                    !string.IsNullOrWhiteSpace(yTitle),
                HasLeft = true,
                Left = request.HasLeft ? request.Left : 300,
                HasTop = true,
                Top = request.HasTop ? request.Top : 20,
                HasWidth = true,
                Width = request.HasWidth ? request.Width : 480,
                HasHeight = true,
                Height = request.HasHeight ? request.Height : 300
            };
        }

        private static ExcelChartMutationPlan UpdatePlan(
            ExcelChartMutationRequest request, ExcelChartState existing)
        {
            var xTitle = request.XAxisTitle ?? string.Empty;
            var yTitle = request.YAxisTitle ?? string.Empty;
            return new ExcelChartMutationPlan
            {
                Kind = ExcelChartMutationKind.Update,
                Sheet = existing.Sheet,
                ChartName = existing.Name,
                HasSourceRange = request.HasSourceRange &&
                    !string.IsNullOrWhiteSpace(request.SourceRange),
                SourceRange = request.SourceRange ?? string.Empty,
                HasChartType = request.HasChartType &&
                    !string.IsNullOrWhiteSpace(request.ChartType),
                ChartType = NormalizeChartType(request.ChartType),
                HasTitle = request.HasTitle,
                Title = request.Title ?? string.Empty,
                ExpectedHasTitle = request.HasTitle &&
                    !string.IsNullOrWhiteSpace(request.Title),
                HasCategoryLabelsRange = request.HasCategoryLabelsRange &&
                    !string.IsNullOrWhiteSpace(request.CategoryLabelsRange),
                CategoryLabelsRange = request.CategoryLabelsRange ?? string.Empty,
                HasXAxisTitle = request.HasXAxisTitle,
                XAxisTitle = xTitle,
                ExpectedHasXAxisTitle = request.HasXAxisTitle &&
                    !string.IsNullOrWhiteSpace(xTitle),
                HasYAxisTitle = request.HasYAxisTitle,
                YAxisTitle = yTitle,
                ExpectedHasYAxisTitle = request.HasYAxisTitle &&
                    !string.IsNullOrWhiteSpace(yTitle),
                HasLeft = request.HasLeft,
                Left = request.Left,
                HasTop = request.HasTop,
                Top = request.Top,
                HasWidth = request.HasWidth,
                Width = Math.Max(40, request.Width),
                HasHeight = request.HasHeight,
                Height = Math.Max(40, request.Height)
            };
        }

        private static List<ExcelChartState> Targets(
            IReadOnlyList<ExcelChartState> charts,
            string sheet, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return new List<ExcelChartState>();
            return (charts ?? new ExcelChartState[0]).Where(chart =>
                (string.IsNullOrWhiteSpace(sheet) || string.Equals(
                    chart.Sheet, sheet, StringComparison.OrdinalIgnoreCase)) &&
                string.Equals(
                    chart.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        private static ExcelChartState ExactTarget(
            IReadOnlyList<ExcelChartState> charts,
            string sheet, string name)
        {
            var matches = Targets(charts, sheet, name);
            return matches.Count == 1 ? matches[0] : null;
        }

        private static ExcelChartState FindCreated(
            IReadOnlyList<ExcelChartState> before,
            IReadOnlyList<ExcelChartState> after)
        {
            before = before ?? new ExcelChartState[0];
            after = after ?? new ExcelChartState[0];
            if (after.Count != before.Count + 1) return null;
            var remaining = new List<ExcelChartState>(after);
            foreach (var expected in before)
            {
                var index = remaining.FindIndex(actual =>
                    SameChart(expected, actual));
                if (index < 0) return null;
                remaining.RemoveAt(index);
            }
            return remaining.Count == 1 ? remaining[0] : null;
        }

        private static bool OtherChartsUnchanged(
            IReadOnlyList<ExcelChartState> before,
            IReadOnlyList<ExcelChartState> after,
            string targetSheet, string targetName,
            bool deleted)
        {
            var expected = (before ?? new ExcelChartState[0]).Where(chart =>
                !SameIdentity(chart, targetSheet, targetName)).ToList();
            var actual = (after ?? new ExcelChartState[0]).Where(chart =>
                !SameIdentity(chart, targetSheet, targetName)).ToList();
            if (expected.Count != actual.Count ||
                deleted && after.Count != before.Count - 1 ||
                !deleted && after.Count != before.Count)
                return false;
            foreach (var chart in expected)
            {
                var index = actual.FindIndex(item => SameChart(chart, item));
                if (index < 0) return false;
                actual.RemoveAt(index);
            }
            return actual.Count == 0;
        }

        private static bool UnrequestedStatePreserved(
            ExcelChartState before, ExcelChartState after,
            ExcelChartMutationPlan plan)
        {
            if (before == null || after == null) return false;
            if (!plan.HasTitle && (before.HasTitle != after.HasTitle ||
                !string.Equals(before.Title, after.Title, StringComparison.Ordinal)))
                return false;
            if (!plan.HasLeft && !Near(before.Left, after.Left) ||
                !plan.HasTop && !Near(before.Top, after.Top) ||
                !plan.HasWidth && !Near(before.Width, after.Width) ||
                !plan.HasHeight && !Near(before.Height, after.Height))
                return false;
            if (!plan.HasChartType && !string.Equals(
                before.ChartType, after.ChartType, StringComparison.Ordinal))
                return false;
            if (!plan.HasXAxisTitle && !plan.HasChartType &&
                (before.HasXAxisTitle != after.HasXAxisTitle ||
                 !string.Equals(before.XAxisTitle, after.XAxisTitle,
                    StringComparison.Ordinal)))
                return false;
            if (!plan.HasYAxisTitle && !plan.HasChartType &&
                (before.HasYAxisTitle != after.HasYAxisTitle ||
                 !string.Equals(before.YAxisTitle, after.YAxisTitle,
                    StringComparison.Ordinal)))
                return false;
            if (!plan.HasSourceRange && !plan.HasCategoryLabelsRange &&
                !plan.HasChartType && !SameSeries(before.Series, after.Series))
                return false;
            return true;
        }

        private static bool MatchesPlan(
            ExcelChartState chart, ExcelChartMutationPlan plan)
        {
            if (chart == null || plan == null) return false;
            return (!plan.HasSourceRange || chart.SourceRangeSatisfied) &&
                (!plan.HasChartType || string.Equals(
                    chart.ChartType, plan.ChartType, StringComparison.Ordinal)) &&
                (!plan.HasTitle || chart.HasTitle == plan.ExpectedHasTitle &&
                    string.Equals(chart.Title, plan.Title,
                        StringComparison.Ordinal)) &&
                (!plan.HasCategoryLabelsRange ||
                    chart.CategoryLabelsRangeSatisfied) &&
                (!plan.HasXAxisTitle ||
                    chart.HasXAxisTitle == plan.ExpectedHasXAxisTitle &&
                    string.Equals(chart.XAxisTitle, plan.XAxisTitle,
                        StringComparison.Ordinal)) &&
                (!plan.HasYAxisTitle ||
                    chart.HasYAxisTitle == plan.ExpectedHasYAxisTitle &&
                    string.Equals(chart.YAxisTitle, plan.YAxisTitle,
                        StringComparison.Ordinal)) &&
                (!plan.HasLeft || Near(chart.Left, plan.Left)) &&
                (!plan.HasTop || Near(chart.Top, plan.Top)) &&
                (!plan.HasWidth || Near(chart.Width, plan.Width)) &&
                (!plan.HasHeight || Near(chart.Height, plan.Height));
        }

        private static bool SameChart(
            ExcelChartState left, ExcelChartState right)
        {
            return left != null && right != null &&
                SameIdentity(left, right.Sheet, right.Name) &&
                left.HasTitle == right.HasTitle &&
                string.Equals(left.Title, right.Title, StringComparison.Ordinal) &&
                string.Equals(left.ChartType, right.ChartType,
                    StringComparison.Ordinal) &&
                left.HasXAxisTitle == right.HasXAxisTitle &&
                string.Equals(left.XAxisTitle, right.XAxisTitle,
                    StringComparison.Ordinal) &&
                left.HasYAxisTitle == right.HasYAxisTitle &&
                string.Equals(left.YAxisTitle, right.YAxisTitle,
                    StringComparison.Ordinal) &&
                Near(left.Left, right.Left) && Near(left.Top, right.Top) &&
                Near(left.Width, right.Width) && Near(left.Height, right.Height) &&
                SameSeries(left.Series, right.Series);
        }

        private static bool SameIdentity(
            ExcelChartState chart, string sheet, string name)
        {
            return chart != null &&
                string.Equals(chart.Sheet, sheet, StringComparison.Ordinal) &&
                string.Equals(chart.Name, name, StringComparison.Ordinal);
        }

        private static bool SameSeries(
            IReadOnlyList<ExcelChartSeriesState> left,
            IReadOnlyList<ExcelChartSeriesState> right)
        {
            left = left ?? new ExcelChartSeriesState[0];
            right = right ?? new ExcelChartSeriesState[0];
            if (left.Count != right.Count) return false;
            for (var index = 0; index < left.Count; index++)
                if (!string.Equals(left[index].Name, right[index].Name,
                        StringComparison.Ordinal) ||
                    !string.Equals(left[index].Formula, right[index].Formula,
                        StringComparison.Ordinal))
                    return false;
            return true;
        }

        private static bool Near(double left, double right)
        {
            return Math.Abs(left - right) <= 0.1;
        }

        private static string NormalizeChartType(string value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "column":
                case "col": return "column";
                case "bar": return "bar";
                case "pie": return "pie";
                default: return "line";
            }
        }

        private static void ValidateChatSource(
            ExcelChatChartSourceSnapshot snapshot)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.Workbook) ||
                string.IsNullOrWhiteSpace(snapshot.Sheet) ||
                string.IsNullOrWhiteSpace(snapshot.Address) ||
                (snapshot.SourceMode != "selection" &&
                 snapshot.SourceMode != "range") || snapshot.Rows < 1 ||
                snapshot.Columns < 1 ||
                snapshot.CellCount != (long)snapshot.Rows * snapshot.Columns ||
                snapshot.CellCount > MaxChatChartCells || snapshot.Values == null ||
                snapshot.Values.Count != snapshot.Rows || snapshot.Values.Any(row =>
                    row == null || row.Count != snapshot.Columns))
                throw InvalidBackend(
                    "Excel chat chart backend returned an invalid source snapshot.");
        }

        private static void ValidateSnapshot(
            ExcelChartCollectionSnapshot snapshot)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.StateToken) ||
                snapshot.Charts == null ||
                snapshot.Charts.Count > MaxWorkbookCharts)
                throw InvalidBackend(
                    "Excel chart backend returned an invalid collection snapshot.");
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var chart in snapshot.Charts)
            {
                if (chart == null || string.IsNullOrWhiteSpace(chart.Sheet) ||
                    string.IsNullOrWhiteSpace(chart.Name) ||
                    string.IsNullOrWhiteSpace(chart.ChartType) ||
                    chart.Series == null || chart.Series.Count > MaxChartSeries ||
                    chart.Series.Any(series => series == null) ||
                    double.IsNaN(chart.Left) || double.IsInfinity(chart.Left) ||
                    double.IsNaN(chart.Top) || double.IsInfinity(chart.Top) ||
                    double.IsNaN(chart.Width) || double.IsInfinity(chart.Width) ||
                    double.IsNaN(chart.Height) || double.IsInfinity(chart.Height) ||
                    chart.Width <= 0 || chart.Height <= 0 ||
                    !identities.Add(chart.Sheet + "\n" + chart.Name))
                    throw InvalidBackend(
                        "Excel chart backend returned invalid chart state.");
            }
        }

        private static ExcelChartOutcome Success(
            string message, ExcelChartState chart, ExcelChartEffect effect)
        {
            return ExcelChartOutcome.Ok(message,
                ChartData(chart, effect).ToString(Formatting.None), effect);
        }

        private static JObject ChartData(
            ExcelChartState chart, ExcelChartEffect effect)
        {
            return new JObject
            {
                ["sheet"] = chart.Sheet,
                ["name"] = chart.Name,
                ["title"] = chart.Title,
                ["chartType"] = chart.ChartType,
                ["xAxisTitle"] = chart.XAxisTitle,
                ["yAxisTitle"] = chart.YAxisTitle,
                ["series"] = JArray.FromObject(chart.Series),
                ["left"] = chart.Left,
                ["top"] = chart.Top,
                ["width"] = chart.Width,
                ["height"] = chart.Height,
                ["verification"] = effect == ExcelChartEffect.VerifiedNoChange
                    ? "no_change" : "changed"
            };
        }

        private static ExcelChartOutcome Failure(
            string message, string code, bool retryable,
            string detailsJson = null)
        {
            return ExcelChartOutcome.Error(message,
                ErrorData(code, retryable, detailsJson).ToString(Formatting.None),
                code, retryable);
        }

        private static ExcelChartOutcome Unknown(
            string message, string code, ExcelChartCollectionSnapshot snapshot,
            string detailsJson = null)
        {
            var data = ErrorData(code, false, detailsJson);
            if (snapshot != null)
            {
                data["activeSheet"] = snapshot.ActiveSheet ?? string.Empty;
                data["chartCount"] = snapshot.Charts == null
                    ? 0 : snapshot.Charts.Count;
            }
            return ExcelChartOutcome.Unknown(
                message, data.ToString(Formatting.None), code);
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

        private static ExcelChartBackendException InvalidBackend(string message)
        {
            return new ExcelChartBackendException(
                message, "excel_chart_snapshot_invalid", false);
        }
    }
}
