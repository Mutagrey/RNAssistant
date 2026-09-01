using System;
using System.Collections.Generic;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Tools
{
    internal static partial class VbaToolCatalog
    {
        internal const string RestoreBackup = "common.vba_restore_backup";
        internal const string WriteModule = "common.vba_write_module";
        internal const string ApplyPatch = "common.vba_apply_patch";
        internal const string DeleteModule = "common.vba_delete_module";
        internal const string RunMacro = "common.office_run_macro";

        internal static bool Owns(string toolId)
        {
            return string.Equals(toolId, RestoreBackup, StringComparison.Ordinal) ||
                string.Equals(toolId, WriteModule, StringComparison.Ordinal) ||
                string.Equals(toolId, ApplyPatch, StringComparison.Ordinal) ||
                string.Equals(toolId, DeleteModule, StringComparison.Ordinal) ||
                string.Equals(toolId, RunMacro, StringComparison.Ordinal);
        }

        internal static IEnumerable<ToolDefinition> GetTools()
        {
            yield return Projection(RestoreBackup,
                "Mutates document: Restore a VBA module from an exact backupId, or resolve the latest backup for moduleName when backupId is omitted. Runtime pins the exact backup and current target state before confirmation.",
                RestoreBackupSchema(), ToolEffect.Write, ToolVerification.Tool);
            yield return Projection(WriteModule,
                "Mutates document with two strict branches. Whole-source write requires moduleName+code and uses mode=upsert/createOnly/updateOnly; componentType applies only on creation. Atomic rename requires moduleName+newModuleName+mode=rename and accepts no code/componentType. Runtime guards both names, normalizes a new destination, rejects collisions, journals both identities, and verifies read-back. Rename preserves the component but does not rewrite textual references to its old name.",
                WriteModuleSchema(), ToolEffect.Write, ToolVerification.Tool);
            yield return Projection(ApplyPatch,
                "Mutates document: Apply ordered exact unique source-block replacements to an existing VBA component. There are no line-number, fuzzy, first-match, regex, or implicit insertion modes. Runtime patches one current full-module snapshot in memory, then performs one guarded whole-module write. Exact replacements already satisfied are skipped; an all-no-op patch succeeds without writing. Use common.vba_write_module with complete source when the module is missing.",
                ApplyPatchSchema(), ToolEffect.Write, ToolVerification.Tool);
            yield return Projection(DeleteModule,
                "Mutates document: Delete an existing StdModule or ClassModule. Runtime reads it, validates the type, and creates a rollback backup; no separate read call is required. Document modules and UserForms are not deleted.",
                ModuleNameSchema(), ToolEffect.Write, ToolVerification.Tool);
            yield return Projection(RunMacro,
                "May execute arbitrary VBA code: Run any existing macro by its exact Office Application.Run name without a manifest or allowlist. Available in Excel, Word, and PowerPoint. The macro may affect files or external state; use only when execution is requested and inspect state after the call.",
                RunMacroSchema(), ToolEffect.External, ToolVerification.None);
        }

        private static ToolDefinition Projection(string id, string description,
            string schema, ToolEffect effect, ToolVerification verification)
        {
            return ControllerToolDefinition.CreateTypedProjection(
                new ToolDescriptor(id, description, schema),
                new ToolPolicy(effect, verification, true, false,
                    new[] { "agent" }, 3));
        }
    }
}
