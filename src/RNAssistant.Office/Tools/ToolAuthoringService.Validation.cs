using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Tools
{
    internal sealed partial class ToolAuthoringService
    {
        internal static ToolAuthoringOutcome ValidateToolDefinition(
            ToolCatalogEntry tool)
        {
            if (tool == null || string.IsNullOrWhiteSpace(tool.Id))
            {
                return ToolAuthoringOutcome.Error("Tool id is required.");
            }
            if (tool.Id.Any(char.IsWhiteSpace))
            {
                return ToolAuthoringOutcome.Error("Tool id cannot contain whitespace: " + tool.Id);
            }
            if (tool.Id.Length > 128)
            {
                return ToolAuthoringOutcome.Error("Tool id is too long (maximum 128 characters).", null, "tool_definition_too_large", false);
            }
            if (string.IsNullOrWhiteSpace(tool.Host))
            {
                return ToolAuthoringOutcome.Error("Tool host is required.");
            }
            if ((tool.Name ?? string.Empty).Length > 200 ||
                (tool.Description ?? string.Empty).Length > 8000 ||
                (tool.ArgumentSchemaJson ?? string.Empty).Length > 64000 ||
                (tool.Code ?? string.Empty).Length > 1000000 ||
                (tool.Readme ?? string.Empty).Length > 500000 ||
                (tool.UseWhen ?? string.Empty).Length > 4000 ||
                (tool.DoNotUseWhen ?? string.Empty).Length > 4000 ||
                (tool.Limitations ?? string.Empty).Length > 4000)
            {
                return ToolAuthoringOutcome.Error("Tool definition exceeds a supported text size limit.", null, "tool_definition_too_large", false);
            }
            var componentsForSize = (tool.Components ?? new List<ToolPackageComponentDefinition>())
                .Where(component => component != null)
                .ToList();
            if (componentsForSize.Count > 50 ||
                componentsForSize.Any(component => (component.Code ?? string.Empty).Length > 1000000) ||
                componentsForSize.Sum(component => (long)(component.Code ?? string.Empty).Length) > 2000000)
            {
                return ToolAuthoringOutcome.Error("VBA package exceeds the supported component or source size limit.", null, "tool_definition_too_large", false);
            }
            if (!new[] { "Common", "Excel", "Word", "PowerPoint", "Outlook" }
                .Any(host => string.Equals(host, tool.Host, StringComparison.OrdinalIgnoreCase)))
            {
                return ToolAuthoringOutcome.Error("Unsupported tool host: " + tool.Host + ".", null, "invalid_tool_host", false);
            }
            if (tool.RiskLevel < 0 || tool.RiskLevel > 3)
            {
                return ToolAuthoringOutcome.Error("Tool riskLevel must be between 0 and 3.");
            }
            if (tool.MutatesDocument && tool.RiskLevel == 0)
            {
                return ToolAuthoringOutcome.Error("Document mutation tools require riskLevel between 1 and 3.");
            }

            var executor = (tool.Executor ?? string.Empty).Trim().ToLowerInvariant();
            if (executor == "pipeline")
            {
                return ToolAuthoringOutcome.Error("Pipelines are disabled during stabilization.", null, "pipeline_disabled", false);
            }
            if (executor != "vba")
            {
                return ToolAuthoringOutcome.Error("Tool executor must be vba.");
            }

            JObject normalizedSchema;
            string schemaError;
            if (!ToolSchemaSupport.TryParse(tool, out normalizedSchema, out schemaError))
            {
                return ToolAuthoringOutcome.Error(schemaError, null, "invalid_tool_schema", false);
            }

            if (executor == "vba" && string.IsNullOrWhiteSpace(tool.Code))
            {
                return ToolAuthoringOutcome.Error("VBA tool requires code.");
            }

            if (executor == "vba")
            {
                var manifest = new VbaToolManifestParser().Parse(tool.Code);
                if (!manifest.Success)
                {
                    return ToolAuthoringOutcome.Error(manifest.ErrorMessage, null, manifest.ErrorCode, false);
                }
                if (!string.Equals(tool.Id, manifest.Tool.Id, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(tool.Host, manifest.Tool.Host, StringComparison.OrdinalIgnoreCase))
                {
                    return ToolAuthoringOutcome.Error("tool.json id/host must match the VBA manifest.", null, "vba_manifest_metadata_mismatch", false);
                }
                tool.Name = manifest.Tool.Name;
                tool.Description = manifest.Tool.Description;
                tool.ArgumentSchemaJson = manifest.Tool.ArgumentSchemaJson;
                tool.EntryPoint = manifest.Tool.EntryPoint;
                tool.PackageVersion = manifest.Tool.PackageVersion;
                tool.ArgumentOrder = manifest.Tool.ArgumentOrder;
                tool.MutatesDocument = manifest.Tool.MutatesDocument;
                tool.AgentCanRun = manifest.Tool.AgentCanRun;
                tool.RequiresConfirmation = manifest.Tool.RequiresConfirmation;
                tool.RiskLevel = manifest.Tool.RiskLevel;
                if ((tool.Name ?? string.Empty).Length > 200 ||
                    (tool.Description ?? string.Empty).Length > 8000 ||
                    (tool.ArgumentSchemaJson ?? string.Empty).Length > 64000)
                {
                    return ToolAuthoringOutcome.Error("VBA manifest metadata exceeds a supported size limit.", null, "tool_definition_too_large", false);
                }
                if (tool.Components == null || tool.Components.Count == 0)
                {
                    tool.Components = manifest.Tool.Components;
                }
                var declared = new HashSet<string>(manifest.Tool.Components.Select(component => component.Name), StringComparer.OrdinalIgnoreCase);
                var components = (tool.Components ?? new List<ToolPackageComponentDefinition>()).Where(component => component != null).ToList();
                var duplicate = components.Where(component => !string.IsNullOrWhiteSpace(component.Name))
                    .GroupBy(component => component.Name, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault(group => group.Count() > 1);
                if (duplicate != null)
                {
                    return ToolAuthoringOutcome.Error("VBA package contains a duplicate component: " + duplicate.Key, null, "vba_component_duplicate", false);
                }
                var invalid = components.FirstOrDefault(component =>
                    !VbaToolManifestParser.ValidComponentName(component.Name) ||
                    (!string.Equals(component.Type, "StdModule", StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(component.Type, "ClassModule", StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(component.Type, "MSForm", StringComparison.OrdinalIgnoreCase)));
                if (invalid != null)
                {
                    return ToolAuthoringOutcome.Error("VBA package component name/type is invalid: " + (invalid.Name ?? string.Empty), null, "vba_component_invalid", false);
                }
                var designerExport = components.FirstOrDefault(component =>
                    string.Equals(component.Type, "MSForm", StringComparison.OrdinalIgnoreCase) &&
                    VbaToolManifestParser.ContainsUserFormDesignerExport(component.Code));
                if (designerExport != null)
                {
                    return ToolAuthoringOutcome.Error(
                        "VBA package MSForm must contain code-behind only, not exported Designer/FRX metadata: " + designerExport.Name,
                        null,
                        "vba_userform_designer_unsupported",
                        false);
                }
                var unexpected = components.FirstOrDefault(component => !declared.Contains(component.Name));
                if (unexpected != null)
                {
                    return ToolAuthoringOutcome.Error("VBA package contains an undeclared component: " + unexpected.Name, null, "vba_component_undeclared", false);
                }
                var entryName = manifest.Tool.Components[0].Name;
                var entry = components.FirstOrDefault(component => string.Equals(component.Name, entryName, StringComparison.OrdinalIgnoreCase));
                if (entry != null && !string.Equals(entry.Type, "StdModule", StringComparison.OrdinalIgnoreCase))
                {
                    return ToolAuthoringOutcome.Error("VBA entry component must be a StdModule: " + entryName, null, "vba_entry_component_type", false);
                }
                var supplied = new HashSet<string>(components.Where(component => !string.IsNullOrWhiteSpace(component.Code)).Select(component => component.Name), StringComparer.OrdinalIgnoreCase)
                {
                    entryName
                };
                var missing = declared.FirstOrDefault(name => !supplied.Contains(name));
                if (!string.IsNullOrWhiteSpace(missing))
                {
                    return ToolAuthoringOutcome.Error("VBA package source is missing declared component: " + missing, null, "vba_component_missing", false);
                }
            }

            return ToolAuthoringOutcome.Ok("Tool definition is valid.");
        }

    }
}
