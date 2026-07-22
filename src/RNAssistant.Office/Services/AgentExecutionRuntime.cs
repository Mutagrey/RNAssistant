using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    internal static class AgentTaskContinuationResolver
    {
        public static bool ShouldContinue(string userText, ChatSession session)
        {
            return session != null &&
                session.PendingAgentTask != null &&
                !string.IsNullOrWhiteSpace(session.PendingAgentTask.Request) &&
                LooksLikeShortFollowUp(userText);
        }

        public static string Resolve(string userText, ChatSession session)
        {
            if (!ShouldContinue(userText, session))
            {
                if (session != null)
                {
                    session.PendingAgentTask = null;
                }
                return userText ?? string.Empty;
            }

            return session.PendingAgentTask.Request.Trim() +
                "\n\nUSER_FOLLOW_UP:\n" +
                (userText ?? string.Empty).Trim();
        }

        private static bool LooksLikeShortFollowUp(string userText)
        {
            var value = (userText ?? string.Empty).Trim().ToLowerInvariant();
            if (value.Length == 0 || value.Length > 120)
            {
                return false;
            }

            if (value.All(char.IsDigit))
            {
                return true;
            }

            var prefixes = new[]
            {
                "да", "нет", "ок", "окей", "хорошо", "верно", "именно", "так и", "сделай", "делай",
                "продолж", "попроб", "давай", "соглас", "подтверж", "перв", "втор", "трет",
                "yes", "no", "ok", "okay", "correct", "exactly", "do it", "proceed", "continue", "try"
            };
            return prefixes.Any(prefix => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
    }

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
            ApplySafety(all);
            return all;
        }

        public void Refresh(ICollection<ToolDefinition> tools, ToolDefinition tool)
        {
            if (tools == null || tool == null || string.IsNullOrWhiteSpace(tool.Id))
            {
                return;
            }

            var existing = Find(tools, tool.Id);
            if (existing != null && existing.BuiltIn)
            {
                return;
            }
            if (existing != null)
            {
                tools.Remove(existing);
            }
            tools.Add(Clone(tool));
            ApplySafety(tools);
        }

        private static void ApplySafety(IEnumerable<ToolDefinition> tools)
        {
            var all = (tools ?? new ToolDefinition[0]).Where(tool => tool != null).ToList();
            var profiles = ToolSafetyPolicy.ResolveAll(all);
            foreach (var tool in all)
            {
                ToolSafetyProfile profile;
                if (!profiles.TryGetValue(tool.Id ?? string.Empty, out profile))
                {
                    continue;
                }
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
            ToolDefinition existing;
            if (tools.TryGetValue(tool.Id, out existing) && existing.BuiltIn && !tool.BuiltIn)
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

    internal static class PlannerBatchPolicy
    {
        public static string Validate(
            IReadOnlyList<ToolCommand> commands,
            IReadOnlyList<ToolDefinition> tools,
            RoutedTask route,
            AppSettings settings)
        {
            if (commands == null || commands.Count <= 1)
            {
                return null;
            }

            settings = settings ?? new AppSettings();
            var selectedTools = commands
                .Select(command => AgentToolCatalogResolver.Find(tools, command == null ? null : command.ToolId))
                .Where(tool => tool != null)
                .ToList();

            if (selectedTools.Count != commands.Count)
            {
                return "Planner batch contains an unresolved tool.";
            }

            if (selectedTools.Any(tool => tool.MutatesDocument))
            {
                return "Document mutation plans may contain exactly one action.";
            }

            if (IsVbaRouteOrTool(route, selectedTools))
            {
                return "VBA plans may contain exactly one action.";
            }

            var pureReadOnly = selectedTools.All(tool =>
                !tool.MutatesDocument &&
                !tool.MutatesLocalState &&
                !tool.RequiresConfirmation);
            var limit = pureReadOnly
                ? Math.Max(1, settings.MaxAgentReadOnlyPlanSteps)
                : Math.Max(1, settings.MaxAgentPlanSteps);
            if (commands.Count > limit)
            {
                return pureReadOnly
                    ? "Read-only planner batch exceeds the configured limit of " + limit + " actions."
                    : "Planner batch exceeds the configured limit of " + limit + " actions.";
            }

            return null;
        }

        private static bool IsVbaRouteOrTool(RoutedTask route, IEnumerable<ToolDefinition> tools)
        {
            if (route != null &&
                (string.Equals(route.TaskType, "vba", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(route.TaskType, "macro_execution", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return (tools ?? new ToolDefinition[0]).Any(tool =>
                tool != null &&
                ((tool.Id ?? string.Empty).IndexOf("vba", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 string.Equals(tool.Executor, "vba", StringComparison.OrdinalIgnoreCase)));
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
                HasSuccessfulMutation(route, observations))
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

        private static bool HasSuccessfulMutation(RoutedTask route, IEnumerable<AgentObservation> observations)
        {
            var localMutationCompletesTask = route != null &&
                (string.Equals(route.TaskType, "tool_authoring", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(route.TaskType, "html", StringComparison.OrdinalIgnoreCase));
            return (observations ?? new AgentObservation[0]).Any(o =>
                o != null &&
                string.Equals(o.Status, "success", StringComparison.OrdinalIgnoreCase) &&
                (o.Mutation || localMutationCompletesTask && o.LocalMutation));
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
