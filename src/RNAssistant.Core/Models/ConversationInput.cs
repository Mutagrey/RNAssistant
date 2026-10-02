using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace RNAssistant.Core.Models
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum InputDelivery { Steer, Queue }
    [JsonConverter(typeof(StringEnumConverter))]
    public enum ConversationInputStatus { Pending, Delivering, Applied, Removed }

    public sealed class ConversationInput
    {
        public string Id { get; set; }
        public string OperationId { get; set; }
        public long Revision { get; set; }
        public string Text { get; set; }
        public InputDelivery Delivery { get; set; }
        public ConversationInputStatus Status { get; set; }
        public string DocumentRuntimeKey { get; set; }
        public DateTime CreatedUtc { get; set; }
        public List<ChatAttachment> Attachments { get; set; } = new List<ChatAttachment>();
    }

    // One immutable event per accepted transition; replay is the queue authority.
    public sealed class ConversationInputEvent
    {
        public ConversationInput Input { get; set; }
        public bool Paused { get; set; }
        public string PauseReason { get; set; }
    }
}
