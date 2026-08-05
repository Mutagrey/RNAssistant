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
                    "common.vba_tool_authoring",
                    "VBA tool authoring",
                    "Create versioned RNAssistant VBA tool packages with a strict manifest and String-returning entry function.",
                    new[] { "authoring", "tools", "vba", "macro", "excel", "word", "powerpoint" },
                    "# VBA Tool Authoring\n\nUse this skill only for reusable VBA tools. Prefer an existing built-in or pipeline tool first.\n\n" +
                    "## Package\n\n- The global AppData package is canonical. Put source in `src/*.bas` and `src/*.cls`.\n- The first component is a standard entry module. Supporting standard/class modules are allowed. UserForms, document modules and binary FRX assets are not supported in v1.\n- Component names start with a Latin letter, contain only letters/numbers/underscore and are at most 40 characters. Use `RNA_<Tool>` for the entry module, `RNATool_<Tool>` for the function and `RNA_<Tool>_<Role>` for dependencies.\n\n" +
                    "## Manifest and signature\n\n- Put exactly one comment-delimited JSON object between `<RNAssistantTool>` and `</RNAssistantTool>` immediately before the entry function.\n- Required fields: protocolVersion=1, id, name, description, host, packageVersion, entryPoint, components, argumentOrder, formal parameters JSON Schema, mutatesDocument, agentCanRun and requiresConfirmation.\n- Use `additionalProperties:false`. Only String, Long, Double and Boolean arguments are allowed, with at most 30 positional arguments. Every function argument is `ByVal`, follows argumentOrder and matches its schema type. Optional arguments require a schema default.\n- The entry declaration is `Public Function ... As String`. Return a concise useful String. Raise a normal VBA error for failure; RNAssistant creates the JSON result envelope. Do not add a VBA JSON parser or manually build a result envelope.\n\n" +
                    "## Code rules\n\n- Use `Option Explicit`, deterministic names and explicit Office object references. Avoid Select/Activate where a direct object reference works.\n- Do not embed secrets, network credentials or machine-specific paths. Validate sheet/range/document inputs before mutation and leave application-wide settings restored in an error handler.\n- Never auto-run newly generated code. Validate and save the package first; installation is a separate confirmed operation. Safety behavior after installation follows manifest metadata."),
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
