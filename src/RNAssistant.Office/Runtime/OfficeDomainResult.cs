using System;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;
using RNAssistant.Office.Domains;

namespace RNAssistant.Office.Runtime
{
    internal static class OfficeDomainResult
    {
        internal static ToolHandlerResult Project(DomainOutcome outcome)
        {
            if (outcome == null) throw new InvalidOperationException("Office operation returned no outcome.");
            var status = outcome.Status == DomainOutcomeStatus.Ok ? ToolResultStatus.Ok :
                outcome.Status == DomainOutcomeStatus.Unknown ? ToolResultStatus.Unknown : ToolResultStatus.Error;
            ToolEffectEvidence effect;
            switch (outcome.Effect)
            {
                case DomainEffect.None: effect = ToolEffectEvidence.None; break;
                case DomainEffect.VerifiedNoChange: effect = ToolEffectEvidence.VerifiedNoChange; break;
                case DomainEffect.VerifiedChange: effect = ToolEffectEvidence.VerifiedChange; break;
                case DomainEffect.Unknown: effect = ToolEffectEvidence.Unknown; break;
                default: throw new InvalidOperationException("Invalid domain effect.");
            }
            return new ToolHandlerResult(new ToolResult(status, outcome.Message, outcome.DataJson), effect,
                recovery: outcome.Status == DomainOutcomeStatus.Error
                    ? OfficeToolFailure.DefiniteDomain(outcome.Retryable) : null);
        }
    }
}
