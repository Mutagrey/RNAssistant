using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Domains.Excel;

namespace RNAssistant.Office.Tools
{
    public static class OfficeToolCatalog
    {
        public static IReadOnlyList<ToolCatalogEntry> ForHost(string host)
        {
            IEnumerable<ToolCatalogEntry> tools;
            if (string.Equals(host, "Excel", StringComparison.OrdinalIgnoreCase)) tools = ExcelTools();
            else if (string.Equals(host, "Word", StringComparison.OrdinalIgnoreCase)) tools = WordTools();
            else if (string.Equals(host, "PowerPoint", StringComparison.OrdinalIgnoreCase)) tools = PowerPointTools();
            else if (string.Equals(host, "Outlook", StringComparison.OrdinalIgnoreCase)) tools = OutlookTools();
            else tools = new ToolCatalogEntry[0];
            return tools.Select(HardenContract).Select(tool => tool.Clone()).ToArray();
        }

        private static IEnumerable<ToolCatalogEntry> ExcelTools()
        {
            return new[]
            {
                Define("Excel", "excel.inspect", "Read-only: Workbook/sheet/chart/table/name/shape metadata, not cell values. Not a write preflight: reuse the same selector until the workbook changes; excel.upsert_chart needs no prior inspection.", "{\"type\":\"object\",\"properties\":{\"kind\":{\"type\":\"string\",\"enum\":[\"workbook\",\"sheets\",\"charts\",\"tables\",\"names\",\"shapes\"],\"description\":\"Workbook information to return.\"},\"sheet\":{\"type\":\"string\",\"description\":\"Optional worksheet filter for charts, tables, or shapes.\"},\"chartName\":{\"type\":\"string\",\"description\":\"Optional exact chart name when kind is charts; omit to list chart summaries.\"}},\"required\":[\"kind\"],\"additionalProperties\":false}", canSourceHtmlData: true, independentLocalRead: true),
                Define("Excel", "excel.find_cells", "Read-only: Find literal or regex matches in cell values or formulas and return stable scope coordinates/hash.", "{\"type\":\"object\",\"properties\":{\"sheet\":{\"type\":\"string\",\"description\":\"Worksheet filter; provide it for sheet or range scope.\"},\"address\":{\"type\":\"string\",\"description\":\"A1 range; required when scope is range.\"},\"scope\":{\"type\":\"string\",\"description\":\"Search or operation scope supported by the tool.\",\"enum\":[\"workbook\",\"sheet\",\"range\",\"selection\"]},\"query\":{\"type\":\"string\",\"description\":\"Non-empty literal or regular-expression search query.\",\"minLength\":1},\"mode\":{\"type\":\"string\",\"description\":\"Text matching mode: literal or regex.\",\"default\":\"literal\",\"enum\":[\"literal\",\"regex\"]},\"matchCase\":{\"type\":\"boolean\",\"description\":\"Whether matching is case-sensitive.\",\"default\":false},\"wholeWord\":{\"type\":\"boolean\",\"description\":\"Whether only whole-word matches are accepted.\",\"default\":false},\"lookIn\":{\"type\":\"string\",\"description\":\"Cell content to inspect: values or formulas.\",\"default\":\"values\",\"enum\":[\"values\",\"formulas\",\"both\"]},\"maxResults\":{\"type\":\"integer\",\"description\":\"Maximum number of matches returned.\",\"default\":50},\"contextChars\":{\"type\":\"integer\",\"description\":\"Maximum context characters returned around each match.\",\"default\":80}},\"required\":[\"query\"],\"additionalProperties\":false}", independentLocalRead: true),
                Define("Excel", "excel.create_chat_chart", "Read-only: Create an interactive chart artifact in chat from a selection or range.", "{\"type\":\"object\",\"properties\":{\"sheet\":{\"type\":\"string\",\"description\":\"Worksheet containing address; omit when charting the current selection.\"},\"address\":{\"type\":\"string\",\"description\":\"A1 range to chart; omit to use the current selection.\"},\"chartType\":{\"type\":\"string\",\"description\":\"Chart type supported by the current host; use auto when available.\",\"default\":\"auto\"},\"title\":{\"type\":\"string\",\"description\":\"Human-readable title.\",\"default\":\"Excel chart\"}},\"required\":[],\"additionalProperties\":false}", independentLocalRead: true),
                Define("Excel", "excel.replace_cells", "Mutates document: Replace bounded literal or regex matches in the current scope. A separate search is optional; runtime reads the scope immediately before mutation and verifies the result.", "{\"type\":\"object\",\"properties\":{\"sheet\":{\"type\":\"string\",\"description\":\"Worksheet filter; provide it for sheet or range scope.\"},\"address\":{\"type\":\"string\",\"description\":\"A1 range; required when scope is range.\"},\"scope\":{\"type\":\"string\",\"description\":\"Search or operation scope supported by the tool.\",\"default\":\"range\",\"enum\":[\"workbook\",\"sheet\",\"range\",\"selection\"]},\"find\":{\"type\":\"string\",\"description\":\"Literal or regular-expression text to find.\",\"minLength\":1},\"replace\":{\"type\":\"string\",\"description\":\"Replacement text; regex capture groups are allowed only in regex mode.\"},\"mode\":{\"type\":\"string\",\"description\":\"Text matching mode: literal or regex.\",\"default\":\"literal\",\"enum\":[\"literal\",\"regex\"]},\"matchCase\":{\"type\":\"boolean\",\"description\":\"Whether matching is case-sensitive.\",\"default\":false},\"wholeWord\":{\"type\":\"boolean\",\"description\":\"Whether only whole-word matches are accepted.\",\"default\":false},\"lookIn\":{\"type\":\"string\",\"description\":\"Cell content to inspect: values or formulas.\",\"default\":\"values\",\"enum\":[\"values\",\"formulas\"]},\"replaceAll\":{\"type\":\"boolean\",\"description\":\"Whether all matches in scope may be replaced.\",\"default\":true},\"maxReplacements\":{\"type\":\"integer\",\"description\":\"Safety limit for replacements.\",\"default\":500}},\"required\":[\"find\"],\"additionalProperties\":false}", true, true, 2, true,
                    verification: ToolVerification.Tool),
                Define("Excel", "excel.write_range", "Mutates document: Write one scalar value, one formula, or a 2D table to a worksheet range.", "{\"type\":\"object\",\"properties\":{\"kind\":{\"type\":\"string\",\"enum\":[\"value\",\"formula\",\"table\"],\"description\":\"Write mode.\"},\"sheet\":{\"type\":\"string\",\"description\":\"Worksheet name; omit only when the active sheet is intended.\"},\"address\":{\"type\":\"string\",\"description\":\"Target A1 range or top-left cell.\",\"default\":\"A1\"},\"value\":{\"type\":[\"string\",\"number\",\"boolean\",\"null\"],\"description\":\"Scalar value when kind is value.\"},\"formula\":{\"type\":\"string\",\"description\":\"Excel formula including the leading equals sign when kind is formula.\"},\"values\":{\"type\":\"array\",\"items\":{\"type\":\"array\",\"items\":{\"type\":[\"string\",\"number\",\"boolean\",\"null\"]}},\"description\":\"Two-dimensional row array when kind is table.\"}},\"required\":[\"kind\"],\"additionalProperties\":false}", true, true, 2,
                    verification: ToolVerification.Tool),
                Define("Excel", "excel.add_table", "Mutates document: Convert a source range into an Excel table.", "{\"type\":\"object\",\"properties\":{\"sheet\":{\"type\":\"string\",\"description\":\"Worksheet name; omit only when the active sheet is intended.\"},\"sourceRange\":{\"type\":\"string\",\"description\":\"A1 range containing the source data.\",\"default\":\"A1:B2\"},\"name\":{\"type\":\"string\",\"description\":\"Human-readable name or exact saved item name, as required by the tool.\"},\"hasHeaders\":{\"type\":\"boolean\",\"description\":\"Whether the first row contains headers.\",\"default\":true},\"style\":{\"type\":\"string\",\"description\":\"Built-in style name supported by the host.\"}},\"required\":[],\"additionalProperties\":false}", true, true, 2,
                    verification: ToolVerification.Tool),
                Define("Excel", "excel.upsert_chart", "Mutates document: Update a named existing chart or create it when missing. Omitted fields are preserved on update; creation uses runtime defaults. Use strict mode only when existence matters.", "{\"type\":\"object\",\"properties\":{\"mode\":{\"type\":\"string\",\"enum\":[\"upsert\",\"createOnly\",\"updateOnly\"],\"description\":\"Existence policy; upsert normally avoids a separate chart lookup.\",\"default\":\"upsert\"},\"sheet\":{\"type\":\"string\",\"description\":\"Worksheet name; omit only when the active sheet is intended for creation or when searching all sheets by chartName.\"},\"chartName\":{\"type\":\"string\",\"description\":\"Exact chart name. Omit to create a chart with Excel's generated name.\"},\"sourceRange\":{\"type\":\"string\",\"description\":\"A1 source range; creation defaults to A1:B6.\"},\"chartType\":{\"type\":\"string\",\"description\":\"Chart type; creation defaults to line.\"},\"title\":{\"type\":\"string\",\"description\":\"Chart title; creation defaults to Chart. Empty text removes an existing title.\"},\"categoryLabelsRange\":{\"type\":\"string\",\"description\":\"A1 range used for chart category labels.\"},\"xAxisTitle\":{\"type\":\"string\",\"description\":\"Horizontal axis title.\"},\"yAxisTitle\":{\"type\":\"string\",\"description\":\"Vertical axis title.\"},\"left\":{\"type\":\"integer\",\"description\":\"Horizontal position in points; creation defaults to 300.\"},\"top\":{\"type\":\"integer\",\"description\":\"Vertical position in points; creation defaults to 20.\"},\"width\":{\"type\":\"integer\",\"description\":\"Width in points; creation defaults to 480.\"},\"height\":{\"type\":\"integer\",\"description\":\"Height in points; creation defaults to 300.\"}},\"required\":[],\"additionalProperties\":false}", true, true, 2,
                    verification: ToolVerification.Tool),
                Define("Excel", "excel.delete_chart", "Mutates document: Delete one existing worksheet chart by name.", "{\"type\":\"object\",\"properties\":{\"sheet\":{\"type\":\"string\",\"description\":\"Optional worksheet filter; omit to find the named chart across the workbook.\"},\"chartName\":{\"type\":\"string\",\"description\":\"Exact worksheet chart object name.\"}},\"required\":[\"chartName\"],\"additionalProperties\":false}", true, true, 3, true,
                    verification: ToolVerification.Tool),
                Define("Excel", "excel.format_range", "Mutates document: Apply number/font/fill/alignment formatting and optionally autofit rows or columns in one call.", "{\"type\":\"object\",\"properties\":{\"sheet\":{\"type\":\"string\",\"description\":\"Worksheet name; omit only when the active sheet is intended.\"},\"address\":{\"type\":\"string\",\"description\":\"Optional A1 range. Formatting defaults to A1; an autofit-only call defaults to the used range.\"},\"numberFormat\":{\"type\":\"string\",\"description\":\"Excel number-format code.\"},\"bold\":{\"type\":\"boolean\",\"description\":\"Whether bold formatting is enabled.\"},\"italic\":{\"type\":\"boolean\",\"description\":\"Whether italic formatting is enabled.\"},\"fillColor\":{\"type\":\"string\",\"description\":\"Fill color as a hex value such as #FFF2CC.\"},\"fontColor\":{\"type\":\"string\",\"description\":\"Font color as a hex value such as #1F1F1F.\"},\"horizontalAlignment\":{\"type\":\"string\",\"description\":\"Horizontal alignment: left, center, right, or general.\",\"enum\":[\"left\",\"center\",\"right\",\"general\"]},\"autoFit\":{\"type\":\"string\",\"description\":\"Optional autofit operation.\",\"enum\":[\"columns\",\"rows\",\"both\"]}},\"required\":[],\"additionalProperties\":false}", true, true, 1,
                    verification: ToolVerification.Tool),
                Define("Excel", "excel.add_sheet", "Mutates document: Add a new worksheet.", "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"description\":\"Human-readable name or exact saved item name, as required by the tool.\",\"default\":\"AI Sheet\"}},\"required\":[],\"additionalProperties\":false}", true, true, 1,
                    verification: ToolVerification.Tool),
                Define("Excel", "excel.rename_sheet", "Mutates document: Rename a worksheet.", "{\"type\":\"object\",\"properties\":{\"sheet\":{\"type\":\"string\",\"description\":\"Worksheet name; omit only when the active sheet is intended.\"},\"newName\":{\"type\":\"string\",\"description\":\"New exact name.\"}},\"required\":[\"newName\"],\"additionalProperties\":false}", true, true, 2, true,
                    verification: ToolVerification.Tool),
                Define("Excel", "excel.clear_range", "Mutates document: Clear cell values, formats, or both in a range.", "{\"type\":\"object\",\"properties\":{\"sheet\":{\"type\":\"string\",\"description\":\"Worksheet name; omit only when the active sheet is intended.\"},\"address\":{\"type\":\"string\",\"description\":\"A1-style range address.\"},\"clearWhat\":{\"type\":\"string\",\"description\":\"Content to clear: values, formats, or all.\",\"default\":\"values\",\"enum\":[\"values\",\"formats\",\"all\"]}},\"required\":[\"address\"],\"additionalProperties\":false}", true, true, 3, true,
                    verification: ToolVerification.Tool),
                Define("Excel", "excel.sort_range", "Mutates document: Sort rows in a range by one key column.", "{\"type\":\"object\",\"properties\":{\"sheet\":{\"type\":\"string\",\"description\":\"Worksheet name; omit only when the active sheet is intended.\"},\"address\":{\"type\":\"string\",\"description\":\"A1-style range address.\"},\"keyColumn\":{\"type\":\"integer\",\"description\":\"One-based sort-key column index within the range.\",\"default\":1},\"descending\":{\"type\":\"boolean\",\"description\":\"Whether to sort in descending order.\",\"default\":false},\"hasHeaders\":{\"type\":\"boolean\",\"description\":\"Whether the first row contains headers.\",\"default\":true}},\"required\":[\"address\"],\"additionalProperties\":false}", true, true, 2, true,
                    verification: ToolVerification.Tool),
                Define("Excel", "excel.filter_range", "Mutates document: Apply AutoFilter criteria to a range.", "{\"type\":\"object\",\"properties\":{\"sheet\":{\"type\":\"string\",\"description\":\"Worksheet name; omit only when the active sheet is intended.\"},\"address\":{\"type\":\"string\",\"description\":\"A1-style range address.\"},\"field\":{\"type\":\"integer\",\"description\":\"One-based column index within the filter range.\",\"default\":1},\"criteria\":{\"type\":\"string\",\"description\":\"AutoFilter criterion understood by Excel.\"}},\"required\":[\"address\"],\"additionalProperties\":false}", true, true, 2, true,
                    verification: ToolVerification.Tool)
            };
        }

