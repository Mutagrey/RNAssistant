using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Skills
{
    public sealed class SkillRegistry
    {
        private readonly Dictionary<string, ISkill> _skills;

        public SkillRegistry()
        {
            _skills = new Dictionary<string, ISkill>(StringComparer.OrdinalIgnoreCase);
        }

        public void Register(ISkill skill)
        {
            if (skill == null || skill.Definition == null || string.IsNullOrWhiteSpace(skill.Definition.Id))
            {
                return;
            }

            _skills[skill.Definition.Id] = skill;
        }

        public IReadOnlyList<SkillDefinition> Definitions(string host)
        {
            return _skills.Values
                .Select(s => s.Definition)
                .Where(d => d.Enabled && (string.Equals(d.Host, host, StringComparison.OrdinalIgnoreCase) || string.Equals(d.Host, "Common", StringComparison.OrdinalIgnoreCase)))
                .OrderBy(d => d.Host)
                .ThenBy(d => d.Id)
                .ToList();
        }

        public SkillResult Execute(SkillCommand command)
        {
            if (command == null || string.IsNullOrWhiteSpace(command.SkillId))
            {
                return SkillResult.Fail("Skill command is empty.");
            }

            ISkill skill;
            if (!_skills.TryGetValue(command.SkillId, out skill))
            {
                return SkillResult.Fail("Unknown skill: " + command.SkillId);
            }

            return skill.Execute(command);
        }
    }
}

