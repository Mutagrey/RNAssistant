using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Domains.Excel;

namespace RNAssistant.Office.Services
{
    internal sealed partial class ExcelResourceProvider
    {
        // Observes existing worksheet data. It never evaluates M or silently samples
        // a large table and calls that a complete validation.
        internal PowerQueryDataCheckResult ValidateQueryData(RNAssistant.Core.Models.ChatSession session,
            string name, PowerQueryDataCheckRequest request, CancellationToken token)
        {
            return _scope.Read(session, () => {
                token.ThrowIfCancellationRequested();
                var before = ReadQuerySnapshot(name);
                var bindings = QueryRead(() => RequirePowerQuery().ReadBindings(name));
                var loads = (bindings?.Loads ?? new ExcelPowerQueryLoadBinding[0]).Where(candidate => string.IsNullOrEmpty(request.Table) ||
                    string.Equals(candidate.Table, request.Table, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (bindings == null || !bindings.IsComplete || bindings.IsAmbiguous || bindings.QueryIdentity != before.QueryIdentity || loads.Length != 1)
                    throw Error("excel_query_load_ambiguous", "Validation needs exactly one complete, unambiguous worksheet load. Read bindings and specify its table.");
                var load = loads[0];
                if (load.Refreshing == true)
                    throw Error("excel_query_busy", "The selected worksheet load is refreshing. Observe completion before validating its data.");
                var result = new PowerQueryDataCheckResult {
                    Target = QueryPrefix + name, TableTarget = "Excel table: " + load.Table,
                    Sheet = load.Sheet, Address = load.DataRange, Rows = load.Rows, Columns = load.Columns,
                    ObservedAtUtc = DateTime.UtcNow, Coverage = "metadata", CapturedRows = 0
                };
                if (request.ExpectedRows.HasValue)
                    result.Checks.Add(new PowerQueryDataCheck { Name = "rowCount", Status = !load.Rows.HasValue ? "not_checked" :
                        load.Rows == request.ExpectedRows ? "passed" : "failed", FailureCount = !load.Rows.HasValue ? null : load.Rows != request.ExpectedRows ? (int?)1 : 0,
                        Detail = "Expected " + request.ExpectedRows.Value.ToString(CultureInfo.InvariantCulture) + "; observed " +
                            (load.Rows.HasValue ? load.Rows.Value.ToString(CultureInfo.InvariantCulture) : "unknown") + " data rows, excluding headers/totals." });

                List<string> headers = null;
                if (!string.IsNullOrEmpty(load.HeaderRange) && load.Columns.HasValue && load.Columns.Value <= ExcelReadService.MaxReadCells)
                {
                    var header = _reader.CaptureRange(load.Sheet, NormalizeAddress(load.HeaderRange), "values");
                    RequireQueryCapture(header, load.Sheet, load.HeaderRange, 1, load.Columns.Value);
                    headers = header.Values[0].Select(value => value == null ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture)).ToList();
                    result.ColumnNames = headers;
                }
                CheckRequiredColumns(result, headers, request.RequiredColumns);

                List<List<object>> data = null;
                if (load.Rows == 0 && string.IsNullOrEmpty(load.DataRange))
                { data = new List<List<object>>(); result.Coverage = "whole_loaded_table"; }
                else if (load.Rows.HasValue && load.Columns.HasValue && load.Rows.Value >= 0 && load.Columns.Value > 0 &&
                    (long)load.Rows.Value * load.Columns.Value <= ExcelReadService.MaxReadCells && !string.IsNullOrEmpty(load.DataRange))
                {
                    var capture = _reader.CaptureRange(load.Sheet, NormalizeAddress(load.DataRange), "analysis-values");
                    RequireQueryCapture(capture, load.Sheet, load.DataRange, load.Rows.Value, load.Columns.Value);
                    data = capture.Values;
                    result.Coverage = "whole_loaded_table";
                    result.CapturedRows = capture.Rows;
                }
                if (data != null)
                {
                    CheckNonempty(result, headers, data, request.NonEmptyColumns, token);
                    CheckUnique(result, headers, data, request.KeyColumns, token);
                }
                else
                {
                    if (request.NonEmptyColumns.Count > 0) result.Checks.Add(NotChecked("nonEmpty", "The complete loaded table exceeds the 100000-cell bound or has unknown dimensions; no rows were sampled."));
                    if (request.KeyColumns.Count > 0) result.Checks.Add(NotChecked("uniqueKey", "Uniqueness requires all rows. The complete table was not captured."));
                }
                // Ask the exact native owner for a bounded worksheet error mask.
                // Never classify an HRESULT-looking integer or '#N/A' text as proof.
                var errors = QueryRead(() => RequirePowerQuery().ReadWorksheetErrors(name, load.Sheet, load.Table, ExcelReadService.MaxReadCells));
                result.Checks.Add(errors?.Complete == true && errors.ErrorCount.HasValue
                    ? new PowerQueryDataCheck { Name = "cellErrors", Status = errors.ErrorCount == 0 ? "passed" : "failed",
                        FailureCount = errors.ErrorCount, Detail = "Native ISERROR mask checked every loaded data cell within the stated bound. Latent M errors outside the loaded result remain unexamined." }
                    : NotChecked("cellErrors", errors?.Message ?? "The exact worksheet error mask is unavailable; absence of errors is not established."));
                token.ThrowIfCancellationRequested();
                var after = ReadQuerySnapshot(name);
                var afterBindings = QueryRead(() => RequirePowerQuery().ReadBindings(name));
                var afterLoads = (afterBindings?.Loads ?? new ExcelPowerQueryLoadBinding[0]).Where(candidate =>
                    candidate.BindingIdentity == load.BindingIdentity && candidate.Sheet == load.Sheet && candidate.Table == load.Table).ToArray();
                result.BindingStable = after.QueryIdentity == before.QueryIdentity && after.Formula == before.Formula &&
                    afterBindings != null && afterBindings.IsComplete && !afterBindings.IsAmbiguous &&
                    afterBindings.QueryIdentity == bindings.QueryIdentity && afterBindings.BindingVersion == bindings.BindingVersion &&
                    afterLoads.Length == 1 && load.Refreshing == false && afterLoads[0].Refreshing == false &&
                    afterLoads[0].Rows == load.Rows && afterLoads[0].Columns == load.Columns &&
                    afterLoads[0].DataRange == load.DataRange && afterLoads[0].HeaderRange == load.HeaderRange;
                if (!result.BindingStable)
                {
                    foreach (var check in result.Checks.Where(check => check.Status == "passed"))
                    { check.Status = "not_checked"; check.Detail = "Query, load binding, data shape or native refresh state was not stable during the sequential observation."; }
                }
                result.Status = result.Checks.Any(check => check.Status == "failed") ? "failed" :
                    result.Checks.Any(check => check.Status == "not_checked") || !result.BindingStable ? "incomplete" : "passed";
                return result;
            });
        }

        private static void RequireQueryCapture(ExcelRangeSnapshot capture, string sheet, string address, int rows, int columns)
        {
            if (capture == null || !string.Equals(capture.Sheet, sheet, StringComparison.OrdinalIgnoreCase) ||
                NormalizeAddress(capture.Address) != NormalizeAddress(address) || capture.Rows != rows || capture.Columns != columns ||
                capture.Values == null || capture.Values.Count != rows || capture.Values.Any(row => row == null || row.Count != columns))
                throw Error("excel_query_data_changed", "The observed load range changed while validation was capturing data.");
        }

        private static void CheckRequiredColumns(PowerQueryDataCheckResult result, List<string> headers, IReadOnlyList<string> requested)
        {
            if (requested.Count == 0) return;
            if (headers == null) { result.Checks.Add(NotChecked("requiredColumns", "Exact header cells were unavailable.")); return; }
            var failures = requested.Where(name => headers.Count(header => header == name) != 1).ToArray();
            result.Checks.Add(new PowerQueryDataCheck { Name = "requiredColumns", Status = failures.Length == 0 ? "passed" : "failed",
                FailureCount = failures.Length, Detail = failures.Length == 0 ? "All requested exact headings exist once." :
                    "Missing or ambiguous headings: " + string.Join(", ", failures) });
        }

        private static int[] ColumnIndexes(List<string> headers, IReadOnlyList<string> requested)
        {
            if (headers == null || requested.Any(name => headers.Count(header => header == name) != 1)) return null;
            return requested.Select(name => headers.IndexOf(name)).ToArray();
        }

        private static void CheckNonempty(PowerQueryDataCheckResult result, List<string> headers, List<List<object>> data,
            IReadOnlyList<string> columns, CancellationToken token)
        {
            if (columns.Count == 0) return;
            var indexes = ColumnIndexes(headers, columns);
            if (indexes == null) { result.Checks.Add(NotChecked("nonEmpty", "Requested columns are missing, ambiguous, or headers were unavailable.")); return; }
            var failures = 0;
            foreach (var row in data)
            {
                token.ThrowIfCancellationRequested();
                foreach (var index in indexes)
                    if (row[index] == null || row[index] is string && string.IsNullOrWhiteSpace((string)row[index])) failures++;
            }
            result.Checks.Add(new PowerQueryDataCheck { Name = "nonEmpty", Status = failures == 0 ? "passed" : "failed",
                FailureCount = failures, Detail = "Checked null, empty and whitespace-only values in every captured row of the requested columns." });
        }

        private static void CheckUnique(PowerQueryDataCheckResult result, List<string> headers, List<List<object>> data,
            IReadOnlyList<string> columns, CancellationToken token)
        {
            if (columns.Count == 0) return;
            var indexes = ColumnIndexes(headers, columns);
            if (indexes == null) { result.Checks.Add(NotChecked("uniqueKey", "Requested key columns are missing, ambiguous, or headers were unavailable.")); return; }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var duplicates = 0;
            foreach (var row in data)
            {
                token.ThrowIfCancellationRequested();
                var values = indexes.Select(index => row[index]).ToArray();
                if (values.Any(value => !PlainCell(value)))
                { result.Checks.Add(NotChecked("uniqueKey", "A key cell has an unsupported/error value. No uniqueness claim is made.")); return; }
                if (!seen.Add(JsonConvert.SerializeObject(values))) duplicates++;
            }
            result.Checks.Add(new PowerQueryDataCheck { Name = "uniqueKey", Status = duplicates == 0 ? "passed" : "failed",
                FailureCount = duplicates, Detail = "Compared typed scalar composite keys across all captured rows; text comparison is ordinal and case-sensitive. Null key values are included; require nonEmpty separately." });
        }

        private static bool PlainCell(object value)
        {
            if (value == null || value is string || value is bool) return true;
            switch (Type.GetTypeCode(value.GetType()))
            {
                case TypeCode.Byte: case TypeCode.SByte: case TypeCode.Int16: case TypeCode.UInt16:
                case TypeCode.Int32: case TypeCode.UInt32: case TypeCode.Int64: case TypeCode.UInt64:
                case TypeCode.Decimal: return true;
                case TypeCode.Double: return !double.IsNaN((double)value) && !double.IsInfinity((double)value);
                case TypeCode.Single: return !float.IsNaN((float)value) && !float.IsInfinity((float)value);
                default: return false;
            }
        }

        private static PowerQueryDataCheck NotChecked(string name, string detail)
        { return new PowerQueryDataCheck { Name = name, Status = "not_checked", Detail = detail }; }
    }