        private static IEnumerable<ToolCatalogEntry> WordTools()
        {
            return new[]
            {
                Define("Word", "word.find_text", "Read-only: Find literal or regex text in an exact resource snapshot of Word stories. Returns story coordinates and bounded previews; runtime retains revision/evidence. Scope capture is limited to 256 stories and one million characters; narrow oversized scopes.", "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"description\":\"Non-empty literal or regular-expression search query.\",\"minLength\":1},\"scope\":{\"type\":\"string\",\"description\":\"Search or operation scope supported by the tool.\",\"default\":\"main\",\"enum\":[\"main\",\"selection\",\"all\"]},\"mode\":{\"type\":\"string\",\"description\":\"Text matching mode: literal or regex.\",\"default\":\"literal\",\"enum\":[\"literal\",\"regex\"]},\"matchCase\":{\"type\":\"boolean\",\"description\":\"Whether matching is case-sensitive.\",\"default\":false},\"wholeWord\":{\"type\":\"boolean\",\"description\":\"Whether only whole-word matches are accepted.\",\"default\":false},\"maxResults\":{\"type\":\"integer\",\"description\":\"Maximum number of matches returned.\",\"default\":50},\"contextChars\":{\"type\":\"integer\",\"description\":\"Maximum context characters returned around each match.\",\"default\":80}},\"required\":[\"query\"],\"additionalProperties\":false}", independentLocalRead: true),
                Define("Word", "word.inspect", "Read-only: Inspect headings, tables, comments, or document statistics with one selector.", "{\"type\":\"object\",\"properties\":{\"kind\":{\"type\":\"string\",\"enum\":[\"headings\",\"tables\",\"comments\",\"stats\"],\"description\":\"Document structure to return.\"},\"maxResults\":{\"type\":\"integer\",\"description\":\"Maximum heading count.\",\"default\":100},\"maxTables\":{\"type\":\"integer\",\"description\":\"Maximum table count.\",\"default\":20},\"maxRows\":{\"type\":\"integer\",\"description\":\"Maximum rows returned per table.\",\"default\":50}},\"required\":[\"kind\"],\"additionalProperties\":false}", canSourceHtmlData: true, independentLocalRead: true),
                Define("Word", "word.write_text", "Mutates document: Insert text, insert a paragraph, or replace the current selection.", "{\"type\":\"object\",\"properties\":{\"mode\":{\"type\":\"string\",\"enum\":[\"insert\",\"paragraph\",\"replaceSelection\"],\"description\":\"Text write operation.\"},\"text\":{\"type\":\"string\",\"description\":\"Complete text to insert or assign.\"},\"location\":{\"type\":\"string\",\"description\":\"For paragraph mode: selection, start, or end.\",\"default\":\"selection\",\"enum\":[\"selection\",\"start\",\"end\"]}},\"required\":[\"mode\",\"text\"],\"additionalProperties\":false}", true, true, 2, verification: ToolVerification.Tool),
                Define("Word", "word.replace_text", "Mutates document: Replace bounded literal or regex text in the current scope. A separate search is optional; runtime reads the scope immediately before mutation and verifies the result.", "{\"type\":\"object\",\"properties\":{\"find\":{\"type\":\"string\",\"description\":\"Literal or regular-expression text to find.\",\"minLength\":1},\"replace\":{\"type\":\"string\",\"description\":\"Replacement text; regex capture groups are allowed only in regex mode.\"},\"scope\":{\"type\":\"string\",\"description\":\"Search or operation scope supported by the tool.\",\"default\":\"main\",\"enum\":[\"main\",\"selection\",\"all\"]},\"mode\":{\"type\":\"string\",\"description\":\"Text matching mode: literal or regex.\",\"default\":\"literal\",\"enum\":[\"literal\",\"regex\"]},\"replaceAll\":{\"type\":\"boolean\",\"description\":\"Whether all matches in scope may be replaced.\",\"default\":true},\"matchCase\":{\"type\":\"boolean\",\"description\":\"Whether matching is case-sensitive.\",\"default\":false},\"wholeWord\":{\"type\":\"boolean\",\"description\":\"Whether only whole-word matches are accepted.\",\"default\":false},\"maxReplacements\":{\"type\":\"integer\",\"description\":\"Safety limit for replacements.\",\"default\":500}},\"required\":[\"find\"],\"additionalProperties\":false}", true, true, 2, true, verification: ToolVerification.Tool),
                Define("Word", "word.format_text", "Mutates document: Apply a named style or basic font formatting with one explicit kind selector.", "{\"type\":\"object\",\"properties\":{\"kind\":{\"type\":\"string\",\"enum\":[\"style\",\"font\"],\"description\":\"Formatting operation.\"},\"style\":{\"type\":\"string\",\"description\":\"Named Word style when kind is style.\"},\"target\":{\"type\":\"string\",\"description\":\"Style target; font formatting always targets the selection.\",\"default\":\"selection\",\"enum\":[\"selection\",\"document\"]},\"bold\":{\"type\":\"boolean\",\"description\":\"Whether bold formatting is enabled for kind=font.\"},\"italic\":{\"type\":\"boolean\",\"description\":\"Whether italic formatting is enabled for kind=font.\"},\"underline\":{\"type\":\"boolean\",\"description\":\"Whether underline formatting is enabled for kind=font.\"},\"fontSize\":{\"type\":\"integer\",\"description\":\"Font size in points for kind=font.\",\"minimum\":1},\"fontName\":{\"type\":\"string\",\"description\":\"Installed font family name for kind=font.\"}},\"required\":[\"kind\"],\"additionalProperties\":false}", true, true, 1, verification: ToolVerification.Tool),
                Define("Word", "word.add_table", "Mutates document: Insert a table at selection, start, or end. Runtime infers dimensions from values when rows/columns are omitted.", "{\"type\":\"object\",\"properties\":{\"rows\":{\"type\":\"integer\",\"description\":\"Optional table row count; omit to infer it from values or use 2 for an empty table.\",\"minimum\":1},\"columns\":{\"type\":\"integer\",\"description\":\"Optional table column count; omit to infer it from values or use 2 for an empty table.\",\"minimum\":1},\"values\":{\"type\":\"array\",\"items\":{\"type\":\"array\",\"items\":{\"type\":[\"string\",\"number\",\"boolean\",\"null\"]}},\"description\":\"Optional two-dimensional row array; dimensions are inferred when omitted.\"},\"location\":{\"type\":\"string\",\"description\":\"Insertion target supported by the tool.\",\"default\":\"selection\",\"enum\":[\"selection\",\"start\",\"end\"]}},\"required\":[],\"additionalProperties\":false}", true, true, 2, verification: ToolVerification.Tool),
                Define("Word", "word.insert_page_break", "Mutates document: Insert a page break at the current cursor position.", "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}", true, true, 1, verification: ToolVerification.Tool),
                Define("Word", "word.add_comment", "Mutates document: Add a comment to the current selection.", "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\",\"description\":\"Complete text to insert, replace, or assign.\"}},\"required\":[\"text\"],\"additionalProperties\":false}", true, true, 1, verification: ToolVerification.Tool)
            };
        }

