using System;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Office.Domains.Excel
{
    public sealed class ExcelRangeMutationService
    {
        public const int MaxMutationCells = 100000;
        public const int MaxAutoFitDimensions = 10000;

        private readonly IExcelRangeMutationBackend _backend;

        public ExcelRangeMutationService(IExcelRangeMutationBackend backend)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        public ExcelRangeMutationOutcome Clear(
            ExcelClearRangeRequest request,
            Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            request = request ?? new ExcelClearRangeRequest();
            if (string.IsNullOrWhiteSpace(request.Address))
                return Failure("address is required.", "invalid_arguments", false);
            var clearWhat = (request.ClearWhat ?? "values").Trim().ToLowerInvariant();
            if (clearWhat != "formats" && clearWhat != "all") clearWhat = "values";
            return Execute(new ExcelRangeMutationSpec
            {
                Kind = ExcelRangeMutationKind.Clear,
                Sheet = request.Sheet ?? string.Empty,
                Address = request.Address.Trim(),
                ClearWhat = clearWhat
            }, markDispatchPossible, cancellationToken);
        }

        public ExcelRangeMutationOutcome Sort(
            ExcelSortRangeRequest request,
            Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            request = request ?? new ExcelSortRangeRequest();
            if (string.IsNullOrWhiteSpace(request.Address))
                return Failure("address is required.", "invalid_arguments", false);
            return Execute(new ExcelRangeMutationSpec
            {
                Kind = ExcelRangeMutationKind.Sort,
                Sheet = request.Sheet ?? string.Empty,
                Address = request.Address.Trim(),
                KeyColumn = Math.Max(1, request.KeyColumn),
                Descending = request.Descending,
                HasHeaders = request.HasHeaders
            }, markDispatchPossible, cancellationToken);
        }

        public ExcelRangeMutationOutcome Filter(
            ExcelFilterRangeRequest request,
            Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            request = request ?? new ExcelFilterRangeRequest();
            if (string.IsNullOrWhiteSpace(request.Address))
                return Failure("address is required.", "invalid_arguments", false);
            return Execute(new ExcelRangeMutationSpec
            {
                Kind = ExcelRangeMutationKind.Filter,
                Sheet = request.Sheet ?? string.Empty,
                Address = request.Address.Trim(),
                Field = Math.Max(1, request.Field),
                Criteria = request.Criteria ?? string.Empty
            }, markDispatchPossible, cancellationToken);
        }

        public ExcelRangeMutationOutcome Format(
            ExcelFormatRangeRequest request,
            Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            request = request ?? new ExcelFormatRangeRequest();
            var autoFit = (request.AutoFit ?? string.Empty).Trim().ToLowerInvariant();
            var hasFormatting = request.HasNumberFormat || request.HasBold ||
                request.HasItalic || request.HasFillColor || request.HasFontColor ||
                request.HasHorizontalAlignment;
            if (!hasFormatting && string.IsNullOrWhiteSpace(autoFit))
                return Failure(
                    "Provide at least one formatting field or autoFit operation.",
                    "invalid_arguments", false);
            if (!string.IsNullOrWhiteSpace(autoFit) && autoFit != "columns" &&
                autoFit != "rows" && autoFit != "both")
                return Failure(
                    "autoFit must be columns, rows, or both.",
                    "invalid_arguments", false);

            return Execute(new ExcelRangeMutationSpec
            {
                Kind = ExcelRangeMutationKind.Format,
                Sheet = request.Sheet ?? string.Empty,
                Address = request.Address == null ? string.Empty : request.Address.Trim(),
                HasNumberFormat = request.HasNumberFormat,
                NumberFormat = request.NumberFormat ?? string.Empty,
                HasBold = request.HasBold,
                Bold = request.Bold,
                HasItalic = request.HasItalic,
                Italic = request.Italic,
                HasFillColor = request.HasFillColor,
                FillColor = request.FillColor ?? string.Empty,
                HasFontColor = request.HasFontColor,
                FontColor = request.FontColor ?? string.Empty,
                HasHorizontalAlignment = request.HasHorizontalAlignment,
                HorizontalAlignment = request.HorizontalAlignment ?? string.Empty,
                AutoFit = autoFit
            }, markDispatchPossible, cancellationToken);
        }

        private ExcelRangeMutationOutcome Execute(
            ExcelRangeMutationSpec spec,
            Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            var dispatched = false;
            Action mark = delegate
            {
                if (dispatched) return;
                dispatched = true;
                if (markDispatchPossible != null) markDispatchPossible();
            };
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var before = _backend.Read(new ExcelRangeMutationReadRequest
                {
                    Spec = spec,
                    Sheet = spec.Sheet,
                    Address = spec.Address,
                    MaxCells = MaxMutationCells
                });
                ValidateSnapshot(before, spec, null);
                ValidateSelectors(before, spec);
                if (before.Satisfied)
                    return Success(before, ExcelRangeMutationEffect.VerifiedNoChange);

                cancellationToken.ThrowIfCancellationRequested();
                _backend.Apply(new ExcelRangeMutationApplyRequest
                {
                    Spec = spec,
                    Sheet = before.Sheet,
                    Address = before.Address,
                    Rows = before.Rows,
                    Columns = before.Columns,
                    MaxCells = MaxMutationCells,
                    ExpectedStateToken = before.StateToken
                }, mark);
                if (!dispatched)
                {
                    mark();
                    return Unknown(
                        "Excel range backend returned without a dispatch boundary.",
                        "excel_range_mutation_dispatch_boundary_missing", before);
                }

                cancellationToken.ThrowIfCancellationRequested();
                var after = _backend.Read(new ExcelRangeMutationReadRequest
                {
                    Spec = spec,
                    Sheet = before.Sheet,
                    Address = before.Address,
                    ExpectedRows = before.Rows,
                    ExpectedColumns = before.Columns,
                    MaxCells = MaxMutationCells
                });
                ValidateSnapshot(after, spec, before);
                if (!after.Satisfied)
                    return Unknown(
                        "Excel range mutation completed, but exact read-back did not match the requested state.",
                        "excel_range_mutation_verification_failed", after);
                return Success(after,
                    string.Equals(before.StateToken, after.StateToken, StringComparison.Ordinal)
                        ? ExcelRangeMutationEffect.VerifiedNoChange
                        : ExcelRangeMutationEffect.VerifiedChange);
            }
            catch (OperationCanceledException)
            {
                if (!dispatched) throw;
                return Unknown(
                    "Cancellation was observed after the Excel range dispatch boundary; inspect the target before retrying.",
                    "excel_range_mutation_effect_unknown", null);
            }
            catch (ExcelRangeMutationBackendException ex)
            {
                return dispatched
                    ? Unknown(
                        "Excel range final state is unknown. " + ex.Message,
                        "excel_range_mutation_effect_unknown", null, ex.DetailsJson)
                    : Failure(ex.Message, ex.ErrorCode, ex.Retryable, ex.DetailsJson);
            }
            catch (Exception ex)
            {
                return dispatched
                    ? Unknown(
                        "Excel range final state is unknown. " + ex.Message,
                        "excel_range_mutation_effect_unknown", null)
                    : Failure(
                        "Excel range mutation failed before dispatch: " + ex.Message,
                        "excel_range_mutation_failed", true);
            }
        }

        private static void ValidateSelectors(
            ExcelRangeMutationSnapshot snapshot, ExcelRangeMutationSpec spec)
        {
            if (spec.Kind == ExcelRangeMutationKind.Sort &&
                spec.KeyColumn > snapshot.Columns)
                throw new ExcelRangeMutationBackendException(
                    "keyColumn is outside the sort range.",
                    "excel_sort_key_out_of_range", false);
            if (spec.Kind == ExcelRangeMutationKind.Filter &&
                spec.Field > snapshot.Columns)
                throw new ExcelRangeMutationBackendException(
                    "field is outside the filter range.",
                    "excel_filter_field_out_of_range", false);
            if (spec.Kind == ExcelRangeMutationKind.Format &&
                ((spec.AutoFit == "columns" || spec.AutoFit == "both") &&
                    snapshot.Columns > MaxAutoFitDimensions ||
                 (spec.AutoFit == "rows" || spec.AutoFit == "both") &&
                    snapshot.Rows > MaxAutoFitDimensions))
                throw new ExcelRangeMutationBackendException(
                    "Excel autoFit target has too many row or column dimensions.",
                    "excel_range_autofit_too_large", false);
        }

        private static void ValidateSnapshot(
            ExcelRangeMutationSnapshot snapshot,
            ExcelRangeMutationSpec spec,
            ExcelRangeMutationSnapshot expectedTarget)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.Sheet) ||
                string.IsNullOrWhiteSpace(snapshot.Address) ||
                string.IsNullOrWhiteSpace(snapshot.StateToken))
                throw InvalidBackend(
                    "Excel range backend returned incomplete target state.");
            if (snapshot.Kind != spec.Kind || snapshot.Rows < 1 ||
                snapshot.Columns < 1 ||
                snapshot.CellCount != (long)snapshot.Rows * snapshot.Columns ||
                snapshot.CellCount > MaxMutationCells)
                throw InvalidBackend(
                    "Excel range backend returned invalid target dimensions.");
            if (expectedTarget != null &&
                (!string.Equals(snapshot.Sheet, expectedTarget.Sheet,
                    StringComparison.OrdinalIgnoreCase) ||
                 !string.Equals(snapshot.Address, expectedTarget.Address,
                    StringComparison.OrdinalIgnoreCase) ||
                 snapshot.Rows != expectedTarget.Rows ||
                 snapshot.Columns != expectedTarget.Columns))
                throw InvalidBackend(
                    "Excel range read-back resolved a different target rectangle.");
        }

        private static ExcelRangeMutationOutcome Success(
            ExcelRangeMutationSnapshot snapshot,
            ExcelRangeMutationEffect effect)
        {
            var operation = Operation(snapshot.Kind);
            var data = Coordinates(snapshot);
            data["operation"] = operation;
            data["verification"] = effect == ExcelRangeMutationEffect.VerifiedChange
                ? "changed" : "no_change";
            return ExcelRangeMutationOutcome.Ok(
                Message(snapshot, operation, effect),
                data.ToString(Formatting.None), effect);
        }

        private static string Message(
            ExcelRangeMutationSnapshot snapshot,
            string operation,
            ExcelRangeMutationEffect effect)
        {
            var target = snapshot.Sheet + "!" + snapshot.Address;
            if (effect == ExcelRangeMutationEffect.VerifiedNoChange)
                return "Excel range already matched " + operation +
                    " state: " + target + ".";
            switch (snapshot.Kind)
            {
                case ExcelRangeMutationKind.Clear:
                    return "Range cleared: " + target;
                case ExcelRangeMutationKind.Sort:
                    return "Range sorted: " + target;
                case ExcelRangeMutationKind.Filter:
                    return "Range filtered: " + target;
                default:
                    return "Range formatted: " + target;
            }
        }

        private static string Operation(ExcelRangeMutationKind kind)
        {
            switch (kind)
            {
                case ExcelRangeMutationKind.Clear: return "clear";
                case ExcelRangeMutationKind.Sort: return "sort";
                case ExcelRangeMutationKind.Filter: return "filter";
                default: return "format";
            }
        }

        private static JObject Coordinates(ExcelRangeMutationSnapshot snapshot)
        {
            return new JObject
            {
                ["sheet"] = snapshot.Sheet,
                ["address"] = snapshot.Address,
                ["rows"] = snapshot.Rows,
                ["columns"] = snapshot.Columns,
                ["cellCount"] = snapshot.CellCount
            };
        }

        private static ExcelRangeMutationOutcome Failure(
            string message, string code, bool retryable,
            string detailsJson = null)
        {
            return ExcelRangeMutationOutcome.Error(message,
                ErrorData(code, retryable, detailsJson).ToString(Formatting.None),
                code, retryable);
        }

        private static ExcelRangeMutationOutcome Unknown(
            string message, string code,
            ExcelRangeMutationSnapshot snapshot,
            string detailsJson = null)
        {
            var data = ErrorData(code, false, detailsJson);
            if (snapshot != null) data["target"] = Coordinates(snapshot);
            return ExcelRangeMutationOutcome.Unknown(
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

        private static ExcelRangeMutationBackendException InvalidBackend(
            string message)
        {
            return new ExcelRangeMutationBackendException(
                message, "excel_range_mutation_snapshot_invalid", false);
        }
    }
}
