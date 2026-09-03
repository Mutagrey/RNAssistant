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
        internal const string RenameModule = "common.vba_rename_module";
        internal const string ApplyPatch = "common.vba_apply_patch";
        internal const string DeleteModule = "common.vba_delete_module";
        internal const string RunMacro = "common.office_run_macro";

        internal static bool Owns(string toolId)
        {
            return string.Equals(toolId, RestoreBackup, StringComparison.Ordinal) ||
                string.Equals(toolId, WriteModule, StringComparison.Ordinal) ||
                string.Equals(toolId, RenameModule, StringComparison.Ordinal) ||
                string.Equals(toolId, ApplyPatch, StringComparison.Ordinal) ||
                string.Equals(toolId, DeleteModule, StringComparison.Ordinal) ||
                string.Equals(toolId, RunMacro, StringComparison.Ordinal);
        }

        internal static IEnumerable<ToolCatalogEntry> GetTools()
        {
            yield return Projection(RestoreBackup,
                "Mutates document: Restore a VBA module from an exact readable backup target returned by common.resources_find with scope=backups, or select the latest backup for moduleName. Runtime resolves and pins the exact backup identity and current target state before confirmation.",
                RestoreBackupSchema(), ToolEffect.Write, ToolVerification.Tool);
            yield return Projection(WriteModule,
                "Mutates document: Write one complete VBA component source. Requires moduleName+code and uses mode=upsert/createOnly/updateOnly; componentType applies only on creation. Runtime binds current state before confirmation and verifies exact source/type read-back. Use common.vba_rename_module for an identity-preserving rename.",
                WriteModuleSchema(), ToolEffect.Write, ToolVerification.Tool);
            yield return Projection(RenameModule,
                "Mutates document: Rename one existing VBA component without rewriting its source. Runtime guards both names, normalizes the destination, rejects collisions, journals both identities, and verifies source/type preservation. Textual references to the old component name are not rewritten.",
                RenameModuleSchema(), ToolEffect.Write, ToolVerification.Tool);
            yield return Projection(ApplyPatch,
                "Mutates document: Apply ordered exact unique source-block replacements to one existing VBA component selected by moduleName. Each hunk requires find and text; optional unchanged contextBefore/contextAfter disambiguates repeated find text inside that component and is verified but not replaced. Runtime owns the fixed replace operation. There are no line-number, fuzzy, first-match, regex, or implicit insertion modes. Runtime patches one current full-module snapshot in memory, then performs one guarded whole-module write. Exact replacements already satisfied are skipped; an all-no-op patch succeeds without writing. Use common.vba_write_module with complete source when the module is missing.",
                ApplyPatchSchema(), ToolEffect.Write, ToolVerification.Tool);
            yield return Projection(DeleteModule,
                "Mutates document: Delete an existing StdModule or ClassModule. Runtime reads it, validates the type, and creates a rollback backup; no separate read call is required. Document modules and UserForms are not deleted.",
                ModuleNameSchema(), ToolEffect.Write, ToolVerification.Tool);
            yield return Projection(RunMacro,
                "May execute arbitrary VBA code: Run any existing macro by its exact Office Application.Run name without a manifest or allowlist. Available in Excel, Word, and PowerPoint. The macro may affect files or external state; use only when execution is requested and inspect state after the call.",
                RunMacroSchema(), ToolEffect.External, ToolVerification.None);
        }

        internal static string SchemaFor(string toolId)
        {
            if (string.Equals(toolId, RestoreBackup, StringComparison.Ordinal))
                return RestoreBackupSchema();
            if (string.Equals(toolId, WriteModule, StringComparison.Ordinal))
                return WriteModuleSchema();
            if (string.Equals(toolId, RenameModule, StringComparison.Ordinal))
                return RenameModuleSchema();
            if (string.Equals(toolId, ApplyPatch, StringComparison.Ordinal))
                return ApplyPatchSchema();
            if (string.Equals(toolId, DeleteModule, StringComparison.Ordinal))
                return ModuleNameSchema();
            if (string.Equals(toolId, RunMacro, StringComparison.Ordinal))
                return RunMacroSchema();
            return null;
        }

        private static ToolCatalogEntry Projection(string id, string description,
            string schema, ToolEffect effect, ToolVerification verification)
        {
            return ControllerToolCatalogEntry.CreateTypedProjection(
                new ToolDescriptor(id, description, schema),
                new ToolPolicy(effect, verification, true, false,
                    new[] { "agent" }, 3),
                mutatesDocument: true);
        }
    }
}
