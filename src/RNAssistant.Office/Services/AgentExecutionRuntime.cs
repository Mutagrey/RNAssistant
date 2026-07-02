using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    internal sealed class AgentToolCatalogResolver
    {
        private readonly OfficeToolExecutor _toolExecutor;
        private readonly bool _includeControllerTools;

        public AgentToolCatalogResolver(OfficeToolExecutor toolExecutor, bool includeControllerTools)
        {
            _toolExecutor = toolExecutor;
            _includeControllerTools = includeControllerTools;
        }

        public List<ToolDefinition> Resolve(IReadOnlyList<ToolDefinition> tools)
        {
            var result = new Dictionary<string, ToolDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var tool in tools ?? new ToolDefinition[0])
            {
                Add(result, tool);
            }
            foreach (var tool in _includeControllerTools ? _toolExecutor.GetControllerTools() : new ToolDefinition[0])
            {
                Add(result, tool);
            }

            var all = result.Values.ToList();
            foreach (var tool in all)
            {
                var profile = ToolSafetyPolicy.Resolve(tool, all);
                if (!profile.Valid)
                {
                    tool.CapabilityStatus = "unavailable";
                    tool.Limitations = profile.Error;
                    continue;
                }
                tool.MutatesDocument = profile.MutatesDocument;
                tool.MutatesLocalState = profile.MutatesLocalState;
                tool.RequiresConfirmation = profile.RequiresConfirmation;
                tool.RiskLevel = profile.RiskLevel;
                tool.AgentCanRun = profile.AgentCanRun;
            }
            return all;
        }

        public static ToolDefinition Find(IEnumerable<ToolDefinition> tools, string id)
        {
            return (tools ?? new ToolDefinition[0]).FirstOrDefault(t =>
                t != null && string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        private static void Add(IDictionary<string, ToolDefinition> tools, ToolDefinition tool)
        {
            if (tool == null || string.IsNullOrWhiteSpace(tool.Id))
            {
                return;
            }
            tools[tool.Id] = Clone(tool);
        }

        private static ToolDefinition Clone(ToolDefinition tool)
        {
            return new ToolDefinition
            {
                Id = tool.Id,
                Host = tool.Host,
                Name = tool.Name,
                Description = tool.Description,
                ArgumentSchemaJson = tool.ArgumentSchemaJson,
                Executor = tool.Executor,
                RequiresConfirmation = tool.RequiresConfirmation,
                MutatesDocument = tool.MutatesDocument,
                MutatesLocalState = tool.MutatesLocalState,
                AgentCanRun = tool.AgentCanRun,
                PipelineJson = tool.PipelineJson,
                Code = tool.Code,
                Readme = tool.Readme,
                StoragePath = tool.StoragePath,
                Enabled = tool.Enabled,
                BuiltIn = tool.BuiltIn,
                RiskLevel = tool.RiskLevel,
                UseWhen = tool.UseWhen,
                DoNotUseWhen = tool.DoNotUseWhen,
                ExamplesJson = tool.ExamplesJson,
                PreconditionsJson = tool.PreconditionsJson,
                VerifyJson = tool.VerifyJson,
                CapabilityStatus = tool.CapabilityStatus,
                Limitations = tool.Limitations,
                ReplacementToolId = tool.ReplacementToolId
            };
        }
    }

    internal static class AgentPhaseController
    {
        public static bool IsRouteComplete(RoutedTask route, bool pendingVerification)
        {
            return route == null ||
                !route.RequiresTool ||
                (!pendingVerification && string.Equals(route.Phase, AgentPhases.Final, StringComparison.OrdinalIgnoreCase));
        }

        public static bool RequiresRiskConfirmation(int riskLevel, AppSettings settings)
        {
            settings = settings ?? new AppSettings();
            return riskLevel >= 2 && !settings.AutoConfirmToolActions;
        }

        public static void Advance(RoutedTask route, IReadOnlyList<AgentObservation> observations, bool pendingVerification)
        {
            if (route == null)
            {
                return;
            }
            if (string.Equals(route.Phase, AgentPhases.ReadOnly, StringComparison.OrdinalIgnoreCase) && HasSuccessfulRead(observations))
            {
                if (RequiresMutationPhase(route.Mode))
                {
                    route.Phase = AgentPhases.Mutation;
                    if (string.Equals(route.Mode, "destructive_mutation", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(route.Mode, "high_risk_execution", StringComparison.OrdinalIgnoreCase))
                    {
                        route.RiskAllowed = Math.Max(route.RiskAllowed, 3);
                    }
                }
                else
                {
                    route.Phase = AgentPhases.Final;
                }
            }
            else if (string.Equals(route.Phase, AgentPhases.Mutation, StringComparison.OrdinalIgnoreCase) &&
                HasSuccessfulMutation(observations))
            {
                route.Phase = AgentPhases.Final;
            }
            else if (string.Equals(route.Phase, AgentPhases.Verification, StringComparison.OrdinalIgnoreCase) &&
                !pendingVerification &&
                HasSuccessfulVerification(observations))
            {
                route.Phase = AgentPhases.Final;
            }
        }

        private static bool HasSuccessfulRead(IEnumerable<AgentObservation> observations)
        {
            return (observations ?? new AgentObservation[0]).Any(o =>
                o != null &&
                string.Equals(o.Status, "success", StringComparison.OrdinalIgnoreCase) &&
                !o.Mutation &&
                !o.LocalMutation &&
                !string.Equals(o.Purpose, AgentObservationPurposes.Verification, StringComparison.OrdinalIgnoreCase));
        }

        private static bool HasSuccessfulMutation(IEnumerable<AgentObservation> observations)
        {
            return (observations ?? new AgentObservation[0]).Any(o =>
                o != null &&
                string.Equals(o.Status, "success", StringComparison.OrdinalIgnoreCase) &&
                (o.Mutation || o.LocalMutation));
        }

        private static bool HasSuccessfulVerification(IEnumerable<AgentObservation> observations)
        {
            return (observations ?? new AgentObservation[0]).Any(o =>
                o != null &&
                string.Equals(o.Status, "success", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(o.Purpose, AgentObservationPurposes.Verification, StringComparison.OrdinalIgnoreCase));
        }

        private static bool RequiresMutationPhase(string mode)
        {
            return !string.IsNullOrWhiteSpace(mode) &&
                (mode.IndexOf("mutate", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 mode.IndexOf("mutation", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 string.Equals(mode, "high_risk_execution", StringComparison.OrdinalIgnoreCase));
        }
    }
}