        private static IEnumerable<ToolCatalogEntry> PowerPointTools()
        {
            return new[]
            {
                Define("PowerPoint", "powerpoint.list_objects", "Read-only: List slide summaries or shapes on one slide with one kind selector.", "{\"type\":\"object\",\"properties\":{\"kind\":{\"type\":\"string\",\"enum\":[\"slides\",\"shapes\"],\"description\":\"Objects to list.\"},\"slideIndex\":{\"type\":\"integer\",\"description\":\"Optional one-based slide for shapes; runtime uses the active slide when omitted.\",\"minimum\":1}},\"required\":[\"kind\"],\"additionalProperties\":false}", canSourceHtmlData: true, independentLocalRead: true),
                Define("PowerPoint", "powerpoint.search_text", "Read-only: Find literal or regex text in slide shapes and notes over exact retained resource snapshots with shape-local coordinates.", "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"description\":\"Non-empty literal or regular-expression search query.\",\"minLength\":1},\"scope\":{\"type\":\"string\",\"description\":\"Search or operation scope supported by the tool.\",\"default\":\"deck\",\"enum\":[\"deck\",\"slide\"]},\"slideIndex\":{\"type\":\"integer\",\"description\":\"One-based target slide when scope is slide; 0 searches the deck.\",\"default\":0},\"includeNotes\":{\"type\":\"boolean\",\"description\":\"Whether speaker notes are included.\",\"default\":true},\"mode\":{\"type\":\"string\",\"description\":\"Text matching mode: literal or regex.\",\"default\":\"literal\",\"enum\":[\"literal\",\"regex\"]},\"matchCase\":{\"type\":\"boolean\",\"description\":\"Whether matching is case-sensitive.\",\"default\":false},\"wholeWord\":{\"type\":\"boolean\",\"description\":\"Whether only whole-word matches are accepted.\",\"default\":false},\"maxResults\":{\"type\":\"integer\",\"description\":\"Maximum number of matches returned.\",\"default\":50},\"contextChars\":{\"type\":\"integer\",\"description\":\"Maximum context characters returned around each match.\",\"default\":80}},\"required\":[\"query\"],\"additionalProperties\":false}", independentLocalRead: true),
                Define("PowerPoint", "powerpoint.add_slide", "Mutates document: Add a text slide.", "{\"type\":\"object\",\"properties\":{\"title\":{\"type\":\"string\",\"description\":\"Human-readable title.\",\"default\":\"AI slide\"},\"body\":{\"type\":\"string\",\"description\":\"Body text for the item being created or updated.\"}},\"required\":[],\"additionalProperties\":false}", true, true, 1, verification: ToolVerification.Tool),
                Define("PowerPoint", "powerpoint.set_text", "Mutates document: Set speaker notes or shape text. The active slide/selected shape is resolved when exact coordinates are omitted.", "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\",\"enum\":[\"notes\",\"shape\"],\"description\":\"Text target.\"},\"slideIndex\":{\"type\":\"integer\",\"description\":\"Optional one-based slide index; runtime uses the active slide when omitted.\",\"minimum\":1},\"shapeName\":{\"type\":\"string\",\"description\":\"Exact shape name for target=shape; omit to use the selected shape.\"},\"text\":{\"type\":\"string\",\"description\":\"Complete replacement text.\"}},\"required\":[\"target\",\"text\"],\"additionalProperties\":false}", true, true, 2, verification: ToolVerification.Tool),
                Define("PowerPoint", "powerpoint.replace_text", "Mutates document: Replace bounded literal or regex text in the current scope. A separate search is optional; runtime reads the scope immediately before mutation and verifies the result.", "{\"type\":\"object\",\"properties\":{\"find\":{\"type\":\"string\",\"description\":\"Literal or regular-expression text to find.\",\"minLength\":1},\"replace\":{\"type\":\"string\",\"description\":\"Replacement text; regex capture groups are allowed only in regex mode.\"},\"scope\":{\"type\":\"string\",\"description\":\"Search or operation scope supported by the tool.\",\"default\":\"deck\",\"enum\":[\"deck\",\"slide\"]},\"slideIndex\":{\"type\":\"integer\",\"description\":\"One-based target slide when scope is slide; 0 searches the deck.\",\"default\":0},\"includeNotes\":{\"type\":\"boolean\",\"description\":\"Whether speaker notes are included.\",\"default\":true},\"mode\":{\"type\":\"string\",\"description\":\"Text matching mode: literal or regex.\",\"default\":\"literal\",\"enum\":[\"literal\",\"regex\"]},\"matchCase\":{\"type\":\"boolean\",\"description\":\"Whether matching is case-sensitive.\",\"default\":false},\"wholeWord\":{\"type\":\"boolean\",\"description\":\"Whether only whole-word matches are accepted.\",\"default\":false},\"replaceAll\":{\"type\":\"boolean\",\"description\":\"Whether all matches in scope may be replaced.\",\"default\":true},\"maxReplacements\":{\"type\":\"integer\",\"description\":\"Safety limit for replacements.\",\"default\":500}},\"required\":[\"find\"],\"additionalProperties\":false}", true, true, 2, true, verification: ToolVerification.Tool),
                Define("PowerPoint", "powerpoint.add_object", "Mutates document: Add a text box, picture, or table to the active slide (or an explicit slide) with one kind selector. Table dimensions are inferred from values when omitted.", "{\"type\":\"object\",\"properties\":{\"kind\":{\"type\":\"string\",\"enum\":[\"textBox\",\"picture\",\"table\"],\"description\":\"Object type to add.\"},\"slideIndex\":{\"type\":\"integer\",\"description\":\"Optional one-based slide index; runtime uses the active slide when omitted.\",\"minimum\":1},\"text\":{\"type\":\"string\",\"description\":\"Text for kind=textBox.\"},\"path\":{\"type\":\"string\",\"description\":\"Local image path for kind=picture.\"},\"rows\":{\"type\":\"integer\",\"description\":\"Optional table row count; omit to infer it from values or use 2 for an empty table.\",\"minimum\":1},\"columns\":{\"type\":\"integer\",\"description\":\"Optional table column count; omit to infer it from values or use 2 for an empty table.\",\"minimum\":1},\"values\":{\"type\":\"array\",\"items\":{\"type\":\"array\",\"items\":{\"type\":[\"string\",\"number\",\"boolean\",\"null\"]}},\"description\":\"Optional two-dimensional table values.\"},\"left\":{\"type\":\"integer\",\"description\":\"Optional horizontal position in points.\"},\"top\":{\"type\":\"integer\",\"description\":\"Optional vertical position in points.\"},\"width\":{\"type\":\"integer\",\"description\":\"Optional width in points; runtime chooses a kind-specific default.\"},\"height\":{\"type\":\"integer\",\"description\":\"Optional height in points; runtime chooses a kind-specific default.\"},\"fontSize\":{\"type\":\"integer\",\"description\":\"Optional font size for kind=textBox.\",\"minimum\":1}},\"required\":[\"kind\"],\"additionalProperties\":false}", true, true, 1, verification: ToolVerification.Tool),
                Define("PowerPoint", "powerpoint.duplicate_slide", "Mutates document: Duplicate one slide.", "{\"type\":\"object\",\"properties\":{\"slideIndex\":{\"type\":\"integer\",\"description\":\"One-based slide index.\"}},\"required\":[\"slideIndex\"],\"additionalProperties\":false}", true, true, 1, verification: ToolVerification.Tool),
                Define("PowerPoint", "powerpoint.move_slide", "Mutates document: Move a slide to a new position.", "{\"type\":\"object\",\"properties\":{\"slideIndex\":{\"type\":\"integer\",\"description\":\"One-based slide index.\"},\"toIndex\":{\"type\":\"integer\",\"description\":\"One-based destination slide index.\"}},\"required\":[\"slideIndex\",\"toIndex\"],\"additionalProperties\":false}", true, true, 2, true, verification: ToolVerification.Tool)
            };
        }

