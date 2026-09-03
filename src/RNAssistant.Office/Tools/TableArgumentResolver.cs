using RNAssistant.Core.Tools;
using System;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Tools
{
    public sealed class ResolvedTableArguments
    {
        public int Rows { get; set; }
        public int Columns { get; set; }
        public JArray Values { get; set; }
    }

    public static class TableArgumentResolver
    {
        public static bool TryResolve(
            ToolInvocation command,
            int defaultRows,
            int defaultColumns,
            out ResolvedTableArguments resolved,
            out string error)
        {
            resolved = null;
            error = null;
            command = command ?? new ToolInvocation();

            JArray values = null;
            object rawValues;
            if (command.Arguments != null &&
                command.Arguments.TryGetValue("values", out rawValues) &&
                rawValues != null)
            {
                values = rawValues as JArray;
                if (values == null)
                {
                    error = "values must be a native two-dimensional JSON array.";
                    return false;
                }
            }

            var valueRows = values == null ? 0 : values.Count;
            var valueColumns = 0;
            if (values != null)
            {
                foreach (var token in values)
                {
                    var row = token as JArray;
                    if (row == null)
                    {
                        error = "values must contain only row arrays.";
                        return false;
                    }
                    valueColumns = Math.Max(valueColumns, row.Count);
                }
            }

            var hasRows = command.Arguments != null && command.Arguments.ContainsKey("rows");
            var hasColumns = command.Arguments != null && command.Arguments.ContainsKey("columns");
            var rows = hasRows
                ? ToolArgumentReader.Int32(command.Arguments, "rows", defaultRows)
                : valueRows > 0 ? valueRows : defaultRows;
            var columns = hasColumns
                ? ToolArgumentReader.Int32(command.Arguments, "columns", defaultColumns)
                : valueColumns > 0 ? valueColumns : defaultColumns;
            if (rows < 1 || columns < 1)
            {
                error = "rows and columns must be positive integers.";
                return false;
            }
            if (valueRows > rows || valueColumns > columns)
            {
                error = "Explicit rows/columns are smaller than the supplied values; omit them to infer the table size.";
                return false;
            }

            resolved = new ResolvedTableArguments
            {
                Rows = rows,
                Columns = columns,
                Values = values
            };
            return true;
        }
    }
}
