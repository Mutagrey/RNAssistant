using System;
using System.Collections.Generic;

namespace RNAssistant.Core.Models
{
    public static class AgentResponseProtocol
    {
        public const int CurrentVersion = 3;
    }

    // Existing runtime/history projection labels, never model-owned v3 fields.
    // Their lifecycle replacement belongs to Phase 3.
    public static class AgentResponseStatuses
    {
        public const string InProgress = "in_progress";
        public const string Completed = "completed";
        public const string AwaitingUser = "awaiting_user";
        public const string Blocked = "blocked";
        public const string Refused = "refused";
        public const string Planned = "planned";

        public static bool IsKnown(string value)
        {
            return string.Equals(value, InProgress, StringComparison.Ordinal) ||
                string.Equals(value, Completed, StringComparison.Ordinal) ||
                string.Equals(value, AwaitingUser, StringComparison.Ordinal) ||
                string.Equals(value, Blocked, StringComparison.Ordinal) ||
                string.Equals(value, Refused, StringComparison.Ordinal) ||
                string.Equals(value, Planned, StringComparison.Ordinal);
        }

        public static bool IsTerminal(string value)
        {
            return IsKnown(value) && !string.Equals(value, InProgress, StringComparison.Ordinal);
        }
    }

    public sealed class AgentToolCall
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public Dictionary<string, object> Arguments { get; set; }

        public AgentToolCall()
        {
            Arguments = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }
    }

}
