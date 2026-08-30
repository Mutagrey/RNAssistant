using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    internal sealed class ResourceListToolHandler : ResourceToolHandlerBase
    {
        internal static readonly ToolDescriptor Descriptor = new ToolDescriptor(
            ResourceToolCatalog.ListToolId,
            "Read-only: Discover providers or list bounded resource metadata from one provider. If multiple providers exist, omit provider once to receive their ids, then select one. Bodies are never returned. Continue only with nextCursor from the same result and the identical provider/kind query.",
            Parameters());
        internal static readonly ToolPolicy Policy = new ToolPolicy(ToolEffect.Read, ToolVerification.None,
            false, true, new[] { "agent", "plan", "chat" });
        internal static readonly ToolBinding Binding = new ToolBinding("resources.list.v1");
        internal ResourceListToolHandler(ResourceGatewayService gateway, RNAssistant.Core.Models.ChatSession session)
            : base(gateway, session)
        {
        }

        protected override ToolHandlerResult Execute(ToolHandlerContext context)
        {
            var data = Gateway.List(Session,
                ToolArgumentReader.String(context.Arguments, "provider", string.Empty),
                ToolArgumentReader.String(context.Arguments, "kind", string.Empty),
                ToolArgumentReader.String(context.Arguments, "cursor", string.Empty),
                ToolArgumentReader.Int32(context.Arguments, "limit", 20));
            return Completed(RuntimeResult.Ok("Resources listed.", Serialize(data)));
        }

        private static string Parameters()
        {
            return "{\"type\":\"object\",\"properties\":{" +
                "\"provider\":{\"type\":\"string\",\"description\":\"Optional exact provider id; omit when only one provider is available.\",\"maxLength\":64}," +
                "\"kind\":{\"type\":\"string\",\"description\":\"Optional exact resource kind filter.\",\"maxLength\":64}," +
                "\"cursor\":{\"type\":\"string\",\"description\":\"Optional continuation: copy nextCursor only from the immediately preceding resources_list result with the identical provider and kind. Omit it for the first page, after changing any filter, or when nextCursor is absent. Never use a resources_read cursor.\",\"maxLength\":256}," +
                "\"limit\":{\"type\":\"integer\",\"description\":\"Maximum metadata rows.\",\"minimum\":1,\"maximum\":50,\"default\":20}" +
                "},\"required\":[],\"additionalProperties\":false}";
        }
    }
}
