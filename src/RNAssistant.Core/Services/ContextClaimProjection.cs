using System.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    public static class ContextClaimProjection
    {
        public const string CapabilityContextNotice =
            "SKILL_CONTEXT_NOTICE: Reuse complete current skill bodies included in this request. " +
            "A historical mention or summary is not a loaded body; load a needed body only when absent or changed. " +
            "TOOL_SCHEMA_NOTICE: Callable schemas are rematerialized from durable admission, including unchanged schemas from earlier turns of this chat when they fit. " +
            "Compaction does not require another admission. The current capability catalog and TOOL_PACK_STATE are authoritative.";

        public static bool CurrentClaim(StructuredContextClaim claim, ModelAuthoritySnapshot authority)
        {
            return claim != null && claim.HasTypedProvenance() &&
                claim.Evidence.All(evidence => new EvidenceStateReducer().Reduce(evidence, authority.Resources).State == EvidenceState.Current);
        }

    }
}
