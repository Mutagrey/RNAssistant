using System;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Tools
{
    // Canonical source-owned binding lookup used while catalog entries are built.
    // A captured ToolPack never reconstructs execution authority from an id.
    internal static class DirectToolBindingCatalog
    {
        internal static ToolBinding Resolve(string toolId)
        {
            if (string.Equals(toolId, ResourceToolCatalog.FindToolId,
                StringComparison.Ordinal)) return ResourceFindToolHandler.Binding;
            if (string.Equals(toolId, ResourceToolCatalog.ReadToolId,
                StringComparison.Ordinal)) return ResourceReadToolHandler.Binding;
            if (ExcelReadToolIds.Owns(toolId))
                return ExcelReadToolHandler.BindingFor(toolId);
            if (ResourceDefinitionToolHandler.Owns(toolId))
                return ResourceDefinitionToolHandler.BindingFor(toolId);
            if (ExcelWriteToolIds.Owns(toolId))
                return ExcelWriteToolHandler.Binding;
            if (ExcelFindReplaceToolIds.Owns(toolId))
                return ExcelFindReplaceToolHandler.BindingFor(toolId);
            if (ExcelSheetToolIds.Owns(toolId))
                return ExcelSheetToolHandler.BindingFor(toolId);
            if (ExcelRangeMutationToolIds.Owns(toolId))
                return ExcelRangeMutationToolHandler.BindingFor(toolId);
            if (ExcelTableToolIds.Owns(toolId))
                return ExcelTableToolHandler.Binding;
            if (ExcelChartToolIds.Owns(toolId))
                return ExcelChartToolHandler.BindingFor(toolId);
            if (WordToolIds.Owns(toolId))
                return WordToolHandler.BindingFor(toolId);
            if (PowerPointToolIds.Owns(toolId))
                return PowerPointToolHandler.BindingFor(toolId);
            if (OutlookToolIds.Owns(toolId))
                return OutlookToolHandler.BindingFor(toolId);
            if (VbaToolCatalog.Owns(toolId))
                return VbaToolHandler.BindingFor(toolId);
            if (string.Equals(toolId, UserQuestionToolCatalog.AskToolId,
                StringComparison.Ordinal)) return UserQuestionToolHandler.Binding;
            if (PlanDocumentToolCatalog.Owns(toolId))
                return PlanDocumentToolHandler.BindingFor(toolId);
            if (TaskListToolCatalog.Owns(toolId))
                return TaskListToolHandler.BindingFor(toolId);
            if (HtmlWorkspaceToolCatalog.Owns(toolId))
                return HtmlWorkspaceToolHandler.BindingFor(toolId);
            if (CapabilityToolCatalog.Owns(toolId))
                return CapabilityToolHandler.BindingFor(toolId);
            if (string.Equals(toolId, PromptToolCatalog.ReadToolId,
                StringComparison.Ordinal)) return PromptReadToolHandler.Binding;
            if (string.Equals(toolId, PromptToolCatalog.SaveToolId,
                StringComparison.Ordinal)) return PromptSaveToolHandler.Binding;
            if (ToolAuthoringCatalog.Owns(toolId))
                return ToolAuthoringMutationToolHandler.BindingFor(toolId);
            if (SkillAuthoringCatalog.Owns(toolId))
                return SkillAuthoringToolHandler.BindingFor(toolId);
            return null;
        }
    }
}
