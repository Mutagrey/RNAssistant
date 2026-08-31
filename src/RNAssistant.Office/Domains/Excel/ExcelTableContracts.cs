using System;
using System.Collections.Generic;

namespace RNAssistant.Office.Domains.Excel
{
    public sealed class ExcelAddTableRequest
    {
        public string Sheet { get; set; }
        public string SourceRange { get; set; }
        public string Name { get; set; }
        public bool HasHeaders { get; set; }
        public string Style { get; set; }
    }

    public sealed class ExcelTableReadRequest
    {
        public string Sheet { get; set; }
        public string SourceRange { get; set; }
        public int ExpectedRows { get; set; }
        public int ExpectedColumns { get; set; }
        public int MaxCells { get; set; }
        public int MaxTables { get; set; }
    }

    public sealed class ExcelTableApplyRequest
    {
        public string Sheet { get; set; }
        public string SourceRange { get; set; }
        public string Name { get; set; }
        public bool HasHeaders { get; set; }
        public string Style { get; set; }
        public int Rows { get; set; }
        public int Columns { get; set; }
        public int MaxCells { get; set; }
        public int MaxTables { get; set; }
        public string ExpectedStateToken { get; set; }
    }

    public sealed class ExcelTableState
    {
        public string Sheet { get; set; }
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string Range { get; set; }
        public int Rows { get; set; }
        public int Columns { get; set; }
        public bool HasHeaders { get; set; }
        public string Style { get; set; }
    }

    public sealed class ExcelTableCollectionSnapshot
    {
        public string Sheet { get; set; }
        public string SourceRange { get; set; }
        public int Rows { get; set; }
        public int Columns { get; set; }
        public long CellCount { get; set; }
        public string StateToken { get; set; }
        public IReadOnlyList<ExcelTableState> Tables { get; set; }
    }

    public interface IExcelTableBackend
    {
        ExcelTableCollectionSnapshot Read(ExcelTableReadRequest request);
        void Add(ExcelTableApplyRequest request, Action markDispatchPossible);
    }

    public sealed class ExcelTableBackendException : InvalidOperationException
    {
        public string ErrorCode { get; private set; }
        public bool Retryable { get; private set; }
        public string DetailsJson { get; private set; }

        public ExcelTableBackendException(
            string message, string errorCode, bool retryable,
            string detailsJson = null)
            : base(message)
        {
            ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                ? "excel_table_failed" : errorCode;
            Retryable = retryable;
            DetailsJson = detailsJson;
        }
    }

    public enum ExcelTableOutcomeStatus { Ok, Error, Unknown }
    public enum ExcelTableEffect { None, VerifiedChange, Unknown }

    public sealed class ExcelTableOutcome
    {
        public ExcelTableOutcomeStatus Status { get; private set; }
        public ExcelTableEffect Effect { get; private set; }
        public string Message { get; private set; }
        public string DataJson { get; private set; }
        public string ErrorCode { get; private set; }
        public bool Retryable { get; private set; }

        public static ExcelTableOutcome Ok(string message, string dataJson)
        {
            return new ExcelTableOutcome
            {
                Status = ExcelTableOutcomeStatus.Ok,
                Effect = ExcelTableEffect.VerifiedChange,
                Message = message ?? string.Empty,
                DataJson = dataJson
            };
        }

        public static ExcelTableOutcome Error(
            string message, string dataJson, string errorCode, bool retryable)
        {
            return new ExcelTableOutcome
            {
                Status = ExcelTableOutcomeStatus.Error,
                Effect = ExcelTableEffect.None,
                Message = message ?? string.Empty,
                DataJson = dataJson,
                ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                    ? "excel_table_failed" : errorCode,
                Retryable = retryable
            };
        }

        public static ExcelTableOutcome Unknown(
            string message, string dataJson, string errorCode)
        {
            return new ExcelTableOutcome
            {
                Status = ExcelTableOutcomeStatus.Unknown,
                Effect = ExcelTableEffect.Unknown,
                Message = message ?? string.Empty,
                DataJson = dataJson,
                ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                    ? "excel_table_effect_unknown" : errorCode,
                Retryable = false
            };
        }
    }
}
