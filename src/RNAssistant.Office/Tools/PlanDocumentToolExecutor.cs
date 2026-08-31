using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.Tools
{
    internal sealed class PlanDocumentToolExecutor
    {
        public const string CreateToolId = "common.plan_doc_create";
        public const string UpdateToolId = "common.plan_doc_update";
        public const string DeleteToolId = "common.plan_doc_delete";

        private readonly PlanDocumentService _service;

        public PlanDocumentToolExecutor()
        {
            _service = new PlanDocumentService();
        }

        public IEnumerable<ToolDefinition> GetControllerTools()
        {
            yield return ControllerToolDefinition.Create(CreateToolId, "Common",
                "Plan document: Create the single revisioned Markdown plan for this chat after discovery and alignment.",
                CreateSchema(), mutatesLocalState: true, name: "plan_doc_create", scope: "session");
            yield return ControllerToolDefinition.Create(UpdateToolId, "Common",
                "Plan document: Replace the active Markdown plan body using an exact current revision guard.",
                UpdateSchema(), mutatesLocalState: true, name: "plan_doc_update", scope: "session");
            yield return ControllerToolDefinition.Create(DeleteToolId, "Common",
                "Plan document: Delete every revision only when the user explicitly asks to remove the plan.",
                DeleteSchema(), mutatesLocalState: true, riskLevel: 1, name: "plan_doc_delete", scope: "session");
        }

        public ToolResult ExecuteControllerTool(ToolCommand command, ChatSession session, bool dryRun)
        {
            if (command == null) return ToolResult.Fail("Tool command is empty.");
            if (session == null) return ToolResult.Fail("Plan document requires an active chat.", null, "plan_session_required", false);
            try
            {
                if (string.Equals(command.ToolId, CreateToolId, StringComparison.OrdinalIgnoreCase)) return Create(command, session, dryRun);
                if (string.Equals(command.ToolId, UpdateToolId, StringComparison.OrdinalIgnoreCase)) return Update(command, session, dryRun);
                if (string.Equals(command.ToolId, DeleteToolId, StringComparison.OrdinalIgnoreCase)) return Delete(command, session, dryRun);
            }
            catch (InvalidOperationException ex)
            {
                return ToolResult.Fail(ex.Message, null, "invalid_plan_document", true);
            }
            return ToolResult.Fail("Unknown plan-document tool: " + command.ToolId);
        }

        private ToolResult Create(ToolCommand command, ChatSession session, bool dryRun)
        {
            return Project(_service.Create(
                session,
                ToolArgumentReader.String(command.Arguments, "title", string.Empty),
                ToolArgumentReader.String(command.Arguments, "markdown", string.Empty),
                ToolArgumentReader.String(command.Arguments, "status", "draft"),
                dryRun));
        }

        private ToolResult Update(ToolCommand command, ChatSession session, bool dryRun)
        {
            return Project(_service.Update(
                session,
                ToolArgumentReader.String(command.Arguments, "id", string.Empty),
                ToolArgumentReader.String(command.Arguments, "expectedRevisionArtifactId", string.Empty),
                ToolArgumentReader.String(command.Arguments, "title", string.Empty),
                HasArgument(command, "title"),
                ToolArgumentReader.String(command.Arguments, "markdown", string.Empty),
                ToolArgumentReader.String(command.Arguments, "status", "draft"),
                dryRun));
        }

        private ToolResult Delete(ToolCommand command, ChatSession session, bool dryRun)
        {
            return Project(_service.Delete(
                session,
                ToolArgumentReader.String(command.Arguments, "id", string.Empty),
                dryRun));
        }

        private static ToolResult Project(PlanDocumentMutation mutation)
        {
            if (!mutation.Success)
            {
                return ToolResult.Fail(
                    mutation.Message,
                    null,
                    mutation.ErrorCode,
                    mutation.Retryable);
            }
            if (mutation.Artifact == null)
            {
                return ToolResult.Ok(
                    mutation.Message,
                    new JObject
                    {
                        ["id"] = mutation.PlanId,
                        ["deletedRevisions"] = mutation.DeletedRevisions
                    }.ToString(Newtonsoft.Json.Formatting.None));
            }
            return ToolResult.Ok(
                mutation.Message,
                Payload(mutation.PlanId, mutation.Status, mutation.Artifact)
                    .ToString(Newtonsoft.Json.Formatting.None));
        }

        private static bool HasArgument(ToolCommand command, string name)
        {
            return command != null && command.Arguments != null && command.Arguments.ContainsKey(name);
        }

        private static JObject Payload(string id, string status, ChatArtifact artifact)
        {
            return new JObject { ["planId"] = id, ["status"] = status, ["artifactId"] = artifact.Id, ["revision"] = artifact.Revision };
        }

        private static string CreateSchema()
        {
            return Schema(false).ToString(Formatting.None);
        }

        private static string UpdateSchema()
        {
            return Schema(true).ToString(Formatting.None);
        }

        private static JObject Schema(bool update)
        {
            var properties = new JObject
            {
                ["id"] = new JObject { ["type"] = "string", ["description"] = "Stable plan id from create or metadata." },
                ["expectedRevisionArtifactId"] = new JObject { ["type"] = "string", ["description"] = "Exact active revision artifact id used as an optimistic concurrency guard." },
                ["title"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 200, ["description"] = "User-visible plan title." },
                ["markdown"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = PlanDocumentService.MaximumMarkdownCharacters, ["description"] = "Complete free-form Markdown plan body; preserve it exactly without trimming or partial patch semantics." },
                ["status"] = new JObject { ["type"] = "string", ["enum"] = new JArray("draft", "ready"), ["default"] = "draft", ["description"] = "Draft while being refined; ready only after requirements are decision-complete." }
            };
            if (!update) { properties.Remove("id"); properties.Remove("expectedRevisionArtifactId"); }
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = update ? new JArray("id", "expectedRevisionArtifactId", "markdown") : new JArray("title", "markdown"),
                ["additionalProperties"] = false
            };
        }

        private static string DeleteSchema()
        {
            return "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\",\"minLength\":1,\"description\":\"Stable plan id; all revisions are removed.\"}},\"required\":[\"id\"],\"additionalProperties\":false}";
        }
    }
}
