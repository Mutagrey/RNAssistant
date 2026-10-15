using System.Threading;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    internal sealed class ResourceSearchToolHandler : ResourceToolHandlerBase
    {
        internal static readonly ToolDescriptor Descriptor = new ToolDescriptor(
            ResourceToolCatalog.SearchToolId,
            "Read-only: Search every available text/source character in one semantic target that advertises text or source (for example, a VBA module). For code across a VBA project, use common.resources_find with scope=vba and query. A VBA project target supports structure only. Returns bounded literal snippets, totalMatches, and scanComplete. For indexed artifacts, partNumber locates a matching source part for common.resources_read. A negative result proves absence only when scanComplete=true. Exact revisions and offsets remain runtime-owned.",
            "{\"type\":\"object\",\"properties\":{" +
            "\"target\":{\"type\":\"string\",\"description\":\"Semantic target with a text or source representation, such as a VBA module. For project-wide VBA search use common.resources_find with scope=vba and query.\",\"minLength\":1,\"maxLength\":1000}," +
            "\"query\":{\"type\":\"string\",\"description\":\"Literal text to find.\",\"minLength\":1,\"maxLength\":500}," +
            "\"matchCase\":{\"type\":\"boolean\",\"description\":\"Whether matching is case-sensitive.\",\"default\":false}," +
            "\"limit\":{\"type\":\"integer\",\"description\":\"Maximum returned matches.\",\"minimum\":1,\"maximum\":50,\"default\":20}" +
            "},\"required\":[\"target\",\"query\"],\"additionalProperties\":false}");
        internal static readonly ToolPolicy Policy = new ToolPolicy(ToolEffect.Read, ToolVerification.None,
            false, true, new[] { "agent", "plan", "chat" });
        internal static readonly ToolBinding Binding = new ToolBinding("resources.search.v2");

        internal ResourceSearchToolHandler(ResourceGatewayService gateway, RNAssistant.Core.Models.ChatSession session)
            : base(gateway, session) { }

        protected override ToolHandlerResult Execute(ToolHandlerContext context, CancellationToken cancellationToken)
        {
            var page = Gateway.SearchWithin(Session,
                ToolArgumentReader.String(context.Arguments, "target", string.Empty),
                ToolArgumentReader.String(context.Arguments, "query", string.Empty),
                ToolArgumentReader.Int32(context.Arguments, "limit", 20),
                ToolArgumentReader.Boolean(context.Arguments, "matchCase", false), cancellationToken);
            return Completed(RuntimeResult.Ok(page.ScanComplete
                ? "The exact text representation was searched completely."
                : "The search covered only the available text representation; absence is not established.",
                Serialize(page), new[] { page.ExactReference }));
        }
    }
}
