using System.Threading;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    internal sealed class ResourceOutlineToolHandler : ResourceToolHandlerBase
    {
        internal static readonly ToolDescriptor Descriptor = new ToolDescriptor(
            ResourceToolCatalog.OutlineToolId,
            "Read-only: Navigate an indexed document text/code target by 32000-character parts. Returns exact part and line ranges, Markdown headings or advisory C#/VBA/JS/TS declarations. Use common.resources_read with partNumber for source. Symbols are hints, not parser or mutation evidence; symbolsComplete=false means declarations may be missing. Exact revision and CAS coordinates stay runtime-owned.",
            "{\"type\":\"object\",\"properties\":{" +
            "\"target\":{\"type\":\"string\",\"description\":\"Semantic target from resource discovery.\",\"minLength\":1,\"maxLength\":1000}," +
            "\"fromPart\":{\"type\":\"integer\",\"description\":\"First 1-based part to list.\",\"minimum\":1,\"default\":1}," +
            "\"limit\":{\"type\":\"integer\",\"description\":\"Maximum parts to list.\",\"minimum\":1,\"maximum\":50,\"default\":20}" +
            "},\"required\":[\"target\"],\"additionalProperties\":false}");
        internal static readonly ToolPolicy Policy = new ToolPolicy(ToolEffect.Read, ToolVerification.None,
            false, true, new[] { "agent", "plan", "chat" });
        internal static readonly ToolBinding Binding = new ToolBinding("resources.outline.v1");

        internal ResourceOutlineToolHandler(ResourceGatewayService gateway, RNAssistant.Core.Models.ChatSession session)
            : base(gateway, session) { }

        protected override ToolHandlerResult Execute(ToolHandlerContext context, CancellationToken cancellationToken)
        {
            var outline = Gateway.Outline(Session,
                ToolArgumentReader.String(context.Arguments, "target", string.Empty),
                ToolArgumentReader.Int32(context.Arguments, "fromPart", 1),
                ToolArgumentReader.Int32(context.Arguments, "limit", 20));
            return Completed(RuntimeResult.Ok("Exact text part and line map returned; symbol names are advisory.",
                Serialize(outline), new[] { outline.ExactReference }));
        }
    }
}
