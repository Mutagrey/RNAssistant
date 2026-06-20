using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office.Tools
{
    internal sealed class ToolAuthoringExecutor
    {
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly ToolStore _toolStore;

        public ToolAuthoringExecutor(IOfficeApplicationAdapter adapter, ToolStore toolStore)
        {
            _adapter = adapter;
            _toolStore = toolStore;
        }

        public IEnumerable<ToolDefinition> GetControllerTools()
        {
            if (_toolStore == null)
            {
                yield break;
            }

            yield return ControllerTool("common.tools_list", "List custom executable RNAssistant tools visible to the current Office host.", "{}", false);
            yield return ControllerTool("common.tools_read", "Read one custom RNAssistant tool by id, including metadata, README, pipeline, and VBA code.", "{\"id\":\"excel.my_tool\"}", false);
            yield return ControllerTool("common.tools_save", "Create or update a custom RNAssistant pipeline or VBA tool.", "{\"id\":\"excel.my_tool\",\"host\":\"Excel\",\"name\":\"My tool\",\"description\":\"What it does\",\"argumentSchemaJson\":\"{}\",\"executor\":\"pipeline|vba\",\"pipelineJson\":\"{\\\"steps\\\":[]}\",\"code\":\"optional VBA\",\"readme\":\"markdown\",\"enabled\":true,\"requiresConfirmation\":true,\"mutatesDocument\":true,\"agentCanRun\":false}", true);
            yield return ControllerTool("common.tools_delete", "Delete a custom RNAssistant tool by id.", "{\"id\":\"excel.my_tool\"}", true);
        }

        public bool IsControllerTool(string toolId)
        {
            return GetControllerTool(toolId) != null;
        }

        public ToolDefinition GetControllerTool(string toolId)
        {
            if (string.IsNullOrWhiteSpace(toolId))
            {
                return null;
            }

            return GetControllerTools().FirstOrDefault(tool => string.Equals(tool.Id, toolId, StringComparison.OrdinalIgnoreCase));
        }

        public ToolResult ExecuteControllerTool(ToolCommand command, AppSettings settings, bool dryRun, bool manualRun)
        {
            if (_toolStore == null)
            {
                return ToolResult.Fail("Tool authoring store is not available.");
            }

            if (string.Equals(command.ToolId, "common.tools_list", StringComparison.OrdinalIgnoreCase))
            {
                return ListTools();
            }

            if (string.Equals(command.ToolId, "common.tools_read", StringComparison.OrdinalIgnoreCase))
            {
                return ReadTool(command);
            }

            if (string.Equals(command.ToolId, "common.tools_save", StringComparison.OrdinalIgnoreCase))
            {
                return SaveTool(command, settings, dryRun, manualRun);
            }

            if (string.Equals(command.ToolId, "common.tools_delete", StringComparison.OrdinalIgnoreCase))
            {
                return DeleteTool(command, settings, dryRun, manualRun);
            }

            return ToolResult.Fail("Unknown tool authoring command: " + command.ToolId);
        }

        private ToolResult ListTools()
        {
            var tools = VisibleTools().Select(t => new
            {
                id = t.Id,
                host = t.Host,
                name = t.Name,
                description = t.Description,
                executor = t.Executor,
                enabled = t.Enabled,
                requiresConfirmation = t.RequiresConfirmation,
                mutatesDocument = t.MutatesDocument,
                agentCanRun = t.AgentCanRun
            }).ToArray();
            return ToolResult.Ok("Custom tools listed.", JsonConvert.SerializeObject(tools));
        }

        private ToolResult ReadTool(ToolCommand command)
        {
            var id = ToolArgumentReader.String(command.Arguments, "id", string.Empty);
            var tool = VisibleTools().FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
            if (tool == null)
            {
                return ToolResult.Fail("Custom tool not found: " + id);
            }

            return ToolResult.Ok("Custom tool read: " + tool.Id, JsonConvert.SerializeObject(tool));
        }

        private ToolResult SaveTool(ToolCommand command, AppSettings settings, bool dryRun, bool manualRun)
        {
            if (!dryRun && !manualRun && !(settings ?? new AppSettings()).AutoConfirmToolActions)
            {
                return ToolResult.WaitingConfirmation("Tool save requires confirmation: " + ToolArgumentReader.String(command.Arguments, "id", string.Empty));
            }

            var tool = ReadToolDefinition(command);
            var validation = ValidateTool(tool);
            if (!validation.Success)
            {
                return validation;
            }

            if (dryRun)
            {
                return ToolResult.Ok("Dry run: would save custom tool " + tool.Id, JsonConvert.SerializeObject(tool));
            }

            var saved = _toolStore.SaveOne(tool);
            return ToolResult.Ok("Custom tool saved: " + tool.Id, JsonConvert.SerializeObject(saved ?? tool));
        }

        private ToolResult DeleteTool(ToolCommand command, AppSettings settings, bool dryRun, bool manualRun)
        {
            var id = ToolArgumentReader.String(command.Arguments, "id", string.Empty);
            if (string.IsNullOrWhiteSpace(id))
            {
                return ToolResult.Fail("Tool id is required.");
            }

            if (!dryRun && !manualRun && !(settings ?? new AppSettings()).AutoConfirmToolActions)
            {
                return ToolResult.WaitingConfirmation("Tool delete requires confirmation: " + id);
            }

            if (dryRun)
            {
                return ToolResult.Ok("Dry run: would delete custom tool " + id);
            }

            return _toolStore.Delete(id)
                ? ToolResult.Ok("Custom tool deleted: " + id)
                : ToolResult.Fail("Custom tool not found: " + id);
        }

        private ToolDefinition ReadToolDefinition(ToolCommand command)
        {
            var id = ToolArgumentReader.String(command.Arguments, "id", string.Empty);
            return new ToolDefinition
            {
                Id = id,
                Host = ToolArgumentReader.String(command.Arguments, "host", DefaultHostFromId(id)),
                Name = ToolArgumentReader.String(command.Arguments, "name", id),
                Description = ToolArgumentReader.String(command.Arguments, "description", string.Empty),
                ArgumentSchemaJson = ToolArgumentReader.String(command.Arguments, "argumentSchemaJson", ToolArgumentReader.String(command.Arguments, "schema", "{}")),
                Executor = ToolArgumentReader.String(command.Arguments, "executor", "pipeline"),
                PipelineJson = ToolArgumentReader.String(command.Arguments, "pipelineJson", ToolArgumentReader.String(command.Arguments, "pipeline", string.Empty)),
                Code = ToolArgumentReader.String(command.Arguments, "code", string.Empty),
                Readme = ToolArgumentReader.String(command.Arguments, "readme", ToolArgumentReader.String(command.Arguments, "README", string.Empty)),
                Enabled = ReadBool(command, "enabled", true),
                RequiresConfirmation = ReadBool(command, "requiresConfirmation", true),
                MutatesDocument = ReadBool(command, "mutatesDocument", true),
                AgentCanRun = ReadBool(command, "agentCanRun", false),
                BuiltIn = false
            };
        }

        private static ToolResult ValidateTool(ToolDefinition tool)
        {
            if (tool == null || string.IsNullOrWhiteSpace(tool.Id))
            {
                return ToolResult.Fail("Tool id is required.");
            }
            if (string.IsNullOrWhiteSpace(tool.Host))
            {
                return ToolResult.Fail("Tool host is required.");
            }

            var executor = (tool.Executor ?? string.Empty).Trim().ToLowerInvariant();
            if (executor != "pipeline" && executor != "vba")
            {
                return ToolResult.Fail("Tool executor must be pipeline or vba.");
            }

            try
            {
                JToken.Parse(string.IsNullOrWhiteSpace(tool.ArgumentSchemaJson) ? "{}" : tool.ArgumentSchemaJson);
            }
            catch (JsonException ex)
            {
                return ToolResult.Fail("Invalid argumentSchemaJson: " + ex.Message);
            }

            if (executor == "pipeline")
            {
                if (string.IsNullOrWhiteSpace(tool.PipelineJson))
                {
                    return ToolResult.Fail("Pipeline tool requires pipelineJson.");
                }

                try
                {
                    var root = JObject.Parse(tool.PipelineJson);
                    var steps = root["steps"] as JArray;
                    if (steps == null || steps.Count == 0)
                    {
                        return ToolResult.Fail("Pipeline tool requires at least one step.");
                    }
                }
                catch (JsonException ex)
                {
                    return ToolResult.Fail("Invalid pipelineJson: " + ex.Message);
                }
            }

            if (executor == "vba" && string.IsNullOrWhiteSpace(tool.Code))
            {
                return ToolResult.Fail("VBA tool requires code.");
            }

            return ToolResult.Ok("Tool definition is valid.");
        }

        private IEnumerable<ToolDefinition> VisibleTools()
        {
            return _toolStore.Load().Where(t =>
                t != null &&
                !t.BuiltIn &&
                (string.Equals(t.Host, _adapter.HostName, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(t.Host, "Common", StringComparison.OrdinalIgnoreCase)));
        }

        private static string DefaultHostFromId(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return "Common";
            }

            var dot = id.IndexOf('.');
            if (dot <= 0)
            {
                return "Common";
            }

            var prefix = id.Substring(0, dot);
            return string.Equals(prefix, "common", StringComparison.OrdinalIgnoreCase) ? "Common" : prefix;
        }

        private static bool ReadBool(ToolCommand command, string name, bool fallback)
        {
            var raw = ToolArgumentReader.String(command.Arguments, name, fallback ? "true" : "false");
            bool value;
            return bool.TryParse(raw, out value) ? value : fallback;
        }

        private static ToolDefinition ControllerTool(string id, string description, string schema, bool requiresConfirmation)
        {
            return new ToolDefinition
            {
                Id = id,
                Host = "Common",
                Name = id,
                Description = description,
                ArgumentSchemaJson = schema,
                BuiltIn = true,
                Enabled = true,
                RequiresConfirmation = requiresConfirmation,
                MutatesDocument = false,
                AgentCanRun = true
            };
        }
    }
}
