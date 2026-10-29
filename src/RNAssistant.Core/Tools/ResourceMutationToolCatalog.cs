using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Core.Tools
{
    public static class ResourceMutationToolCatalog
    {
        public const string Write = "core.write", Patch = "core.patch", Replace = "core.replace";
        public const string Move = "resources.move", Copy = "resources.copy", Delete = "resources.delete", Restore = "resources.restore";
        public static bool Owns(string id) { return id == Write || id == Patch || id == Replace || id == Move || id == Copy || id == Delete || id == Restore; }
        public static ToolBinding BindingFor(string id) { return Owns(id) ? new ToolBinding(id + (id == Patch ? ".resource.v4" : ".resource.v1")) : null; }

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
                    description = "Create missing text/source or replace completely observed existing source. Runtime pins existence and revision; concurrent changes fail without overwrite. Use core.patch for partial edits. VBA, files and authored sources share this contract. " + RNAssistant.Core.Services.PlanSourceDocument.Instructions;
                }
                else if (id == Patch)
                {
                    properties.Remove("target"); required.Clear();
                    var lines = new JObject { ["type"] = "array", ["maxItems"] = 100, ["items"] = new JObject { ["type"] = "string", ["pattern"] = "^[^\\r\\n]*$" } };
                    var context = Object(new JObject { ["before"] = lines.DeepClone(), ["after"] = lines.DeepClone() }, new JArray());
                    var replace = Object(new JObject { ["op"] = new JObject { ["type"] = "string", ["enum"] = new JArray("replace") },
                        ["before"] = String("Exact nonempty substring or multiline fragment from delivered current source."),
                        ["after"] = String("Replacement text; empty deletes. Include intended line separators explicitly.", false),
                        ["context"] = context.DeepClone() }, new JArray("before", "after"));
                    var insert = Object(new JObject { ["op"] = new JObject { ["type"] = "string", ["enum"] = new JArray("insert") },
                        ["text"] = String("Inserted text. Include separators explicitly; none are added automatically."),
                        ["position"] = new JObject { ["type"] = "string", ["enum"] = new JArray("before", "after") },
                        ["anchor"] = String("Complete source line, excluding its EOL. before inserts at its start; after inserts after its EOL (or EOF).", false),
                        ["context"] = context.DeepClone() }, new JArray("op", "text", "position", "anchor"));
                    var boundary = Object(new JObject { ["op"] = new JObject { ["type"] = "string", ["enum"] = new JArray("insert") },
                        ["text"] = String("Inserted text, including intended separators."),
                        ["position"] = new JObject { ["type"] = "string", ["enum"] = new JArray("start", "end") },
                        ["context"] = context.DeepClone() }, new JArray("op", "text", "position"));
                    properties["targets"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = ResourcePatch.MaximumTargets,
                        ["items"] = Object(new JObject { ["target"] = String("Exact existing semantic target; never URI/revision/hash."),
                            ["edits"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 100,
                                ["items"] = new JObject { ["anyOf"] = new JArray(replace, insert, boundary) } } }, new JArray("target", "edits")) };
                    DescribePatchProperties(properties);
                    required.Add("targets");
                    description = "Patch existing sources with structured edits, at most 262144 JSON characters. Read current source first or use delivered verified after-source. " +
                        "Replace uses before/after (optional op=replace); insert uses op=insert, text, position and anchor for before/after. " +
                        "Context before/after arrays are ordered complete lines with gaps allowed, strictly outside the edit/anchor; they are not language scopes. " +
                        "Exactly one edit location is required; multiple anchor chains to it are allowed. Empty lines are literal anchors. " +
                        "LF/CRLF/CR match as logical separators; new separators use local EOL. Unaffected bytes and BOM stay unchanged. " +
                        "Start/end insertion requires complete delivered source. All edits match the same original snapshot; dependent edits need separate calls. " +
                        "VBA whole-line canonical matching is allowed only if exact matching fails; substrings remain exact. " +
                        "All targets prepare before writes. Aliases, overlaps, stale or unobserved source reject the package. Writes are sequential and stop on failure. " +
                        "Wait for TOOL_RESULT and inspect each target: Changed is verified; Unchanged made no edit; Rejected/NotStarted were not applied; Unknown needs inspection. " +
                        "Never replay/rollback the package automatically. Text read-back does not prove program logic. Use core.write for creation/overwrite and resources.* for lifecycle.";

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
                    Policy = new ToolPolicy(ToolEffect.Write, ToolVerification.Tool, true, false, (id == Write || id == Patch ? new[] { "agent", "plan" } : new[] { "agent" }), 1),
                    RequiresConfirmation = true, RiskLevel = 1, Binding = BindingFor(id), ContractVersion = id == Patch ? "2" : "1" };
            }
        }

        private static void DescribePatchProperties(JObject properties)
        {
            foreach (var property in properties.Properties())
            {
                var schema = (JObject)property.Value;
                if (schema["description"] == null)
                    schema["description"] = property.Name == "targets" ? "Existing sources and their structured edits." :
                        property.Name == "edits" ? "Edits against one original snapshot." :
                        property.Name == "context" ? "Optional ordered complete source lines surrounding the edit or insertion anchor." :
                        property.Name == "op" ? "Edit operation." : property.Name == "position" ? "Insertion boundary." :
                        "Ordered complete source lines, allowing gaps; empty strings are literal empty lines.";
                foreach (var child in schema.DescendantsAndSelf().OfType<JObject>().Where(o => o["properties"] is JObject).ToArray())
                    DescribePatchProperties((JObject)child["properties"]);
            }
        }

        private static JObject String(string description, bool nonempty = true)
        { var value = new JObject { ["type"] = "string", ["description"] = description }; if (nonempty) value["minLength"] = 1; return value; }
        private static JObject Object(JObject properties, JArray required)
        { return new JObject { ["type"] = "object", ["properties"] = properties, ["required"] = required, ["additionalProperties"] = false }; }
    }
}
