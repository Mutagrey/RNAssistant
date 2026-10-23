using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Core.Services
{
    // Exact Office model read schema, shared by its direct tool and CLI binding.
    public static class ResourceModelReadSchema
    {
        public static JObject SemanticTarget()
        { return new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 1024,
            ["description"] = "Copy a semantic target verbatim from runtime context, discovery, a workspace member or binding sourceTarget. For a known Excel sheet and A1 address, use Excel range: Sheet!A1:B10 directly; discovery is optional. Never construct a target from a display title or pass a URI. HTML data names describe bindings; sourceTarget identifies their values." }; }

        public static JObject RecordPath()
        { return new JObject { ["type"] = "string", ["maxLength"] = 256, ["pattern"] = @"^\$(?:\.[A-Za-z_][A-Za-z0-9_]*)*$",
            ["description"] = "Optional JSON record-array path. Omit for Office targets: runtime selects the canonical view. For JSON use $ or a declared property such as $.records. This is not a page index, file path or worksheet address." }; }

        public static string Office()
        {
            var target = "\"target\":" + SemanticTarget().ToString(Formatting.None);
            const string representation = "\"representation\":{\"type\":\"string\",\"description\":\"Representation to read. Complete views cannot be combined with table/tree selectors.\",\"enum\":[\"metadata\",\"text\",\"structure\",\"source\",\"media\",\"formulas\",\"table\",\"tree\"]}";
            const string procedure = "\"procedure\":{\"type\":\"string\",\"minLength\":1,\"description\":\"Unique VBA procedure name, case-insensitive and without brackets. Selects its declaration through End Sub/Function/Property from one exact source snapshot.\"},\"procedureKind\":{\"type\":\"string\",\"enum\":[\"sub\",\"function\",\"property-get\",\"property-let\",\"property-set\"],\"description\":\"Optional exact kind; use to disambiguate property accessors. Conditional duplicate declarations still require line/character selectors.\"}";
            const string section = "\"section\":{\"type\":\"string\",\"description\":\"Exact unique ATX heading title copied from Markdown, without leading #. Includes nested subsections; maximum selected text is 32000 characters. Does not grant whole-resource read evidence.\",\"minLength\":1,\"maxLength\":200}";
            const string part = "\"partNumber\":{\"type\":\"integer\",\"minimum\":1,\"description\":\"1-based text/source part of an indexed uploaded or authored artifact, not a live VBA module; each part has at most 32000 characters.\"}";
            const string lines = "\"startLine\":{\"type\":\"integer\",\"minimum\":1,\"description\":\"1-based first source/text line. Use with lineCount for an exact excerpt.\"},\"lineCount\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":2147483647,\"description\":\"Maximum selected lines; the excerpt must fit the configured model read budget. Not whole-file observation.\"}";
            const string chars = "\"startChar\":{\"type\":\"integer\",\"minimum\":0,\"description\":\"Zero-based UTF-16 character offset in source/text. Use with charCount for a long line.\"},\"charCount\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":2147483647,\"description\":\"Maximum selected UTF-16 characters within the configured model read budget; do not split a surrogate pair. Not whole-file observation.\"}";
            const string page = "\"pageIndex\":{\"type\":\"integer\",\"minimum\":0,\"description\":\"Zero-based page in an exact PDF attachment original. Text extraction may omit visual or scanned content.\"}";
            const string pageRange = "\"startPage\":{\"type\":\"integer\",\"minimum\":0,\"description\":\"Zero-based first PDF page.\"},\"pageCount\":{\"type\":\"integer\",\"minimum\":1,\"description\":\"Requested number of consecutive complete extracted pages; nextPageIndex continues if the model budget fills.\"}";
            const string treeSelector = "\"pointer\":{\"type\":\"string\",\"description\":\"RFC 6901-style pointer for JSON/YAML, empty string for root; copy exact key spelling and escape ~ as ~0, / as ~1.\"},\"depth\":{\"type\":\"integer\",\"minimum\":0,\"description\":\"Levels of descendants to return, default 1.\"}";
            var selectors = "\"limit\":{\"type\":\"integer\",\"description\":\"Maximum rows in this table batch.\",\"minimum\":1,\"maximum\":5000},\"offset\":{\"type\":\"integer\",\"description\":\"Zero-based table row offset.\",\"minimum\":0}" + ",\"path\":" + RecordPath().ToString(Formatting.None) + ",\"fields\":{\"type\":\"array\",\"description\":\"Exact column keys from returned columns metadata, not display headings or translated aliases.\",\"maxItems\":128,\"items\":{\"type\":\"string\",\"maxLength\":128}}";
            return "{\"type\":\"object\",\"properties\":{" + target + "," + representation + "," + section + "," + procedure + "," + part + "," + page + "," + pageRange + "," + selectors + "," + lines + "," + chars + "," + treeSelector +
                "},\"required\":[\"target\"],\"additionalProperties\":false,\"anyOf\":[" +
                "{\"type\":\"object\",\"description\":\"Read one complete metadata, text, structure, source, media, or formulas representation. Do not send limit, offset, path, or fields.\",\"properties\":{" + target + "," +
                "\"representation\":{\"type\":\"string\",\"description\":\"Complete representation to read; omit for provider-selected auto.\",\"enum\":[\"metadata\",\"text\",\"structure\",\"source\",\"media\",\"formulas\"]}},\"required\":[\"target\"],\"additionalProperties\":false}," +
                "{\"type\":\"object\",\"description\":\"Read bounded rows. Structural selectors are valid only in this table branch.\",\"properties\":{" + target + "," +
                "\"representation\":{\"type\":\"string\",\"description\":\"Bounded structural representation to read.\",\"enum\":[\"table\"]}," +
                selectors +
                "},\"required\":[\"target\",\"representation\"],\"additionalProperties\":false}," +
                "{\"type\":\"object\",\"description\":\"Read a JSON/YAML tree from one exact text source. Only returned nodes use model budget.\",\"properties\":{" + target + "," + treeSelector + "," +
                "\"representation\":{\"type\":\"string\",\"enum\":[\"tree\"]},\"offset\":{\"type\":\"integer\",\"minimum\":0},\"limit\":{\"type\":\"integer\",\"minimum\":1}" +
                "},\"required\":[\"target\",\"representation\",\"pointer\"],\"additionalProperties\":false}," +
                "{\"type\":\"object\",\"description\":\"Read exact bounded source/text lines. No section or structural selectors.\",\"properties\":{" + target + "," + lines + ",\"representation\":{\"type\":\"string\",\"enum\":[\"source\",\"text\"]}},\"required\":[\"target\",\"representation\",\"startLine\",\"lineCount\"],\"additionalProperties\":false}," +
                "{\"type\":\"object\",\"description\":\"Read exact bounded UTF-16 characters from source/text. No other selectors.\",\"properties\":{" + target + "," + chars + ",\"representation\":{\"type\":\"string\",\"enum\":[\"source\",\"text\"]}},\"required\":[\"target\",\"representation\",\"startChar\",\"charCount\"],\"additionalProperties\":false}," +
                "{\"type\":\"object\",\"description\":\"Extract text from one exact PDF attachment page within the model budget. Continue with pageIndex, startChar and charCount.\",\"properties\":{" + target + "," + page + ",\"representation\":{\"type\":\"string\",\"enum\":[\"text\"]}},\"required\":[\"target\",\"representation\",\"pageIndex\"],\"additionalProperties\":false}," +
                "{\"type\":\"object\",\"description\":\"Read an exact UTF-16 window within one PDF attachment page.\",\"properties\":{" + target + "," + page + "," + chars + ",\"representation\":{\"type\":\"string\",\"enum\":[\"text\"]}},\"required\":[\"target\",\"representation\",\"pageIndex\",\"startChar\",\"charCount\"],\"additionalProperties\":false}," +
                "{\"type\":\"object\",\"description\":\"Read consecutive complete extracted PDF pages within the model budget; continue at nextPageIndex or use one-page character windows.\",\"properties\":{" + target + "," + pageRange + ",\"representation\":{\"type\":\"string\",\"enum\":[\"text\"]}},\"required\":[\"target\",\"representation\",\"startPage\",\"pageCount\"],\"additionalProperties\":false}," +
                "{\"type\":\"object\",\"description\":\"Read one unique VBA procedure from source; no other selectors.\",\"properties\":{" + target + "," + procedure + ",\"representation\":{\"type\":\"string\",\"enum\":[\"source\"]}},\"required\":[\"target\",\"representation\",\"procedure\"],\"additionalProperties\":false}," +
                "{\"type\":\"object\",\"description\":\"Read one bounded text/source part.\",\"properties\":{" + target + "," + part + ",\"representation\":{\"type\":\"string\",\"enum\":[\"source\",\"text\"]}},\"required\":[\"target\",\"representation\",\"partNumber\"],\"additionalProperties\":false}," +
                "{\"type\":\"object\",\"description\":\"Read one uniquely named document Markdown/Plan section.\",\"properties\":{" + target + "," + section + "," +
                "\"representation\":{\"type\":\"string\",\"enum\":[\"text\"]}},\"required\":[\"target\",\"representation\",\"section\"],\"additionalProperties\":false}]}";
        }

    }
}
