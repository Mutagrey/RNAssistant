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
                        ["description"] = "Existing or intended VBA component name. Invalid new names are normalized deterministically only when creating.",
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
                        ["description"] = "Write behavior: upsert updates or creates; createOnly/updateOnly guard existence.",
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
                    ["description"] = "Exact readable VBA backup target returned by common.resources_find with scope=backups.",
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

        private static string ApplyPatchSchema()
        {
            var find = new JObject
            {
                ["type"] = "string",
                ["description"] = "Exact current VBA source to replace, copied from a recent read of moduleName. If it repeats, keep find minimal and add exact contextBefore/contextAfter. LF and CRLF are accepted.",
                ["minLength"] = 1
            };
            var text = new JObject
            {
                ["type"] = "string",
                ["description"] = "Exact replacement text. Runtime does not trim boundary newlines; it only converts LF/CRLF to the module's current newline style. Empty text deletes find. For insertion, repeat find in text with the new block before or after it."
            };
            var exactReplace = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["find"] = find,
                    ["text"] = text,
                    ["contextBefore"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Optional exact source immediately before find, copied from the same current module read. Use it to disambiguate repeated find text; it is verified but not replaced."
                    },
                    ["contextAfter"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Optional exact source immediately after find, copied from the same current module read. Use it to disambiguate repeated find text; it is verified but not replaced."
                    }
                },
                ["required"] = new JArray("find", "text"),
                ["additionalProperties"] = false
            };
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["moduleName"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Existing VBA component name. Case and safely normalizable punctuation/length are resolved by runtime.",
                        ["minLength"] = 1,
                        ["maxLength"] = 255
                    },
                    ["patch"] = new JObject
                    {
                        ["type"] = "array",
                        ["description"] = "Native JSON array of ordered exact replacements applied in memory to one current module snapshot. Each later find sees earlier replacements. The final source must not contain duplicate procedure/property declarations. Never encode this array as a string.",
                        ["minItems"] = 1,
                        ["maxItems"] = 100,
                        ["items"] = exactReplace
                    }
                },
                ["required"] = new JArray("moduleName", "patch"),
                ["additionalProperties"] = false
            }.ToString(Formatting.None);
        }

    }
}
