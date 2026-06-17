using RNAssistant.Core.Models;

namespace RNAssistant.Core.Skills
{
    public interface ISkill
    {
        SkillDefinition Definition { get; }
        SkillResult Execute(SkillCommand command);
    }
}

