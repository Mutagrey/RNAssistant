using System.Collections.Generic;

namespace RNAssistant.Office.Domains.Excel
{
    public enum ExcelDeletionScope { Worksheet, Table }
    public enum ExcelDeletionAxis { Rows, Columns }

    public sealed class ExcelDeleteRowsColumnsRequest
    {
        public string Sheet { get; set; }
        public string Table { get; set; }
        public ExcelDeletionScope Scope { get; set; }
        public ExcelDeletionAxis Axis { get; set; }
        public int Start { get; set; }
        public int Count { get; set; }
    }

    public sealed class ExcelDeletionWindow
    {
        public int Row { get; set; }
        public int Column { get; set; }
        public int Rows { get; set; }
        public int Columns { get; set; }
    }

    public sealed class ExcelDeletionCell
    {
        // Canonical scalar JSON for constants; formulas retain exact guard text.
        public string ValueJson { get; set; }
        public string Formula { get; set; }
    }

    public sealed class ExcelDeletionSnapshot
    {
        public string Sheet { get; set; }
        public string Table { get; set; }
        public ExcelDeletionWindow Window { get; set; }
        public ExcelDeletionWindow UsedWindow { get; set; }
        public int DataRows { get; set; }
        public int HeaderRows { get; set; }
        public int TotalRows { get; set; }
        public List<string> ColumnNames { get; set; }
        public List<List<ExcelDeletionCell>> Cells { get; set; }
    }

    public sealed class ExcelDeletionReadRequest
    {
        public ExcelDeleteRowsColumnsRequest Selection { get; set; }
        // Worksheet verification stays pinned to the original observation window.
        public ExcelDeletionWindow Window { get; set; }
    }

    public sealed class ExcelDeletionApplyRequest
    {
        public ExcelDeleteRowsColumnsRequest Selection { get; set; }
        public ExcelDeletionWindow Window { get; set; }
        public string ExpectedStateToken { get; set; }
    }

    public sealed class ExcelDeletionPreparedState
    {
        public ExcelDeleteRowsColumnsRequest Selection { get; set; }
        public ExcelDeletionWindow Window { get; set; }
        public string ExpectedStateToken { get; set; }
    }
}
