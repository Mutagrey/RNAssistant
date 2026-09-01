using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    internal sealed class ConversationRunPolicy
    {
        private static readonly HashSet<string> ChatToolIds = new HashSet<string>(
            new[]
            {
                ResourceToolCatalog.ListToolId,
                ResourceToolCatalog.ResolveToolId,
                ResourceToolCatalog.SearchToolId,
                ResourceToolCatalog.ReadToolId
            },
            StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> PlanLocalToolIds = new HashSet<string>(new[]
        {
            TaskListToolExecutor.CreateToolId,
            TaskListToolExecutor.UpdateToolId,
            TaskListToolExecutor.CloseToolId,
            PlanDocumentToolCatalog.CreateToolId,
            PlanDocumentToolCatalog.UpdateToolId,
            PlanDocumentToolCatalog.RestoreToolId,
            PlanDocumentToolCatalog.DeleteToolId,
            UserQuestionToolCatalog.AskToolId
        }, StringComparer.OrdinalIgnoreCase);

        private ConversationRunPolicy(string mode)
        {
            Mode = ChatModes.Normalize(mode);
        }

        public string Mode { get; private set; }

        public bool AllowsSkills
        {
            get { return !string.Equals(Mode, ChatModes.Chat, StringComparison.Ordinal); }
        }

        public bool AllowsConfirmation
        {
            get { return string.Equals(Mode, ChatModes.Agent, StringComparison.Ordinal); }
        }

        public static ConversationRunPolicy For(string mode)
        {
            return new ConversationRunPolicy(mode);
        }

        public List<ToolDefinition> SelectTools(IEnumerable<ToolDefinition> tools)
        {
            var source = (tools ?? new ToolDefinition[0]).Where(tool => tool != null);
            if (string.Equals(Mode, ChatModes.Agent, StringComparison.Ordinal))
            {
                return source.ToList();
            }

            if (string.Equals(Mode, ChatModes.Plan, StringComparison.Ordinal))
            {
                return source.Where(tool => tool.AgentCanRun &&
                        !tool.MutatesDocument &&
                        !tool.RequiresConfirmation &&
                        (!tool.MutatesLocalState || PlanLocalToolIds.Contains(tool.Id ?? string.Empty)))
                    .OrderBy(tool => tool.Id, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            return source.Where(tool =>
                    ChatToolIds.Contains(tool.Id ?? string.Empty) &&
                    tool.BuiltIn &&
                    tool.AgentCanRun &&
                    !tool.MutatesDocument &&
                    !tool.MutatesLocalState &&
                    !tool.RequiresConfirmation)
                .OrderBy(tool => tool.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public List<SkillDefinition> SelectSkills(IEnumerable<SkillDefinition> skills)
        {
            if (!AllowsSkills) return new List<SkillDefinition>();
            return (skills ?? new SkillDefinition[0])
                .Where(skill => skill != null && skill.Enabled)
                .ToList();
        }
    }
}
