using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.Tools
{
    internal sealed class ArtifactToolExecutor
    {
        public const string ListToolId = "common.artifacts_list";
        public const string SearchToolId = "common.artifacts_search";
        public const string ReadToolId = "common.artifacts_read";

        private readonly ArtifactGatewayService _gateway;

        public ArtifactToolExecutor(ArtifactGatewayService gateway)
        {
            _gateway = gateway ?? new ArtifactGatewayService();
        }

        public IEnumerable<ToolDefinition> GetControllerTools()
        {
            yield return ControllerToolDefinition.Create(
                ListToolId,
                "Common",
                "Read-only: List bounded metadata for artifacts owned by the active chat. Use cursor to page; artifact bodies are not returned.",
                ListSchema(),
                name: "artifacts_list",
                scope: "session");
            yield return ControllerToolDefinition.Create(
                SearchToolId,
                "Common",
                "Read-only: Search artifact metadata and locally extracted text. Returns bounded snippets with artifact id, revision, representation, and offsets; it never returns raw binary media.",
                SearchSchema(),
                name: "artifacts_search",
                scope: "session");
            yield return ControllerToolDefinition.Create(
                ReadToolId,
                "Common",
                "Read-only: Read one exact artifact representation. text/analysis are bounded and pageable with nextCursor; media hydrates the referenced image, audio, or visual PDF only for the next model step and never embeds base64 in JSON. Use metadata to inspect availability first when uncertain.",
                ReadSchema(),
                name: "artifacts_read",
                scope: "session");
        }

        public ToolResult ExecuteControllerTool(ToolCommand command, ChatSession session)
        {
            if (command == null) return ToolResult.Fail("Tool command is empty.");
            if (session == null)
            {
                return ToolResult.Fail("Artifact tools require an active chat session.", null, "artifact_session_required", false);
            }
            try
            {
                if (string.Equals(command.ToolId, ListToolId, StringComparison.OrdinalIgnoreCase))
                {
                    var data = _gateway.List(
                        session,
                        ToolArgumentReader.String(command.Arguments, "kind", string.Empty),
                        ToolArgumentReader.String(command.Arguments, "cursor", string.Empty),
                        ToolArgumentReader.Int32(command.Arguments, "limit", 20));
                    return ToolResult.Ok("Artifacts listed.", data.ToString(Formatting.None));
                }
                if (string.Equals(command.ToolId, SearchToolId, StringComparison.OrdinalIgnoreCase))
                {
                    var data = _gateway.Search(
                        session,
                        ToolArgumentReader.String(command.Arguments, "query", string.Empty),
                        ToolArgumentReader.String(command.Arguments, "kind", string.Empty),
                        ToolArgumentReader.Int32(command.Arguments, "limit", 10),
                        ToolArgumentReader.Int32(command.Arguments, "maxCharsPerMatch", 600));
                    return ToolResult.Ok("Artifact search completed.", data.ToString(Formatting.None));
                }
                if (string.Equals(command.ToolId, ReadToolId, StringComparison.OrdinalIgnoreCase))
                {
                    var selection = _gateway.Read(
                        session,
                        ToolArgumentReader.String(command.Arguments, "artifactId", string.Empty),
                        ToolArgumentReader.String(command.Arguments, "representation", "auto"),
                        ParseCursor(ToolArgumentReader.String(command.Arguments, "cursor", string.Empty)),
                        ToolArgumentReader.Int32(command.Arguments, "maxChars", 8000));
                    var result = ToolResult.Ok("Artifact representation read.", selection.Data.ToString(Formatting.None));
                    result.ModelAttachments = selection.ModelAttachments;
                    result.ModelArtifactIds = selection.ArtifactIds;
                    return result;
                }
            }
            catch (KeyNotFoundException ex)
            {
                return ToolResult.Fail(ex.Message, null, "artifact_not_found", false);
            }
            catch (InvalidOperationException ex)
            {
                return ToolResult.Fail(ex.Message, null, "artifact_read_invalid", true);
            }
            return ToolResult.Fail("Unknown artifact tool: " + command.ToolId);
        }

        private static int ParseCursor(string cursor)
        {
            int offset;
            if (string.IsNullOrWhiteSpace(cursor)) return 0;
            if (!int.TryParse(cursor, out offset) || offset < 0)
            {
                throw new InvalidOperationException("Artifact read cursor is invalid.");
            }
            return offset;
        }

        private static string ListSchema()
        {
            return "{\"type\":\"object\",\"properties\":{" +
                "\"kind\":{\"type\":\"string\",\"description\":\"Optional exact artifact kind filter.\",\"maxLength\":64}," +
                "\"cursor\":{\"type\":\"string\",\"description\":\"Opaque nextCursor from a previous result.\",\"maxLength\":32}," +
                "\"limit\":{\"type\":\"integer\",\"description\":\"Maximum metadata rows.\",\"minimum\":1,\"maximum\":50,\"default\":20}" +
                "},\"required\":[],\"additionalProperties\":false}";
        }

        private static string SearchSchema()
        {
            return "{\"type\":\"object\",\"properties\":{" +
                "\"query\":{\"type\":\"string\",\"description\":\"Literal case-insensitive search text.\",\"minLength\":1,\"maxLength\":500}," +
                "\"kind\":{\"type\":\"string\",\"description\":\"Optional exact artifact kind filter.\",\"maxLength\":64}," +
                "\"limit\":{\"type\":\"integer\",\"description\":\"Maximum matches.\",\"minimum\":1,\"maximum\":20,\"default\":10}," +
                "\"maxCharsPerMatch\":{\"type\":\"integer\",\"description\":\"Maximum snippet characters per match.\",\"minimum\":128,\"maximum\":2000,\"default\":600}" +
                "},\"required\":[\"query\"],\"additionalProperties\":false}";
        }

        private static string ReadSchema()
        {
            return "{\"type\":\"object\",\"properties\":{" +
                "\"artifactId\":{\"type\":\"string\",\"description\":\"Exact artifact id from RUNTIME_CONTEXT or artifacts_list/search.\",\"minLength\":1,\"maxLength\":200}," +
                "\"representation\":{\"type\":\"string\",\"description\":\"Representation to read; auto prefers text, then saved analysis, then media.\",\"enum\":[\"auto\",\"metadata\",\"text\",\"analysis\",\"media\"],\"default\":\"auto\"}," +
                "\"cursor\":{\"type\":\"string\",\"description\":\"nextCursor from a previous text or analysis read.\",\"maxLength\":32}," +
                "\"maxChars\":{\"type\":\"integer\",\"description\":\"Maximum text or analysis characters returned.\",\"minimum\":128,\"maximum\":32000,\"default\":8000}" +
                "},\"required\":[\"artifactId\"],\"additionalProperties\":false}";
        }
    }
}