        private static IEnumerable<ToolCatalogEntry> OutlookTools()
        {
            return new[]
            {
                Define("Outlook", "outlook.search_mail", "Read-only: Search bounded exact mail-field snapshots. Returns semantic mail targets, field coordinates and snippets; use resources_read for complete bodies.", "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"description\":\"Non-empty literal or regular-expression search query.\",\"minLength\":1},\"mode\":{\"type\":\"string\",\"description\":\"Text matching mode: literal or regex.\",\"default\":\"literal\",\"enum\":[\"literal\",\"regex\"]},\"matchCase\":{\"type\":\"boolean\",\"description\":\"Whether matching is case-sensitive.\",\"default\":false},\"wholeWord\":{\"type\":\"boolean\",\"description\":\"Whether only whole-word matches are accepted.\",\"default\":false},\"fields\":{\"type\":\"string\",\"description\":\"Comma-separated mail fields to search: subject, sender, recipients, body.\",\"default\":\"subject,sender,body\"},\"maxItems\":{\"type\":\"integer\",\"description\":\"Maximum newest folder items inspected (1-500); reduce for large captures. Body scans are capped at 100000 characters per mail; sourceTruncated is explicit.\",\"default\":100},\"maxResults\":{\"type\":\"integer\",\"description\":\"Maximum number of matches returned.\",\"default\":50},\"contextChars\":{\"type\":\"integer\",\"description\":\"Maximum context characters returned around each match.\",\"default\":80}},\"required\":[\"query\"],\"additionalProperties\":false}", independentLocalRead: true),
                Define("Outlook", "outlook.create_draft", "Mutates document: Create and display a new, reply, reply-all, or forward draft without sending it.", "{\"type\":\"object\",\"properties\":{\"kind\":{\"type\":\"string\",\"enum\":[\"new\",\"reply\",\"replyAll\",\"forward\"],\"description\":\"Draft operation.\"},\"to\":{\"type\":\"string\",\"description\":\"Semicolon-separated primary recipients for new/forward drafts.\"},\"cc\":{\"type\":\"string\",\"description\":\"Semicolon-separated CC recipients for a new draft.\"},\"bcc\":{\"type\":\"string\",\"description\":\"Semicolon-separated BCC recipients for a new draft.\"},\"subject\":{\"type\":\"string\",\"description\":\"Subject for a new draft.\"},\"body\":{\"type\":\"string\",\"description\":\"Body text for the draft.\"}},\"required\":[\"kind\"],\"additionalProperties\":false}", true, true, 1, verification: ToolVerification.Tool),
                Define("Outlook", "outlook.update_mail", "Mutates document: Set categories or mark the selected mail as read with one explicit operation.", "{\"type\":\"object\",\"properties\":{\"kind\":{\"type\":\"string\",\"enum\":[\"categories\",\"markRead\"],\"description\":\"Mail update operation.\"},\"categories\":{\"type\":\"string\",\"description\":\"Comma-separated categories for kind=categories; empty text clears them.\"}},\"required\":[\"kind\"],\"additionalProperties\":false}", true, true, 1, verification: ToolVerification.Tool),
            };
        }

