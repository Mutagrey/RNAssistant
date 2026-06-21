using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ChartArtifactBuildsDefaultConfig()
        {
            var rows = new List<IList<object>>
            {
                new List<object> { "Month", "Sales", "Cost" },
                new List<object> { "Jan", 120, 80 },
                new List<object> { "Feb", 160, 90 }
            };

            var artifact = new ChartArtifactBuilder().Build(rows, Source(), "Sales", "auto");

            AssertEqual("rnassistant.chart", artifact.Type, "artifact type");
            AssertEqual("Sales", artifact.Title, "artifact title");
            AssertEqual(3, artifact.Columns.Count, "column count");
            AssertEqual("Month", artifact.Config.X, "x column");
            AssertEqual(2, artifact.Config.Series.Count, "series count");
            AssertTrue(artifact.Config.Series.Contains("Sales"), "sales series");
            AssertTrue(artifact.Config.Series.Contains("Cost"), "cost series");
            AssertEqual("column", artifact.Config.ChartType, "chart type");
            AssertEqual(2, artifact.Rows.Count, "row count");
        }

        private static void ChartArtifactHonorsRequestedTypeAndTruncates()
        {
            var rows = new List<IList<object>>();
            rows.Add(new List<object> { "Label", "Value" });
            for (var i = 0; i < ChartArtifactBuilder.MaxRows + 5; i++)
            {
                rows.Add(new List<object> { "Item " + i, i });
            }

            var artifact = new ChartArtifactBuilder().Build(rows, Source(), "Pie", "pie");

            AssertEqual("pie", artifact.Config.ChartType, "requested chart type");
            AssertEqual(ChartArtifactBuilder.MaxRows, artifact.Rows.Count, "truncated rows");
            AssertTrue(artifact.Warnings.Count > 0, "truncation warning");
        }

        private static ChartArtifactSource Source()
        {
            return new ChartArtifactSource
            {
                Host = "Excel",
                Workbook = "Book.xlsx",
                Sheet = "Sheet1",
                Address = "A1:C3",
                SourceMode = "selection"
            };
        }
    }
}
