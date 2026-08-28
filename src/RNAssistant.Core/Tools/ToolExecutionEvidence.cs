using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace RNAssistant.Core.Tools
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum ToolDispatchEvidence { NotDispatched, MayHaveDispatched }
    [JsonConverter(typeof(StringEnumConverter))]
    public enum ToolEffectEvidence { Unreported, None, VerifiedNoChange, VerifiedChange, Unknown }

    // Compact facts, without model payloads, domain hashes or a second journal.
    public sealed class ToolExecutionEvidence
    {
        [JsonProperty(Required = Required.Always)]
        public ToolDispatchEvidence Dispatch { get; private set; }
        [JsonProperty(Required = Required.Always)]
        public ToolEffectEvidence Effect { get; private set; }

        [JsonConstructor]
        public ToolExecutionEvidence(ToolDispatchEvidence dispatch, ToolEffectEvidence effect)
        {
            if (!Enum.IsDefined(typeof(ToolDispatchEvidence), dispatch)) throw new ArgumentOutOfRangeException(nameof(dispatch));
            if (!Enum.IsDefined(typeof(ToolEffectEvidence), effect)) throw new ArgumentOutOfRangeException(nameof(effect));
            if (dispatch == ToolDispatchEvidence.NotDispatched &&
                (effect == ToolEffectEvidence.VerifiedChange || effect == ToolEffectEvidence.Unknown))
                throw new ArgumentException("Changed or unknown effects cannot certify no dispatch.");
            Dispatch = dispatch;
            Effect = effect;
        }
    }
}
