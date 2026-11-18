using System;
using System.Collections.Generic;

namespace RNAssistant.Office.Domains.Excel
{
    // This optional provider does not change the ordinary range/table backend contract.
    public interface IExcelPowerQueryBackendProvider
    {
        IExcelPowerQueryBackend ExcelPowerQueryBackend { get; }
    }

    // All methods are called on the already-bound document's owner dispatcher.
    // Native identities and versions are runtime evidence, never model arguments.
    public interface IExcelPowerQueryBackend
    {
        ExcelPowerQueryCapabilities GetCapabilities();
        ExcelPowerQueryListSnapshot ListQueries(int maxQueries);
        ExcelPowerQuerySnapshot ReadQuery(string queryName);
        ExcelPowerQueryBindingsSnapshot ReadBindings(string queryName);
        ExcelPowerQueryCellErrorSnapshot ReadWorksheetErrors(
            string queryName, string sheet, string table, int maximumCells);
        void WriteSource(ExcelPowerQueryWriteRequest request, Action markDispatchPossible);
        void CreateDefinition(ExcelPowerQueryCreateRequest request, Action markDispatchPossible);
        ExcelPowerQueryRefreshResult RefreshWorksheet(
            ExcelPowerQueryRefreshRequest request, Action markDispatchPossible);
        ExcelPowerQueryLoadResult CreateWorksheetLoad(
            ExcelPowerQueryLoadRequest request, Action markDispatchPossible);
    }

    public sealed class ExcelPowerQueryCapabilities
    {
        public bool Supported { get; set; }
        public bool ReadSource { get; set; }
        public bool WriteSource { get; set; }
        public bool CreateDefinition { get; set; }
        public bool WorksheetRefresh { get; set; }
        public bool WorksheetLoad { get; set; }
        public bool NativeCancellation { get; set; }
        public bool DataModelLoad { get; set; }
        public bool EvaluatedPreview { get; set; }
        public string UnavailableReason { get; set; }
    }

    public sealed class ExcelPowerQueryMetadata
    {
        public string QueryName { get; set; }
        public string QueryIdentity { get; set; }
        public string Description { get; set; }
    }

    public sealed class ExcelPowerQueryListSnapshot
    {
        public IReadOnlyList<ExcelPowerQueryMetadata> Items { get; set; }
        public int TotalCount { get; set; }
        public bool IsTruncated { get; set; }
        public DateTime ObservedAtUtc { get; set; }
    }

    public sealed class ExcelPowerQuerySnapshot
    {
        public string QueryName { get; set; }
        public string QueryIdentity { get; set; }
        public string Formula { get; set; }
        public string Description { get; set; }
        public string SourceVersion { get; set; }
        public DateTime ObservedAtUtc { get; set; }
    }

    public sealed class ExcelPowerQueryConnectionBinding
    {
        public string ConnectionName { get; set; }
        public string ConnectionIdentity { get; set; }
        public bool InDataModel { get; set; }
    }

    public sealed class ExcelPowerQueryLoadBinding
    {
        public string Sheet { get; set; }
        public string Table { get; set; }
        public string ConnectionName { get; set; }
        public string ConnectionIdentity { get; set; }
        public string BindingIdentity { get; set; }
        public string Range { get; set; }
        public string HeaderRange { get; set; }
        public string DataRange { get; set; }
        public string Anchor { get; set; }
        public int? Rows { get; set; }
        public int? Columns { get; set; }
        public bool? Refreshing { get; set; }
    }

    public sealed class ExcelPowerQueryBindingsSnapshot
    {
        public string QueryName { get; set; }
        public string QueryIdentity { get; set; }
        public string BindingVersion { get; set; }
        public IReadOnlyList<ExcelPowerQueryConnectionBinding> Connections { get; set; }
        public IReadOnlyList<ExcelPowerQueryLoadBinding> Loads { get; set; }
        public bool IsAmbiguous { get; set; }
        public bool IsComplete { get; set; }
        public string UnavailableReason { get; set; }
        public DateTime ObservedAtUtc { get; set; }
    }

    public sealed class ExcelPowerQueryWriteRequest
    {
        public string QueryName { get; set; }
        public string ExpectedQueryIdentity { get; set; }
        public string ExpectedFormula { get; set; }
        public string Formula { get; set; }
    }

    public sealed class ExcelPowerQueryCellErrorSnapshot
    {
        public bool Complete { get; set; }
        public int? ErrorCount { get; set; }
        public DateTime ObservedAtUtc { get; set; }
        public string Message { get; set; }
    }

    public sealed class ExcelPowerQueryCreateRequest
    {
        public string QueryName { get; set; }
        public string Formula { get; set; }
        public string Description { get; set; }
    }

    public sealed class ExcelPowerQueryRefreshRequest
    {
        public string QueryName { get; set; }
        public string ExpectedQueryIdentity { get; set; }
        public string ExpectedSourceVersion { get; set; }
        public string ExpectedBindingVersion { get; set; }
        public string Sheet { get; set; }
        public string Table { get; set; }
    }

    public enum ExcelPowerQueryRefreshState
    {
        Unresolved,
        Succeeded,
        Failed,
        Cancelled
    }

    public sealed class ExcelPowerQueryRefreshResult
    {
        public ExcelPowerQueryRefreshState State { get; set; }
        public bool SourceStillCurrent { get; set; }
        public bool BindingStillCurrent { get; set; }
        public bool? NativeRunning { get; set; }
        public DateTime ObservedAtUtc { get; set; }
        public string ErrorCode { get; set; }
        public string Message { get; set; }
        public ExcelPowerQueryLoadBinding Binding { get; set; }
    }

    public sealed class ExcelPowerQueryLoadRequest
    {
        public string QueryName { get; set; }
        public string ExpectedQueryIdentity { get; set; }
        public string ExpectedSourceVersion { get; set; }
        public string ExpectedBindingVersion { get; set; }
        public string Sheet { get; set; }
        public string Address { get; set; }
        public string Table { get; set; }
        public bool CreateSheet { get; set; }
    }

    public sealed class ExcelPowerQueryLoadResult
    {
        public bool SheetCreated { get; set; }
        public bool TableCreated { get; set; }
        public string Sheet { get; set; }
        public string Table { get; set; }
        public bool BindingVerified { get; set; }
        public bool? NativeRunning { get; set; }
        public string ErrorCode { get; set; }
        public string Message { get; set; }
        public ExcelPowerQueryLoadBinding Binding { get; set; }
        public ExcelPowerQueryRefreshResult Refresh { get; set; }
    }

    public sealed class ExcelPowerQueryBackendException : InvalidOperationException
    {
        public ExcelPowerQueryBackendException(
            string message, string errorCode, bool retryable = false)
            : base(message)
        {
            ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                ? "excel_query_failed" : errorCode;
            Retryable = retryable;
        }

        public string ErrorCode { get; private set; }
        public bool Retryable { get; private set; }
    }
}
