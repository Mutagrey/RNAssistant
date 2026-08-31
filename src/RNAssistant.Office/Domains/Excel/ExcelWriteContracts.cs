using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace RNAssistant.Office.Domains.Excel
{
    public sealed class ExcelWriteRequest
    {
        public string Kind { get; set; }
        public string Sheet { get; set; }
        public string Address { get; set; }
        public bool HasValue { get; set; }
        public object Value { get; set; }
        public string Formula { get; set; }
        public IReadOnlyList<IReadOnlyList<object>> Values { get; set; }
    }

    public sealed class ExcelWriteReadRequest
    {
        public string Kind { get; set; }
        public string Sheet { get; set; }
        public string Address { get; set; }
        public int Rows { get; set; }
        public int Columns { get; set; }
        public int MaxCells { get; set; }
    }

    public sealed class ExcelWriteApplyRequest
    {
        public string Kind { get; set; }
        public string Sheet { get; set; }
        public string Address { get; set; }
        public int Rows { get; set; }
        public int Columns { get; set; }
        public int MaxCells { get; set; }
        public object Value { get; set; }
        public string Formula { get; set; }
        public IReadOnlyList<IReadOnlyList<object>> Values { get; set; }
    }

    public interface IExcelWriteBackend
    {
        ExcelWriteSnapshot Read(ExcelWriteReadRequest request);
        void Apply(ExcelWriteApplyRequest request, Action markDispatchPossible);
    }

    public sealed class ExcelWriteSnapshot
    {
        [JsonProperty("kind")] public string Kind { get; set; }
        [JsonProperty("sheet")] public string Sheet { get; set; }
        [JsonProperty("address")] public string Address { get; set; }
        [JsonProperty("rows")] public int Rows { get; set; }
        [JsonProperty("columns")] public int Columns { get; set; }
        [JsonProperty("cellCount")] public long CellCount { get; set; }
        [JsonProperty("values")] public List<List<object>> Values { get; set; }
        [JsonProperty("formulas")] public List<List<object>> Formulas { get; set; }
        [JsonProperty("hasFormulas")] public List<List<bool>> HasFormulas { get; set; }
    }

    public sealed class ExcelWriteBackendException : InvalidOperationException
    {
        public string ErrorCode { get; private set; }
        public bool Retryable { get; private set; }
        public string DetailsJson { get; private set; }

        public ExcelWriteBackendException(string message, string errorCode, bool retryable, string detailsJson = null)
            : base(message)
        {
            ErrorCode = string.IsNullOrWhiteSpace(errorCode) ? "excel_write_failed" : errorCode;
            Retryable = retryable;
            DetailsJson = detailsJson;
        }
    }

    public enum ExcelWriteOutcomeStatus { Ok, Error, Unknown }
    public enum ExcelWriteEffect { None, VerifiedNoChange, VerifiedChange, Unknown }

    public sealed class ExcelWriteOutcome
    {
        public ExcelWriteOutcomeStatus Status { get; private set; }
        public ExcelWriteEffect Effect { get; private set; }
        public string Message { get; private set; }
        public string DataJson { get; private set; }
        public string ErrorCode { get; private set; }
        public bool Retryable { get; private set; }

        public static ExcelWriteOutcome Ok(string message, string dataJson, ExcelWriteEffect effect)
        {
            if (effect != ExcelWriteEffect.VerifiedNoChange && effect != ExcelWriteEffect.VerifiedChange)
                throw new ArgumentException("A verified Excel write effect is required.", nameof(effect));
            return new ExcelWriteOutcome
            {
                Status = ExcelWriteOutcomeStatus.Ok,
                Effect = effect,
                Message = message ?? string.Empty,
                DataJson = dataJson
            };
        }

        public static ExcelWriteOutcome Error(string message, string dataJson, string errorCode, bool retryable)
        {
            return new ExcelWriteOutcome
            {
                Status = ExcelWriteOutcomeStatus.Error,
                Effect = ExcelWriteEffect.None,
                Message = message ?? string.Empty,
                DataJson = dataJson,
                ErrorCode = string.IsNullOrWhiteSpace(errorCode) ? "excel_write_failed" : errorCode,
                Retryable = retryable
            };
        }

        public static ExcelWriteOutcome Unknown(string message, string dataJson, string errorCode)
        {
            return new ExcelWriteOutcome
            {
                Status = ExcelWriteOutcomeStatus.Unknown,
                Effect = ExcelWriteEffect.Unknown,
                Message = message ?? string.Empty,
                DataJson = dataJson,
                ErrorCode = string.IsNullOrWhiteSpace(errorCode) ? "excel_write_effect_unknown" : errorCode,
                Retryable = false
            };
        }
    }
}
