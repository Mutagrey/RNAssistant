using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Tools
{
    internal static class BuiltInToolAliases
    {
        private static readonly IDictionary<string, string> AliasMap =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "common.skills_list", "common.skills_read" },
                { "common.skills_create", "common.skills_upsert" },
                { "common.skills_update", "common.skills_upsert" },
                { "common.tools_list", "common.tools_read" },
                { "common.tools_create", "common.tools_upsert" },
                { "common.tools_update", "common.tools_upsert" },
                { "common.prompts_read_defaults", "common.prompts_read" },
                { "common.html_workspace_upsert_file", "common.html_workspace_upsert" },
                { "common.html_workspace_upsert_data", "common.html_workspace_upsert" },
                { "common.html_workspace_delete_file", "common.html_workspace_delete" },
                { "common.html_workspace_delete_data", "common.html_workspace_delete" },

                { "excel.workbook_summary", "excel.inspect" },
                { "excel.list_sheets", "excel.inspect" },
                { "excel.list_charts", "excel.inspect" },
                { "excel.list_tables", "excel.inspect" },
                { "excel.list_names", "excel.inspect" },
                { "excel.list_shapes", "excel.inspect" },
                { "excel.get_chart", "excel.inspect" },
                { "excel.read_formula_range", "excel.read_range" },
                { "excel.profile_range", "excel.read_range" },
                { "excel.write_table", "excel.write_range" },
                { "excel.set_formula", "excel.write_range" },
                { "excel.add_chart", "excel.upsert_chart" },
                { "excel.update_chart", "excel.upsert_chart" },
                { "excel.autofit", "excel.format_range" },

                { "word.get_selection_text", "word.read_text" },
                { "word.read_document", "word.read_text" },
                { "word.read_selection", "word.read_text" },
                { "word.read_range", "word.read_text" },
                { "word.read_headings", "word.inspect" },
                { "word.read_tables", "word.inspect" },
                { "word.list_comments", "word.inspect" },
                { "word.document_stats", "word.inspect" },
                { "word.insert_text", "word.write_text" },
                { "word.insert_paragraph", "word.write_text" },
                { "word.replace_selection", "word.write_text" },
                { "word.apply_style", "word.format_text" },
                { "word.format_selection", "word.format_text" },

                { "powerpoint.read_slide", "powerpoint.read_slides" },
                { "powerpoint.read_speaker_notes", "powerpoint.read_slides" },
                { "powerpoint.list_slides", "powerpoint.list_objects" },
                { "powerpoint.list_shapes", "powerpoint.list_objects" },
                { "powerpoint.replace_selection_text", "powerpoint.set_text" },
                { "powerpoint.set_speaker_notes", "powerpoint.set_text" },
                { "powerpoint.set_shape_text", "powerpoint.set_text" },
                { "powerpoint.add_text_box", "powerpoint.add_object" },
                { "powerpoint.add_picture", "powerpoint.add_object" },
                { "powerpoint.add_table", "powerpoint.add_object" },

                { "outlook.read_current_mail", "outlook.read_mail" },
                { "outlook.read_selection", "outlook.read_mail" },
                { "outlook.read_mail_by_entry_id", "outlook.read_mail" },
                { "outlook.list_attachments", "outlook.read_mail" },
                { "outlook.create_mail_draft", "outlook.create_draft" },
                { "outlook.create_reply_draft", "outlook.create_draft" },
                { "outlook.create_reply_all_draft", "outlook.create_draft" },
                { "outlook.create_forward_draft", "outlook.create_draft" },
                { "outlook.collect_folder_mail", "outlook.collect_mail" },
                { "outlook.collect_monthly_summary_data", "outlook.collect_mail" },
                { "outlook.set_categories", "outlook.update_mail" },
                { "outlook.mark_as_read", "outlook.update_mail" }
            };

        public static IEnumerable<KeyValuePair<string, string>> Aliases()
        {
            return AliasMap;
        }

        public static string Canonicalize(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return id;
            string canonical;
            return AliasMap.TryGetValue(id, out canonical) ? canonical : id;
        }

        public static bool IsLegacyAlias(string id)
        {
            return !string.IsNullOrWhiteSpace(id) && AliasMap.ContainsKey(id);
        }

        public static void NormalizeCommand(ToolCommand command, string requestedId)
        {
            if (command == null) return;
            var arguments = JObject.FromObject(command.Arguments ?? new Dictionary<string, object>());
            if (string.Equals(command.ToolId, HtmlArtifactToolExecutor.ReadWorkspaceToolId, StringComparison.OrdinalIgnoreCase) &&
                HasValue(arguments, "path") && HasValue(arguments, "dataName"))
            {
                throw new InvalidOperationException("Specify either path or dataName, not both.");
            }
            NormalizeArguments(requestedId, command.ToolId, arguments);
            if (command.Arguments == null)
            {
                command.Arguments = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                command.Arguments.Clear();
            }
            ToolArgumentNormalizer.AddProperties(arguments, command.Arguments);
        }

        public static void NormalizePipeline(ToolDefinition tool)
        {
            if (tool == null || !string.Equals(tool.Executor, "pipeline", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(tool.PipelineJson)) return;
            try
            {
                var pipeline = JObject.Parse(tool.PipelineJson);
                var changed = false;
                foreach (var step in (pipeline["steps"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    var requestedId = (string)step["toolId"];
                    var canonicalId = Canonicalize(CanonicalizePipelineVbaToolId(requestedId));
                    var arguments = step["arguments"] as JObject ?? new JObject();
                    var normalized = (JObject)arguments.DeepClone();
                    NormalizeArguments(requestedId, canonicalId, normalized);
                    if (!string.Equals(requestedId, canonicalId, StringComparison.Ordinal) || !JToken.DeepEquals(arguments, normalized))
                    {
                        step["toolId"] = canonicalId;
                        step["arguments"] = normalized;
                        changed = true;
                    }
                }
                if (changed) tool.PipelineJson = pipeline.ToString(Formatting.None);
            }
            catch (JsonException)
            {
                // Normal definition validation reports malformed pipelines.
            }
            catch (InvalidOperationException)
            {
                // Conflicting legacy selectors are reported when the step executes.
            }
        }

        public static void NormalizeArguments(string requestedId, string canonicalId, JObject arguments)
        {
            requestedId = requestedId ?? string.Empty;
            canonicalId = canonicalId ?? requestedId;
            arguments = arguments ?? new JObject();

            if (string.Equals(requestedId, "common.prompts_read_defaults", StringComparison.OrdinalIgnoreCase))
            {
                SetDefault(arguments, "includeDefaults", true);
            }

            NormalizeVbaArguments(requestedId, canonicalId, arguments);
            NormalizeAuthoringArguments(requestedId, canonicalId, arguments);
            NormalizeHtmlArguments(requestedId, canonicalId, arguments);
            NormalizeExcelArguments(requestedId, canonicalId, arguments);
            NormalizeWordArguments(requestedId, canonicalId, arguments);
            NormalizePowerPointArguments(requestedId, canonicalId, arguments);
            NormalizeOutlookArguments(requestedId, canonicalId, arguments);

            if (string.Equals(canonicalId, "excel.replace_cells", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(canonicalId, "word.replace_text", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(canonicalId, "powerpoint.replace_text", StringComparison.OrdinalIgnoreCase))
            {
                // These used to be model-owned optimistic-concurrency guards. The runtime now
                // reads the current scope under the mutation lock, so legacy values are ignored.
                arguments.Remove("expectedMatches");
                arguments.Remove("expectedScopeSha256");
            }
        }

        private static string CanonicalizePipelineVbaToolId(string id)
        {
            var canonical = VbaPublicToolIds.Canonicalize(id);
            if (!string.Equals(canonical, id, StringComparison.OrdinalIgnoreCase)) return canonical;
            foreach (var host in new[] { "excel.", "word.", "powerpoint." })
            {
                if (!string.IsNullOrWhiteSpace(id) && id.StartsWith(host, StringComparison.OrdinalIgnoreCase))
                {
                    var common = VbaPublicToolIds.Canonicalize("common." + id.Substring(host.Length));
                    if (IsPublicVbaFacadeId(common)) return common;
                }
            }
            return canonical;
        }

        private static bool IsPublicVbaFacadeId(string id)
        {
            return string.Equals(id, "common.vba_read_module", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(id, "common.vba_search_code", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(id, "common.vba_write_module", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(id, "common.vba_apply_patch", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(id, "common.vba_delete_module", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(id, "common.vba_list_backups", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(id, "common.vba_restore_backup", StringComparison.OrdinalIgnoreCase);
        }

        private static void NormalizeVbaArguments(string requestedId, string canonicalId, JObject arguments)
        {
            if (string.Equals(canonicalId, "common.vba_write_module", StringComparison.OrdinalIgnoreCase) &&
                VbaPublicToolIds.IsLegacyCreate(requestedId))
            {
                SetDefault(arguments, "mode", "createOnly");
            }
            if (string.Equals(canonicalId, "common.vba_read_module", StringComparison.OrdinalIgnoreCase) &&
                VbaPublicToolIds.IsLegacyReadLines(requestedId))
            {
                SetDefault(arguments, "startLine", 1);
                SetDefault(arguments, "lineCount", 200);
            }
            if (string.Equals(canonicalId, "common.vba_apply_patch", StringComparison.OrdinalIgnoreCase) &&
                !HasValue(arguments, "patch") && HasValue(arguments, "find"))
            {
                arguments["patch"] = new JArray(new JObject
                {
                    ["op"] = TokenBoolean(arguments["replaceAll"]) ? "replaceAll" : "replace",
                    ["find"] = arguments["find"].DeepClone(),
                    ["text"] = arguments["replace"] == null
                        ? JValue.CreateString(string.Empty)
                        : arguments["replace"].DeepClone()
                });
                arguments.Remove("find");
                arguments.Remove("replace");
                arguments.Remove("replaceAll");
            }
            if (IsPublicVbaFacadeId(canonicalId)) arguments.Remove("expectedCodeSha256");
        }

        private static void NormalizeAuthoringArguments(string requestedId, string canonicalId, JObject arguments)
        {
            if (string.Equals(canonicalId, "common.skills_upsert", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(requestedId, "common.skills_create", StringComparison.OrdinalIgnoreCase))
                {
                    SetDefault(arguments, "mode", "createOnly");
                }
                else if (string.Equals(requestedId, "common.skills_update", StringComparison.OrdinalIgnoreCase))
                {
                    SetDefault(arguments, "mode", "updateOnly");
                }
            }
            else if (string.Equals(canonicalId, "common.tools_upsert", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(requestedId, "common.tools_create", StringComparison.OrdinalIgnoreCase))
                {
                    SetDefault(arguments, "mode", "createOnly");
                }
                else if (string.Equals(requestedId, "common.tools_update", StringComparison.OrdinalIgnoreCase))
                {
                    SetDefault(arguments, "mode", "updateOnly");
                }
            }
        }

        private static void NormalizeHtmlArguments(string requestedId, string canonicalId, JObject arguments)
        {
            if (string.Equals(canonicalId, HtmlArtifactToolExecutor.ReadWorkspaceToolId, StringComparison.OrdinalIgnoreCase))
            {
                MoveSelector(arguments, "path", "file");
                MoveSelector(arguments, "dataName", "data");
                return;
            }

            if (string.Equals(canonicalId, HtmlArtifactToolExecutor.UpsertToolId, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(requestedId, "common.html_workspace_upsert_file", StringComparison.OrdinalIgnoreCase))
                {
                    SetDefault(arguments, "resourceType", "file");
                    Rename(arguments, "path", "name");
                    SetDefault(arguments, "name", "index.html");
                    arguments.Remove("kind");
                }
                else if (string.Equals(requestedId, "common.html_workspace_upsert_data", StringComparison.OrdinalIgnoreCase))
                {
                    SetDefault(arguments, "resourceType", "data");
                    Rename(arguments, "json", "content");
                }
                return;
            }

            if (string.Equals(canonicalId, HtmlArtifactToolExecutor.SetActiveToolId, StringComparison.OrdinalIgnoreCase))
            {
                Rename(arguments, "path", "name");
                SetDefault(arguments, "name", "index.html");
                return;
            }

            if (!string.Equals(canonicalId, HtmlArtifactToolExecutor.DeleteToolId, StringComparison.OrdinalIgnoreCase)) return;
            if (string.Equals(requestedId, "common.html_workspace_delete_file", StringComparison.OrdinalIgnoreCase))
            {
                SetDefault(arguments, "resourceType", "file");
                Rename(arguments, "path", "name");
            }
            else if (string.Equals(requestedId, "common.html_workspace_delete_data", StringComparison.OrdinalIgnoreCase))
            {
                SetDefault(arguments, "resourceType", "data");
            }

        }

        private static void MoveSelector(JObject arguments, string legacyName, string resourceType)
        {
            var value = arguments[legacyName];
            if (!HasValue(arguments, legacyName))
            {
                arguments.Remove(legacyName);
                return;
            }
            if (!HasValue(arguments, "name")) arguments["name"] = value;
            SetDefault(arguments, "resourceType", resourceType);
            arguments.Remove(legacyName);
        }

        private static void NormalizeExcelArguments(string requestedId, string canonicalId, JObject arguments)
        {
            if (string.Equals(canonicalId, "excel.inspect", StringComparison.OrdinalIgnoreCase))
            {
                var kind = ExcelInspectionKind(requestedId);
                if (!string.IsNullOrWhiteSpace(kind)) SetDefault(arguments, "kind", kind);
                return;
            }

            if (string.Equals(canonicalId, "excel.read_range", StringComparison.OrdinalIgnoreCase))
            {
                SetDefault(arguments, "content",
                    string.Equals(requestedId, "excel.read_formula_range", StringComparison.OrdinalIgnoreCase)
                        ? "formulas"
                        : string.Equals(requestedId, "excel.profile_range", StringComparison.OrdinalIgnoreCase)
                            ? "profile"
                        : "values");
                return;
            }

            if (string.Equals(canonicalId, "excel.upsert_chart", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(requestedId, "excel.add_chart", StringComparison.OrdinalIgnoreCase))
                {
                    SetDefault(arguments, "mode", "createOnly");
                }
                else if (string.Equals(requestedId, "excel.update_chart", StringComparison.OrdinalIgnoreCase))
                {
                    SetDefault(arguments, "mode", "updateOnly");
                }
                return;
            }

            if (string.Equals(canonicalId, "excel.format_range", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(requestedId, "excel.autofit", StringComparison.OrdinalIgnoreCase))
            {
                SetDefault(arguments, "autoFit", "both");
                return;
            }

            if (!string.Equals(canonicalId, "excel.write_range", StringComparison.OrdinalIgnoreCase)) return;
            if (string.Equals(requestedId, "excel.write_table", StringComparison.OrdinalIgnoreCase))
            {
                SetDefault(arguments, "kind", "table");
                Rename(arguments, "startAddress", "address");
            }
            else if (string.Equals(requestedId, "excel.set_formula", StringComparison.OrdinalIgnoreCase))
            {
                SetDefault(arguments, "kind", "formula");
            }
            else if (!HasValue(arguments, "kind"))
            {
                arguments["kind"] = HasValue(arguments, "formula")
                    ? "formula"
                    : HasValue(arguments, "values") ? "table" : "value";
            }
        }

        private static string ExcelInspectionKind(string requestedId)
        {
            if (string.Equals(requestedId, "excel.workbook_summary", StringComparison.OrdinalIgnoreCase)) return "workbook";
            if (string.Equals(requestedId, "excel.list_sheets", StringComparison.OrdinalIgnoreCase)) return "sheets";
            if (string.Equals(requestedId, "excel.list_charts", StringComparison.OrdinalIgnoreCase)) return "charts";
            if (string.Equals(requestedId, "excel.list_tables", StringComparison.OrdinalIgnoreCase)) return "tables";
            if (string.Equals(requestedId, "excel.list_names", StringComparison.OrdinalIgnoreCase)) return "names";
            if (string.Equals(requestedId, "excel.list_shapes", StringComparison.OrdinalIgnoreCase)) return "shapes";
            if (string.Equals(requestedId, "excel.get_chart", StringComparison.OrdinalIgnoreCase)) return "charts";
            return string.Empty;
        }

        private static void NormalizeWordArguments(string requestedId, string canonicalId, JObject arguments)
        {
            if (string.Equals(canonicalId, "word.read_text", StringComparison.OrdinalIgnoreCase))
            {
                var source = string.Equals(requestedId, "word.read_range", StringComparison.OrdinalIgnoreCase)
                    ? "range"
                    : string.Equals(requestedId, "word.read_selection", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(requestedId, "word.get_selection_text", StringComparison.OrdinalIgnoreCase)
                        ? "selection"
                        : "document";
                SetDefault(arguments, "source", source);
                return;
            }

            if (string.Equals(canonicalId, "word.inspect", StringComparison.OrdinalIgnoreCase))
            {
                var kind = string.Equals(requestedId, "word.read_headings", StringComparison.OrdinalIgnoreCase) ? "headings" :
                    string.Equals(requestedId, "word.read_tables", StringComparison.OrdinalIgnoreCase) ? "tables" :
                    string.Equals(requestedId, "word.list_comments", StringComparison.OrdinalIgnoreCase) ? "comments" :
                    string.Equals(requestedId, "word.document_stats", StringComparison.OrdinalIgnoreCase) ? "stats" : string.Empty;
                if (!string.IsNullOrWhiteSpace(kind)) SetDefault(arguments, "kind", kind);
                return;
            }

            if (string.Equals(canonicalId, "word.format_text", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(requestedId, "word.apply_style", StringComparison.OrdinalIgnoreCase))
                {
                    SetDefault(arguments, "kind", "style");
                }
                else if (string.Equals(requestedId, "word.format_selection", StringComparison.OrdinalIgnoreCase))
                {
                    SetDefault(arguments, "kind", "font");
                }
                else if (!HasValue(arguments, "kind"))
                {
                    if (HasValue(arguments, "style")) arguments["kind"] = "style";
                    else if (HasValue(arguments, "bold") || HasValue(arguments, "italic") || HasValue(arguments, "underline") ||
                        HasValue(arguments, "fontSize") || HasValue(arguments, "fontName")) arguments["kind"] = "font";
                }
                return;
            }

            if (!string.Equals(canonicalId, "word.write_text", StringComparison.OrdinalIgnoreCase)) return;
            var mode = string.Equals(requestedId, "word.insert_paragraph", StringComparison.OrdinalIgnoreCase) ? "paragraph" :
                string.Equals(requestedId, "word.replace_selection", StringComparison.OrdinalIgnoreCase) ? "replaceSelection" : "insert";
            SetDefault(arguments, "mode", mode);
        }

        private static void NormalizePowerPointArguments(string requestedId, string canonicalId, JObject arguments)
        {
            if (string.Equals(canonicalId, "powerpoint.read_slides", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(requestedId, "powerpoint.read_slide", StringComparison.OrdinalIgnoreCase))
            {
                SetDefault(arguments, "slideIndex", 1);
                SetDefault(arguments, "content", "both");
                return;
            }
            if (string.Equals(canonicalId, "powerpoint.read_slides", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(requestedId, "powerpoint.read_speaker_notes", StringComparison.OrdinalIgnoreCase))
            {
                SetDefault(arguments, "content", "notes");
                var slideIndex = arguments["slideIndex"];
                if (slideIndex != null && slideIndex.Type == JTokenType.Integer && slideIndex.Value<int>() <= 0)
                {
                    arguments.Remove("slideIndex");
                }
                return;
            }
            if (string.Equals(canonicalId, "powerpoint.list_objects", StringComparison.OrdinalIgnoreCase))
            {
                SetDefault(arguments, "kind",
                    string.Equals(requestedId, "powerpoint.list_shapes", StringComparison.OrdinalIgnoreCase) ? "shapes" : "slides");
                if (string.Equals(requestedId, "powerpoint.list_shapes", StringComparison.OrdinalIgnoreCase))
                {
                    SetDefault(arguments, "slideIndex", 1);
                }
                return;
            }
            if (string.Equals(canonicalId, "powerpoint.set_text", StringComparison.OrdinalIgnoreCase))
            {
                var notes = string.Equals(requestedId, "powerpoint.set_speaker_notes", StringComparison.OrdinalIgnoreCase);
                if (notes) SetDefault(arguments, "target", "notes");
                else if (string.Equals(requestedId, "powerpoint.set_shape_text", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(requestedId, "powerpoint.replace_selection_text", StringComparison.OrdinalIgnoreCase)) SetDefault(arguments, "target", "shape");
                if (notes) Rename(arguments, "notes", "text");
                if (notes || string.Equals(requestedId, "powerpoint.set_shape_text", StringComparison.OrdinalIgnoreCase))
                {
                    SetDefault(arguments, "slideIndex", 1);
                }
                return;
            }
            if (string.Equals(canonicalId, "powerpoint.add_object", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(requestedId, "powerpoint.add_picture", StringComparison.OrdinalIgnoreCase)) SetDefault(arguments, "kind", "picture");
                else if (string.Equals(requestedId, "powerpoint.add_table", StringComparison.OrdinalIgnoreCase)) SetDefault(arguments, "kind", "table");
                else if (string.Equals(requestedId, "powerpoint.add_text_box", StringComparison.OrdinalIgnoreCase))
                {
                    SetDefault(arguments, "kind", "textBox");
                    var fontSize = arguments["fontSize"];
                    if (fontSize != null && fontSize.Type == JTokenType.Integer && fontSize.Value<int>() <= 0)
                    {
                        arguments.Remove("fontSize");
                    }
                }
                else if (!HasValue(arguments, "kind"))
                {
                    if (HasValue(arguments, "path")) arguments["kind"] = "picture";
                    else if (HasValue(arguments, "values") || HasValue(arguments, "rows") || HasValue(arguments, "columns")) arguments["kind"] = "table";
                    else if (HasValue(arguments, "text")) arguments["kind"] = "textBox";
                }
                if (string.Equals(requestedId, "powerpoint.add_picture", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(requestedId, "powerpoint.add_table", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(requestedId, "powerpoint.add_text_box", StringComparison.OrdinalIgnoreCase))
                {
                    SetDefault(arguments, "slideIndex", 1);
                }
            }
        }

        private static void NormalizeOutlookArguments(string requestedId, string canonicalId, JObject arguments)
        {
            if (string.Equals(canonicalId, "outlook.read_mail", StringComparison.OrdinalIgnoreCase))
            {
                SetDefault(arguments, "content",
                    string.Equals(requestedId, "outlook.list_attachments", StringComparison.OrdinalIgnoreCase) ? "attachments" : "message");
            }
            else if (string.Equals(canonicalId, "outlook.create_draft", StringComparison.OrdinalIgnoreCase))
            {
                var kind = string.Equals(requestedId, "outlook.create_reply_draft", StringComparison.OrdinalIgnoreCase) ? "reply" :
                    string.Equals(requestedId, "outlook.create_reply_all_draft", StringComparison.OrdinalIgnoreCase) ? "replyAll" :
                    string.Equals(requestedId, "outlook.create_forward_draft", StringComparison.OrdinalIgnoreCase) ? "forward" : "new";
                SetDefault(arguments, "kind", kind);
            }
            else if (string.Equals(canonicalId, "outlook.collect_mail", StringComparison.OrdinalIgnoreCase))
            {
                var monthly = string.Equals(requestedId, "outlook.collect_monthly_summary_data", StringComparison.OrdinalIgnoreCase);
                SetDefault(arguments, "groupBy", monthly ? "month" : "none");
                if (monthly)
                {
                    SetDefault(arguments, "maxItems", 500);
                    SetDefault(arguments, "maxBodyChars", 500);
                }
            }
            else if (string.Equals(canonicalId, "outlook.update_mail", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(requestedId, "outlook.mark_as_read", StringComparison.OrdinalIgnoreCase)) SetDefault(arguments, "kind", "markRead");
                else if (string.Equals(requestedId, "outlook.set_categories", StringComparison.OrdinalIgnoreCase)) SetDefault(arguments, "kind", "categories");
            }
        }

        private static void Rename(JObject arguments, string oldName, string newName)
        {
            var value = arguments[oldName];
            if (HasValue(arguments, oldName) && !HasValue(arguments, newName)) arguments[newName] = value;
            arguments.Remove(oldName);
        }

        private static void SetDefault(JObject arguments, string name, object value)
        {
            if (!HasValue(arguments, name)) arguments[name] = JToken.FromObject(value);
        }

        private static bool HasValue(JObject arguments, string name)
        {
            var value = arguments == null ? null : arguments[name];
            return value != null && value.Type != JTokenType.Null && value.Type != JTokenType.Undefined;
        }

        private static bool TokenBoolean(JToken value)
        {
            if (value == null) return false;
            if (value.Type == JTokenType.Boolean) return value.Value<bool>();
            bool parsed;
            return bool.TryParse(value.ToString(), out parsed) && parsed;
        }
    }
}
