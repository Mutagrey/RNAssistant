using System.Linq;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    internal sealed class ResourceFindToolHandler : ResourceToolHandlerBase
    {
        internal static readonly ToolDescriptor Descriptor = new ToolDescriptor(
            ResourceToolCatalog.FindToolId,
            "Read-only: Find readable conversation, document, selection, HTML, VBA, or backup resources. Returns semantic targets for common.resources_read. A query returns filtered matches, not a complete inventory. For all VBA modules, prefer the bound project target in RUNTIME_CONTEXT; if absent, browse scope=vba without query and read the first VBA project target as structure. Provider routing, resource kinds, exact references, paging, and limits are runtime-owned.",
            Parameters());
        internal static readonly ToolPolicy Policy = new ToolPolicy(
            ToolEffect.Read,
            ToolVerification.None,
            false,
            true,
            new[] { "agent", "plan", "chat" });
        internal static readonly ToolBinding Binding =
            new ToolBinding("resources.find.v1");

        internal ResourceFindToolHandler(
            ResourceGatewayService gateway,
            RNAssistant.Core.Models.ChatSession session)
            : base(gateway, session)
        {
        }

        protected override ToolHandlerResult Execute(ToolHandlerContext context)
        {
            var result = Gateway.Find(
                Session,
                ToolArgumentReader.String(context.Arguments, "query", string.Empty),
                ToolArgumentReader.String(context.Arguments, "scope", "all"));
            return new ToolHandlerResult(RuntimeResult.Ok(
                result.Empty
                    ? "No resources matched the semantic scope."
                    : result.Partial
                        ? "Resource find completed with unavailable scopes."
                        : "Resource find completed.",
                Serialize(result),
                ExactReferences(result.ResourceRefs)), ToolEffectEvidence.None, resourceEvidence: result.Items.SelectMany(item => item.Evidence ??
                    new RNAssistant.Core.Models.ResourceEvidence[0]));
        }

        private static string Parameters()
        {
            return "{\"type\":\"object\",\"properties\":{" +
                "\"query\":{\"type\":\"string\",\"description\":\"Optional literal text from the resource name, metadata, or readable body. Omit to browse the selected semantic scope.\",\"minLength\":1,\"maxLength\":500}," +
                "\"scope\":{\"type\":\"string\",\"description\":\"Optional user-level resource category. Catalog bodies are historical resources, not callable admission.\",\"enum\":[\"all\",\"conversation\",\"document\",\"selection\",\"html\",\"vba\",\"backups\",\"catalogs\"],\"default\":\"all\"}" +
                "},\"required\":[],\"additionalProperties\":false}";
        }
    }
}
