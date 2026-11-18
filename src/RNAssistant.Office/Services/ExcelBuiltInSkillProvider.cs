using System.Collections.Generic;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    // Shared with the fake host so model evaluations use the shipped instructions.
    public static class ExcelBuiltInSkillProvider
    {
        public static IEnumerable<SkillDefinition> GetSkills()
        {
            return new[]
            {
                new SkillDefinition
                {
                    Id = "skills.excel.analysis.reporting",
                    Host = "Excel",
                    Name = "Excel analysis reporting",
                    Description = "Analyze ranges, create summaries, tables, and charts in Excel.",
                    BodyMarkdown = "# Excel Analysis Reporting\n\nUse this skill for workbook analysis, tables, summaries, or charts. A report with source inspection, construction, and verification normally also uses `skills.task.tracking`. Exact loaded tool schemas remain authoritative for arguments.\n\n## Workflow\n\n- Inspect unknown existing content before mutation: use `excel.inspect` for workbook objects and `core.read` on an explicit `Excel range: Sheet!A1:B10` target for values (`text`/`table`), formulas (`formulas`), or profiling (`structure`). Preserve source formulas and do not overwrite an unknown range. For a clearly requested new report sheet, a redundant read is unnecessary; use `excel.inspect` with `kind=sheets` only when a name collision or existing-sheet dependency matters.\n- Define the source range, output location, headers, units, formulas, and intended chart before writing. For a new report, use the direct sequence `excel.sheet.add`, `excel.range.write` with `kind=table`, optional `excel.table.add` with explicit `sourceRange` / `excel.range.format`, then `excel.chart.upsert` with explicit `sourceRange`. Use stable headers and chart source ranges that include them.\n- Keep generated sheets and chart objects named clearly. Apply formatting after values exist, preserve number meaning, and avoid decorative formatting that hides errors or source provenance.\n- Use `excel.cells.find` / `excel.cells.replace` only for text search-and-replace; load `skills.text.search.replace` for its safeguards.\n\n## Definition of done\n\nFinish only when every requested range, table, formula, format, and chart has matching successful tool evidence, including mutation read-back where provided. Re-read any downstream range whose calculated values must be proven. Do not claim workbook calculation or visual quality beyond the available Excel evidence, and state when Windows Excel validation is still required.",
                    Enabled = true,
                    BuiltIn = true
                },
                new SkillDefinition
                {
                    Id = "skills.excel.powerquery",
                    Host = "Excel",
                    Name = "Excel Power Query",
                    Description = "Read, edit and create Power Query M definitions, load and refresh exact worksheet results, and verify requested data checks with explicit execution and coverage evidence.",
                    BodyMarkdown = "# Excel Power Query\n\n" +
                    "Use the loaded schemas as the exact argument contract. Keep three claims separate: M source saved, native refresh completed, requested data checks passed. No one of these proves the others.\n\n" +
                    "## Inspect and edit M\n\n" +
                    "- Find existing queries with `core.find` in document scope. Copy the returned `Excel query: <name>` target without changing its spelling. Read `source` for exact M. Read `metadata` for capabilities, `bindings` for exact connections and loaded table targets, and `dependencies` for the actual analysis coverage. Reads never refresh or evaluate M.\n" +
                    "- This slice supports exact names up to 255 characters, including Unicode and internal spaces. Boundary whitespace, control characters, slash/backslash and dot-only path names are unavailable. Discovery keeps supported queries and reports omitted names as incomplete coverage; do not infer absence, trim names, or rename another query to bypass this boundary.\n" +
                    "- Use `core.search` on the query target to locate source. Change existing M through `core.patch`; use `core.write` only with complete current source observation. Reuse still-current delivered source or verified after-state; stale/conflicting source requires a fresh relevant read. Do not fabricate runtime revisions, IDs, or URIs. M changes do not need VBProject access.\n" +
                    "- Create a new definition with `excel.query.create` and explicit name/source. A name conflict requires reading the existing definition; do not upsert, delete/recreate, or silently choose another name. Creating M does not create a load/connection. Queries may return tables, functions, scalars, records, lists or binary values; do not execute one to classify it.\n" +
                    "- `excel.query.validate` with `kind=source` runs the fixed offline Microsoft parser when available. Report syntax passed/failed/not_checked separately from name resolution, dependencies, source access and execution. Diagnostics refer to the captured source snapshot, with UTF-16 offsets and one-based line/column. If sourceMatchesCurrent is false/null, read current M before applying the finding. The parser stops at the first error and accepts workbook expressions, not section documents. Analysis is limited to 262144 UTF-16 characters; source read/edit has its own larger bound. Missing host, cancellation, limits or internal failure mean not_checked, never passed or a fabricated M error. Saving M and outer `try` do not prove successful execution or absence of lazy errors. Preserve parameters and exact M spelling; change only what the user asked for.\n\n" +
                    "## Load and refresh\n\n" +
                    "- Read `bindings` to choose the existing worksheet table. `excel.query.refresh` refreshes that exact load; provide `table` when there is more than one. Keep its worksheet, table and destination. Do not substitute RefreshAll, a loop over dependencies, a temporary preview load, or a connection chosen from its display-name prefix. Excel evaluates dependencies.\n" +
                    "- Use `excel.query.load` only for an explicitly intended new worksheet output, with sheet/address/table. `createSheet=true` requires a new sheet; otherwise the existing sheet must be entirely empty. Expansion size is unknown before execution. Preserve partial created objects reported on failure and inspect them; do not remove a sheet or overwrite an unrelated table as cleanup.\n" +
                    "- Refresh/load executes M and may contact configured sources independently of WebView2 network settings. Keep credentials, privacy levels and native SQL/source prompts in Excel. Do not lower firewall/privacy rules or edit connection credential stores. Mention an observed source/credential blocker when it occurs.\n" +
                    "- A bounded wait returning Unknown does not mean native execution stopped. Read the same query with `representation=status`; this reads RNAssistant's session observer and reports the timestamp/source of its observation. Do not run another refresh while the native operation owns the workbook. Do not alter arguments to bypass a replay guard. A completed observer fact allows the runtime to reconcile the exact prior operation; another authorized refresh is a new generation.\n" +
                    "- This build uses synchronous native worksheet refresh. Native cancellation, Data Model load, computed preview and global rename/delete are unavailable. A user Stop or closing the chat is not a native cancellation confirmation. Report the observed status and use Excel's own controls if native intervention is needed.\n\n" +
                    "## Verify data and report\n\n" +
                    "- Read the returned `Excel table: <name>` target through the common table/range reader. Separate saved old data from a successful refresh. Use `excel.query.validate` with `kind=loadedData` for exact required headings, expectedRows, nonEmptyColumns, composite keyColumns and native worksheet-cell errors. Full captures are bounded to 100000 cells; larger/unknown results remain metadata-only or `not_checked`, never a falsely complete sample. Numeric totals use `excel.columns.summarize` on an explicit returned sheet/range.\n" +
                    "- Choose checks from the user's requirement. For combining yearly tables, verify expected input years/headers, row counts and reconciliation totals, key uniqueness when required, empty values and cell errors. Preserve original sources and units. Reading a sample cannot prove whole-table uniqueness or totals. Native worksheet errors do not cover latent nested M values or unexecuted branches.\n" +
                    "- A fresh exact status read can establish that a native call stopped even while its outcome remains unresolved. That permits a new explicitly intended invocation under normal guards; it does not prove the earlier refresh succeeded or automatically repeat it. Confirmed workbook closure alone never releases an uncertain replay guard.\n" +
                    "- Report the exact query and output, source change, native completion, syntax and requested data checks with their scope. A completed native call does not prove business correctness or external-source freshness. Keep unavailable analysis, dependencies, model and Windows execution evidence explicit.",
                    Enabled = true,
                    BuiltIn = true
                }
            };
        }
    }
}
