using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Office.Domains.Excel
{
    public sealed class ExcelWriteService
    {
        public const int MaxWriteCells = 100000;
        public const int MaxWriteRows = 1048576;
        public const int MaxWriteColumns = 16384;

        private readonly IExcelWriteBackend _backend;

        public ExcelWriteService(IExcelWriteBackend backend)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        public ExcelWriteOutcome Write(ExcelWriteRequest request, Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            var validation = ValidateAndNormalize(request);
            if (validation.Error != null) return validation.Error;

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
                var before = _backend.Read(ReadRequest(validation.Request));
                ValidateSnapshot(before, validation.Request.Kind, null);
                var intended = IntendedState(validation.Request, before.Rows, before.Columns);
                if (Matches(before, intended, validation.Request.Kind))
                    return Success(before, validation.Request.Kind, ExcelWriteEffect.VerifiedNoChange);

                cancellationToken.ThrowIfCancellationRequested();
                _backend.Apply(ApplyRequest(validation.Request, before), mark);
                if (!dispatched)
                {
                    // A backend that claims completion without its required boundary
                    // cannot prove whether an external write occurred.
                    mark();
                    return Unknown("Excel write backend returned without a dispatch boundary.",
                        "excel_write_dispatch_boundary_missing", before);
                }

                cancellationToken.ThrowIfCancellationRequested();
                var after = _backend.Read(new ExcelWriteReadRequest
                {
                    Kind = validation.Request.Kind,
                    Sheet = before.Sheet,
                    Address = before.Address,
                    Rows = before.Rows,
                    Columns = before.Columns,
                    MaxCells = MaxWriteCells
                });
                ValidateSnapshot(after, validation.Request.Kind, before);
                if (!Matches(after, intended, validation.Request.Kind))
                    return Unknown("Excel write completed, but exact read-back did not match the intended state.",
                        "excel_write_verification_failed", after);
                return Success(after, validation.Request.Kind, ExcelWriteEffect.VerifiedChange);
            }
            catch (OperationCanceledException)
            {
                if (!dispatched) throw;
                return Unknown("Cancellation was observed after the Excel write dispatch boundary; inspect the target before retrying.",
                    "excel_write_effect_unknown", null);
            }
            catch (ExcelWriteBackendException ex)
            {
                return dispatched
                    ? Unknown("Excel write final state is unknown. " + ex.Message,
                        "excel_write_effect_unknown", null, ex.DetailsJson)
                    : Failure(ex.Message, ex.ErrorCode, ex.Retryable, ex.DetailsJson);
            }
            catch (Exception ex)
            {
                return dispatched
                    ? Unknown("Excel write final state is unknown. " + ex.Message,
                        "excel_write_effect_unknown", null)
                    : Failure("Excel write failed before dispatch: " + ex.Message, "excel_write_failed", true);
            }
        }

        private static ExcelWriteValidation ValidateAndNormalize(ExcelWriteRequest request)
        {
            if (request == null)
                return Invalid("Excel write request is empty.", "excel_write_request_missing");
            var kind = (request.Kind ?? string.Empty).Trim().ToLowerInvariant();
            if (kind != "value" && kind != "formula" && kind != "table")
                return Invalid("kind must be value, formula, or table.", "excel_write_kind_invalid");

            var normalized = new ExcelWriteRequest
            {
                Kind = kind,
                Sheet = request.Sheet ?? string.Empty,
                Address = string.IsNullOrWhiteSpace(request.Address) ? "A1" : request.Address.Trim(),
                HasValue = request.HasValue,
                Value = request.Value,
                Formula = request.Formula
            };
            if (kind == "value")
            {
                if (!request.HasValue || !IsCellValue(request.Value))
                    return Invalid("value is required and must be a scalar when kind is value.", "excel_write_value_invalid");
            }
            else if (kind == "formula")
            {
                if (string.IsNullOrWhiteSpace(request.Formula))
                    return Invalid("formula is required when kind is formula.", "excel_write_formula_invalid");
            }
            else
            {
                if (request.Values == null || request.Values.Count == 0)
                    return Invalid("values must be a non-empty 2D table when kind is table.", "excel_write_table_invalid");
                if (request.Values.Count > MaxWriteRows)
                    return Invalid("Excel table row count exceeds the write bound.", "excel_write_too_large");
                var columns = 0;
                foreach (var row in request.Values)
                {
                    if (row == null || row.Count == 0 || row.Count > MaxWriteColumns || row.Any(cell => !IsCellValue(cell)))
                        return Invalid("Each Excel table row must contain only scalar cells within the write bound.", "excel_write_table_invalid");
                    columns = Math.Max(columns, row.Count);
                }
                var cellCount = (long)request.Values.Count * columns;
                if (cellCount > MaxWriteCells)
                    return Invalid("Excel table is too large: " + cellCount + " cells. Limit is " + MaxWriteCells + ".",
                        "excel_write_too_large");
                var rows = new List<IReadOnlyList<object>>(request.Values.Count);
                foreach (var source in request.Values)
                {
                    var row = new List<object>(columns);
                    for (var column = 0; column < columns; column++)
                        row.Add(column < source.Count ? source[column] : null);
                    rows.Add(row);
                }
                normalized.Values = rows;
            }
            return new ExcelWriteValidation { Request = normalized };
        }

        private static ExcelWriteReadRequest ReadRequest(ExcelWriteRequest request)
        {
            return new ExcelWriteReadRequest
            {
                Kind = request.Kind,
                Sheet = request.Sheet,
                Address = request.Address,
                Rows = request.Kind == "table" ? request.Values.Count : 0,
                Columns = request.Kind == "table" ? request.Values[0].Count : 0,
                MaxCells = MaxWriteCells
            };
        }

        private static ExcelWriteApplyRequest ApplyRequest(ExcelWriteRequest request, ExcelWriteSnapshot target)
        {
            return new ExcelWriteApplyRequest
            {
                Kind = request.Kind,
                Sheet = target.Sheet,
                Address = target.Address,
                Rows = target.Rows,
                Columns = target.Columns,
                MaxCells = MaxWriteCells,
                Value = request.Value,
                Formula = request.Formula,
                Values = request.Values
            };
        }

        private static List<List<object>> IntendedState(ExcelWriteRequest request, int rows, int columns)
        {
            var result = new List<List<object>>(rows);
            for (var row = 0; row < rows; row++)
            {
                var line = new List<object>(columns);
                for (var column = 0; column < columns; column++)
                {
                    line.Add(request.Kind == "table" ? request.Values[row][column]
                        : request.Kind == "formula" ? (object)request.Formula : request.Value);
                }
                result.Add(line);
            }
            return result;
        }

        private static bool Matches(ExcelWriteSnapshot snapshot, IReadOnlyList<List<object>> intended, string kind)
        {
            var actual = kind == "formula" ? snapshot.Formulas : snapshot.Values;
            for (var row = 0; row < snapshot.Rows; row++)
            {
                for (var column = 0; column < snapshot.Columns; column++)
                {
                    var requiresFormula = kind == "formula";
                    if (snapshot.HasFormulas[row][column] != requiresFormula ||
                        !CellEquals(actual[row][column], intended[row][column])) return false;
                }
            }
            return true;
        }

        private static bool CellEquals(object actual, object intended)
        {
            actual = PlainValue(actual);
            intended = PlainValue(intended);
            if (IsBlank(actual) && IsBlank(intended)) return true;
            if (actual == null || intended == null) return false;
            if (IsNumeric(actual) && IsNumeric(intended))
            {
                decimal left;
                decimal right;
                if (decimal.TryParse(Convert.ToString(actual, CultureInfo.InvariantCulture), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out left) &&
                    decimal.TryParse(Convert.ToString(intended, CultureInfo.InvariantCulture), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out right)) return left == right;
            }
            return actual.Equals(intended);
        }

        private static object PlainValue(object value)
        {
            var token = value as JValue;
            return token == null ? value : token.Type == JTokenType.Null ? null : token.Value;
        }

        private static bool IsBlank(object value)
        {
            return value == null || value == DBNull.Value || value is string && ((string)value).Length == 0;
        }

        private static bool IsNumeric(object value)
        {
            return value is byte || value is sbyte || value is short || value is ushort ||
                value is int || value is uint || value is long || value is ulong ||
                value is float || value is double || value is decimal;
        }

        private static bool IsCellValue(object value)
        {
            value = PlainValue(value);
            if (value is double && (double.IsNaN((double)value) || double.IsInfinity((double)value))) return false;
            if (value is float && (float.IsNaN((float)value) || float.IsInfinity((float)value))) return false;
            return value == null || value is string || value is bool || IsNumeric(value);
        }

        private static void ValidateSnapshot(ExcelWriteSnapshot snapshot, string kind, ExcelWriteSnapshot expectedTarget)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.Sheet) || string.IsNullOrWhiteSpace(snapshot.Address))
                throw InvalidBackend("Excel write backend returned incomplete target coordinates.");
            if (!string.Equals(snapshot.Kind, kind, StringComparison.Ordinal) || snapshot.Rows < 1 || snapshot.Columns < 1 ||
                snapshot.CellCount != (long)snapshot.Rows * snapshot.Columns || snapshot.CellCount > MaxWriteCells)
                throw InvalidBackend("Excel write backend returned invalid target dimensions.");
            if (!MatrixMatches(snapshot.Values, snapshot.Rows, snapshot.Columns) ||
                !MatrixMatches(snapshot.Formulas, snapshot.Rows, snapshot.Columns) ||
                !MatrixMatches(snapshot.HasFormulas, snapshot.Rows, snapshot.Columns))
                throw InvalidBackend("Excel write backend returned an invalid state matrix.");
            if (expectedTarget != null &&
                (!string.Equals(snapshot.Sheet, expectedTarget.Sheet, StringComparison.OrdinalIgnoreCase) ||
                 !string.Equals(snapshot.Address, expectedTarget.Address, StringComparison.OrdinalIgnoreCase) ||
                 snapshot.Rows != expectedTarget.Rows || snapshot.Columns != expectedTarget.Columns))
                throw InvalidBackend("Excel write read-back resolved a different target rectangle.");
        }

        private static bool MatrixMatches<T>(IReadOnlyList<List<T>> matrix, int rows, int columns)
        {
            return matrix != null && matrix.Count == rows && matrix.All(row => row != null && row.Count == columns);
        }

        private static ExcelWriteOutcome Success(ExcelWriteSnapshot snapshot, string kind, ExcelWriteEffect effect)
        {
            var data = Coordinates(snapshot, kind);
            data["verification"] = effect == ExcelWriteEffect.VerifiedChange ? "changed" : "no_change";
            return ExcelWriteOutcome.Ok(
                effect == ExcelWriteEffect.VerifiedChange
                    ? "Excel range write verified: " + snapshot.Sheet + "!" + snapshot.Address + "."
                    : "Excel range already matched the intended state: " + snapshot.Sheet + "!" + snapshot.Address + ".",
                data.ToString(Formatting.None), effect);
        }

        private static ExcelWriteOutcome Failure(string message, string code, bool retryable, string detailsJson = null)
        {
            var data = ErrorData(code, retryable, detailsJson);
            return ExcelWriteOutcome.Error(message, data.ToString(Formatting.None), code, retryable);
        }

        private static ExcelWriteOutcome Unknown(string message, string code, ExcelWriteSnapshot snapshot,
            string detailsJson = null)
        {
            var data = ErrorData(code, false, detailsJson);
            if (snapshot != null) data["target"] = Coordinates(snapshot, snapshot.Kind);
            return ExcelWriteOutcome.Unknown(message, data.ToString(Formatting.None), code);
        }

        private static JObject Coordinates(ExcelWriteSnapshot snapshot, string kind)
        {
            return new JObject
            {
                ["kind"] = kind,
                ["sheet"] = snapshot.Sheet,
                ["address"] = snapshot.Address,
                ["rows"] = snapshot.Rows,
                ["columns"] = snapshot.Columns,
                ["cellCount"] = snapshot.CellCount
            };
        }

        private static JObject ErrorData(string code, bool retryable, string detailsJson)
        {
            var data = new JObject { ["code"] = code, ["retryable"] = retryable };
            if (!string.IsNullOrWhiteSpace(detailsJson))
            {
                try { data["details"] = JToken.Parse(detailsJson); }
                catch (JsonException) { data["details"] = detailsJson; }
            }
            return data;
        }

        private static ExcelWriteValidation Invalid(string message, string code)
        {
            return new ExcelWriteValidation { Error = Failure(message, code, false) };
        }

        private static ExcelWriteBackendException InvalidBackend(string message)
        {
            return new ExcelWriteBackendException(message, "excel_write_snapshot_invalid", false);
        }

        private sealed class ExcelWriteValidation
        {
            internal ExcelWriteRequest Request { get; set; }
            internal ExcelWriteOutcome Error { get; set; }
        }
    }
}