    internal sealed class PowerQueryDataCheckRequest
    {
        public string Table { get; set; }
        public int? ExpectedRows { get; set; }
        public IReadOnlyList<string> RequiredColumns { get; set; } = new string[0];
        public IReadOnlyList<string> NonEmptyColumns { get; set; } = new string[0];
        public IReadOnlyList<string> KeyColumns { get; set; } = new string[0];
    }

    internal sealed class PowerQueryDataCheckResult
    {
        [JsonProperty("target")] public string Target { get; set; }
        [JsonProperty("tableTarget")] public string TableTarget { get; set; }
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("coverage")] public string Coverage { get; set; }
        [JsonProperty("sheet")] public string Sheet { get; set; }
        [JsonProperty("address")] public string Address { get; set; }
        [JsonProperty("rows")] public int? Rows { get; set; }
        [JsonProperty("columns")] public int? Columns { get; set; }
        [JsonProperty("columnNames")] public IReadOnlyList<string> ColumnNames { get; set; }
        [JsonProperty("capturedRows")] public int CapturedRows { get; set; }
        [JsonProperty("checks")] public List<PowerQueryDataCheck> Checks { get; set; } = new List<PowerQueryDataCheck>();
        [JsonProperty("bindingStable")] public bool BindingStable { get; set; }
        [JsonProperty("observedAtUtc")] public DateTime ObservedAtUtc { get; set; }
        [JsonProperty("refreshPerformed")] public bool RefreshPerformed { get { return false; } }
        [JsonProperty("dataFreshness")] public string DataFreshness { get { return "not_proven"; } }
        [JsonProperty("observation")] public string Observation { get { return "Sequential observation of already loaded worksheet data. Source/refresh validity and an atomic workbook snapshot are not implied."; } }
    }

    internal sealed class PowerQueryDataCheck
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("failureCount")] public int? FailureCount { get; set; }
        [JsonProperty("detail")] public string Detail { get; set; }
    }
}
