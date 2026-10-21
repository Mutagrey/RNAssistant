using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Office.Domains.Excel;

namespace RNAssistant.Office.Services
{
    internal sealed partial class ExcelResourceProvider
    {
        internal static bool NeedsRangeOverview(ResourceDescriptor descriptor)
        {
            string address;
            return descriptor.Kind == RangeKind && descriptor.Metadata.TryGetValue("address", out address) &&
                CellCount(address) > ExcelReadService.MaxReadCells;
        }

        private ResourceReadSelection ReadRangeOverview(ChatSession session, ResourceReadRequest request,
            string sheet, string range)
        {
            // Resolve the exact bound worksheet/range without reading Value2 or Formula.
            // This is a complete structure view, not evidence of worksheet cell contents.
            var snapshot = _reader.CaptureRange(sheet, range, "metadata");
            var corners = RangeCorners(range);
            var first = corners[0]; var last = corners[corners.Length - 1];
            if (!string.Equals(sheet, snapshot.Sheet, StringComparison.OrdinalIgnoreCase) ||
                NormalizeAddress(snapshot.Address) != range || snapshot.Rows != last[1] - first[1] + 1 ||
                snapshot.Columns != last[0] - first[0] + 1)
                throw new ExcelReadBackendException("Range metadata differs from the requested extent.",
                    "excel_read_snapshot_invalid", false);
            var overview = new ExcelRangeOverview {
                Sheet = snapshot.Sheet, Address = range, Rows = snapshot.Rows, Columns = snapshot.Columns,
                CellCount = snapshot.CellCount, SampleTargets = new List<ExcelRangeSampleTarget>()
            };
            // At most nine spatial windows, 3 rows x 4 columns each. These are
            // suggestions, not an automatic scan or a representative random sample.
            foreach (var rows in SampleWindows(first[1], snapshot.Rows, 3))
                foreach (var columns in SampleWindows(first[0], snapshot.Columns, 4))
                {
                    var start = CellAddress(columns[0], rows[0]);
                    var end = CellAddress(columns[0] + columns[1] - 1, rows[0] + rows[1] - 1);
                    overview.SampleTargets.Add(new ExcelRangeSampleTarget {
                        Target = "Excel range: " + ExcelWorksheetNameRules.FormatRangeReference(snapshot.Sheet,
                            start == end ? start : start + ":" + end),
                        Cells = rows[1] * columns[1]
                    });
                }
            return SelectCapture(request, Describe(session, snapshot.Sheet, range),
                JsonConvert.SerializeObject(overview), "structure");
        }

        private static IEnumerable<int[]> SampleWindows(int start, int length, int width)
        {
            if (length <= 3 * width)
            {
                for (var offset = 0; offset < length; offset += width)
                    yield return new[] { start + offset, Math.Min(width, length - offset) };
            }
            else
            {
                yield return new[] { start, width };
                yield return new[] { start + (length - width) / 2, width };
                yield return new[] { start + length - width, width };
            }
        }

        private static string CellAddress(int column, int row)
        {
            var letters = string.Empty;
            while (column > 0)
            {
                column--;
                letters = (char)('A' + column % 26) + letters;
                column /= 26;
            }
            return letters + row.ToString(CultureInfo.InvariantCulture);
        }
    }
}
