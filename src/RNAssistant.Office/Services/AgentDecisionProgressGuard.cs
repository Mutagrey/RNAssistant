using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    internal sealed class AgentDecisionProgressGuard
    {
        private readonly HashSet<string> _successfulReads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _completedParameterlessReads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public bool PlanDecisionAllowed(AgentRunState state)
        {
            return state == null || !state.PlanDeclared && state.TotalToolSteps == 0;
        }

        public IReadOnlyList<ToolDefinition> FilterAvailableTools(IEnumerable<ToolDefinition> tools)
        {
            return (tools ?? new ToolDefinition[0])
                .Where(tool => tool != null && !_completedParameterlessReads.Contains(tool.Id ?? string.Empty))
                .ToList();
        }

        public string ValidateCommands(IEnumerable<ToolCommand> commands, IEnumerable<ToolDefinition> tools)
        {
            foreach (var command in commands ?? new ToolCommand[0])
            {
                var tool = AgentToolCatalogResolver.Find(tools, command == null ? null : command.ToolId);
                var profile = ToolSafetyPolicy.Resolve(tool, tools);
                if (tool == null || profile == null || !profile.Valid || profile.MutatesDocument || profile.MutatesLocalState)
                {
                    continue;
                }

                if (_successfulReads.Contains(CommandSignature(command)))
                {
                    return "Read-only tool " + tool.Id + " with the same arguments already succeeded. " +
                        "Use its existing observation and choose the next mutation, a different required read, or finish.";
                }
            }

            return null;
        }

        public void RecordToolResult(ToolCommand command, ToolDefinition tool, ToolResult result, IEnumerable<ToolDefinition> tools)
        {
            if (command == null || tool == null || result == null || !result.Success)
            {
                return;
            }

            var profile = ToolSafetyPolicy.Resolve(tool, tools);
            if (profile == null || !profile.Valid)
            {
                return;
            }
            if (profile.MutatesDocument || profile.MutatesLocalState)
            {
                _successfulReads.Clear();
                _completedParameterlessReads.Clear();
                return;
            }

            _successfulReads.Add(CommandSignature(command));
            if (command.Arguments == null || command.Arguments.Count == 0)
            {
                _completedParameterlessReads.Add(tool.Id);
            }
        }

        private static string CommandSignature(ToolCommand command)
        {
            var arguments = command == null || command.Arguments == null
                ? new JObject()
                : JObject.FromObject(command.Arguments);
            return (command == null ? string.Empty : command.ToolId ?? string.Empty).Trim().ToLowerInvariant() + "|" +
                SortToken(arguments).ToString(Formatting.None);
        }

        private static JToken SortToken(JToken token)
        {
            var obj = token as JObject;
            if (obj != null)
            {
                var sorted = new JObject();
                foreach (var property in obj.Properties().OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
                {
                    sorted[property.Name] = SortToken(property.Value);
                }
                return sorted;
            }

            var array = token as JArray;
            if (array != null)
            {
                return new JArray(array.Select(SortToken));
            }

            return token == null ? JValue.CreateNull() : token.DeepClone();
        }
    }
}
