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
                    ["description"] = "Exact readable VBA backup target copied verbatim from common.resources_find with scope=backups. It never contains ://; do not construct it from a module name.",
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
                ["description"] = "Current VBA source block to replace, copied from current source or an exact source excerpt. A complete module read is not required for a focused patch. Exact matching runs first; if it fails, one unique complete-line token-equivalent block may match VBE spacing/case changes. Strings and comments remain significant. Use unchanged context or startLine/startColumn to select a repeated occurrence; explicit locations require exact text with no format-normalized search.",
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
                    ["startLine"] = new JObject
                    {
                        ["type"] = "integer", ["minimum"] = 1,
                        ["description"] = "Optional exact 1-based line where find starts in the current candidate module. Use with startColumn to select one repeated occurrence. Runtime checks find and any adjacent context at this position only; a mismatch never searches elsewhere. Later hunks use positions after preceding hunks."
                    },
                    ["startColumn"] = new JObject
                    {
                        ["type"] = "integer", ["minimum"] = 1,
                        ["description"] = "Optional 1-based UTF-16 column, including indentation, where find starts. Requires startLine; defaults to 1. Prefer locations returned by an ambiguous-patch diagnostic."
                    },
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
                        ["description"] = "Existing component name from the VBA project inventory, such as Module1; not a procedure name, Module1.Run, or the VBA module resource target.",
                        ["minLength"] = 1,
                        ["maxLength"] = 255
                    },
                    ["patch"] = new JObject
                    {
                        ["type"] = "array",
                        ["description"] = "Native JSON array of ordered exact replacements applied in memory to one current module snapshot. Each later find sees earlier replacements. The final source must not contain duplicate procedure/property declarations. Never encode this array as a string.",
                        ["minItems"] = 1,
                        ["maxItems"] = int.MaxValue,
                        ["items"] = exactReplace
                    }
                },
                ["required"] = new JArray("moduleName", "patch"),
                ["additionalProperties"] = false
            }.ToString(Formatting.None);
        }

    }
}
