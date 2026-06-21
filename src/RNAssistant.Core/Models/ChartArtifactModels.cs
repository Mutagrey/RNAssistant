using System.Collections.Generic;

namespace RNAssistant.Core.Models
{
    public sealed class ChartArtifact
    {
        public string Type { get; set; }
        public int Version { get; set; }
        public string Title { get; set; }
        public ChartArtifactSource Source { get; set; }
        public List<ChartArtifactColumn> Columns { get; set; }
        public List<Dictionary<string, object>> Rows { get; set; }
        public ChartArtifactConfig Config { get; set; }
        public List<string> Warnings { get; set; }

        public ChartArtifact()
        {
            Type = "rnassistant.chart";
            Version = 1;
            Columns = new List<ChartArtifactColumn>();
            Rows = new List<Dictionary<string, object>>();
            Config = new ChartArtifactConfig();
            Warnings = new List<string>();
        }
    }

    public sealed class ChartArtifactSource
    {
        public string Host { get; set; }
        public string Workbook { get; set; }
        public string Sheet { get; set; }
        public string Address { get; set; }
        public string SourceMode { get; set; }
    }

    public sealed class ChartArtifactColumn
    {
        public string Name { get; set; }
        public int Index { get; set; }
        public string Kind { get; set; }
    }

    public sealed class ChartArtifactConfig
    {
        public string ChartType { get; set; }
        public string X { get; set; }
        public List<string> Series { get; set; }
        public List<string> Colors { get; set; }

        public ChartArtifactConfig()
        {
            Series = new List<string>();
            Colors = new List<string>();
        }
    }
}
