using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace RNAssistant.Core.Tools
{
    public static class CapabilityNaming
    {
        private static readonly HashSet<string> RetiredTools = new HashSet<string>(StringComparer.Ordinal) {
            "common.capabilities_read",
            "common.capabilities_search",
            "common.file_copy",
            "common.file_create",
            "common.file_patch",
            "common.html_assets_import",
            "common.html_assets_list",
            "common.html_assets_publish",
            "common.html_data_bind",
            "common.html_data_freeze",
            "common.html_data_refresh",
            "common.html_data_write",
            "common.html_workspace_apply_patch",
            "common.html_workspace_delete",
            "common.html_workspace_write_file",
            "common.js_run",
            "common.markdown_restore",
            "common.markdown_save",
            "common.office_run_macro",
            "common.plan_doc_delete",
            "common.plan_doc_restore",
            "common.plan_doc_save",
            "common.prompts_save",
            "common.questions_ask",
            "common.resources_derive",
            "common.resources_find",
            "common.resources_mapping_publish",
            "common.resources_outline",
            "common.resources_read",
            "common.resources_schema_draft",
            "common.resources_schema_publish",
            "common.resources_search",
            "common.skills_load",
            "common.task_list_set",
            "common.tools_delete",
            "common.tools_upsert",
            "common.vba_apply_patch",
            "common.vba_delete_module",
            "common.vba_rename_module",
            "common.vba_restore_backup",
            "common.vba_write_module",
            "excel.add_sheet",
            "excel.add_table",
            "excel.clear_range",
            "excel.create_chat_chart",
            "excel.delete_chart",
            "excel.delete_rows_columns",
            "excel.filter_range",
            "excel.find_cells",
            "excel.format_range",
            "excel.rename_sheet",
            "excel.replace_cells",
            "excel.sort_range",
            "excel.summarize_columns",
            "excel.upsert_chart",
            "excel.write_range",
            "files.copy",
            "files.create",
            "files.delete",
            "files.move",
            "files.patch",
            "files.replace",
            "files.restore",
            "outlook.create_draft",
            "outlook.index_archive",
            "outlook.search_mail",
            "outlook.update_mail",
            "powerpoint.add_object",
            "powerpoint.add_slide",
            "powerpoint.duplicate_slide",
            "powerpoint.list_objects",
            "powerpoint.move_slide",
            "powerpoint.replace_text",
            "powerpoint.search_text",
            "powerpoint.set_text",
            "word.add_comment",
            "word.add_table",
            "word.find_text",
            "word.format_text",
            "word.insert_page_break",
            "word.replace_text",
            "word.write_text",
        };

        public static bool IsRetiredTool(string id) { return id != null && RetiredTools.Contains(id); }

        public static bool ValidNewId(string id, bool skill)
        {
            return id != null && id.Length <= 128 &&
                Regex.IsMatch(id, @"^[a-z][a-z0-9]*(\.[a-z][a-z0-9]*)+$", RegexOptions.CultureInvariant) &&
                (skill ? id.StartsWith("skills.", System.StringComparison.Ordinal) :
                    !id.StartsWith("core.", System.StringComparison.Ordinal) && !id.StartsWith("skills.", System.StringComparison.Ordinal));
        }
    }
}
