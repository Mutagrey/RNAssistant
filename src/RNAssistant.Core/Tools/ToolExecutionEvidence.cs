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
        // A normal handler return is a dispatch fact, never proof of its effects.
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool InvocationCompleted { get; private set; }

        [JsonConstructor]
        public ToolExecutionEvidence(ToolDispatchEvidence dispatch, ToolEffectEvidence effect, bool invocationCompleted = false)
        {
            if (!Enum.IsDefined(typeof(ToolDispatchEvidence), dispatch)) throw new ArgumentOutOfRangeException(nameof(dispatch));
            if (!Enum.IsDefined(typeof(ToolEffectEvidence), effect)) throw new ArgumentOutOfRangeException(nameof(effect));
            if (dispatch == ToolDispatchEvidence.NotDispatched &&
                (effect == ToolEffectEvidence.VerifiedChange || effect == ToolEffectEvidence.Unknown))
                throw new ArgumentException("Changed or unknown effects cannot certify no dispatch.");
            if (invocationCompleted && dispatch != ToolDispatchEvidence.MayHaveDispatched)
                throw new ArgumentException("A completed invocation requires dispatch evidence.");
            Dispatch = dispatch;
            Effect = effect;
            InvocationCompleted = invocationCompleted;
        }
    }
}
