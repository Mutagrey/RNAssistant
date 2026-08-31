using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using System.Linq;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    internal sealed class ResourceSearchToolHandler : ResourceToolHandlerBase
    {
        internal static readonly ToolDescriptor Descriptor = new ToolDescriptor(
            ResourceToolCatalog.SearchToolId,
            "Read-only: Search resource metadata and locally available text. Returns bounded snippets and canonical URIs; match/snippet offsets are informational and are never resources_read arguments. It never returns raw binary media.",
            Parameters());
        internal static readonly ToolPolicy Policy = new ToolPolicy(ToolEffect.Read, ToolVerification.None,
            false, true, new[] { "agent", "plan", "chat" });
        internal static readonly ToolBinding Binding = new ToolBinding("resources.search.v1");

        internal ResourceSearchToolHandler(ResourceGatewayService gateway, RNAssistant.Core.Models.ChatSession session)
            : base(gateway, session)
        {
        }

        protected override ToolHandlerResult Execute(ToolHandlerContext context)
        {
            var data = Gateway.Search(
                Session,
                ToolArgumentReader.String(context.Arguments, "provider", string.Empty),
                ToolArgumentReader.String(context.Arguments, "query", string.Empty),
                ToolArgumentReader.String(context.Arguments, "kind", string.Empty),
                ToolArgumentReader.Int32(context.Arguments, "limit", 10),
                ToolArgumentReader.Int32(context.Arguments, "maxCharsPerMatch", 600));
            return Completed(RuntimeResult.Ok("Resource search completed.", Serialize(data),
                ExactReferences(data.Matches
                    .Where(match => match != null)
                    .Select(match => match.Reference))));
        }

        private static string Parameters()
        {
            return "{\"type\":\"object\",\"properties\":{" +
                "\"query\":{\"type\":\"string\",\"description\":\"Literal case-insensitive search text.\",\"minLength\":1,\"maxLength\":500}," +
                "\"provider\":{\"type\":\"string\",\"description\":\"Optional exact provider id; omit when only one provider is available.\",\"maxLength\":64}," +
                "\"kind\":{\"type\":\"string\",\"description\":\"Optional exact resource kind filter.\",\"maxLength\":64}," +
                "\"limit\":{\"type\":\"integer\",\"description\":\"Maximum matches.\",\"minimum\":1,\"maximum\":20,\"default\":10}," +
                "\"maxCharsPerMatch\":{\"type\":\"integer\",\"description\":\"Maximum snippet characters per match.\",\"minimum\":128,\"maximum\":2000,\"default\":600}" +
                "},\"required\":[\"query\"],\"additionalProperties\":false}";
        }
    }
}