        private static ToolCatalogEntry HardenContract(ToolCatalogEntry tool)
        {
            var schema = JObject.Parse(tool.ArgumentSchemaJson);
            AddCommonBounds(schema);
            switch (tool.Id)
            {
                case "excel.inspect":
                    SetDiscriminatorVariants(schema, "kind",
                        Variant("workbook", new[] { "kind" }, "kind"),
                        Variant("sheets", new[] { "kind" }, "kind"),
                        Variant("charts", new[] { "kind", "sheet", "chartName" }, "kind"),
                        Variant("tables", new[] { "kind", "sheet" }, "kind"),
                        Variant("names", new[] { "kind" }, "kind"),
                        Variant("shapes", new[] { "kind", "sheet" }, "kind"));
                    break;
                case "excel.find_cells":
                    SetStringLimit(schema, "query", 2048);
                    Property(schema, "sheet")["description"] = "Optional worksheet for sheet/range scope; omit to use the active worksheet.";
                    Property(schema, "scope")["description"] = "Optional scope. When omitted, address selects range, sheet selects that sheet, otherwise the workbook is searched.";
                    break;
                case "excel.replace_cells":
                    SetStringLimit(schema, "find", 2048);
                    Property(schema, "maxReplacements")["maximum"] = 10000;
                    Property(schema, "sheet")["description"] = "Optional worksheet for sheet/range scope; omit to use the active worksheet.";
                    Property(schema, "scope").Remove("default");
                    Property(schema, "scope")["description"] = "Optional scope. When omitted, address selects range, sheet selects that sheet, otherwise the current selection is used.";
                    break;
                case "excel.write_range":
                    tool.Description = "Mutates document: Write exactly one scalar value, one non-empty Excel formula, or one non-empty 2D table. kind selects and requires the matching value/formula/values argument.";
                    Property(schema, "formula")["minLength"] = 1;
                    Property(schema, "values")["minItems"] = 1;
                    Property(schema, "values")["maxItems"] = ExcelWriteService.MaxWriteCells;
                    var rowItems = Property(schema, "values")["items"] as JObject;
                    if (rowItems != null)
                    {
                        rowItems["minItems"] = 1;
                        rowItems["maxItems"] = ExcelWriteService.MaxWriteColumns;
                    }
                    SetStringLimit(schema, "sheet", 31);
                    SetStringLimit(schema, "address", 512);
                    SetDiscriminatorVariants(schema, "kind",
                        Variant("value", new[] { "kind", "sheet", "address", "value" }, "kind", "value"),
                        Variant("formula", new[] { "kind", "sheet", "address", "formula" }, "kind", "formula"),
                        Variant("table", new[] { "kind", "sheet", "address", "values" }, "kind", "values"));
                    break;
                case "excel.add_sheet":
                    SetStringLimit(schema, "name", 31);
                    break;
                case "excel.rename_sheet":
                    SetStringLimit(schema, "sheet", 31);
                    SetStringLimit(schema, "newName", 31);
                    break;
                case "excel.sort_range":
                    Property(schema, "keyColumn")["minimum"] = 1;
                    break;
                case "excel.filter_range":
                    Property(schema, "field")["minimum"] = 1;
                    break;
                case "excel.format_range":
                    SetAtLeastOneVariants(schema, new[] { "sheet", "address", "numberFormat", "bold", "italic", "fillColor", "fontColor", "horizontalAlignment", "autoFit" },
                        "numberFormat", "bold", "italic", "fillColor", "fontColor", "horizontalAlignment", "autoFit");
                    break;
                case "word.inspect":
                    SetDiscriminatorVariants(schema, "kind",
                        Variant("headings", new[] { "kind", "maxResults" }, "kind"),
                        Variant("tables", new[] { "kind", "maxTables", "maxRows" }, "kind"),
                        Variant("comments", new[] { "kind" }, "kind"),
                        Variant("stats", new[] { "kind" }, "kind"));
                    break;
                case "word.write_text":
                    SetDiscriminatorVariants(schema, "mode",
                        Variant("insert", new[] { "mode", "text" }, "mode", "text"),
                        Variant("paragraph", new[] { "mode", "text", "location" }, "mode", "text"),
                        Variant("replaceSelection", new[] { "mode", "text" }, "mode", "text"));
                    break;
                case "word.format_text":
                    SetFormatTextVariants(schema);
                    break;
                case "powerpoint.list_objects":
                    SetDiscriminatorVariants(schema, "kind",
                        Variant("slides", new[] { "kind" }, "kind"),
                        Variant("shapes", new[] { "kind", "slideIndex" }, "kind"));
                    break;
                case "powerpoint.search_text":
                    SetDeckScopeVariants(schema, false);
                    break;
                case "powerpoint.replace_text":
                    SetDeckScopeVariants(schema, true);
                    break;
                case "powerpoint.set_text":
                    SetDiscriminatorVariants(schema, "target",
                        Variant("notes", new[] { "target", "slideIndex", "text" }, "target", "text"),
                        Variant("shape", new[] { "target", "slideIndex", "shapeName", "text" }, "target", "text"));
                    break;
                case "powerpoint.add_object":
                    SetDiscriminatorVariants(schema, "kind",
                        Variant("textBox", new[] { "kind", "slideIndex", "text", "left", "top", "width", "height", "fontSize" }, "kind", "text"),
                        Variant("picture", new[] { "kind", "slideIndex", "path", "left", "top", "width", "height" }, "kind", "path"),
                        Variant("table", new[] { "kind", "slideIndex", "rows", "columns", "values", "left", "top", "width", "height" }, "kind"));
                    break;
                case "powerpoint.duplicate_slide":
                case "powerpoint.move_slide":
                    Property(schema, "slideIndex")["minimum"] = 1;
                    if (Property(schema, "toIndex") != null) Property(schema, "toIndex")["minimum"] = 1;
                    break;
                case "outlook.create_draft":
                    SetDiscriminatorVariants(schema, "kind",
                        Variant("new", new[] { "kind", "to", "cc", "bcc", "subject", "body" }, "kind"),
                        Variant("reply", new[] { "kind", "body" }, "kind"),
                        Variant("replyAll", new[] { "kind", "body" }, "kind"),
                        Variant("forward", new[] { "kind", "to", "body" }, "kind"));
                    break;
                case "outlook.update_mail":
                    SetDiscriminatorVariants(schema, "kind",
                        Variant("categories", new[] { "kind", "categories" }, "kind", "categories"),
                        Variant("markRead", new[] { "kind" }, "kind"));
                    break;
            }
            tool.ArgumentSchemaJson = schema.ToString(Formatting.None);
            return tool;
        }

