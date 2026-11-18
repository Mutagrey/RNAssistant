using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.Tools
{
    internal static class ExcelPowerQueryToolCatalog
    {
        internal const string Create = "excel.query.create";
        internal const string Validate = "excel.query.validate";
        internal const string Refresh = "excel.query.refresh";
        internal const string Load = "excel.query.load";

        internal static bool Owns(string id)
        { return id == Create || id == Validate || id == Refresh || id == Load; }

        internal static ToolBinding BindingFor(string id)
        { if (!Owns(id)) throw new ArgumentException("Unknown Power Query tool.", nameof(id)); return new ToolBinding(id + ".v1"); }

        internal static IEnumerable<ToolCatalogEntry> Entries()
        {
            yield return Define(Create, "Create one Power Query M definition in the bound workbook. Existing names conflict; this never overwrites a query, creates a connection/table, or refreshes data. Read/edit existing M through core.read/patch/write with an Excel query target. Saving M is not syntax validation.",
                Schema(new JObject {
                    ["name"] = Text("Exact new query name.", 255),
                    ["source"] = Text("Complete Power Query M expression; never an Excel cell formula.", ExcelResourceProvider.MaximumQuerySourceCharacters),
                    ["description"] = Text("Optional description.", 4096, false)
                }, "name", "source"), true);
            yield return Define(Validate, "Validate without evaluating M. kind=source uses the fixed offline Microsoft parser when its local host is available: reports syntax passed/failed/not_checked, bounded diagnostics on the captured source snapshot and whether live M still matches. Syntax does not prove name resolution, dependencies, source access or successful execution; these remain not_checked. kind=loadedData checks the existing worksheet load only: requested headings, row count, null/empty cells, key uniqueness and native cell errors within a bounded complete capture. It does not refresh data or prove freshness or absence of latent M errors. For numeric totals use excel.columns.summarize on the returned exact range.",
                ValidationSchema(), false);
            yield return Define(Refresh, "Execute the exact existing worksheet load for a Power Query. Runtime pins M/native query/load bindings and waits for a bounded period; timeout means unknown, never cancellation. Continue with core.read on the same target, representation=status, to observe native completion. Read the returned Excel table target separately to verify data. No RefreshAll, Data Model, preview or connection-only execution fallback.",
                Schema(new JObject {
                    ["target"] = ResourceModelReadSchema.SemanticTarget(),
                    ["table"] = Text("Exact loaded table name from bindings; required when more than one load exists.", 255, false)
                }, "target"), true);
            yield return Define(Load, "Create a new worksheet load for an existing Power Query at an explicit cell and table name, then execute it. Destination must be a new sheet (createSheet=true) or an entirely empty existing sheet. Existing table names conflict. No overwrite, sheet deletion, automatic cleanup or Data Model load; partial state remains visible on failure. Runtime owns refresh lifetime and status.",
                Schema(new JObject {
                    ["target"] = ResourceModelReadSchema.SemanticTarget(),
                    ["sheet"] = Text("Exact destination worksheet name.", 31),
                    ["address"] = new JObject { ["type"] = "string", ["pattern"] = "^[A-Za-z]{1,3}[1-9][0-9]{0,6}$", ["maxLength"] = 10,
                        ["description"] = "Explicit top-left A1 cell; the empty sheet reserves expansion space." },
                    ["table"] = Text("New Excel table name; existing names conflict.", 255),
                    ["createSheet"] = new JObject { ["type"] = "boolean", ["default"] = false,
                        ["description"] = "Create exactly this new sheet. False requires an existing entirely empty sheet." }
                }, "target", "sheet", "address", "table"), true);
        }

        private static string ValidationSchema()
        {
            var properties = new JObject {
                ["target"] = ResourceModelReadSchema.SemanticTarget(),
                ["kind"] = new JObject { ["type"] = "string", ["enum"] = new JArray("source", "loadedData"), ["default"] = "source",
                    ["description"] = "Choose source analysis coverage or checks of existing loaded worksheet data." },
                ["table"] = Text("Exact existing worksheet load table; needed if the query has multiple loads.", 255, false),
                ["requiredColumns"] = Columns("Exact required column headings; comparison preserves case.", 64),
                ["nonEmptyColumns"] = Columns("Columns whose data values must be non-null and nonempty.", 64),
                ["keyColumns"] = Columns("Composite key columns that must be unique in the captured data.", 8),
                ["expectedRows"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 1048576,
                    ["description"] = "Expected data row count, excluding headers/totals." }
            };
            var result = JObject.Parse(Schema(properties, "target"));
            result["anyOf"] = new JArray(
                new JObject { ["type"] = "object", ["properties"] = new JObject {
                    ["target"] = properties["target"].DeepClone(),
                    ["kind"] = new JObject { ["type"] = "string", ["enum"] = new JArray("source"),
                        ["description"] = "Check captured M syntax with the offline parser when available; no M evaluation." } },
                    ["required"] = new JArray("target"), ["additionalProperties"] = false },
                new JObject { ["type"] = "object", ["properties"] = properties.DeepClone(),
                    ["required"] = new JArray("target", "kind"), ["additionalProperties"] = false });
            ((JObject)result["anyOf"][1]["properties"])["kind"] = new JObject { ["type"] = "string", ["enum"] = new JArray("loadedData"),
                ["description"] = "Check existing loaded worksheet data without refreshing it." };
            return result.ToString(Formatting.None);
        }

        private static JObject Columns(string description, int maximum)
        { return new JObject { ["type"] = "array", ["description"] = description, ["maxItems"] = maximum,
            ["items"] = Text("Exact heading.", 255) }; }

        private static JObject Text(string description, int maximum, bool nonempty = true)
        { var result = new JObject { ["type"] = "string", ["description"] = description, ["maxLength"] = maximum };
            if (nonempty) result["minLength"] = 1; return result; }

        private static string Schema(JObject properties, params string[] required)
        { return new JObject { ["type"] = "object", ["properties"] = properties, ["required"] = new JArray(required),
            ["additionalProperties"] = false }.ToString(Formatting.None); }

        private static ToolCatalogEntry Define(string id, string description, string schema, bool mutation)
        {
            return new ToolCatalogEntry { Id = id, Name = id, Host = "Excel", Description = description,
                ArgumentSchemaJson = schema, BuiltIn = true, Enabled = true, AgentCanRun = true,
                MutatesDocument = mutation, RiskLevel = mutation ? 2 : 0,
                Policy = new ToolPolicy(mutation ? ToolEffect.Write : ToolEffect.Read,
                    mutation ? ToolVerification.Tool : ToolVerification.None, false, !mutation, new[] { "agent" }, mutation ? 2 : 0,
                    executesCode: id == Refresh || id == Load),
                Binding = BindingFor(id) };
        }
    }
}
