using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal static class BuiltInSkillProvider
    {
        public static IReadOnlyList<SkillDefinition> GetSkills(IOfficeApplicationAdapter adapter)
        {
            var result = new List<SkillDefinition>
            {
                Skill(
                    "common.task_planning",
                    "Task planning",
                    "Break Office requests into safe, executable tool steps.",
                    new[] { "planning", "agent", "tools" },
                    "# Task Planning\n\nUse this skill when the user asks RNAssistant to act on Office content.\n\n- Decide whether the task needs document inspection, mutation, or only prose.\n- Use existing tools exactly by id; never invent tool ids.\n- Prefer small steps with clear arguments.\n- If data is missing, inspect the document or ask a concise question.\n- Stop after the local tool result is sufficient and answer normally."),
                Skill(
                    "common.tool_authoring",
                    "Tool authoring",
                    "Design reusable pipeline or VBA tools safely.",
                    new[] { "authoring", "tools", "pipeline", "vba" },
                    "# Tool Authoring\n\nUse this skill when creating or editing executable RNAssistant tools.\n\n- Tools are executable actions, not guidance documents.\n- Pipeline tools call existing tool ids through ordered JSON steps.\n- VBA tools must be host-specific and require confirmation for mutations.\n- Keep schemas small and explicit.\n- Do not store secrets in tool code or metadata."),
                Skill(
                    "common.skill_authoring",
                    "Skill authoring",
                    "Create markdown skills that guide agent behavior without executing actions.",
                    new[] { "authoring", "skills", "markdown" },
                    "# Skill Authoring\n\nUse this skill when creating or editing RNAssistant skills.\n\nA skill is a markdown instruction file, usually named SKILL.md. It should describe when to use the skill, the approach, constraints, and preferred tools. It must not pretend to execute actions by itself.\n\nRecommended sections:\n\n- Purpose\n- When to use\n- Workflow\n- Constraints\n- Useful tools")
            };

            var hostProvider = adapter as IOfficeBuiltInSkillProvider;
            if (hostProvider != null)
            {
                result.AddRange((hostProvider.GetBuiltInSkills() ?? new SkillDefinition[0]).Where(skill => skill != null));
            }

            return result;
        }

        private static SkillDefinition Skill(string id, string name, string description, string[] tags, string body)
        {
            return new SkillDefinition
            {
                Id = id,
                Host = "Common",
                Name = name,
                Description = description,
                Tags = new List<string>(tags ?? new string[0]),
                BodyMarkdown = body,
                Enabled = true,
                BuiltIn = true
            };
        }
    }
}
