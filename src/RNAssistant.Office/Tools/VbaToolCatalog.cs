using System;
using System.Collections.Generic;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Tools
{
    internal static partial class VbaToolCatalog
    {
        internal const string RestoreBackup = "vba.module.restore";
        internal const string WriteModule = "vba.module.write";
        internal const string RenameModule = "vba.module.rename";
        internal const string ApplyPatch = "vba.module.patch";
        internal const string DeleteModule = "vba.module.delete";
        internal const string RunMacro = "office.macro.run";

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
                "Mutates document: Restore a VBA module from an exact readable backup target returned by core.find with scope=backups, or select the latest backup for moduleName. Runtime resolves and pins the exact backup identity and current target state before confirmation.",
                RestoreBackupSchema(), ToolEffect.Write, ToolVerification.Tool);
            yield return Projection(WriteModule,
                "Mutates document: Write one complete VBA component source. Requires moduleName+code. Use createOnly for creation, updateOnly for an existing component, or upsert when either outcome is intended; componentType applies only on creation. Consolidate all changes derived from one fresh source snapshot into one complete write. Runtime rejects unsafe non-VBA syntax, export headers, hidden/control characters, joined block terminators, and duplicate procedure/property declarations before dispatch. Runtime binds current state before confirmation and verifies exact source/type read-back. Before replacing existing source, reuse its complete current observation, including verified after-state supplied in context; read only when absent or stale. If full source does not fit, read relevant source lines and use vba.module.patch. A snapshot/context error does not mean VBA access was denied. Use vba.module.rename for an identity-preserving rename.",
                WriteModuleSchema(), ToolEffect.Write, ToolVerification.Tool);
            yield return Projection(RenameModule,
                "Mutates document: Rename one existing VBA component without rewriting its source. Runtime guards both names, normalizes the destination, rejects collisions, journals both identities, and verifies source/type preservation. Textual references to the old component name are not rewritten.",
                RenameModuleSchema(), ToolEffect.Write, ToolVerification.Tool);
            yield return Projection(ApplyPatch,
                "Mutates document: Apply one ordered patch array to an existing moduleName. Later hunks see earlier edits; runtime validates the final source before one guarded write. Use current exact source excerpts or verified after-source. Matching, context and position rules are defined by the hunk fields. Ambiguity returns candidate locations. No first-match, replace-all or regex mode. Already-satisfied replacements are skipped; an all-no-op patch does not write. A missing target requires resolving its owning component; use vba.module.write only for intended creation, never to bypass a failed edit.",
                ApplyPatchSchema(), ToolEffect.Write, ToolVerification.Tool);
            yield return Projection(DeleteModule,
                "Mutates document: Delete an existing StdModule, ClassModule or verified CodeOnly MSForm. Runtime reads it, validates the type and absence of Designer state, and creates a rollback backup; no separate read call is required. Document modules belong to their Office object and cannot be removed as standalone VBA components. Forms with Designer state are unsupported because code backup cannot restore that state.",
                ModuleNameSchema(), ToolEffect.Write, ToolVerification.Tool);
            yield return Projection(RunMacro,
                "May execute arbitrary VBA code: Run an existing macro in the exact bound Excel, Word, or PowerPoint document without a manifest or allowlist. Pass module/procedure name; runtime replaces any incoming document qualifier with the bound document name before Office Application.Run. The macro may affect files or external state; use only when execution is requested and inspect state after the call.",
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
                VbaToolHandler.BindingFor(id),
                mutatesDocument: true);
        }
    }
}
