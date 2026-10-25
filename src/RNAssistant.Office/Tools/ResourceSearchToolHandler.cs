using System.Threading;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    internal sealed class ResourceSearchToolHandler : ResourceToolHandlerBase
    {
        private readonly RNAssistant.Core.Services.ResourceTextSearch _search = new RNAssistant.Core.Services.ResourceTextSearch();
        internal static readonly ToolDescriptor Descriptor = new ToolDescriptor(
            ResourceToolCatalog.SearchToolId, RNAssistant.Core.Services.ResourceModelSearchSchema.Description,
            RNAssistant.Core.Services.ResourceModelSearchSchema.Create(false, true));
        internal static readonly ToolPolicy Policy = new ToolPolicy(ToolEffect.Read, ToolVerification.None,
            false, false, new[] { "agent", "plan", "chat" });
        internal static readonly ToolBinding Binding = new ToolBinding("resources.search.v3");

        internal ResourceSearchToolHandler(ResourceGatewayService gateway, RNAssistant.Core.Models.ChatSession session)
            : base(gateway, session) { }

        protected override ToolHandlerResult Execute(ToolHandlerContext context, CancellationToken cancellationToken)
        {
            var page = Gateway.SearchText(Session,
                RNAssistant.Core.Services.ResourceModelSearchSchema.Parse(context.Arguments), _search, cancellationToken);
            return Completed(RuntimeResult.Ok(page.ScanComplete && page.SourceComplete
                ? "The selected text scope was searched completely."
                : "Search is incomplete; inspect reasons or continue. Absence is not established.",
                Serialize(page), page.ExactReferences));
        }
    }
}
