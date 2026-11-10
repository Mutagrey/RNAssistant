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
        private readonly RNAssistant.Application.Conversations.ConversationCatalogAccess _chats;
        internal static readonly ToolDescriptor Descriptor = new ToolDescriptor(
            ResourceToolCatalog.FindToolId,
            "Read-only: Find readable conversation, document, selection, HTML, VBA, or backup resources. Explicit scope=chats lists bounded overviews of saved chats in this document without starting them; it is not included in all. Returns semantic targets, supported representations, and usage constraints for core.read. Follow each candidate's usage; a query returns filtered matches, not a complete inventory. Exact Office and qualified HTML target queries are point lookups. backups contains VBA journal entries; for retained HTML files read the workspace structure and copy its qualified member target. For all VBA modules, prefer the bound project target in RUNTIME_CONTEXT; if absent, browse scope=vba without query and read the first VBA project target as structure. Provider routing, resource kinds, exact references, paging, record paths, and limits are runtime-owned.",
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
            RNAssistant.Core.Models.ChatSession session,
            RNAssistant.Application.Conversations.ConversationCatalogAccess chats = null)
            : base(gateway, session)
        {
            _chats = chats;
        }

        protected override ToolHandlerResult Execute(ToolHandlerContext context, System.Threading.CancellationToken cancellationToken)
        {
            var scope = ToolArgumentReader.String(context.Arguments, "scope", "all");
            if (scope == "chats" && _chats == null)
                throw new ResourceRequestException("Chat overview is unavailable.", "chat_catalog_unavailable", false);
            var result = scope == "chats" ? _chats.Find(ToolArgumentReader.String(context.Arguments, "query", string.Empty)) : Gateway.Find(
                Session,
                ToolArgumentReader.String(context.Arguments, "query", string.Empty),
                scope);
            return new ToolHandlerResult(RuntimeResult.Ok(
                ResourceFindProjection.Message(result),
                ResourceFindProjection.Serialize(result),
                ExactReferences(result.ResourceRefs)), ToolEffectEvidence.None, resourceEvidence: result.Items.SelectMany(item => item.Evidence ??
                    new RNAssistant.Core.Models.ResourceEvidence[0]));
        }


    }
}
