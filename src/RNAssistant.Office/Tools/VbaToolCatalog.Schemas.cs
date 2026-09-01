using System;
using System.Collections.Generic;
using System.Linq;
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
                "\"macroName\":{\"type\":\"string\",\"description\":\"Exact macro name accepted by the current Office Application.Run, optionally qualified by document and module.\",\"minLength\":1,\"maxLength\":512}," +
                "\"arguments\":{\"type\":\"array\",\"description\":\"Optional positional String, integer, number, boolean, or null arguments in declaration order.\",\"items\":{\"type\":[\"string\",\"integer\",\"number\",\"boolean\",\"null\"]},\"maxItems\":30}}," +
                "\"required\":[\"macroName\"],\"additionalProperties\":false}";
        }

        private static string WriteModuleSchema()
        {
            var moduleName = new JObject
            {
                ["type"] = "string",
                ["description"] = "Existing or intended VBA component name. Invalid new names are normalized deterministically only when creating.",
                ["minLength"] = 1,
                ["maxLength"] = 255
            };
            var code = new JObject
            {
                ["type"] = "string",
                ["description"] = "Complete VBA source or MSForm code-behind, never source reconstructed from a truncated read or partial context. Empty text intentionally clears or creates an empty component."
            };
            var componentType = new JObject
            {
                ["type"] = "string",
                ["description"] = "Type used only when the write branch creates a component.",
                ["default"] = "StdModule",
                ["enum"] = new JArray("StdModule", "ClassModule", "MSForm")
            };
            var writeMode = new JObject
            {
                ["type"] = "string",
                ["description"] = "Write behavior: upsert updates or creates; createOnly/updateOnly guard existence.",
                ["default"] = "upsert",
                ["enum"] = new JArray("upsert", "createOnly", "updateOnly")
            };
            var newModuleName = new JObject
            {
                ["type"] = "string",
                ["description"] = "Requested destination name for mode=rename. Runtime normalizes it and rejects collisions.",
                ["minLength"] = 1,
                ["maxLength"] = 255
            };
            var renameMode = new JObject
            {
                ["type"] = "string",
                ["description"] = "Select the atomic rename branch; code and componentType are not accepted in this branch.",
                ["enum"] = new JArray("rename")
            };
            Func<JObject, string[], JObject> variant = (properties, required) => new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JArray(required),
                ["additionalProperties"] = false
            };
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["moduleName"] = moduleName,
                    ["code"] = code,
                    ["componentType"] = componentType,
                    ["mode"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Choose whole-source write semantics or the explicit rename branch.",
                        ["default"] = "upsert",
                        ["enum"] = new JArray("upsert", "createOnly", "updateOnly", "rename")
                    },
                    ["newModuleName"] = newModuleName
                },
                ["required"] = new JArray(),
                ["additionalProperties"] = false,
                ["anyOf"] = new JArray
                {
                    variant(new JObject
                    {
                        ["moduleName"] = moduleName.DeepClone(),
                        ["code"] = code.DeepClone(),
                        ["componentType"] = componentType.DeepClone(),
                        ["mode"] = writeMode
                    }, new[] { "moduleName", "code" }),
                    variant(new JObject
                    {
                        ["moduleName"] = moduleName.DeepClone(),
                        ["newModuleName"] = newModuleName.DeepClone(),
                        ["mode"] = renameMode
                    }, new[] { "moduleName", "newModuleName", "mode" })
                }
            }.ToString(Formatting.None);
        }

        private static string RestoreBackupSchema()
        {
            Func<JObject> properties = () => new JObject
            {
                ["backupId"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Exact rollback backup identifier from provider vba, kind vba-backup resource metadata.",
                    ["minLength"] = 1
                },
                ["moduleName"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "VBA component whose latest backup is selected when backupId is omitted.",
                    ["minLength"] = 1
                }
            };
            Func<string, JObject> variant = required => new JObject
            {
                ["type"] = "object",
                ["properties"] = properties(),
                ["required"] = new JArray(required),
                ["additionalProperties"] = false
            };
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties(),
                ["required"] = new JArray(),
                ["additionalProperties"] = false,
                ["anyOf"] = new JArray(variant("backupId"), variant("moduleName"))
            }.ToString(Formatting.None);
        }

        private static string ApplyPatchSchema()
        {
            var find = new JObject
            {
                ["type"] = "string",
                ["description"] = "Exact current VBA source block copied from a recent bounded read. Include enough unchanged surrounding source for exactly one match. LF and CRLF are accepted.",
                ["minLength"] = 1
            };
            var text = new JObject
            {
                ["type"] = "string",
                ["description"] = "Exact replacement text. Runtime does not trim boundary newlines; it only converts LF/CRLF to the module's current newline style. Empty text deletes find. For insertion, repeat find in text with the new block before or after it."
            };
            var exactReplace = PatchOperationSchema(
                "replace",
                "Replace one exact unique current source block. Missing or ambiguous source is rejected without writing; an already-satisfied identical replacement is skipped.",
                new JObject { ["find"] = find, ["text"] = text },
                "find",
                "text");
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
                        ["description"] = "Native JSON array of ordered exact replacements applied in memory to one current module snapshot. Each later find sees earlier replacements. Never encode this array as a string.",
                        ["minItems"] = 1,
                        ["maxItems"] = 100,
                        ["items"] = exactReplace
                    }
                },
                ["required"] = new JArray("moduleName", "patch"),
                ["additionalProperties"] = false
            }.ToString(Formatting.None);
        }

        private static JObject PatchOperationSchema(string operation, string description, JObject properties, params string[] required)
        {
            properties = properties ?? new JObject();
            properties.AddFirst(new JProperty("op", new JObject
            {
                ["type"] = "string",
                ["description"] = description,
                ["enum"] = new JArray(operation)
            }));
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JArray(new[] { "op" }.Concat(required ?? new string[0])),
                ["additionalProperties"] = false
            };
        }

    }
}
