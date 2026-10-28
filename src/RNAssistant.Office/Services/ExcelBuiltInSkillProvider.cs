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
                }
            };
        }
    }
}
