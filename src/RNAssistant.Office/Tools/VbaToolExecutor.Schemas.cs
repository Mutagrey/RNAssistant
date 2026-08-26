using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Office.Tools
{
    internal sealed partial class VbaToolExecutor
    {
        private static string ModuleNameSchema()
        {
            return "{\"type\":\"object\",\"properties\":{" +
                "\"moduleName\":{\"type\":\"string\",\"description\":\"VBA component name. Case and safely normalizable punctuation/length are resolved by runtime.\",\"minLength\":1,\"maxLength\":255}" +
                "},\"required\":[\"moduleName\"],\"additionalProperties\":false}";
        }

        private static string ReadModuleSchema()
        {
            var properties = new JObject
            {
                ["moduleName"] = new JObject { ["type"] = "string", ["description"] = "Exact VBA component name. Omit all arguments to list component metadata.", ["minLength"] = 1, ["maxLength"] = 255 },
                ["startLine"] = new JObject { ["type"] = "integer", ["description"] = "One-based first line for range mode; supplied alone it returns up to 200 lines.", ["minimum"] = 1 },
                ["lineCount"] = new JObject { ["type"] = "integer", ["description"] = "Maximum consecutive lines in range mode; supplied alone the range starts at line 1.", ["minimum"] = 1, ["maximum"] = 500 },
                ["maxChars"] = new JObject { ["type"] = "integer", ["description"] = "Maximum source characters in whole-module mode.", ["default"] = 30000, ["minimum"] = 1, ["maximum"] = 1000000 }
            };
            Func<IEnumerable<string>, IEnumerable<string>, JObject> variant = (allowed, required) =>
            {
                var selected = new JObject();
                foreach (var name in allowed) selected[name] = properties[name].DeepClone();
                return new JObject
                {
                    ["type"] = "object",
                    ["properties"] = selected,
                    ["required"] = new JArray(required),
                    ["additionalProperties"] = false
                };
            };
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JArray(),
                ["additionalProperties"] = false,
                ["anyOf"] = new JArray
                {
                    variant(new string[0], new string[0]),
                    variant(new[] { "moduleName", "maxChars" }, new[] { "moduleName" }),
                    variant(new[] { "moduleName", "startLine", "lineCount" }, new[] { "moduleName", "startLine" }),
                    variant(new[] { "moduleName", "lineCount" }, new[] { "moduleName", "lineCount" })
                }
            }.ToString(Formatting.None);
        }

        private static string WriteModuleSchema()
        {
            return "{\"type\":\"object\",\"properties\":{" +
                "\"moduleName\":{\"type\":\"string\",\"description\":\"Exact target component name, not a rename destination. Invalid punctuation, a non-letter prefix, and names over the VBE limit of 31 characters are normalized deterministically only when creating; the result returns the actual name.\",\"minLength\":1,\"maxLength\":255}," +
                "\"code\":{\"type\":\"string\",\"description\":\"Complete VBA source or MSForm code-behind, never source reconstructed from a truncated read or partial context. Empty text intentionally clears an existing component or creates an empty one.\"}," +
                "\"componentType\":{\"type\":\"string\",\"description\":\"Type used only if the component must be created.\",\"default\":\"StdModule\",\"enum\":[\"StdModule\",\"ClassModule\",\"MSForm\"]}," +
                "\"mode\":{\"type\":\"string\",\"description\":\"upsert updates or creates automatically; createOnly/updateOnly are optional strict modes.\",\"default\":\"upsert\",\"enum\":[\"upsert\",\"createOnly\",\"updateOnly\"]}" +
                "},\"required\":[\"moduleName\",\"code\"],\"additionalProperties\":false}";
        }

        private static string RestoreBackupSchema()
        {
            Func<JObject> properties = () => new JObject
            {
                ["backupId"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Exact rollback backup identifier from common.vba_list_backups.",
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
                "Replace one exact unique current source block. Missing or ambiguous source is rejected without writing.",
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
