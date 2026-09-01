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

        internal static bool Owns(string toolId)
        {
            return string.Equals(toolId, UpsertToolId,
                    StringComparison.Ordinal) ||
                string.Equals(toolId, DeleteToolId,
                    StringComparison.Ordinal);
        }

        internal static bool IsMutation(string toolId)
        {
            return Owns(toolId);
        }

        internal static IEnumerable<ToolDefinition> GetTools(
            SkillAuthoringService service)
        {
            if (service == null) throw new ArgumentNullException(nameof(service));
            if (!service.CanUse) yield break;

            yield return Projection(
                UpsertToolId,
                "Mutates settings: Create/update either one custom skill core or one direct references/*.md file per call. Never mix core fields with referencePath/referenceMarkdown. Omitted core fields are preserved; use strict mode only when existence itself matters.",
                UpsertSchema(), "skills_upsert");
            yield return Projection(
                DeleteToolId,
                "Mutates settings: Delete one custom skill, or delete one direct Markdown reference when referencePath is supplied.",
                DeleteSchema(), "skills_delete");
        }

        private static ToolDefinition Projection(
            string id, string description, string schema, string name)
        {
            return ControllerToolDefinition.CreateTypedProjection(
                new ToolDescriptor(id, description, schema),
                new ToolPolicy(ToolEffect.Write,
                    ToolVerification.Tool, true, false,
                    new[] { "agent" }, 1),
                name: name, scope: "global",
                mutatesLocalState: true);
        }
    }
}
