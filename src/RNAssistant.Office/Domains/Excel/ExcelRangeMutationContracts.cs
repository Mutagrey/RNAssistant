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

}
