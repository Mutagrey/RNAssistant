using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Core.Tools
{
    public static class ResourceMutationToolCatalog
    {
        public const string Write = "core.write", Patch = "core.patch", Replace = "core.replace";
        public const string Move = "resources.move", Copy = "resources.copy", Delete = "resources.delete", Restore = "resources.restore";
        public static bool Owns(string id) { return id == Write || id == Patch || id == Replace || id == Move || id == Copy || id == Delete || id == Restore; }
        public static ToolBinding BindingFor(string id) { return Owns(id) ? new ToolBinding(id + (id == Patch ? ".resource.v3" : ".resource.v1")) : null; }

        public static IEnumerable<ToolCatalogEntry> GetTools()
        {
            foreach (var id in new[] { Write, Patch, Replace, Move, Copy, Delete, Restore })
            {
                var properties = new JObject { ["target"] = String("Exact semantic resource target, or an explicit destination for creation. Workspace: relative path; Office: VBA module: Name, HTML file: path, or an exact discovered target.") };
                var required = new JArray("target");
                string description;
                if (id == Write)
                {
                    properties["content"] = String("Complete content. Existing source requires a complete current observation.", false);
                    properties["resourceType"] = new JObject { ["description"] = "Optional creation kind; VBA component type is resolved by the owner.", ["type"] = "string", ["enum"] = new JArray("text", "StdModule", "ClassModule", "MSForm") };
                    required.Add("content");
                    description = "Create missing text/source or replace completely observed existing source. Runtime pins existence and revision; concurrent changes fail without overwrite. Use core.patch for partial edits. VBA, files and authored sources share this contract.";
                }
                else if (id == Patch)
                {
                    properties.Remove("target"); required.Clear();
                    properties["patch"] = String("One or more sections: *** Update Target: <semantic target>, then @@ hunks. @@ <complete source line> is a distant text anchor; consecutive anchors are ordered. Prefix complete lines with -, + or a space. Optional *** End of File requires EOF. No file operations or line numbers. Example: *** Update Target: src/app.js\n@@ function next() {\n-  return 1;\n+  return 2;\n }\n");
                    properties["patch"]["maxLength"] = ResourcePatch.MaximumPatchCharacters;
                    required.Add("patch");
                    description = "Patch up to 16 related existing sources. All targets and original-snapshot hunks prepare before any write; aliases of the same target, ambiguity, overlaps or stale/unobserved source fail the package. Writes are sequential, with per-target results; stop on failure/unknown, never replay or roll back the package automatically. Dependent edits require separate calls with a read between them. Live VBA alone tolerates identifier case/token spacing, preserving literals/comments. Use core.write for creation/full overwrite and resources.* for lifecycle.";
                }
                else if (id == Replace)
                {
                    properties["query"] = String("The identical query from a completed core.search on this target.");
                    properties["replacement"] = String("Literal text, or .NET replacement groups in regex mode.", false);
                    properties["mode"] = new JObject { ["description"] = "Identical matching mode used for core.search.", ["type"] = "string", ["enum"] = new JArray("literal", "regex"), ["default"] = "literal" };
                    properties["matchCase"] = new JObject { ["description"] = "Identical case sensitivity used for core.search; default false.", ["type"] = "boolean" };
                    properties["expectedMatches"] = new JObject { ["description"] = "Exact completed search count, checked again before dispatch.", ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 10000 };
                    required.Add("query"); required.Add("replacement"); required.Add("expectedMatches");
                    description = "Replace all literal or line-oriented .NET regex matches in one resource, using the exact completed core.search snapshot and parameters. A changed source/count fails before writing. Zero-length replacement matches are rejected; use core.patch for insertion. This is text replacement, including matching comments/strings, not semantic rename.";
                }
                else if (id == Move || id == Copy)
                {
                    properties["destination"] = String("Explicit new semantic destination; never overwrite an existing target."); required.Add("destination");
                    description = id == Move ? "Move or rename an observed resource through its owner. Source and destination are verified; unsupported cross-owner moves fail explicitly." : "Create an independent copy of an observed source at a new destination. Never overwrite.";
                }
                else if (id == Restore)
                {
                    properties["version"] = new JObject { ["type"] = "integer", ["minimum"] = 1,
                        ["description"] = "Historical document version when the owner exposes numbered history. For a VBA backup use its exact discovered target; workspace files restore the latest managed deletion." };
                    description = "Restore a selected retained version through its owner. Runtime pins recovery source and current state before confirmation. Workspace files restore their latest managed deletion without overwriting an existing path.";
                }
                else description = id == Delete ? "Delete the observed exact resource through its owner, retaining managed recovery history. A changed source is a conflict." : "Restore a retained resource version through its owner without silently overwriting changed current state.";
                yield return new ToolCatalogEntry { Id = id, Name = id, Host = "Common", Description = description,
                    ArgumentSchemaJson = Object(properties, required).ToString(Formatting.None),
                    BuiltIn = true, Enabled = true, AgentCanRun = true, MutatesLocalState = true,
                    Policy = new ToolPolicy(ToolEffect.Write, ToolVerification.Tool, true, false, new[] { "agent" }, 1),
                    RequiresConfirmation = true, RiskLevel = 1, Binding = BindingFor(id), ContractVersion = "1" };
            }
        }

        private static JObject String(string description, bool nonempty = true)
        { var value = new JObject { ["type"] = "string", ["description"] = description }; if (nonempty) value["minLength"] = 1; return value; }
        private static JObject Object(JObject properties, JArray required)
        { return new JObject { ["type"] = "object", ["properties"] = properties, ["required"] = required, ["additionalProperties"] = false }; }
    }
}
