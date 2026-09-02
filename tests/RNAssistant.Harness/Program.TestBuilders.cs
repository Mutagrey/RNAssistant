using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Storage;
using RNAssistant.Office;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using RNAssistant.Office.WebView;
using RNAssistant.Desktop;
using RNAssistant.OfficeHosts;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private const string EmptyFormalToolSchema = "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}";
        private const string SheetFormalToolSchema = "{\"type\":\"object\",\"properties\":{\"sheet\":{\"type\":\"string\",\"description\":\"Worksheet name.\"}},\"required\":[\"sheet\"],\"additionalProperties\":false}";

        private static ToolCatalogEntry CustomTool(string host, string id, string name = null)
        {
            var manifest = new
            {
                protocolVersion = 1, id, host, name = name ?? id, description = "Test custom tool.",
                packageVersion = "1.0.0", entryPoint = "Run", components = new[] { "RNA_Test" },
                argumentOrder = new string[0], parameters = Newtonsoft.Json.Linq.JObject.Parse(EmptyFormalToolSchema),
                mutatesDocument = true, agentCanRun = false, requiresConfirmation = true
            };
            var code = "Option Explicit\n' <RNAssistantTool>\n' " + JsonConvert.SerializeObject(manifest) +
                "\n' </RNAssistantTool>\nPublic Function Run() As String\n    Run = \"ok\"\nEnd Function";
            var parsed = new VbaToolManifestParser().Parse(code);
            AssertTrue(parsed.Success, "custom VBA fixture manifest: " + parsed.ErrorMessage);
            return parsed.Tool;
        }

        private static Newtonsoft.Json.Linq.JArray ToolComponentsPayload(ToolCatalogEntry tool)
        {
            return new Newtonsoft.Json.Linq.JArray(tool.Components.Select(component =>
                new Newtonsoft.Json.Linq.JObject
                {
                    ["name"] = component.Name, ["type"] = component.Type, ["code"] = component.Code
                }));
        }

        private static ToolCatalogEntry CustomToolWithParameter(
            string id, string parameterName, string description)
        {
            var schema = new Newtonsoft.Json.Linq.JObject
            {
                ["type"] = "object",
                ["properties"] = new Newtonsoft.Json.Linq.JObject
                {
                    [parameterName] = new Newtonsoft.Json.Linq.JObject
                    {
                        ["type"] = "string",
                        ["description"] = description
                    }
                },
                ["required"] = new Newtonsoft.Json.Linq.JArray(parameterName),
                ["additionalProperties"] = false
            };
            var manifest = new
            {
                protocolVersion = 1,
                id,
                host = "Excel",
                name = id,
                description = "Test custom tool.",
                packageVersion = "1.0.0",
                entryPoint = "Run",
                components = new[] { "RNA_Test" },
                argumentOrder = new[] { parameterName },
                parameters = schema,
                mutatesDocument = false,
                agentCanRun = true,
                requiresConfirmation = false
            };
            var code = "Option Explicit\n' <RNAssistantTool>\n' " +
                JsonConvert.SerializeObject(manifest) +
                "\n' </RNAssistantTool>\nPublic Function Run(ByVal " +
                parameterName + " As String) As String\n    Run = \"ok\"\nEnd Function";
            var parsed = new VbaToolManifestParser().Parse(code);
            AssertTrue(parsed.Success,
                "custom parameter fixture manifest: " + parsed.ErrorMessage);
            return parsed.Tool;
        }

        private static bool HasTool(IEnumerable<ToolCatalogEntry> tools, string id)
        {
            foreach (var tool in tools)
            {
                if (tool != null && string.Equals(tool.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasSkill(IEnumerable<SkillDefinition> skills, string id)
        {
            foreach (var skill in skills)
            {
                if (skill != null && string.Equals(skill.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static ToolCatalogEntry FindTool(IEnumerable<ToolCatalogEntry> tools, string id)
        {
            foreach (var tool in tools ?? new ToolCatalogEntry[0])
            {
                if (tool != null && string.Equals(tool.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return tool;
                }
            }

            return null;
        }

        private static ToolInvocation Command(string id, params object[] keyValues)
        {
            var command = new ToolInvocation { ToolId = id };
            for (var i = 0; i + 1 < (keyValues == null ? 0 : keyValues.Length); i += 2)
            {
                command.Arguments[Convert.ToString(keyValues[i])] = keyValues[i + 1];
            }

            return command;
        }

        private static string LoadToolSchemaResponse(string toolId)
        {
            return JsonConvert.SerializeObject(new
            {
                message = "Загружаю схему инструмента.",
                tool_calls = new[]
                {
                    new
                    {
                        name = CapabilityToolCatalog.ReadToolId,
                        arguments = new { id = toolId }
                    }
                }
            });
        }

        private static ChatSession NewSession(FakeOfficeAdapter adapter)
        {
            return new ChatSession
            {
                Host = adapter.HostName,
                DocumentKey = adapter.DocumentKey,
                DocumentTitle = adapter.DocumentTitle,
                Title = "New chat"
            };
        }

        private static DocumentContext NewContext(FakeOfficeAdapter adapter)
        {
            return new DocumentContext
            {
                Host = adapter.HostName,
                DocumentKey = adapter.DocumentKey,
                Title = adapter.DocumentTitle
            };
        }
    }
}
