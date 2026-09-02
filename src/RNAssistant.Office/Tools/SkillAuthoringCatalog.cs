using System;
using System.Collections.Generic;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Tools
{
    internal static partial class SkillAuthoringCatalog
    {
        internal const string UpsertToolId = "common.skills_upsert";
        internal const string DeleteToolId = "common.skills_delete";
        internal const string ReferenceUpsertToolId =
            "common.skills_reference_upsert";
        internal const string ReferenceDeleteToolId =
            "common.skills_reference_delete";

        internal static bool Owns(string toolId)
        {
            return string.Equals(toolId, UpsertToolId,
                    StringComparison.Ordinal) ||
                string.Equals(toolId, DeleteToolId,
                    StringComparison.Ordinal) ||
                string.Equals(toolId, ReferenceUpsertToolId,
                    StringComparison.Ordinal) ||
                string.Equals(toolId, ReferenceDeleteToolId,
                    StringComparison.Ordinal);
        }

        internal static bool IsMutation(string toolId)
        {
            return Owns(toolId);
        }

        internal static IEnumerable<ToolCatalogEntry> GetTools(
            SkillAuthoringService service)
        {
            if (service == null) throw new ArgumentNullException(nameof(service));
            if (!service.CanUse) yield break;

            yield return Projection(
                UpsertToolId,
                "Mutates settings: Create or update one custom skill core. Omitted fields are preserved; references use their dedicated operations.",
                SchemaFor(UpsertToolId), "skills_upsert");
            yield return Projection(
                DeleteToolId,
                "Mutates settings: Delete one complete custom skill by exact id.",
                SchemaFor(DeleteToolId), "skills_delete");
            yield return Projection(
                ReferenceUpsertToolId,
                "Mutates settings: Create or replace one direct references/*.md file in an existing custom skill.",
                SchemaFor(ReferenceUpsertToolId),
                "skills_reference_upsert");
            yield return Projection(
                ReferenceDeleteToolId,
                "Mutates settings: Delete one exact direct Markdown reference from an existing custom skill.",
                SchemaFor(ReferenceDeleteToolId),
                "skills_reference_delete");
        }

        internal static string SchemaFor(string toolId)
        {
            if (string.Equals(toolId, UpsertToolId,
                    StringComparison.Ordinal)) return UpsertSchema();
            if (string.Equals(toolId, DeleteToolId,
                    StringComparison.Ordinal)) return DeleteSchema();
            if (string.Equals(toolId, ReferenceUpsertToolId,
                    StringComparison.Ordinal)) return ReferenceUpsertSchema();
            if (string.Equals(toolId, ReferenceDeleteToolId,
                    StringComparison.Ordinal)) return ReferenceDeleteSchema();
            throw new ArgumentException("Unknown skill authoring id: " +
                toolId, nameof(toolId));
        }

        private static ToolCatalogEntry Projection(
            string id, string description, string schema, string name)
        {
            return ControllerToolCatalogEntry.CreateTypedProjection(
                new ToolDescriptor(id, description, schema),
                new ToolPolicy(ToolEffect.Write,
                    ToolVerification.Tool, true, false,
                    new[] { "agent" }, 1),
                name: name, scope: "global",
                mutatesLocalState: true);
        }
    }
}
