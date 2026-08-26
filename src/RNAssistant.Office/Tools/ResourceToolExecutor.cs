using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.Tools
{
    internal sealed class ResourceToolExecutor
    {
        private static readonly JsonSerializerSettings ResultJsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore
        };

        public const string ListToolId = "common.resources_list";
        public const string ResolveToolId = "common.resources_resolve";
        public const string SearchToolId = "common.resources_search";
        public const string ReadToolId = "common.resources_read";

        private readonly ResourceGatewayService _gateway;

        public ResourceToolExecutor(ResourceGatewayService gateway)
        {
            _gateway = gateway ?? new ResourceGatewayService();
        }

        public IEnumerable<ToolDefinition> GetControllerTools()
        {
            yield return ControllerToolDefinition.Create(
                ListToolId,
                "Common",
                "Read-only: Discover providers or list bounded resource metadata from one provider. If multiple providers exist, omit provider once to receive their ids, then select one. Bodies are never returned.",
                ListSchema(),
                name: "resources_list",
                scope: "session");
            yield return ControllerToolDefinition.Create(
                ResolveToolId,
                "Common",
                "Read-only: Resolve one canonical rna:// resource URI to current metadata and available representations.",
                ResolveSchema(),
                name: "resources_resolve",
                scope: "session");
            yield return ControllerToolDefinition.Create(
                SearchToolId,
                "Common",
                "Read-only: Search resource metadata and locally available text. Returns bounded snippets and canonical URIs; it never returns raw binary media.",
                SearchSchema(),
                name: "resources_search",
                scope: "session");
            yield return ControllerToolDefinition.Create(
                ReadToolId,
                "Common",
                "Read-only: Read one exact resource representation by canonical URI. Text is bounded and pageable; media is hydrated only for the next model step and base64 is never embedded in JSON.",
                ReadSchema(),
                name: "resources_read",
                scope: "session");
        }

        public ToolResult ExecuteControllerTool(ToolCommand command, ChatSession session)
        {
            if (command == null) return ToolResult.Fail("Tool command is empty.");
            if (session == null)
            {
                return ToolResult.Fail("Resource tools require an active chat session.", null, "resource_session_required", false);
            }
            try
            {
                if (string.Equals(command.ToolId, ListToolId, StringComparison.OrdinalIgnoreCase))
                {
                    var data = _gateway.List(
                        session,
                        ToolArgumentReader.String(command.Arguments, "provider", string.Empty),
                        ToolArgumentReader.String(command.Arguments, "kind", string.Empty),
                        ToolArgumentReader.String(command.Arguments, "cursor", string.Empty),
                        ToolArgumentReader.Int32(command.Arguments, "limit", 20));
                    return ToolResult.Ok("Resources listed.", Serialize(data));
                }
                if (string.Equals(command.ToolId, ResolveToolId, StringComparison.OrdinalIgnoreCase))
                {
                    var data = _gateway.Resolve(
                        session,
                        ToolArgumentReader.String(command.Arguments, "uri", string.Empty));
                    return ToolResult.Ok("Resource resolved.", Serialize(data));
                }
                if (string.Equals(command.ToolId, SearchToolId, StringComparison.OrdinalIgnoreCase))
                {
                    var data = _gateway.Search(
                        session,
                        ToolArgumentReader.String(command.Arguments, "provider", string.Empty),
                        ToolArgumentReader.String(command.Arguments, "query", string.Empty),
                        ToolArgumentReader.String(command.Arguments, "kind", string.Empty),
                        ToolArgumentReader.Int32(command.Arguments, "limit", 10),
                        ToolArgumentReader.Int32(command.Arguments, "maxCharsPerMatch", 600));
                    return ToolResult.Ok("Resource search completed.", Serialize(data));
                }
                if (string.Equals(command.ToolId, ReadToolId, StringComparison.OrdinalIgnoreCase))
                {
                    var selection = _gateway.Read(
                        session,
                        new ResourceReadRequest
                        {
                            Reference = new ResourceRef(
                                ToolArgumentReader.String(command.Arguments, "uri", string.Empty),
                                ToolArgumentReader.String(command.Arguments, "revision", null)),
                            Representation = ToolArgumentReader.String(command.Arguments, "representation", "auto"),
                            Cursor = ToolArgumentReader.String(command.Arguments, "cursor", string.Empty),
                            MaxChars = ToolArgumentReader.Int32(command.Arguments, "maxChars", 8000)
                        });
                    var result = ToolResult.Ok("Resource representation read.", Serialize(selection.Result));
                    result.ModelAttachments = selection.ModelAttachments;
                    result.ModelResourceRefs = selection.ResourceRefs;
                    return result;
                }
            }
            catch (KeyNotFoundException ex)
            {
                return ToolResult.Fail(ex.Message, null, "resource_not_found", false);
            }
            catch (ResourceRequestException ex)
            {
                return ToolResult.Fail(ex.Message, null, ex.ErrorCode, ex.Retryable);
            }
            catch (InvalidOperationException ex)
            {
                return ToolResult.Fail(ex.Message, null, "resource_request_invalid", true);
            }
            return ToolResult.Fail("Unknown resource tool: " + command.ToolId);
        }

        private static string Serialize(object value)
        {
            return JsonConvert.SerializeObject(value, ResultJsonSettings);
        }

        private static string ListSchema()
        {
            return "{\"type\":\"object\",\"properties\":{" +
                "\"provider\":{\"type\":\"string\",\"description\":\"Optional exact provider id; omit when only one provider is available.\",\"maxLength\":64}," +
                "\"kind\":{\"type\":\"string\",\"description\":\"Optional exact resource kind filter.\",\"maxLength\":64}," +
                "\"cursor\":{\"type\":\"string\",\"description\":\"Opaque nextCursor from a previous result.\",\"maxLength\":256}," +
                "\"limit\":{\"type\":\"integer\",\"description\":\"Maximum metadata rows.\",\"minimum\":1,\"maximum\":50,\"default\":20}" +
                "},\"required\":[],\"additionalProperties\":false}";
        }

        private static string ResolveSchema()
        {
            return "{\"type\":\"object\",\"properties\":{" +
                "\"uri\":{\"type\":\"string\",\"description\":\"Exact canonical rna:// resource URI.\",\"minLength\":1,\"maxLength\":1000}" +
                "},\"required\":[\"uri\"],\"additionalProperties\":false}";
        }

        private static string SearchSchema()
        {
            return "{\"type\":\"object\",\"properties\":{" +
                "\"query\":{\"type\":\"string\",\"description\":\"Literal case-insensitive search text.\",\"minLength\":1,\"maxLength\":500}," +
                "\"provider\":{\"type\":\"string\",\"description\":\"Optional exact provider id; omit when only one provider is available.\",\"maxLength\":64}," +
                "\"kind\":{\"type\":\"string\",\"description\":\"Optional exact resource kind filter.\",\"maxLength\":64}," +
                "\"limit\":{\"type\":\"integer\",\"description\":\"Maximum matches.\",\"minimum\":1,\"maximum\":20,\"default\":10}," +
                "\"maxCharsPerMatch\":{\"type\":\"integer\",\"description\":\"Maximum snippet characters per match.\",\"minimum\":128,\"maximum\":2000,\"default\":600}" +
                "},\"required\":[\"query\"],\"additionalProperties\":false}";
        }

        private static string ReadSchema()
        {
            return "{\"type\":\"object\",\"properties\":{" +
                "\"uri\":{\"type\":\"string\",\"description\":\"Exact canonical URI from resources_list/search/resolve.\",\"minLength\":1,\"maxLength\":1000}," +
                "\"revision\":{\"type\":\"string\",\"description\":\"Optional exact revision returned with the resource reference. Mutable reads fail if it has changed.\",\"minLength\":1,\"maxLength\":128}," +
                "\"representation\":{\"type\":\"string\",\"description\":\"Representation to read; auto selects the provider's preferred bounded form.\",\"enum\":[\"auto\",\"metadata\",\"text\",\"structure\",\"source\",\"media\"],\"default\":\"auto\"}," +
                "\"cursor\":{\"type\":\"string\",\"description\":\"Copy the opaque nextCursor from the previous read unchanged. Mutable-resource cursors are revision-bound and fail on drift.\",\"maxLength\":256}," +
                "\"maxChars\":{\"type\":\"integer\",\"description\":\"Maximum text characters returned.\",\"minimum\":128,\"maximum\":32000,\"default\":8000}" +
                "},\"required\":[\"uri\"],\"additionalProperties\":false}";
        }
    }
}