        private static void AddCommonBounds(JObject schema)
        {
            var properties = schema["properties"] as JObject;
            if (properties == null) return;
            SetBounds(properties, "maxResults", 1, 500);
            SetBounds(properties, "contextChars", 0, 1000);
            SetBounds(properties, "maxReplacements", 1, 500);
            SetBounds(properties, "maxItems", 1, 500);
            SetBounds(properties, "maxSlides", 1, 200);
            SetBounds(properties, "maxTables", 1, 50);
            SetBounds(properties, "maxRows", 1, 500);
            SetBounds(properties, "maxChars", 1, 1000000);
            SetBounds(properties, "fontSize", 1, 400);
            SetBounds(properties, "rows", 1, 10000);
            SetBounds(properties, "columns", 1, 10000);
            SetBounds(properties, "width", 1, 100000);
            SetBounds(properties, "height", 1, 100000);
            SetBounds(properties, "start", 0, int.MaxValue);
            SetBounds(properties, "end", 0, int.MaxValue);
        }

        private static void SetBounds(JObject properties, string name, int minimum, int maximum)
        {
            var property = properties[name] as JObject;
            if (property == null) return;
            property["minimum"] = minimum;
            property["maximum"] = maximum;
        }

        private static void SetStringLimit(JObject schema, string name, int maxLength)
        {
            var property = Property(schema, name);
            if (property != null) property["maxLength"] = maxLength;
        }

