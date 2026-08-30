using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    internal sealed class ResourceResolveToolHandler : ResourceToolHandlerBase
    {
        internal static readonly ToolDescriptor Descriptor = new ToolDescriptor(
            ResourceToolCatalog.ResolveToolId,
            "Read-only: Resolve one canonical rna:// resource URI to current metadata and available representations.",
            Parameters());
        internal static readonly ToolPolicy Policy = new ToolPolicy(ToolEffect.Read, ToolVerification.None,
            false, true, new[] { "agent", "plan", "chat" });
        internal static readonly ToolBinding Binding = new ToolBinding("resources.resolve.v1");

        internal ResourceResolveToolHandler(ResourceGatewayService gateway, RNAssistant.Core.Models.ChatSession session)
            : base(gateway, session)
        {
        }

        protected override ToolHandlerResult Execute(ToolHandlerContext context)
        {
            var data = Gateway.Resolve(Session,
                ToolArgumentReader.String(context.Arguments, "uri", string.Empty));
            return Completed(RuntimeResult.Ok("Resource resolved.", Serialize(data)));
        }

        private static string Parameters()
        {
            return "{\"type\":\"object\",\"properties\":{" +
                "\"uri\":{\"type\":\"string\",\"description\":\"Exact canonical rna:// resource URI.\",\"minLength\":1,\"maxLength\":1000}" +
                "},\"required\":[\"uri\"],\"additionalProperties\":false}";
        }
    }
}
