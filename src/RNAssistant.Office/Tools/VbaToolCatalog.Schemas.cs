using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Office.Tools
{
    internal static partial class VbaToolCatalog
    {
        // Closed public schemas stay with the exact native catalog owner.
        private static string ModuleNameSchema()
        {
            return "{\"type\":\"object\",\"properties\":{" +
                "\"moduleName\":{\"type\":\"string\",\"description\":\"VBA component name. Case and safely normalizable punctuation/length are resolved by runtime.\",\"minLength\":1,\"maxLength\":255}" +
                "},\"required\":[\"moduleName\"],\"additionalProperties\":false}";
        }

        private static string RunMacroSchema()
        {
            return "{\"type\":\"object\",\"properties\":{" +
                "\"macroName\":{\"type\":\"string\",\"description\":\"Exact module and procedure name in the bound document. Any incoming document qualifier is replaced by runtime before Office Application.Run.\",\"minLength\":1,\"maxLength\":512}," +
                "\"arguments\":{\"type\":\"array\",\"description\":\"Optional positional String, integer, number, boolean, or null arguments in declaration order.\",\"items\":{\"type\":[\"string\",\"integer\",\"number\",\"boolean\",\"null\"]},\"maxItems\":30}}," +
                "\"required\":[\"macroName\"],\"additionalProperties\":false}";
        }

        private static string WriteModuleSchema()
        {
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["moduleName"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Component name from the VBA project inventory, such as Module1; not a procedure name, Module1.Run, or a resource target. New names are normalized deterministically; use the returned actual name.",
                        ["minLength"] = 1,
                        ["maxLength"] = 255
                    },
                    ["code"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Complete VBA source or MSForm code-behind, never source reconstructed from a truncated read or partial context. Empty text intentionally clears or creates an empty component. Final source must not contain duplicate Sub/Function or duplicate same Property accessor declarations."
                    },
                    ["componentType"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Type used only when creating a component.",
                        ["default"] = "StdModule",
                        ["enum"] = new JArray("StdModule", "ClassModule", "MSForm")
                    },
                    ["mode"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Use createOnly for a new component and updateOnly when replacing an existing component. upsert permits either outcome; use it only when either is intended.",
                        ["default"] = "upsert",
                        ["enum"] = new JArray("upsert", "createOnly", "updateOnly")
                    }
                },
                ["required"] = new JArray("moduleName", "code"),
                ["additionalProperties"] = false
            }.ToString(Formatting.None);
        }

        private static string RenameModuleSchema()
        {
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["moduleName"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Existing VBA component name.",
                        ["minLength"] = 1,
                        ["maxLength"] = 255
                    },
                    ["newModuleName"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Requested destination name. Runtime normalizes it deterministically and rejects collisions.",
                        ["minLength"] = 1,
                        ["maxLength"] = 255
                    }
                },
                ["required"] = new JArray("moduleName", "newModuleName"),
                ["additionalProperties"] = false
            }.ToString(Formatting.None);
        }

        private static string RestoreBackupSchema()
        {
            Func<JObject> properties = () => new JObject
            {
                ["target"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Exact readable VBA backup target copied verbatim from core.find with scope=backups. It never contains ://; do not construct it from a module name.",
                    ["minLength"] = 1
                },
                ["moduleName"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "VBA component whose latest available backup should be restored.",
                    ["minLength"] = 1,
                    ["maxLength"] = 255
                }
            };
            Func<string, JObject> variant = required => new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    [required] = properties()[required]
                },
                ["required"] = new JArray(required),
                ["additionalProperties"] = false
            };
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties(),
                ["required"] = new JArray(),
                ["additionalProperties"] = false,
                ["anyOf"] = new JArray(variant("target"), variant("moduleName"))
            }.ToString(Formatting.None);
        }

    }
}
