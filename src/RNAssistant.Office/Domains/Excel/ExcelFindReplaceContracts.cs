using System;
using System.Collections.Generic;

namespace RNAssistant.Office.Domains.Excel
{
    public sealed class ExcelFindRequest
    {
        public string Sheet { get; set; }
        public string Address { get; set; }
        public string Scope { get; set; }
        public string Query { get; set; }
        public string Mode { get; set; }
        public bool MatchCase { get; set; }
        public bool WholeWord { get; set; }
        public string LookIn { get; set; }
        public int MaxResults { get; set; }
        public int ContextChars { get; set; }
    }

    public sealed class ExcelReplaceRequest
    {
        public string Sheet { get; set; }
        public string Address { get; set; }
        public string Scope { get; set; }
        public string Find { get; set; }
        public string Replacement { get; set; }
        public string Mode { get; set; }
        public bool MatchCase { get; set; }
        public bool WholeWord { get; set; }
        public string LookIn { get; set; }
        public bool ReplaceAll { get; set; }
        public int MaxReplacements { get; set; }
    }

    public sealed class ExcelCellScopeRequest
    {
        public string Scope { get; set; }
        public string Sheet { get; set; }
        public string Address { get; set; }
        public int MaxCells { get; set; }
    }

    public sealed class ExcelSearchSnapshot
    {
        public string Scope { get; set; }
        public string Sheet { get; set; }
        public string Address { get; set; }
        public IReadOnlyList<ExcelCellSnapshot> Cells { get; set; }
    }

    public sealed class ExcelCellSnapshot
    {
        public string Sheet { get; set; }
        public string Address { get; set; }
        public string Value { get; set; }
        public string Formula { get; set; }
        public bool HasFormula { get; set; }
    }

    public sealed class ExcelCellReplacementRequest
    {
        public string Sheet { get; set; }
        public string Address { get; set; }
        public string ExpectedValue { get; set; }
        public string ExpectedFormula { get; set; }
        public bool ExpectedHasFormula { get; set; }
        public bool Formula { get; set; }
        public string Text { get; set; }
    }

    public sealed class ExcelReplaceApplyRequest
    {
        public IReadOnlyList<ExcelCellReplacementRequest> Replacements { get; set; }
    }

    public interface IExcelFindReplaceBackend
    {
        void ReadScope(ExcelCellScopeRequest request, Action<ExcelCellSnapshot> visit);
        void Apply(ExcelReplaceApplyRequest request, Action markDispatchPossible);
    }

    public sealed class ExcelFindReplaceBackendException : InvalidOperationException
    {
        public string ErrorCode { get; private set; }
        public bool Retryable { get; private set; }
        public string DetailsJson { get; private set; }

        public ExcelFindReplaceBackendException(
            string message, string errorCode, bool retryable, string detailsJson = null)
            : base(message)
        {
            ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                ? "excel_find_replace_failed" : errorCode;
            Retryable = retryable;
            DetailsJson = detailsJson;
        }
    }

    public sealed class ExcelFindOutcome
    {
        public bool Success { get; private set; }
        public string Message { get; private set; }
        public string DataJson { get; private set; }
        public string ErrorCode { get; private set; }
        public bool Retryable { get; private set; }

        public static ExcelFindOutcome Ok(string message, string dataJson)
        {
            return new ExcelFindOutcome
            {
                Success = true,
                Message = message ?? string.Empty,
                DataJson = dataJson
            };
        }

        public static ExcelFindOutcome Error(
            string message, string dataJson, string errorCode, bool retryable)
        {
            return new ExcelFindOutcome
            {
                Success = false,
                Message = message ?? string.Empty,
                DataJson = dataJson,
                ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                    ? "excel_find_failed" : errorCode,
                Retryable = retryable
            };
        }
    }

    public enum ExcelReplaceOutcomeStatus { Ok, Error, Unknown }
    public enum ExcelReplaceEffect { None, VerifiedNoChange, VerifiedChange, Unknown }

    public sealed class ExcelReplaceOutcome
    {
        public ExcelReplaceOutcomeStatus Status { get; private set; }
        public ExcelReplaceEffect Effect { get; private set; }
        public string Message { get; private set; }
        public string DataJson { get; private set; }
        public string ErrorCode { get; private set; }
        public bool Retryable { get; private set; }

        public static ExcelReplaceOutcome Ok(
            string message, string dataJson, ExcelReplaceEffect effect)
        {
            if (effect != ExcelReplaceEffect.VerifiedNoChange &&
                effect != ExcelReplaceEffect.VerifiedChange)
                throw new ArgumentException("A verified Excel replace effect is required.", nameof(effect));
            return new ExcelReplaceOutcome
            {
                Status = ExcelReplaceOutcomeStatus.Ok,
                Effect = effect,
                Message = message ?? string.Empty,
                DataJson = dataJson
            };
        }

        public static ExcelReplaceOutcome Error(
            string message, string dataJson, string errorCode, bool retryable)
        {
            return new ExcelReplaceOutcome
            {
                Status = ExcelReplaceOutcomeStatus.Error,
                Effect = ExcelReplaceEffect.None,
                Message = message ?? string.Empty,
                DataJson = dataJson,
                ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                    ? "excel_replace_failed" : errorCode,
                Retryable = retryable
            };
        }

        public static ExcelReplaceOutcome Unknown(
            string message, string dataJson, string errorCode)
        {
            return new ExcelReplaceOutcome
            {
                Status = ExcelReplaceOutcomeStatus.Unknown,
                Effect = ExcelReplaceEffect.Unknown,
                Message = message ?? string.Empty,
                DataJson = dataJson,
                ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                    ? "excel_replace_effect_unknown" : errorCode,
                Retryable = false
            };
        }
    }
}
