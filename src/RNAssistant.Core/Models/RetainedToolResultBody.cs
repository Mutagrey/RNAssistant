using System;
using Newtonsoft.Json;
using RNAssistant.Core.Tools.Contracts;

namespace RNAssistant.Core.Models
{
    public enum ToolResultBodyKind { JsonData = 1, ResourceText = 2 }

    // Occurrence metadata only. The immutable bytes are ChatMessage.ResultPayload;
    // neither call identity nor timestamp is part of that body.
    public sealed class RetainedToolResultBody
    {
        public int Version { get; private set; }
        public ToolResultBodyKind Kind { get; private set; }
        public ToolResultStatus Status { get; private set; }
        public string Message { get; private set; }
        public ResourceRef ResultResource { get; private set; }
        private readonly ResourceReadProjection _read;
        public ResourceReadProjection Read { get { return _read?.CopyText(null); } }

        [JsonConstructor]
        public RetainedToolResultBody(int version, ToolResultBodyKind kind, ToolResultStatus status, string message,
            ResourceRef resultResource = null, ResourceReadProjection read = null)
        {
            if (version != 1 || !Enum.IsDefined(typeof(ToolResultBodyKind), kind) || !Enum.IsDefined(typeof(ToolResultStatus), status) ||
                (kind == ToolResultBodyKind.ResourceText) != (read != null) || read?.Text != null)
                throw new ArgumentException("Invalid retained tool result body contract.");
            Version = version; Kind = kind; Status = status; Message = message ?? string.Empty;
            ResultResource = resultResource?.Copy(); _read = read?.CopyText(null);
        }
    }
}
