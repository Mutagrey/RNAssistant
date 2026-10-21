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

    public static class ExcelWorksheetNameRules
    {
        public static string FormatRangeReference(string sheet, string address)
        {
            // A worksheet lookup uses its literal name. Only a composed Excel
            // reference quotes the name and escapes embedded apostrophes.
            return "'" + sheet.Replace("'", "''") + "'!" + address;
        }

        public static bool IsValid(string name)
        {
            return !string.IsNullOrWhiteSpace(name) && name.Length <= 31 &&
                name.IndexOfAny(new[] { ':', '\\', '/', '?', '*', '[', ']' }) < 0 &&
                name[0] != '\'' && name[name.Length - 1] != '\'';
        }
    }
}
