using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;

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

            yield return ControllerToolDefinition.Create("common.tools_list", "Common", "Read-only: List custom executable RNAssistant tools visible to the current Office host.", "{}");
            yield return ControllerToolDefinition.Create("common.tools_read", "Common", "Read-only: Read one custom RNAssistant tool by id, including metadata, README, pipeline, and VBA code.", "{\"id\":\"excel.my_tool\"}");
            yield return ControllerToolDefinition.Create("common.tools_validate", "Common", "Read-only: Validate a custom RNAssistant pipeline or manifest-based VBA package without saving it.", "{\"id\":\"excel.my_tool\",\"host\":\"Excel\",\"name\":\"My tool\",\"description\":\"What it does\",\"argumentSchemaJson\":\"{\\\"type\\\":\\\"object\\\",\\\"properties\\\":{},\\\"required\\\":[],\\\"additionalProperties\\\":false}\",\"executor\":\"pipeline\",\"pipelineJson\":\"{\\\"steps\\\":[{\\\"toolId\\\":\\\"excel.list_sheets\\\",\\\"arguments\\\":{}}]}\",\"code\":\"\",\"componentsJson\":\"[]\",\"readme\":\"markdown\",\"enabled\":true,\"requiresConfirmation\":true,\"mutatesDocument\":true,\"mutatesLocalState\":false,\"agentCanRun\":false,\"riskLevel\":2}");
            yield return ControllerToolDefinition.Create("common.tools_save", "Common", "Mutates settings: Create or update a custom RNAssistant pipeline or manifest-based VBA package.", "{\"id\":\"excel.my_tool\",\"host\":\"Excel\",\"name\":\"My tool\",\"description\":\"What it does\",\"argumentSchemaJson\":\"{\\\"type\\\":\\\"object\\\",\\\"properties\\\":{},\\\"required\\\":[],\\\"additionalProperties\\\":false}\",\"executor\":\"pipeline\",\"pipelineJson\":\"{\\\"steps\\\":[{\\\"toolId\\\":\\\"excel.list_sheets\\\",\\\"arguments\\\":{}}]}\",\"code\":\"\",\"componentsJson\":\"[]\",\"readme\":\"markdown\",\"enabled\":true,\"requiresConfirmation\":true,\"mutatesDocument\":true,\"mutatesLocalState\":false,\"agentCanRun\":false,\"riskLevel\":2}", mutatesLocalState: true, requiresConfirmation: true, riskLevel: 1);
            yield return ControllerToolDefinition.Create("common.tools_delete", "Common", "Mutates settings: Delete a custom RNAssistant tool by id.", "{\"id\":\"excel.my_tool\"}", mutatesLocalState: true, requiresConfirmation: true, riskLevel: 1);
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

            if (string.Equals(command.ToolId, "common.tools_validate", StringComparison.OrdinalIgnoreCase))
            {
                return ValidateToolPayload(command);
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
                agentCanRun = t.AgentCanRun,
                riskLevel = t.RiskLevel,
                capabilityStatus = t.CapabilityStatus,
                limitations = t.Limitations
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

        private ToolResult ValidateToolPayload(ToolCommand command)
        {
            var tool = ReadToolDefinition(command);
            var validation = ValidateToolDefinition(tool);
            if (!validation.Success)
            {
                return validation;
            }

            return ToolResult.Ok("Tool definition is valid: " + tool.Id, JsonConvert.SerializeObject(tool));
        }

        private ToolResult SaveTool(ToolCommand command, AppSettings settings, bool dryRun, bool manualRun)
        {
            if (!dryRun && !manualRun && !(settings ?? new AppSettings()).AutoConfirmToolActions)
            {
                return ToolResult.WaitingConfirmation("Tool save requires confirmation: " + ToolArgumentReader.String(command.Arguments, "id", string.Empty));
            }

            var tool = ReadToolDefinition(command);
            var validation = ValidateToolDefinition(tool);
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
            var mutatesDocument = ReadBool(command, "mutatesDocument", true);
            return new ToolDefinition
            {
                Id = id,
                Host = ToolArgumentReader.String(command.Arguments, "host", DefaultHostFromId(id)),
                Name = ToolArgumentReader.String(command.Arguments, "name", id),
                Description = ToolArgumentReader.String(command.Arguments, "description", string.Empty),
                ArgumentSchemaJson = ToolArgumentReader.String(command.Arguments, "argumentSchemaJson", ToolArgumentReader.String(command.Arguments, "schema", "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}")),
                Executor = ToolArgumentReader.String(command.Arguments, "executor", "pipeline"),
                PipelineJson = ToolArgumentReader.String(command.Arguments, "pipelineJson", ToolArgumentReader.String(command.Arguments, "pipeline", string.Empty)),
                Code = ToolArgumentReader.String(command.Arguments, "code", string.Empty),
                Readme = ToolArgumentReader.String(command.Arguments, "readme", ToolArgumentReader.String(command.Arguments, "README", string.Empty)),
                Enabled = ReadBool(command, "enabled", true),
                RequiresConfirmation = ReadBool(command, "requiresConfirmation", true),
                MutatesDocument = mutatesDocument,
                MutatesLocalState = ReadBool(command, "mutatesLocalState", false),
                AgentCanRun = ReadBool(command, "agentCanRun", false),
                BuiltIn = false,
                RiskLevel = ReadInt(command, "riskLevel", mutatesDocument ? 2 : 0),
                UseWhen = ToolArgumentReader.String(command.Arguments, "useWhen", string.Empty),
                DoNotUseWhen = ToolArgumentReader.String(command.Arguments, "doNotUseWhen", string.Empty),
                ExamplesJson = ToolArgumentReader.String(command.Arguments, "examplesJson", string.Empty),
                VerifyJson = ToolArgumentReader.String(command.Arguments, "verifyJson", string.Empty),
                CapabilityStatus = ToolArgumentReader.String(command.Arguments, "capabilityStatus", "available"),
                Limitations = ToolArgumentReader.String(command.Arguments, "limitations", string.Empty),
                Components = ReadComponents(ToolArgumentReader.String(command.Arguments, "componentsJson", "[]"))
            };
        }

        private static List<VbaToolComponent> ReadComponents(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<VbaToolComponent>();
            try
            {
                return JArray.Parse(json).OfType<JObject>().Select(component => new VbaToolComponent
                {
                    Name = (string)component["name"],
                    Type = (string)component["type"],
                    FileName = (string)component["fileName"],
                    Code = (string)component["code"] ?? string.Empty
                }).ToList();
            }
            catch (JsonException)
            {
                return new List<VbaToolComponent>();
            }
        }

        private static int ReadInt(ToolCommand command, string name, int fallback)
        {
            if (command == null || command.Arguments == null || !command.Arguments.ContainsKey(name) || command.Arguments[name] == null)
            {
                return fallback;
            }
            int value;
            return int.TryParse(Convert.ToString(command.Arguments[name]), out value) ? value : fallback;
        }

        internal static ToolResult ValidateToolDefinition(ToolDefinition tool)
        {
            if (tool == null || string.IsNullOrWhiteSpace(tool.Id))
            {
                return ToolResult.Fail("Tool id is required.");
            }
            if (tool.Id.Any(char.IsWhiteSpace))
            {
                return ToolResult.Fail("Tool id cannot contain whitespace: " + tool.Id);
            }
            if (string.IsNullOrWhiteSpace(tool.Host))
            {
                return ToolResult.Fail("Tool host is required.");
            }
            if (tool.RiskLevel < 0 || tool.RiskLevel > 3)
            {
                return ToolResult.Fail("Tool riskLevel must be between 0 and 3.");
            }
            if (tool.MutatesDocument && tool.RiskLevel == 0)
            {
                return ToolResult.Fail("Document mutation tools require riskLevel between 1 and 3.");
            }

            var executor = (tool.Executor ?? string.Empty).Trim().ToLowerInvariant();
            if (executor != "pipeline" && executor != "vba")
            {
                return ToolResult.Fail("Tool executor must be pipeline or vba.");
            }

            JObject normalizedSchema;
            string schemaError;
            if (!ToolSchemaSupport.TryNormalize(tool, out normalizedSchema, out schemaError))
            {
                return ToolResult.Fail(schemaError, null, "invalid_tool_schema", false);
            }

            if (executor == "pipeline")
            {
                PipelineDefinition definition;
                string error;
                if (!PipelineDefinitionParser.TryParse(tool.Id, tool.PipelineJson, out definition, out error))
                {
                    return ToolResult.Fail(error);
                }
            }

            if (executor == "vba" && string.IsNullOrWhiteSpace(tool.Code))
            {
                return ToolResult.Fail("VBA tool requires code.");
            }

            if (executor == "vba")
            {
                var manifest = new VbaToolManifestParser().Parse(tool.Code);
                if (!manifest.Success)
                {
                    return ToolResult.Fail(manifest.ErrorMessage, null, manifest.ErrorCode, false);
                }
                if (!string.Equals(tool.Id, manifest.Tool.Id, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(tool.Host, manifest.Tool.Host, StringComparison.OrdinalIgnoreCase))
                {
                    return ToolResult.Fail("tool.json id/host must match the VBA manifest.", null, "vba_manifest_metadata_mismatch", false);
                }
                tool.Name = manifest.Tool.Name;
                tool.Description = manifest.Tool.Description;
                tool.ArgumentSchemaJson = manifest.Tool.ArgumentSchemaJson;
                tool.EntryPoint = manifest.Tool.EntryPoint;
                tool.PackageVersion = manifest.Tool.PackageVersion;
                tool.ArgumentOrder = manifest.Tool.ArgumentOrder;
                tool.MutatesDocument = manifest.Tool.MutatesDocument;
                tool.AgentCanRun = manifest.Tool.AgentCanRun;
                tool.RequiresConfirmation = manifest.Tool.RequiresConfirmation;
                tool.RiskLevel = manifest.Tool.RiskLevel;
                if (tool.Components == null || tool.Components.Count == 0)
                {
                    tool.Components = manifest.Tool.Components;
                }
                var declared = new HashSet<string>(manifest.Tool.Components.Select(component => component.Name), StringComparer.OrdinalIgnoreCase);
                var components = (tool.Components ?? new List<VbaToolComponent>()).Where(component => component != null).ToList();
                var duplicate = components.Where(component => !string.IsNullOrWhiteSpace(component.Name))
                    .GroupBy(component => component.Name, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault(group => group.Count() > 1);
                if (duplicate != null)
                {
                    return ToolResult.Fail("VBA package contains a duplicate component: " + duplicate.Key, null, "vba_component_duplicate", false);
                }
                var invalid = components.FirstOrDefault(component =>
                    !VbaToolManifestParser.ValidIdentifier(component.Name) ||
                    (!string.Equals(component.Type, "StdModule", StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(component.Type, "ClassModule", StringComparison.OrdinalIgnoreCase)));
                if (invalid != null)
                {
                    return ToolResult.Fail("VBA package component name/type is invalid: " + (invalid.Name ?? string.Empty), null, "vba_component_invalid", false);
                }
                var unexpected = components.FirstOrDefault(component => !declared.Contains(component.Name));
                if (unexpected != null)
                {
                    return ToolResult.Fail("VBA package contains an undeclared component: " + unexpected.Name, null, "vba_component_undeclared", false);
                }
                var entryName = manifest.Tool.Components[0].Name;
                var entry = components.FirstOrDefault(component => string.Equals(component.Name, entryName, StringComparison.OrdinalIgnoreCase));
                if (entry != null && !string.Equals(entry.Type, "StdModule", StringComparison.OrdinalIgnoreCase))
                {
                    return ToolResult.Fail("VBA entry component must be a StdModule: " + entryName, null, "vba_entry_component_type", false);
                }
                var supplied = new HashSet<string>(components.Where(component => !string.IsNullOrWhiteSpace(component.Code)).Select(component => component.Name), StringComparer.OrdinalIgnoreCase)
                {
                    entryName
                };
                var missing = declared.FirstOrDefault(name => !supplied.Contains(name));
                if (!string.IsNullOrWhiteSpace(missing))
                {
                    return ToolResult.Fail("VBA package source is missing declared component: " + missing, null, "vba_component_missing", false);
                }
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

    }
}