        private static JObject Property(JObject schema, string name)
        {
            var properties = schema == null ? null : schema["properties"] as JObject;
            return properties == null ? null : properties[name] as JObject;
        }

        private static ContractVariant Variant(string value, string[] allowed, params string[] required)
        {
            return new ContractVariant { Value = value, Allowed = allowed, Required = required };
        }

        private static void SetDiscriminatorVariants(JObject schema, string discriminator, params ContractVariant[] variants)
        {
            var alternatives = new List<JObject>();
            foreach (var variant in variants ?? new ContractVariant[0])
            {
                var candidate = ObjectVariant(schema, variant.Allowed, variant.Required);
                var discriminatorSchema = Property(candidate, discriminator);
                discriminatorSchema["enum"] = new JArray(variant.Value);
                if (discriminatorSchema["default"] == null ||
                    !string.Equals((string)discriminatorSchema["default"], variant.Value, StringComparison.Ordinal))
                {
                    discriminatorSchema.Remove("default");
                }
                alternatives.Add(candidate);
            }
            SetAnyOf(schema, alternatives.ToArray());
        }

        private static void SetAtLeastOneVariants(JObject schema, string[] allowed, params string[] requiredAlternatives)
        {
            SetAnyOf(schema, (requiredAlternatives ?? new string[0])
                .Select(name => ObjectVariant(schema, allowed, new[] { name }))
                .ToArray());
        }

