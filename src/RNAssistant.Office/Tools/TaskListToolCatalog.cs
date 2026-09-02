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
        internal const string SetToolId = "common.task_list_set";

        internal static bool Owns(string toolId)
        {
            return string.Equals(toolId, SetToolId, StringComparison.Ordinal);
        }

        internal static IEnumerable<ToolCatalogEntry> GetTools()
        {
            yield return Projection(SetToolId,
                "Task list: Save the complete visible checklist, or close the active checklist with a terminal outcome. Runtime owns list and stable step identity.",
                Schema(), "task_list_set");
        }

        private static ToolCatalogEntry Projection(
            string id, string description, string schema, string name)
        {
            return ControllerToolCatalogEntry.CreateTypedProjection(
                new ToolDescriptor(id, description, schema),
                new ToolPolicy(ToolEffect.Write, ToolVerification.Tool,
                    false, false, new[] { "agent", "plan" }),
                name: name, scope: "session", mutatesLocalState: true);
        }

        internal static string Schema()
        {
            var action = new JObject
            {
                ["type"] = "string",
                ["description"] = "Use save to create or replace the active list; use close only after its steps are terminal.",
                ["enum"] = new JArray("save", "close")
            };
            var steps = new JObject
            {
                ["type"] = "array",
                ["description"] = "Complete ordered meaningful task stages; runtime generates and preserves their stable ids.",
                ["minItems"] = 3,
                ["maxItems"] = TaskListService.MaxSteps,
                ["items"] = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["text"] = new JObject { ["type"] = "string", ["description"] = "Concise user-visible step description.", ["minLength"] = 1, ["maxLength"] = TaskListService.MaxStepCharacters },
                        ["status"] = new JObject { ["type"] = "string", ["description"] = "Explicit current step status.", ["enum"] = new JArray("pending", "in_progress", "completed", "blocked", "cancelled"), ["default"] = "pending" }
                    },
                    ["required"] = new JArray("text"),
                    ["additionalProperties"] = false
                }
            };
            var properties = new JObject
            {
                ["action"] = action,
                ["goal"] = new JObject { ["type"] = "string", ["description"] = "Concise user-visible goal for the current task.", ["minLength"] = 1, ["maxLength"] = TaskListService.MaxGoalCharacters },
                ["steps"] = steps,
                ["outcome"] = new JObject { ["type"] = "string", ["enum"] = new JArray("completed", "cancelled", "superseded"), ["description"] = "Terminal outcome used only with action=close." }
            };
            var saveProperties = new JObject
            {
                ["action"] = new JObject { ["type"] = "string", ["const"] = "save", ["description"] = "Save the complete active checklist." },
                ["goal"] = properties["goal"].DeepClone(),
                ["steps"] = steps.DeepClone()
            };
            var closeProperties = new JObject
            {
                ["action"] = new JObject { ["type"] = "string", ["const"] = "close", ["description"] = "Close the active checklist." },
                ["outcome"] = properties["outcome"].DeepClone()
            };
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JArray("action"),
                ["additionalProperties"] = false,
                ["anyOf"] = new JArray(
                    new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = saveProperties,
                        ["required"] = new JArray("action", "goal", "steps"),
                        ["additionalProperties"] = false
                    },
                    new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = closeProperties,
                        ["required"] = new JArray("action", "outcome"),
                        ["additionalProperties"] = false
                    })
            }.ToString(Formatting.None);
        }
    }
}
