using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.Tools
{
    internal static class TaskListToolCatalog
    {
        internal const string CreateToolId = "common.task_list_create";
        internal const string UpdateToolId = "common.task_list_update";
        internal const string CloseToolId = "common.task_list_close";

        internal static bool Owns(string toolId)
        {
            return string.Equals(toolId, CreateToolId, StringComparison.Ordinal) ||
                string.Equals(toolId, UpdateToolId, StringComparison.Ordinal) ||
                string.Equals(toolId, CloseToolId, StringComparison.Ordinal);
        }

        internal static IEnumerable<ToolDefinition> GetTools()
        {
            yield return Projection(CreateToolId,
                "Task list: Create the visible checklist for the current active chat task. Use for at least three meaningful stages, not individual tool calls.",
                PlanPayloadSchema(false), "task_list_create");
            yield return Projection(UpdateToolId,
                "Task list: Replace the complete steps of the active checklist after material progress. Stable step ids must be preserved.",
                PlanPayloadSchema(true), "task_list_update");
            yield return Projection(CloseToolId,
                "Task list: Close and hide the active checklist while preserving its final revision in chat history.",
                CloseSchema(), "task_list_close");
        }

        private static ToolDefinition Projection(
            string id, string description, string schema, string name)
        {
            return ControllerToolDefinition.CreateTypedProjection(
                new ToolDescriptor(id, description, schema),
                new ToolPolicy(ToolEffect.Write, ToolVerification.Tool,
                    false, false, new[] { "agent", "plan" }),
                name: name, scope: "session", mutatesLocalState: true);
        }

        private static string PlanPayloadSchema(bool update)
        {
            var properties = new JObject
            {
                ["id"] = new JObject { ["type"] = "string", ["description"] = "Stable task-list id returned by task_list_create, or any revision artifact id." },
                ["goal"] = new JObject { ["type"] = "string", ["description"] = "Concise user-visible goal for the current task.", ["minLength"] = 1, ["maxLength"] = TaskListService.MaxGoalCharacters },
                ["steps"] = new JObject
                {
                    ["type"] = "array",
                    ["description"] = update ? "Complete replacement list of task steps." : "Complete ordered meaningful task stages.",
                    ["minItems"] = 3,
                    ["maxItems"] = TaskListService.MaxSteps,
                    ["items"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["id"] = new JObject { ["type"] = "string", ["description"] = "Stable unique step id without whitespace." },
                            ["text"] = new JObject { ["type"] = "string", ["description"] = "Concise user-visible step description.", ["minLength"] = 1, ["maxLength"] = TaskListService.MaxStepCharacters },
                            ["status"] = new JObject { ["type"] = "string", ["description"] = "Explicit current step status.", ["enum"] = new JArray("pending", "in_progress", "completed", "blocked", "cancelled"), ["default"] = "pending" }
                        },
                        ["required"] = new JArray("id", "text"),
                        ["additionalProperties"] = false
                    }
                }
            };
            if (!update) properties.Remove("id");
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = update
                    ? new JArray("id") : new JArray("goal", "steps"),
                ["additionalProperties"] = false
            }.ToString(Formatting.None);
        }

        private static string CloseSchema()
        {
            return "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\",\"description\":\"Stable task-list id or any revision id.\",\"minLength\":1},\"outcome\":{\"type\":\"string\",\"enum\":[\"completed\",\"cancelled\",\"superseded\"],\"description\":\"Why the task list is being closed.\"}},\"required\":[\"id\",\"outcome\"],\"additionalProperties\":false}";
        }
    }
}
