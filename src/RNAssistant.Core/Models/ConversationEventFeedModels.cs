using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace RNAssistant.Core.Models
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum ConversationResyncReason { None, InvalidCursor, DifferentSession, HistoryChanged, HistoryUnavailable }

    // Bounded notifications over the canonical stream, never event bodies or a
    // second replay format. Consumers refresh state after relevant notifications.
    public sealed class ConversationEventNotice
    {
        public long Sequence { get; set; }
        public string EventId { get; set; }
        public DateTime CreatedUtc { get; set; }
        public string Type { get; set; }
        public string RunId { get; set; }
        public string TurnId { get; set; }
        public string StepId { get; set; }
        public IReadOnlyList<string> Operations { get; set; }
    }

    public sealed class ConversationEventPage
    {
        public string SessionId { get; set; }
        public string After { get; set; }
        public string HighWater { get; set; }
        public string NextCursor { get; set; }
        public bool HasMore { get; set; }
        public bool ResyncRequired { get { return Reason != ConversationResyncReason.None; } }
        public ConversationResyncReason Reason { get; set; }
        public IReadOnlyList<ConversationEventNotice> Events { get; set; } = new ConversationEventNotice[0];
    }
}
