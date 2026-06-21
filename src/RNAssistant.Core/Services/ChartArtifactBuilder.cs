using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    public sealed class ChartArtifactBuilder
    {
        public const int MaxRows = 1000;
        public const int MaxColumns = 30;

        public ChartArtifact Build(
            IList<IList<object>> rawRows,
            ChartArtifactSource source,
            string title,
            string requestedChartType)
        {
            var artifact = new ChartArtifact
            {
                Title = string.IsNullOrWhiteSpace(title) ? "Chart" : title.Trim(),
                Source = source ?? new ChartArtifactSource()
            };

            rawRows = rawRows ?? new List<IList<object>>();
            var rows = rawRows
                .Where(r => r != null && r.Any(v => !IsBlank(v)))
                .Take(MaxRows + 1)
                .Select(r => (IList<object>)r.Take(MaxColumns).ToList())
                .ToList();
            if (rows.Count == 0)
            {
                artifact.Warnings.Add("No non-empty rows were found.");
                return artifact;
            }
            if (rawRows.Count > MaxRows)
            {
                artifact.Warnings.Add("Data was truncated to " + MaxRows + " rows.");
            }
            if (rawRows.Any(r => r != null && r.Count > MaxColumns))
            {
                artifact.Warnings.Add("Data was truncated to " + MaxColumns + " columns.");
            }

            var hasHeader = LooksLikeHeader(rows);
            var headers = BuildHeaders(rows[0], hasHeader ? 0 : -1, ColumnCount(rows));
            var dataRows = hasHeader ? rows.Skip(1).ToList() : rows;
            for (var c = 0; c < headers.Count; c++)
            {
                artifact.Columns.Add(new ChartArtifactColumn
                {
                    Name = headers[c],
                    Index = c,
                    Kind = InferKind(dataRows, c)
                });
            }

            foreach (var rawRow in dataRows.Take(MaxRows))
            {
                var item = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                for (var c = 0; c < headers.Count; c++)
                {
                    item[headers[c]] = NormalizeValue(c < rawRow.Count ? rawRow[c] : null);
                }
                artifact.Rows.Add(item);
            }

            ApplyDefaultConfig(artifact, requestedChartType);
            return artifact;
        }

        private static int ColumnCount(IEnumerable<IList<object>> rows)
        {
            return Math.Min(MaxColumns, (rows ?? new List<IList<object>>()).Select(r => r == null ? 0 : r.Count).DefaultIfEmpty(0).Max());
        }

        private static bool LooksLikeHeader(IList<IList<object>> rows)
        {
            if (rows == null || rows.Count < 2 || rows[0] == null)
            {
                return false;
            }

            var nonBlank = rows[0].Where(v => !IsBlank(v)).ToList();
            if (nonBlank.Count == 0)
            {
                return false;
            }

            var textCount = nonBlank.Count(v => !IsNumeric(v) && !IsDate(v));
            return textCount >= Math.Max(1, nonBlank.Count / 2);
        }

        private static List<string> BuildHeaders(IList<object> row, int headerRowIndex, int columnCount)
        {
            var result = new List<string>();
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var c = 0; c < columnCount; c++)
            {
                var raw = headerRowIndex >= 0 && row != null && c < row.Count ? Convert.ToString(row[c], CultureInfo.InvariantCulture) : string.Empty;
                var name = string.IsNullOrWhiteSpace(raw) ? "Column " + (c + 1) : raw.Trim();
                int count;
                if (seen.TryGetValue(name, out count))
                {
                    count += 1;
                    seen[name] = count;
                    name = name + " " + count;
                }
                else
                {
                    seen[name] = 1;
                }
                result.Add(name);
            }
            return result;
        }

        private static string InferKind(IList<IList<object>> rows, int column)
        {
            var nonBlank = 0;
            var numeric = 0;
            var dates = 0;
            foreach (var row in rows ?? new List<IList<object>>())
            {
                var value = row != null && column < row.Count ? row[column] : null;
                if (IsBlank(value))
                {
                    continue;
                }
                nonBlank += 1;
                if (IsNumeric(value))
                {
                    numeric += 1;
                }
                if (IsDate(value))
                {
                    dates += 1;
                }
            }

            if (nonBlank == 0)
            {
                return "empty";
            }
            if (dates * 1.0 / nonBlank >= 0.8)
            {
                return "date";
            }
            if (numeric * 1.0 / nonBlank >= 0.8)
            {
                return "number";
            }
            return "category";
        }

        private static void ApplyDefaultConfig(ChartArtifact artifact, string requestedChartType)
        {
            var columns = artifact.Columns ?? new List<ChartArtifactColumn>();
            var x = columns.FirstOrDefault(c => string.Equals(c.Kind, "date", StringComparison.OrdinalIgnoreCase)) ??
                columns.FirstOrDefault(c => string.Equals(c.Kind, "category", StringComparison.OrdinalIgnoreCase)) ??
                columns.FirstOrDefault();
            var series = columns
                .Where(c => x == null || !string.Equals(c.Name, x.Name, StringComparison.OrdinalIgnoreCase))
                .Where(c => string.Equals(c.Kind, "number", StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Name)
                .ToList();
            if (series.Count == 0)
            {
                var fallback = columns.FirstOrDefault(c => x == null || !string.Equals(c.Name, x.Name, StringComparison.OrdinalIgnoreCase));
                if (fallback != null)
                {
                    series.Add(fallback.Name);
                }
            }

            artifact.Config.X = x == null ? string.Empty : x.Name;
            artifact.Config.Series = series;
            artifact.Config.ChartType = NormalizeChartType(requestedChartType, x, series);
        }

        private static string NormalizeChartType(string requested, ChartArtifactColumn x, IList<string> series)
        {
            var value = (requested ?? string.Empty).Trim().ToLowerInvariant();
            if (value == "bar" || value == "column" || value == "line" || value == "scatter" || value == "pie")
            {
                return value;
            }
            if (x != null && string.Equals(x.Kind, "date", StringComparison.OrdinalIgnoreCase))
            {
                return "line";
            }
            return "column";
        }

        private static object NormalizeValue(object value)
        {
            if (value == null)
            {
                return null;
            }
            if (value is DateTime)
            {
                return ((DateTime)value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            return value;
        }

        private static bool IsBlank(object value)
        {
            return value == null || string.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        private static bool IsNumeric(object value)
        {
            if (value == null || value is DateTime)
            {
                return false;
            }
            if (value is byte || value is short || value is int || value is long || value is float || value is double || value is decimal)
            {
                return true;
            }

            double parsed;
            return double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out parsed);
        }

        private static bool IsDate(object value)
        {
            if (value is DateTime)
            {
                return true;
            }
            DateTime parsed;
            return value != null && DateTime.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed);
        }
    }
}
