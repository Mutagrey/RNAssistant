using System;

namespace RNAssistant.Office.Domains.Excel
{
    public sealed class ExcelClearRangeRequest
    {
        public string Sheet { get; set; }
        public string Address { get; set; }
        public string ClearWhat { get; set; }
    }

    public sealed class ExcelSortRangeRequest
    {
        public string Sheet { get; set; }
        public string Address { get; set; }
        public int KeyColumn { get; set; }
        public bool Descending { get; set; }
        public bool HasHeaders { get; set; }
    }

    public sealed class ExcelFilterRangeRequest
    {
        public string Sheet { get; set; }
        public string Address { get; set; }
        public int Field { get; set; }
        public string Criteria { get; set; }
    }

    public sealed class ExcelFormatRangeRequest
    {
        public string Sheet { get; set; }
        public string Address { get; set; }
        public bool HasNumberFormat { get; set; }
        public string NumberFormat { get; set; }
        public bool HasBold { get; set; }
        public bool Bold { get; set; }
        public bool HasItalic { get; set; }
        public bool Italic { get; set; }
        public bool HasFillColor { get; set; }
        public string FillColor { get; set; }
        public bool HasFontColor { get; set; }
        public string FontColor { get; set; }
        public bool HasHorizontalAlignment { get; set; }
        public string HorizontalAlignment { get; set; }
        public string AutoFit { get; set; }
    }

    public enum ExcelRangeMutationKind
    {
        Clear,
        Sort,
        Filter,
        Format
    }

    public sealed class ExcelRangeMutationSpec
    {
        public ExcelRangeMutationKind Kind { get; set; }
        public string Sheet { get; set; }
        public string Address { get; set; }
        public string ClearWhat { get; set; }
        public int KeyColumn { get; set; }
        public bool Descending { get; set; }
        public bool HasHeaders { get; set; }
        public int Field { get; set; }
        public string Criteria { get; set; }
        public bool HasNumberFormat { get; set; }
        public string NumberFormat { get; set; }
        public bool HasBold { get; set; }
        public bool Bold { get; set; }
        public bool HasItalic { get; set; }
        public bool Italic { get; set; }
        public bool HasFillColor { get; set; }
        public string FillColor { get; set; }
        public bool HasFontColor { get; set; }
        public string FontColor { get; set; }
        public bool HasHorizontalAlignment { get; set; }
        public string HorizontalAlignment { get; set; }
        public string AutoFit { get; set; }
    }

    public sealed class ExcelRangeMutationReadRequest
    {
        public ExcelRangeMutationSpec Spec { get; set; }
        public string Sheet { get; set; }
        public string Address { get; set; }
        public int ExpectedRows { get; set; }
        public int ExpectedColumns { get; set; }
        public int MaxCells { get; set; }
    }

    public sealed class ExcelRangeMutationApplyRequest
    {
        public ExcelRangeMutationSpec Spec { get; set; }
        public string Sheet { get; set; }
        public string Address { get; set; }
        public int Rows { get; set; }
        public int Columns { get; set; }
        public int MaxCells { get; set; }
        public string ExpectedStateToken { get; set; }
    }

    public sealed class ExcelRangeMutationSnapshot
    {
        public ExcelRangeMutationKind Kind { get; set; }
        public string Sheet { get; set; }
        public string Address { get; set; }
        public int Rows { get; set; }
        public int Columns { get; set; }
        public long CellCount { get; set; }
        public string StateToken { get; set; }
        public bool Satisfied { get; set; }
    }

    public interface IExcelRangeMutationBackend
    {
        ExcelRangeMutationSnapshot Read(ExcelRangeMutationReadRequest request);
        void Apply(ExcelRangeMutationApplyRequest request, Action markDispatchPossible);
    }

    public sealed class ExcelRangeMutationBackendException : InvalidOperationException
    {
        public string ErrorCode { get; private set; }
        public bool Retryable { get; private set; }
        public string DetailsJson { get; private set; }

        public ExcelRangeMutationBackendException(
            string message, string errorCode, bool retryable,
            string detailsJson = null)
            : base(message)
        {
            ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                ? "excel_range_mutation_failed" : errorCode;
            Retryable = retryable;
            DetailsJson = detailsJson;
        }
    }

    public enum ExcelRangeMutationOutcomeStatus { Ok, Error, Unknown }
    public enum ExcelRangeMutationEffect
    {
        None,
        VerifiedNoChange,
        VerifiedChange,
        Unknown
    }

    public sealed class ExcelRangeMutationOutcome
    {
        public ExcelRangeMutationOutcomeStatus Status { get; private set; }
        public ExcelRangeMutationEffect Effect { get; private set; }
        public string Message { get; private set; }
        public string DataJson { get; private set; }
        public string ErrorCode { get; private set; }
        public bool Retryable { get; private set; }

        public static ExcelRangeMutationOutcome Ok(
            string message, string dataJson, ExcelRangeMutationEffect effect)
        {
            if (effect != ExcelRangeMutationEffect.VerifiedNoChange &&
                effect != ExcelRangeMutationEffect.VerifiedChange)
                throw new ArgumentException(
                    "A verified Excel range effect is required.", nameof(effect));
            return new ExcelRangeMutationOutcome
            {
                Status = ExcelRangeMutationOutcomeStatus.Ok,
                Effect = effect,
                Message = message ?? string.Empty,
                DataJson = dataJson
            };
        }

        public static ExcelRangeMutationOutcome Error(
            string message, string dataJson, string errorCode, bool retryable)
        {
            return new ExcelRangeMutationOutcome
            {
                Status = ExcelRangeMutationOutcomeStatus.Error,
                Effect = ExcelRangeMutationEffect.None,
                Message = message ?? string.Empty,
                DataJson = dataJson,
                ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                    ? "excel_range_mutation_failed" : errorCode,
                Retryable = retryable
            };
        }

        public static ExcelRangeMutationOutcome Unknown(
            string message, string dataJson, string errorCode)
        {
            return new ExcelRangeMutationOutcome
            {
                Status = ExcelRangeMutationOutcomeStatus.Unknown,
                Effect = ExcelRangeMutationEffect.Unknown,
                Message = message ?? string.Empty,
                DataJson = dataJson,
                ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                    ? "excel_range_mutation_effect_unknown" : errorCode,
                Retryable = false
            };
        }
    }
}
