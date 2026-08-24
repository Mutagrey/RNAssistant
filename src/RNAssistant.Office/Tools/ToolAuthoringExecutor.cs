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

            yield return ControllerToolDefinition.Create("common.tools_read", "Common", "Read-only: Read one custom tool in authoring shape; omit id to list compact metadata for visible custom tools.", OptionalIdSchema());
            yield return ControllerToolDefinition.Create("common.tools_validate", "Common", "Read-only: Validate a complete custom pipeline or manifest-based VBA tool definition without saving it.", ToolPayloadSchema(false));
            yield return ControllerToolDefinition.Create("common.tools_upsert", "Common", "Mutates settings: Create a missing custom tool or update an existing one after validating the effective definition. Omitted fields are preserved on update; use strict mode only when existence itself matters.", ToolUpsertSchema(), mutatesLocalState: true, requiresConfirmation: true, riskLevel: 1);
            yield return ControllerToolDefinition.Create("common.tools_delete", "Common", "Mutates settings: Delete a custom RNAssistant tool by id.", "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\",\"description\":\"Exact stable identifier.\"}},\"required\":[\"id\"],\"additionalProperties\":false}", mutatesLocalState: true, requiresConfirmation: true, riskLevel: 1);
        }

        public ToolResult ExecuteControllerTool(ToolCommand command, AppSettings settings, bool dryRun, bool manualRun)
        {
            if (_toolStore == null)
            {
                return ToolResult.Fail("Tool authoring store is not available.");
            }

            if (string.Equals(command.ToolId, "common.tools_read", StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrWhiteSpace(ToolArgumentReader.String(command.Arguments, "id", string.Empty))
                    ? ListTools()
                    : ReadTool(command);
            }

            if (string.Equals(command.ToolId, "common.tools_validate", StringComparison.OrdinalIgnoreCase))
            {
                return ValidateToolPayload(command);
            }

            if (string.Equals(command.ToolId, "common.tools_upsert", StringComparison.OrdinalIgnoreCase))
            {
                return UpsertTool(command, settings, dryRun, manualRun);
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

            return ToolResult.Ok("Custom tool read: " + tool.Id, ToolPayload(tool).ToString(Formatting.None));
        }

        private ToolResult ValidateToolPayload(ToolCommand command)
        {
            var tool = ReadToolDefinition(command);
            var validation = ValidateToolDefinition(tool);
            if (!validation.Success)
            {
                return validation;
            }

            return ToolResult.Ok("Tool definition is valid: " + tool.Id, ToolPayload(tool).ToString(Formatting.None));
        }

        private ToolResult UpsertTool(ToolCommand command, AppSettings settings, bool dryRun, bool manualRun)
        {
            var id = ToolArgumentReader.String(command.Arguments, "id", string.Empty);
            var mode = ToolArgumentReader.String(command.Arguments, "mode", "upsert");
            var existing = _toolStore.Load().FirstOrDefault(tool => string.Equals(tool.Id, id, StringComparison.OrdinalIgnoreCase));
            if (existing != null && string.Equals(mode, "createOnly", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Fail("Custom tool already exists: " + id + ". Use mode=upsert or updateOnly.", null, "tool_already_exists", false);
            }
            if (existing == null && string.Equals(mode, "updateOnly", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Fail("Custom tool not found: " + id + ". Use mode=upsert or createOnly.", null, "tool_not_found", false);
            }
            if (existing != null && !HasMutableArguments(command))
            {
                return ToolResult.Fail("Tool update requires at least one supplied field besides id/mode.", null, "tool_update_empty", true);
            }

            return existing == null
                ? PersistTool(ReadToolDefinition(command), settings, dryRun, manualRun, "create")
                : PersistTool(UpdateToolDefinition(existing, command), settings, dryRun, manualRun, "update");
        }

        private ToolResult PersistTool(ToolDefinition tool, AppSettings settings, bool dryRun, bool manualRun, string operation)
        {
            if (!dryRun && !manualRun && !(settings ?? new AppSettings()).AutoConfirmToolActions)
            {
                return ToolResult.WaitingConfirmation("Tool " + operation + " requires confirmation: " + (tool == null ? string.Empty : tool.Id));
            }
            var validation = ValidateToolDefinition(tool);
            if (!validation.Success) return validation;
            if (dryRun)
            {
                return ToolResult.Ok("Dry run: would " + operation + " custom tool " + tool.Id, ToolPayload(tool).ToString(Formatting.None));
            }

            var saved = _toolStore.SaveOne(tool);
            return ToolResult.Ok("Custom tool " + (operation == "create" ? "created: " : "updated: ") + tool.Id,
                ToolPayload(saved ?? tool).ToString(Formatting.None));
        }

        private ToolResult DeleteTool(ToolCommand command, AppSettings settings, bool dryRun, bool manualRun)
        {
            var id = ToolArgumentReader.String(command.Arguments, "id", string.Empty);
            if (string.IsNullOrWhiteSpace(id))
            {
                return ToolResult.Fail("Tool id is required.");
            }
            if (!_toolStore.Load().Any(tool => tool != null && string.Equals(tool.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                return ToolResult.Fail("Custom tool not found: " + id, null, "tool_not_found", false);
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
            var components = ReadComponents(ToolArgumentReader.String(command.Arguments, "components", "[]"));
            return NormalizeVbaEntryCode(new ToolDefinition
            {
                Id = id,
                Host = ToolArgumentReader.String(command.Arguments, "host", DefaultHostFromId(id)),
                Name = ToolArgumentReader.String(command.Arguments, "name", id),
                Description = ToolArgumentReader.String(command.Arguments, "description", string.Empty),
                ArgumentSchemaJson = ToolArgumentReader.String(command.Arguments, "parameters", "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}"),
                Executor = ToolArgumentReader.String(command.Arguments, "executor", "pipeline"),
                PipelineJson = ToolArgumentReader.String(command.Arguments, "pipeline", string.Empty),
                Readme = ToolArgumentReader.String(command.Arguments, "readme", string.Empty),
                Enabled = ReadBool(command, "enabled", true),
                RequiresConfirmation = ReadBool(command, "requiresConfirmation", false),
                MutatesDocument = ReadBool(command, "mutatesDocument", false),
                MutatesLocalState = ReadBool(command, "mutatesLocalState", false),
                AgentCanRun = ReadBool(command, "agentCanRun", true),
                BuiltIn = false,
                RiskLevel = ReadInt(command, "riskLevel", 0),
                UseWhen = ToolArgumentReader.String(command.Arguments, "useWhen", string.Empty),
                DoNotUseWhen = ToolArgumentReader.String(command.Arguments, "doNotUseWhen", string.Empty),
                CapabilityStatus = ToolArgumentReader.String(command.Arguments, "capabilityStatus", "available"),
                Limitations = ToolArgumentReader.String(command.Arguments, "limitations", string.Empty),
                Components = components
            });
        }

        private static ToolDefinition UpdateToolDefinition(ToolDefinition existing, ToolCommand command)
        {
            var tool = existing.Clone();
            tool.StoragePath = existing.StoragePath;
            SetString(command, "host", value => tool.Host = value);
            SetString(command, "name", value => tool.Name = value);
            SetString(command, "description", value => tool.Description = value);
            SetString(command, "parameters", value => tool.ArgumentSchemaJson = value);
            SetString(command, "executor", value => tool.Executor = value);
            SetString(command, "pipeline", value => tool.PipelineJson = value);
            SetString(command, "readme", value => tool.Readme = value);
            SetString(command, "useWhen", value => tool.UseWhen = value);
            SetString(command, "doNotUseWhen", value => tool.DoNotUseWhen = value);
            SetString(command, "capabilityStatus", value => tool.CapabilityStatus = value);
            SetString(command, "limitations", value => tool.Limitations = value);
            SetBool(command, "enabled", value => tool.Enabled = value);
            SetBool(command, "requiresConfirmation", value => tool.RequiresConfirmation = value);
            SetBool(command, "mutatesDocument", value => tool.MutatesDocument = value);
            SetBool(command, "mutatesLocalState", value => tool.MutatesLocalState = value);
            SetBool(command, "agentCanRun", value => tool.AgentCanRun = value);
            if (HasArgument(command, "riskLevel")) tool.RiskLevel = ReadInt(command, "riskLevel", tool.RiskLevel);
            if (HasArgument(command, "components")) tool.Components = ReadComponents(ToolArgumentReader.String(command.Arguments, "components", "[]"));
            return NormalizeVbaEntryCode(tool);
        }

        private static ToolDefinition NormalizeVbaEntryCode(ToolDefinition tool)
        {
            if (tool != null && string.Equals(tool.Executor, "vba", StringComparison.OrdinalIgnoreCase))
            {
                var entry = (tool.Components ?? new List<VbaToolComponent>()).FirstOrDefault();
                tool.Code = entry == null ? string.Empty : entry.Code ?? string.Empty;
            }
            return tool;
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

        private static JObject ToolPayload(ToolDefinition tool)
        {
            tool = tool ?? new ToolDefinition();
            return new JObject
            {
                ["id"] = tool.Id ?? string.Empty,
                ["host"] = tool.Host ?? string.Empty,
                ["name"] = tool.Name ?? string.Empty,
                ["description"] = tool.Description ?? string.Empty,
                ["parameters"] = ParseJsonObject(tool.ArgumentSchemaJson),
                ["executor"] = tool.Executor ?? string.Empty,
                ["pipeline"] = ParseJsonObject(tool.PipelineJson),
                ["components"] = new JArray((tool.Components ?? new List<VbaToolComponent>())
                    .Where(component => component != null)
                    .Select(component => new JObject
                    {
                        ["name"] = component.Name ?? string.Empty,
                        ["type"] = component.Type ?? string.Empty,
                        ["fileName"] = component.FileName ?? string.Empty,
                        ["code"] = component.Code ?? string.Empty
                    })),
                ["readme"] = tool.Readme ?? string.Empty,
                ["enabled"] = tool.Enabled,
                ["requiresConfirmation"] = tool.RequiresConfirmation,
                ["mutatesDocument"] = tool.MutatesDocument,
                ["mutatesLocalState"] = tool.MutatesLocalState,
                ["agentCanRun"] = tool.AgentCanRun,
                ["riskLevel"] = tool.RiskLevel,
                ["useWhen"] = tool.UseWhen ?? string.Empty,
                ["doNotUseWhen"] = tool.DoNotUseWhen ?? string.Empty,
                ["capabilityStatus"] = tool.CapabilityStatus ?? "available",
                ["limitations"] = tool.Limitations ?? string.Empty
            };
        }

        private static JToken ParseJsonObject(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return JValue.CreateNull();
            try
            {
                var parsed = JToken.Parse(json);
                return parsed.Type == JTokenType.Object ? parsed : JValue.CreateNull();
            }
            catch (JsonException)
            {
                return JValue.CreateNull();
            }
        }

        private static string IdSchema()
        {
            return "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\",\"description\":\"Exact stable custom tool id.\",\"minLength\":1,\"maxLength\":128}},\"required\":[\"id\"],\"additionalProperties\":false}";
        }

        private static string OptionalIdSchema()
        {
            return "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\",\"description\":\"Exact custom tool id; omit to list compact metadata.\"}},\"required\":[],\"additionalProperties\":false}";
        }

        private static string ToolPayloadSchema(bool update)
        {
            var properties = new JObject
            {
                ["id"] = BoundedStringProperty("Exact stable custom tool id; it cannot shadow a built-in id.", 128),
                ["host"] = EnumProperty("Office host where the tool is available.", "Common", "Excel", "Word", "PowerPoint", "Outlook"),
                ["name"] = BoundedStringProperty("Human-readable tool name.", 200),
                ["description"] = BoundedStringProperty("Clear model-facing description of what the tool does.", 8000),
                ["parameters"] = ParametersProperty(),
                ["executor"] = EnumProperty("Execution type.", "pipeline", "vba"),
                ["pipeline"] = PipelineProperty(),
                ["components"] = new JObject
                {
                    ["type"] = "array",
                    ["description"] = "Ordered VBA package source components; the first component is the StdModule containing the manifest and entry function.",
                    ["maxItems"] = 50,
                    ["items"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["name"] = Property("string", "Exact VBA component name."),
                            ["type"] = EnumProperty("VBA component type.", "StdModule", "ClassModule"),
                            ["fileName"] = Property("string", "Optional source file name ending in .bas or .cls."),
                            ["code"] = BoundedStringProperty("Complete VBA source code for this component.", 1000000)
                        },
                        ["required"] = new JArray("name", "type", "code"),
                        ["additionalProperties"] = false
                    }
                },
                ["readme"] = BoundedStringProperty("Markdown documentation stored with the custom tool.", 500000),
                ["enabled"] = Property("boolean", "Whether the tool is enabled."),
                ["requiresConfirmation"] = Property("boolean", "Whether execution requires explicit user confirmation."),
                ["mutatesDocument"] = Property("boolean", "Whether execution may change the Office document."),
                ["mutatesLocalState"] = Property("boolean", "Whether execution may change RNAssistant local state."),
                ["agentCanRun"] = Property("boolean", "Whether Agent mode may select this tool."),
                ["riskLevel"] = new JObject
                {
                    ["type"] = "integer",
                    ["description"] = "Risk level from 0 through 3.",
                    ["minimum"] = 0,
                    ["maximum"] = 3
                },
                ["useWhen"] = BoundedStringProperty("Positive selection guidance for the model.", 4000),
                ["doNotUseWhen"] = BoundedStringProperty("Cases where the model should not select this tool.", 4000),
                ["capabilityStatus"] = EnumProperty("Current capability status.", "available", "partial", "unavailable"),
                ["limitations"] = BoundedStringProperty("Known limitations presented to the model.", 4000)
            };
            if (!update)
            {
                properties["enabled"]["default"] = true;
                properties["requiresConfirmation"]["default"] = false;
                properties["mutatesDocument"]["default"] = false;
                properties["mutatesLocalState"]["default"] = false;
                properties["agentCanRun"]["default"] = true;
                properties["riskLevel"]["default"] = 0;
                properties["capabilityStatus"]["default"] = "available";
            }
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = update
                    ? new JArray("id")
                    : new JArray("id", "host", "description", "parameters", "executor"),
                ["additionalProperties"] = false
            }.ToString(Formatting.None);
        }

        private static string ToolUpsertSchema()
        {
            var schema = JObject.Parse(ToolPayloadSchema(true));
            ((JObject)schema["properties"])["mode"] = new JObject
            {
                ["type"] = "string",
                ["description"] = "Existence policy; upsert is normally sufficient.",
                ["enum"] = new JArray("upsert", "createOnly", "updateOnly"),
                ["default"] = "upsert"
            };
            return schema.ToString(Formatting.None);
        }

        private static JObject Property(string type, string description)
        {
            return new JObject { ["type"] = type, ["description"] = description };
        }

        private static JObject BoundedStringProperty(string description, int maxLength)
        {
            return new JObject
            {
                ["type"] = "string",
                ["description"] = description,
                ["maxLength"] = maxLength
            };
        }

        private static JObject ParametersProperty()
        {
            return new JObject
            {
                ["type"] = "object",
                ["description"] = "Strict object JSON Schema for the custom tool arguments.",
                ["properties"] = new JObject
                {
                    ["type"] = new JObject { ["type"] = "string", ["description"] = "Root schema type; must be object.", ["enum"] = new JArray("object") },
                    ["properties"] = Property("object", "Named argument schemas with types and useful descriptions."),
                    ["required"] = new JObject { ["type"] = "array", ["description"] = "Names of required arguments.", ["items"] = new JObject { ["type"] = "string" } },
                    ["additionalProperties"] = new JObject { ["type"] = "boolean", ["description"] = "Must be false." }
                },
                ["required"] = new JArray("type", "properties", "required", "additionalProperties")
            };
        }

        private static JObject PipelineProperty()
        {
            return new JObject
            {
                ["type"] = "object",
                ["description"] = "Pipeline definition with ordered calls to existing tools.",
                ["properties"] = new JObject
                {
                    ["version"] = new JObject { ["type"] = "integer", ["description"] = "Pipeline format version.", ["default"] = 1 },
                    ["steps"] = new JObject
                    {
                        ["type"] = "array",
                        ["description"] = "Ordered pipeline steps.",
                        ["minItems"] = 1,
                        ["maxItems"] = 50,
                        ["items"] = new JObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JObject
                            {
                                ["id"] = Property("string", "Unique step id used by result placeholders."),
                                ["toolId"] = Property("string", "Exact existing tool id."),
                                ["arguments"] = Property("object", "Arguments for the nested tool; placeholders may reference args or prior step results.")
                            },
                            ["required"] = new JArray("toolId"),
                            ["additionalProperties"] = false
                        }
                    }
                },
                ["required"] = new JArray("steps"),
                ["additionalProperties"] = false
            };
        }

        private static JObject EnumProperty(string description, params string[] values)
        {
            return new JObject
            {
                ["type"] = "string",
                ["description"] = description,
                ["enum"] = new JArray(values ?? new string[0])
            };
        }

        private static bool HasArgument(ToolCommand command, string name)
        {
            return command != null && command.Arguments != null && command.Arguments.ContainsKey(name);
        }

        private static bool HasMutableArguments(ToolCommand command)
        {
            return command != null && command.Arguments != null && command.Arguments.Keys.Any(name =>
                !string.Equals(name, "id", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, "mode", StringComparison.OrdinalIgnoreCase));
        }

        private static void SetString(ToolCommand command, string name, Action<string> apply)
        {
            if (HasArgument(command, name) && apply != null) apply(ToolArgumentReader.String(command.Arguments, name, string.Empty));
        }

        private static void SetBool(ToolCommand command, string name, Action<bool> apply)
        {
            if (HasArgument(command, name) && apply != null) apply(ReadBool(command, name, false));
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
            if (tool.Id.Length > 128)
            {
                return ToolResult.Fail("Tool id is too long (maximum 128 characters).", null, "tool_definition_too_large", false);
            }
            if (string.IsNullOrWhiteSpace(tool.Host))
            {
                return ToolResult.Fail("Tool host is required.");
            }
            if ((tool.Name ?? string.Empty).Length > 200 ||
                (tool.Description ?? string.Empty).Length > 8000 ||
                (tool.ArgumentSchemaJson ?? string.Empty).Length > 64000 ||
                (tool.PipelineJson ?? string.Empty).Length > 250000 ||
                (tool.Code ?? string.Empty).Length > 1000000 ||
                (tool.Readme ?? string.Empty).Length > 500000 ||
                (tool.UseWhen ?? string.Empty).Length > 4000 ||
                (tool.DoNotUseWhen ?? string.Empty).Length > 4000 ||
                (tool.Limitations ?? string.Empty).Length > 4000)
            {
                return ToolResult.Fail("Tool definition exceeds a supported text size limit.", null, "tool_definition_too_large", false);
            }
            var componentsForSize = (tool.Components ?? new List<VbaToolComponent>())
                .Where(component => component != null)
                .ToList();
            if (componentsForSize.Count > 50 ||
                componentsForSize.Any(component => (component.Code ?? string.Empty).Length > 1000000) ||
                componentsForSize.Sum(component => (long)(component.Code ?? string.Empty).Length) > 2000000)
            {
                return ToolResult.Fail("VBA package exceeds the supported component or source size limit.", null, "tool_definition_too_large", false);
            }
            if (!new[] { "Common", "Excel", "Word", "PowerPoint", "Outlook" }
                .Any(host => string.Equals(host, tool.Host, StringComparison.OrdinalIgnoreCase)))
            {
                return ToolResult.Fail("Unsupported tool host: " + tool.Host + ".", null, "invalid_tool_host", false);
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
            if (!ToolSchemaSupport.TryParse(tool, out normalizedSchema, out schemaError))
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
                if ((tool.Name ?? string.Empty).Length > 200 ||
                    (tool.Description ?? string.Empty).Length > 8000 ||
                    (tool.ArgumentSchemaJson ?? string.Empty).Length > 64000)
                {
                    return ToolResult.Fail("VBA manifest metadata exceeds a supported size limit.", null, "tool_definition_too_large", false);
                }
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
                    !VbaToolManifestParser.ValidComponentName(component.Name) ||
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
