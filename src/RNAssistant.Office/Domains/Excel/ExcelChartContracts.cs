using System;
using System.Collections.Generic;

namespace RNAssistant.Office.Domains.Excel
{
    public sealed class ExcelChatChartRequest
    {
        public string Sheet { get; set; }
        public string Address { get; set; }
        public string ChartType { get; set; }
        public string Title { get; set; }
    }

    public sealed class ExcelChatChartSourceRequest
    {
        public string Sheet { get; set; }
        public string Address { get; set; }
        public int MaxCells { get; set; }
    }

    public sealed class ExcelChatChartSourceSnapshot
    {
        public string Workbook { get; set; }
        public string Sheet { get; set; }
        public string Address { get; set; }
        public string SourceMode { get; set; }
        public int Rows { get; set; }
        public int Columns { get; set; }
        public long CellCount { get; set; }
        public IReadOnlyList<IReadOnlyList<object>> Values { get; set; }
    }

    public sealed class ExcelChartMutationRequest
    {
        public string ToolId { get; set; }
        public string Mode { get; set; }
        public string Sheet { get; set; }
        public string ChartName { get; set; }
        public bool HasSourceRange { get; set; }
        public string SourceRange { get; set; }
        public bool HasChartType { get; set; }
        public string ChartType { get; set; }
        public bool HasTitle { get; set; }
        public string Title { get; set; }
        public bool HasCategoryLabelsRange { get; set; }
        public string CategoryLabelsRange { get; set; }
        public bool HasXAxisTitle { get; set; }
        public string XAxisTitle { get; set; }
        public bool HasYAxisTitle { get; set; }
        public string YAxisTitle { get; set; }
        public bool HasLeft { get; set; }
        public int Left { get; set; }
        public bool HasTop { get; set; }
        public int Top { get; set; }
        public bool HasWidth { get; set; }
        public int Width { get; set; }
        public bool HasHeight { get; set; }
        public int Height { get; set; }
    }

    public enum ExcelChartMutationKind { Create, Update, Delete }

    public sealed class ExcelChartMutationPlan
    {
        public ExcelChartMutationKind Kind { get; set; }
        public string Sheet { get; set; }
        public string ChartName { get; set; }
        public bool HasSourceRange { get; set; }
        public string SourceRange { get; set; }
        public bool HasChartType { get; set; }
        public string ChartType { get; set; }
        public bool HasTitle { get; set; }
        public string Title { get; set; }
        public bool ExpectedHasTitle { get; set; }
        public bool HasCategoryLabelsRange { get; set; }
        public string CategoryLabelsRange { get; set; }
        public bool HasXAxisTitle { get; set; }
        public string XAxisTitle { get; set; }
        public bool ExpectedHasXAxisTitle { get; set; }
        public bool HasYAxisTitle { get; set; }
        public string YAxisTitle { get; set; }
        public bool ExpectedHasYAxisTitle { get; set; }
        public bool HasLeft { get; set; }
        public double Left { get; set; }
        public bool HasTop { get; set; }
        public double Top { get; set; }
        public bool HasWidth { get; set; }
        public double Width { get; set; }
        public bool HasHeight { get; set; }
        public double Height { get; set; }
    }

    public sealed class ExcelChartReadRequest
    {
        public ExcelChartMutationPlan Plan { get; set; }
        public int MaxCharts { get; set; }
        public int MaxSeries { get; set; }
        public int MaxSourceCells { get; set; }
    }

    public sealed class ExcelChartApplyRequest
    {
        public ExcelChartMutationPlan Plan { get; set; }
        public int MaxCharts { get; set; }
        public int MaxSeries { get; set; }
        public int MaxSourceCells { get; set; }
        public string ExpectedStateToken { get; set; }
    }

    public sealed class ExcelChartSeriesState
    {
        public string Name { get; set; }
        public string Formula { get; set; }
    }

    public sealed class ExcelChartState
    {
        public string Sheet { get; set; }
        public string Name { get; set; }
        public bool HasTitle { get; set; }
        public string Title { get; set; }
        public string ChartType { get; set; }
        public bool HasXAxisTitle { get; set; }
        public string XAxisTitle { get; set; }
        public bool HasYAxisTitle { get; set; }
        public string YAxisTitle { get; set; }
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public IReadOnlyList<ExcelChartSeriesState> Series { get; set; }
        public bool SourceRangeSatisfied { get; set; }
        public bool CategoryLabelsRangeSatisfied { get; set; }
    }

    public sealed class ExcelChartCollectionSnapshot
    {
        public string ActiveSheet { get; set; }
        public string StateToken { get; set; }
        public IReadOnlyList<ExcelChartState> Charts { get; set; }
    }

    public interface IExcelChartBackend
    {
        ExcelChatChartSourceSnapshot ReadChatSource(
            ExcelChatChartSourceRequest request);
        ExcelChartCollectionSnapshot Read(ExcelChartReadRequest request);
        void Apply(ExcelChartApplyRequest request, Action markDispatchPossible);
    }

    public sealed class ExcelChartBackendException : InvalidOperationException
    {
        public string ErrorCode { get; private set; }
        public bool Retryable { get; private set; }
        public string DetailsJson { get; private set; }

        public ExcelChartBackendException(
            string message, string errorCode, bool retryable,
            string detailsJson = null)
            : base(message)
        {
            ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                ? "excel_chart_failed" : errorCode;
            Retryable = retryable;
            DetailsJson = detailsJson;
        }
    }

    public enum ExcelChartOutcomeStatus { Ok, Error, Unknown }
    public enum ExcelChartEffect
    {
        None,
        VerifiedNoChange,
        VerifiedChange,
        Unknown
    }

    public sealed class ExcelChartOutcome
    {
        public ExcelChartOutcomeStatus Status { get; private set; }
        public ExcelChartEffect Effect { get; private set; }
        public string Message { get; private set; }
        public string DataJson { get; private set; }
        public string ErrorCode { get; private set; }
        public bool Retryable { get; private set; }

        public static ExcelChartOutcome Ok(
            string message, string dataJson, ExcelChartEffect effect)
        {
            return new ExcelChartOutcome
            {
                Status = ExcelChartOutcomeStatus.Ok,
                Effect = effect,
                Message = message ?? string.Empty,
                DataJson = dataJson
            };
        }

        public static ExcelChartOutcome Error(
            string message, string dataJson, string errorCode, bool retryable)
        {
            return new ExcelChartOutcome
            {
                Status = ExcelChartOutcomeStatus.Error,
                Effect = ExcelChartEffect.None,
                Message = message ?? string.Empty,
                DataJson = dataJson,
                ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                    ? "excel_chart_failed" : errorCode,
                Retryable = retryable
            };
        }

        public static ExcelChartOutcome Unknown(
            string message, string dataJson, string errorCode)
        {
            return new ExcelChartOutcome
            {
                Status = ExcelChartOutcomeStatus.Unknown,
                Effect = ExcelChartEffect.Unknown,
                Message = message ?? string.Empty,
                DataJson = dataJson,
                ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                    ? "excel_chart_effect_unknown" : errorCode,
                Retryable = false
            };
        }
    }
}
