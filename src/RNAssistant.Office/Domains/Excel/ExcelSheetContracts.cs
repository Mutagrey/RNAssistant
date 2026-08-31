using System;
using System.Collections.Generic;

namespace RNAssistant.Office.Domains.Excel
{
    public sealed class ExcelAddSheetRequest
    {
        public string Name { get; set; }
    }

    public sealed class ExcelRenameSheetRequest
    {
        public string Sheet { get; set; }
        public string NewName { get; set; }
    }

    public sealed class ExcelSheetCollectionSnapshot
    {
        public string ActiveSheet { get; set; }
        public IReadOnlyList<string> SheetNames { get; set; }
    }

    public sealed class ExcelAddSheetApplyRequest
    {
        public string Name { get; set; }
        public IReadOnlyList<string> ExpectedSheetNames { get; set; }
    }

    public sealed class ExcelRenameSheetApplyRequest
    {
        public string Sheet { get; set; }
        public string NewName { get; set; }
        public IReadOnlyList<string> ExpectedSheetNames { get; set; }
    }

    public interface IExcelSheetBackend
    {
        ExcelSheetCollectionSnapshot Read();
        void Add(ExcelAddSheetApplyRequest request, Action markDispatchPossible);
        void Rename(ExcelRenameSheetApplyRequest request, Action markDispatchPossible);
    }

    public sealed class ExcelSheetBackendException : InvalidOperationException
    {
        public string ErrorCode { get; private set; }
        public bool Retryable { get; private set; }
        public string DetailsJson { get; private set; }

        public ExcelSheetBackendException(
            string message, string errorCode, bool retryable, string detailsJson = null)
            : base(message)
        {
            ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                ? "excel_sheet_failed" : errorCode;
            Retryable = retryable;
            DetailsJson = detailsJson;
        }
    }

    public enum ExcelSheetOutcomeStatus { Ok, Error, Unknown }
    public enum ExcelSheetEffect { None, VerifiedNoChange, VerifiedChange, Unknown }

    public sealed class ExcelSheetOutcome
    {
        public ExcelSheetOutcomeStatus Status { get; private set; }
        public ExcelSheetEffect Effect { get; private set; }
        public string Message { get; private set; }
        public string DataJson { get; private set; }
        public string ErrorCode { get; private set; }
        public bool Retryable { get; private set; }

        public static ExcelSheetOutcome Ok(
            string message, string dataJson, ExcelSheetEffect effect)
        {
            if (effect != ExcelSheetEffect.VerifiedNoChange &&
                effect != ExcelSheetEffect.VerifiedChange)
                throw new ArgumentException(
                    "A verified Excel sheet effect is required.", nameof(effect));
            return new ExcelSheetOutcome
            {
                Status = ExcelSheetOutcomeStatus.Ok,
                Effect = effect,
                Message = message ?? string.Empty,
                DataJson = dataJson
            };
        }

        public static ExcelSheetOutcome Error(
            string message, string dataJson, string errorCode, bool retryable)
        {
            return new ExcelSheetOutcome
            {
                Status = ExcelSheetOutcomeStatus.Error,
                Effect = ExcelSheetEffect.None,
                Message = message ?? string.Empty,
                DataJson = dataJson,
                ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                    ? "excel_sheet_failed" : errorCode,
                Retryable = retryable
            };
        }

        public static ExcelSheetOutcome Unknown(
            string message, string dataJson, string errorCode)
        {
            return new ExcelSheetOutcome
            {
                Status = ExcelSheetOutcomeStatus.Unknown,
                Effect = ExcelSheetEffect.Unknown,
                Message = message ?? string.Empty,
                DataJson = dataJson,
                ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                    ? "excel_sheet_effect_unknown" : errorCode,
                Retryable = false
            };
        }
    }

    public static class ExcelWorksheetNameRules
    {
        public static bool IsValid(string name)
        {
            return !string.IsNullOrWhiteSpace(name) && name.Length <= 31 &&
                name.IndexOfAny(new[] { ':', '\\', '/', '?', '*', '[', ']' }) < 0 &&
                name[0] != '\'' && name[name.Length - 1] != '\'';
        }
    }
}
