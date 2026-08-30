using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace RNAssistant.Office.Domains.Excel
{
    public sealed class ExcelInspectRequest
    {
        public string Kind { get; set; }
        public string Sheet { get; set; }
        public string ChartName { get; set; }
        public int MaxItems { get; set; }
        public int MaxSeries { get; set; }
    }

    public sealed class ExcelRangeReadRequest
    {
        public string Sheet { get; set; }
        public string Address { get; set; }
        public string Content { get; set; }
        public int MaxCells { get; set; }
    }

    public interface IExcelReadBackend
    {
        ExcelInspectSnapshot Inspect(ExcelInspectRequest request);
        ExcelRangeSnapshot ReadRange(ExcelRangeReadRequest request);
    }

    public sealed class ExcelReadBackendException : InvalidOperationException
    {
        public string ErrorCode { get; private set; }
        public bool Retryable { get; private set; }
        public string DetailsJson { get; private set; }

        public ExcelReadBackendException(string message, string errorCode, bool retryable, string detailsJson = null)
            : base(message)
        {
            ErrorCode = string.IsNullOrWhiteSpace(errorCode) ? "excel_read_failed" : errorCode;
            Retryable = retryable;
            DetailsJson = detailsJson;
        }
    }

    public sealed class ExcelInspectSnapshot
    {
        [JsonProperty("kind")] public string Kind { get; set; }
        [JsonProperty("workbook", NullValueHandling = NullValueHandling.Ignore)] public ExcelWorkbookSnapshot Workbook { get; set; }
        [JsonProperty("sheets", NullValueHandling = NullValueHandling.Ignore)] public List<ExcelSheetSnapshot> Sheets { get; set; }
        [JsonProperty("charts", NullValueHandling = NullValueHandling.Ignore)] public List<ExcelChartSnapshot> Charts { get; set; }
        [JsonProperty("chart", NullValueHandling = NullValueHandling.Ignore)] public ExcelChartSnapshot Chart { get; set; }
        [JsonProperty("tables", NullValueHandling = NullValueHandling.Ignore)] public List<ExcelTableSnapshot> Tables { get; set; }
        [JsonProperty("names", NullValueHandling = NullValueHandling.Ignore)] public List<ExcelNameSnapshot> Names { get; set; }
        [JsonProperty("shapes", NullValueHandling = NullValueHandling.Ignore)] public List<ExcelShapeSnapshot> Shapes { get; set; }
        [JsonProperty("returnedCount")] public int ReturnedCount { get; set; }
        [JsonProperty("truncated")] public bool Truncated { get; set; }
    }

    public sealed class ExcelWorkbookSnapshot
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("fullName")] public string FullName { get; set; }
        [JsonProperty("sheets")] public List<ExcelSheetSnapshot> Sheets { get; set; }
    }

    public sealed class ExcelSheetSnapshot
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("usedRange", NullValueHandling = NullValueHandling.Ignore)] public string UsedRange { get; set; }
    }

    public sealed class ExcelChartSnapshot
    {
        [JsonProperty("sheet")] public string Sheet { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("title", NullValueHandling = NullValueHandling.Ignore)] public string Title { get; set; }
        [JsonProperty("chartType", NullValueHandling = NullValueHandling.Ignore)] public string ChartType { get; set; }
        [JsonProperty("xAxisTitle", NullValueHandling = NullValueHandling.Ignore)] public string XAxisTitle { get; set; }
        [JsonProperty("yAxisTitle", NullValueHandling = NullValueHandling.Ignore)] public string YAxisTitle { get; set; }
        [JsonProperty("series", NullValueHandling = NullValueHandling.Ignore)] public List<ExcelChartSeriesSnapshot> Series { get; set; }
        [JsonProperty("seriesTruncated")] public bool SeriesTruncated { get; set; }
        [JsonProperty("left", NullValueHandling = NullValueHandling.Ignore)] public double? Left { get; set; }
        [JsonProperty("top", NullValueHandling = NullValueHandling.Ignore)] public double? Top { get; set; }
        [JsonProperty("width", NullValueHandling = NullValueHandling.Ignore)] public double? Width { get; set; }
        [JsonProperty("height", NullValueHandling = NullValueHandling.Ignore)] public double? Height { get; set; }
    }

    public sealed class ExcelChartSeriesSnapshot
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("formula")] public string Formula { get; set; }
    }

    public sealed class ExcelTableSnapshot
    {
        [JsonProperty("sheet")] public string Sheet { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("displayName")] public string DisplayName { get; set; }
        [JsonProperty("range")] public string Range { get; set; }
        [JsonProperty("rows")] public int Rows { get; set; }
        [JsonProperty("columns")] public int Columns { get; set; }
    }

    public sealed class ExcelNameSnapshot
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("refersTo")] public string RefersTo { get; set; }
        [JsonProperty("sheet", NullValueHandling = NullValueHandling.Ignore)] public string Sheet { get; set; }
        [JsonProperty("address", NullValueHandling = NullValueHandling.Ignore)] public string Address { get; set; }
    }

    public sealed class ExcelShapeSnapshot
    {
        [JsonProperty("sheet")] public string Sheet { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("left")] public double Left { get; set; }
        [JsonProperty("top")] public double Top { get; set; }
        [JsonProperty("width")] public double Width { get; set; }
        [JsonProperty("height")] public double Height { get; set; }
        [JsonProperty("alternativeText")] public string AlternativeText { get; set; }
    }

    public sealed class ExcelRangeSnapshot
    {
        [JsonProperty("sheet")] public string Sheet { get; set; }
        [JsonProperty("address")] public string Address { get; set; }
        [JsonProperty("rows")] public int Rows { get; set; }
        [JsonProperty("columns")] public int Columns { get; set; }
        [JsonProperty("cellCount")] public long CellCount { get; set; }
        [JsonProperty("values", NullValueHandling = NullValueHandling.Ignore)] public List<List<object>> Values { get; set; }
        [JsonProperty("formulas", NullValueHandling = NullValueHandling.Ignore)] public List<List<object>> Formulas { get; set; }
    }

    public sealed class ExcelReadOutcome
    {
        public bool Success { get; private set; }
        public string Message { get; private set; }
        public string DataJson { get; private set; }
        public string ErrorCode { get; private set; }
        public bool Retryable { get; private set; }

        public static ExcelReadOutcome Ok(string message, string dataJson)
        {
            return new ExcelReadOutcome { Success = true, Message = message ?? string.Empty, DataJson = dataJson };
        }

        public static ExcelReadOutcome Fail(string message, string dataJson, string errorCode, bool retryable)
        {
            return new ExcelReadOutcome
            {
                Success = false,
                Message = message ?? string.Empty,
                DataJson = dataJson,
                ErrorCode = string.IsNullOrWhiteSpace(errorCode) ? "excel_read_failed" : errorCode,
                Retryable = retryable
            };
        }
    }
}
