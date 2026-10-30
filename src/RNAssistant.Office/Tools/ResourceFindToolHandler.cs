using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    internal sealed class ResourceFindToolHandler : ResourceToolHandlerBase
    {
        internal static readonly ToolDescriptor Descriptor = new ToolDescriptor(
            ResourceToolCatalog.FindToolId,
            "Read-only: Find readable conversation, document, selection, HTML, VBA, or backup resources. Returns semantic targets, supported representations, and usage constraints for core.read. Follow each candidate's usage; a query returns filtered matches, not a complete inventory. Exact Office and qualified HTML target queries are point lookups. backups contains VBA journal entries; for retained HTML files read the workspace structure and copy its qualified member target. For all VBA modules, prefer the bound project target in RUNTIME_CONTEXT; if absent, browse scope=vba without query and read the first VBA project target as structure. Provider routing, resource kinds, exact references, paging, record paths, and limits are runtime-owned.",
            ResourceModelFindSchema.Office().ToString(Formatting.None));
        internal static readonly ToolPolicy Policy = new ToolPolicy(
            ToolEffect.Read,
            ToolVerification.None,
            false,
            true,
            new[] { "agent", "plan", "chat" }, evidenceEligibility: EvidenceEligibility.None);
        internal static readonly ToolBinding Binding =
            new ToolBinding("resources.find.v1");

        internal ResourceFindToolHandler(
            ResourceGatewayService gateway,
            RNAssistant.Core.Models.ChatSession session)
            : base(gateway, session)
        {
        }

        protected override ToolHandlerResult Execute(ToolHandlerContext context, System.Threading.CancellationToken cancellationToken)
        {
            var result = Gateway.Find(
                Session,
                ToolArgumentReader.String(context.Arguments, "query", string.Empty),
                ToolArgumentReader.String(context.Arguments, "scope", "all"));
            return new ToolHandlerResult(RuntimeResult.Ok(
                ResourceFindProjection.Message(result),
                ResourceFindProjection.Serialize(result),
                ExactReferences(result.ResourceRefs)), ToolEffectEvidence.None, resourceEvidence: result.Items.SelectMany(item => item.Evidence ??
                    new RNAssistant.Core.Models.ResourceEvidence[0]));
        }


    }
}
