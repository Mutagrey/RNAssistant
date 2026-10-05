using RNAssistant.Core.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    public sealed class ConversationRunPolicy
    {
        private static readonly HashSet<string> ChatToolIds = new HashSet<string>(
            new[]
            {
                "common.resources_find",
                "common.resources_read"
            },
            StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> PlanLocalToolIds = new HashSet<string>(new[]
        {
            "common.task_list_set",
            "common.plan_doc_save",
            "common.plan_doc_restore",
            "common.plan_doc_delete",
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

        public List<ToolCatalogEntry> SelectTools(IEnumerable<ToolCatalogEntry> tools)
        {
            var source = (tools ?? new ToolCatalogEntry[0]).Where(tool => tool != null);
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