        private static void SetFormatTextVariants(JObject schema)
        {
            var alternatives = new List<JObject>
            {
                ObjectVariant(schema, new[] { "kind", "style", "target" }, new[] { "kind", "style" })
            };
            Property(alternatives[0], "kind")["enum"] = new JArray("style");
            foreach (var field in new[] { "bold", "italic", "underline", "fontSize", "fontName" })
            {
                var candidate = ObjectVariant(schema, new[] { "kind", "bold", "italic", "underline", "fontSize", "fontName" }, new[] { "kind", field });
                Property(candidate, "kind")["enum"] = new JArray("font");
                alternatives.Add(candidate);
            }
            SetAnyOf(schema, alternatives.ToArray());
        }

        private static void SetDeckScopeVariants(JObject schema, bool replacement)
        {
            var common = replacement
                ? new[] { "find", "replace", "scope", "includeNotes", "mode", "matchCase", "wholeWord", "replaceAll", "maxReplacements" }
                : new[] { "query", "scope", "includeNotes", "mode", "matchCase", "wholeWord", "maxResults", "contextChars" };
            var required = replacement ? new[] { "find" } : new[] { "query" };
            var deck = ObjectVariant(schema, common, required);
            Property(deck, "scope")["enum"] = new JArray("deck");
            var slide = ObjectVariant(schema, common.Concat(new[] { "slideIndex" }).ToArray(), required.Concat(new[] { "scope", "slideIndex" }).ToArray());
            Property(slide, "scope")["enum"] = new JArray("slide");
            Property(slide, "scope").Remove("default");
            Property(slide, "slideIndex")["minimum"] = 1;
            Property(slide, "slideIndex").Remove("default");
            SetAnyOf(schema, deck, slide);
        }

        private static JObject ObjectVariant(JObject schema, IEnumerable<string> allowed, IEnumerable<string> required)
        {
            var source = schema["properties"] as JObject ?? new JObject();
            var selected = new JObject();
            foreach (var name in allowed ?? new string[0])
            {
                if (source[name] != null) selected[name] = source[name].DeepClone();
            }
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = selected,
                ["required"] = new JArray(required ?? new string[0]),
                ["additionalProperties"] = false
            };
        }

        private static void SetAnyOf(JObject schema, params JObject[] alternatives)
        {
            schema["anyOf"] = new JArray(alternatives ?? new JObject[0]);
        }

        private sealed class ContractVariant
        {
            public string Value { get; set; }
            public string[] Allowed { get; set; }
            public string[] Required { get; set; }
        }

        private static ToolCatalogEntry Define(
            string host,
            string id,
            string description,
            string schema,
            bool mutatesDocument = false,
            bool agentCanRun = true,
            int riskLevel = 0,
            bool requiresConfirmation = false,
            bool canSourceHtmlData = false,
            bool independentLocalRead = false,
            ToolVerification verification = ToolVerification.None)
        {
            return new ToolCatalogEntry
            {
                Id = id,
                Host = host,
                Name = id,
                Description = description,
                ArgumentSchemaJson = schema,
                BuiltIn = true,
                Enabled = true,
                MutatesDocument = mutatesDocument,
                AgentCanRun = agentCanRun,
                RiskLevel = riskLevel,
                RequiresConfirmation = requiresConfirmation,
                CanSourceHtmlData = canSourceHtmlData,
                Policy = new ToolPolicy(
                    mutatesDocument ? ToolEffect.Write : ToolEffect.Read,
                    verification,
                    requiresConfirmation,
                    independentLocalRead && !mutatesDocument &&
                        !requiresConfirmation,
                    new[] { "agent" },
                    riskLevel),
                Binding = DirectToolBindingCatalog.Resolve(id)
            };
        }

    }
}
