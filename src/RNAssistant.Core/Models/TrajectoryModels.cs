using System.Collections.Generic;

namespace RNAssistant.Core.Models
{
    public static class TrajectoryVisibility
    {
        public const string Current = "current";
        public const string Shadowed = "shadowed";
        public const string LogOnly = "log-only";

        public static bool IsValid(string value)
        {
            return string.IsNullOrWhiteSpace(value) ||
                string.Equals(value, Current, System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, Shadowed, System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, LogOnly, System.StringComparison.OrdinalIgnoreCase);
        }
    }

    public sealed class TrajectoryQueryRequest
    {
        public string Cursor { get; set; }
        public int PageSize { get; set; }
        public string Search { get; set; }
        public long? MinSequence { get; set; }
        public long? MaxSequence { get; set; }
        public List<string> EventTypes { get; set; }
        public string RunId { get; set; }
        public string TurnId { get; set; }
        public string StepId { get; set; }
        public string ToolCallId { get; set; }
        public string ArtifactId { get; set; }
        public string Status { get; set; }
        public string Visibility { get; set; }

        public TrajectoryQueryRequest()
        {
            PageSize = 100;
            EventTypes = new List<string>();
        }
    }

    public sealed class TrajectoryEventRecord
    {
        public SessionEvent Event { get; set; }
        public string Visibility { get; set; }
        public List<long> SourceEventSeqs { get; set; }
        public List<string> SourceEventIds { get; set; }
        public List<string> ToolCallIds { get; set; }
        public List<string> ArtifactIds { get; set; }
        public List<string> Statuses { get; set; }

        public TrajectoryEventRecord()
        {
            SourceEventSeqs = new List<long>();
            SourceEventIds = new List<string>();
            ToolCallIds = new List<string>();
            ArtifactIds = new List<string>();
            Statuses = new List<string>();
        }
    }

    public sealed class TrajectoryQueryPage
    {
        public int TotalEvents { get; set; }
        public int TotalMatches { get; set; }
        public string Cursor { get; set; }
        public string NextCursor { get; set; }
        public bool HasMore { get; set; }
        public List<TrajectoryEventRecord> Records { get; set; }

        public TrajectoryQueryPage()
        {
            Records = new List<TrajectoryEventRecord>();
        }
    }
}
