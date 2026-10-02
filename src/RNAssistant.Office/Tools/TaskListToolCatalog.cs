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
                "Task list: Save or revise your plan, update progress by current 1-based index, or record an outcome. Statuses express your assessment, not verified tool effects. An open list does not prevent a final answer. Runtime owns list and step identity.",
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

        private static JObject NoteSchema()
        {
            return new JObject { ["type"] = "string", ["maxLength"] = TaskListService.MaxStepCharacters,
                ["description"] = "Optional result, remaining work or reason no change was needed. Omit to preserve an unchanged stage note; empty clears it." };
        }

        internal static string Schema()
        {
            var action = new JObject
            {
                ["type"] = "string",
                ["description"] = "Use save to create or revise goal and stages, update_statuses for progress, or close for an outcome or blocker.",
                ["enum"] = new JArray("save", "update_statuses", "close")
            };
            var steps = new JObject
            {
                ["type"] = "array",
                ["description"] = "Complete revised plan; stages may be added, rewritten, removed or reordered. Runtime preserves identity and omitted progress only for unchanged unambiguous stage text.",
                ["minItems"] = 1,
                ["maxItems"] = TaskListService.MaxSteps,
                ["items"] = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["text"] = new JObject { ["type"] = "string", ["description"] = "Concise user-visible step description.", ["minLength"] = 1, ["maxLength"] = TaskListService.MaxStepCharacters },
                        ["status"] = new JObject { ["type"] = "string", ["description"] = "Omit to preserve an existing stage status; a new stage defaults to pending.", ["enum"] = new JArray("pending", "in_progress", "completed", "blocked", "cancelled") },
                        ["note"] = NoteSchema()
                    },
                    ["required"] = new JArray("text"),
                    ["additionalProperties"] = false
                }
            };
            var properties = new JObject
            {
                ["action"] = action,
                ["goal"] = new JObject { ["type"] = "string", ["description"] = "Concise goal. Required for save; optional on update_statuses as a check that this is still the intended list.", ["minLength"] = 1, ["maxLength"] = TaskListService.MaxGoalCharacters },
                ["steps"] = steps,
                ["updates"] = new JObject
                {
                    ["type"] = "array",
                    ["description"] = "Statuses to change on the active list. Index is the current 1-based step position, not a stable id. Unmentioned steps keep their statuses.",
                    ["minItems"] = 1,
                    ["maxItems"] = TaskListService.MaxSteps,
                    ["items"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["index"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = TaskListService.MaxSteps },
                            ["status"] = new JObject { ["type"] = "string", ["enum"] = new JArray("pending", "in_progress", "completed", "blocked", "cancelled") },
                            ["note"] = NoteSchema()
                        },
                        ["required"] = new JArray("index", "status"),
                        ["additionalProperties"] = false
                    }
                },
                ["outcome"] = new JObject { ["type"] = "string", ["enum"] = new JArray("completed", "cancelled", "superseded", "blocked"), ["description"] = "Your assessment of the task outcome. blocked retains the plan for resumption; other outcomes archive it. Unmentioned step statuses stay unchanged; no source change is required." },
                ["reason"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 500, ["description"] = "Explain a plan revision or outcome; required only for outcome=blocked." }
            };
            var saveProperties = new JObject
            {
                ["action"] = new JObject { ["type"] = "string", ["const"] = "save", ["description"] = "Save the complete active checklist." },
                ["goal"] = properties["goal"].DeepClone(),
                ["steps"] = steps.DeepClone(),
                ["reason"] = properties["reason"].DeepClone()
            };
            var closeProperties = new JObject
            {
                ["action"] = new JObject { ["type"] = "string", ["const"] = "close", ["description"] = "Record your outcome or retain the plan as blocked, optionally updating steps in the same call." },
                ["outcome"] = properties["outcome"].DeepClone(),
                ["updates"] = properties["updates"].DeepClone(),
                ["reason"] = properties["reason"].DeepClone()
            };
            var statusProperties = new JObject
            {
                ["action"] = new JObject { ["type"] = "string", ["const"] = "update_statuses", ["description"] = "Change only existing step statuses on the active list." },
                ["goal"] = properties["goal"].DeepClone(),
                ["updates"] = properties["updates"].DeepClone()
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
                        ["properties"] = statusProperties,
                        ["required"] = new JArray("action", "updates"),
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
