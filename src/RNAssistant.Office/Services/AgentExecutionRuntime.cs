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
            tools.Add(tool.Clone());
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
            tools[tool.Id] = tool.Clone();
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
