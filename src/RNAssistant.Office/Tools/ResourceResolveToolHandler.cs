using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using Newtonsoft.Json.Linq;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    internal sealed class ResourceResolveToolHandler : ResourceToolHandlerBase
    {
        internal static readonly ToolDescriptor Descriptor = new ToolDescriptor(
            ResourceToolCatalog.ResolveToolId,
            "Read-only: Resolve one canonical rna:// resource URI, or resolve one member path under an exact revision URI, to metadata and available representations.",
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
            var uri = ToolArgumentReader.String(context.Arguments, "uri", string.Empty);
            var data = !string.IsNullOrWhiteSpace(uri)
                ? Gateway.Resolve(Session, uri)
                : Gateway.ResolveMember(
                    Session,
                    ToolArgumentReader.String(context.Arguments, "parentUri", string.Empty),
                    ToolArgumentReader.String(context.Arguments, "memberPath", string.Empty),
                    ToolArgumentReader.String(context.Arguments, "memberType", string.Empty));
            return Completed(RuntimeResult.Ok("Resource resolved.", Serialize(data),
                ExactReferences(new[] { data.Resource == null ? null : data.Resource.Reference })));
        }

        private static string Parameters()
        {
            var uri = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["uri"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Exact canonical rna:// resource URI.",
                        ["minLength"] = 1,
                        ["maxLength"] = 1000
                    }
                },
                ["required"] = new JArray("uri"),
                ["additionalProperties"] = false
            };
            var member = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["parentUri"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Exact canonical artifact revision URI.",
                        ["minLength"] = 1,
                        ["maxLength"] = 1000
                    },
                    ["memberPath"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Exact human-readable member path or data-source name.",
                        ["minLength"] = 1,
                        ["maxLength"] = 260
                    },
                    ["memberType"] = new JObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JArray("file", "data"),
                        ["description"] = "Optional member type used only to disambiguate equal names."
                    }
                },
                ["required"] = new JArray("parentUri", "memberPath"),
                ["additionalProperties"] = false
            };
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["uri"] = uri.SelectToken("properties.uri").DeepClone(),
                    ["parentUri"] = member.SelectToken("properties.parentUri").DeepClone(),
                    ["memberPath"] = member.SelectToken("properties.memberPath").DeepClone(),
                    ["memberType"] = member.SelectToken("properties.memberType").DeepClone()
                },
                ["required"] = new JArray(),
                ["additionalProperties"] = false,
                ["anyOf"] = new JArray(uri, member)
            }.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
