using System.Collections.Generic;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Skills
{
    public static class BuiltInSkillProvider
    {
        public static IReadOnlyList<SkillDefinition> GetSkills()
        {
            return new[]
            {
                Skill(
                    "common.task_planning",
                    "Common",
                    "Task planning",
                    "Break Office requests into safe, executable tool steps.",
                    new[] { "planning", "agent", "tools" },
                    "# Task Planning\n\nUse this skill when the user asks RNAssistant to act on Office content.\n\n- Decide whether the task needs document inspection, mutation, or only prose.\n- Use existing tools exactly by id; never invent tool ids.\n- Prefer small steps with clear arguments.\n- If data is missing, inspect the document or ask a concise question.\n- Stop after the local tool result is sufficient and answer normally."),
                Skill(
                    "common.tool_authoring",
                    "Common",
                    "Tool authoring",
                    "Design reusable pipeline or VBA tools safely.",
                    new[] { "authoring", "tools", "pipeline", "vba" },
                    "# Tool Authoring\n\nUse this skill when creating or editing executable RNAssistant tools.\n\n- Tools are executable actions, not guidance documents.\n- Pipeline tools call existing tool ids through ordered JSON steps.\n- VBA tools must be host-specific and require confirmation for mutations.\n- Keep schemas small and explicit.\n- Do not store secrets in tool code or metadata."),
                Skill(
                    "common.skill_authoring",
                    "Common",
                    "Skill authoring",
                    "Create markdown skills that guide agent behavior without executing actions.",
                    new[] { "authoring", "skills", "markdown" },
                    "# Skill Authoring\n\nUse this skill when creating or editing RNAssistant skills.\n\nA skill is a markdown instruction file, usually named SKILL.md. It should describe when to use the skill, the approach, constraints, and preferred tools. It must not pretend to execute actions by itself.\n\nRecommended sections:\n\n- Purpose\n- When to use\n- Workflow\n- Constraints\n- Useful tools"),
                Skill(
                    "excel.analysis_reporting",
                    "Excel",
                    "Excel analysis reporting",
                    "Analyze ranges, create summaries, tables, and charts in Excel.",
                    new[] { "excel", "analysis", "reporting", "charts" },
                    "# Excel Analysis Reporting\n\nUse this skill for Excel reporting tasks.\n\n- Inspect sheets/ranges before modifying unknown workbooks.\n- Write tables with stable headers and predictable start addresses.\n- Prefer chart source ranges that include headers.\n- Autofit after writing tables when available.\n- Keep generated sheets named clearly and avoid overwriting existing sheets unless asked."),
                Skill(
                    "word.document_editing",
                    "Word",
                    "Word document editing",
                    "Rewrite, insert, format, and review Word document content.",
                    new[] { "word", "editing", "review", "formatting" },
                    "# Word Document Editing\n\nUse this skill for Word drafting and editing tasks.\n\n- Read selection or document context before targeted edits.\n- Preserve user tone unless the user asks to change it.\n- Use insert/replace tools for document mutations.\n- Keep formatting changes explicit.\n- For review tasks, separate findings from suggested edits."),
                Skill(
                    "powerpoint.deck_building",
                    "PowerPoint",
                    "PowerPoint deck building",
                    "Create and improve slide structure, content, and speaker notes.",
                    new[] { "powerpoint", "slides", "deck", "notes" },
                    "# PowerPoint Deck Building\n\nUse this skill for slide creation and cleanup.\n\n- Create one clear idea per slide.\n- Use short titles and concise body bullets.\n- Keep slide order logical: context, evidence, recommendation, next steps.\n- Add speaker notes only when useful.\n- Do not overload slides with long paragraphs."),
                Skill(
                    "outlook.email_assistant",
                    "Outlook",
                    "Outlook email assistant",
                    "Draft, summarize, and reply to Outlook mail.",
                    new[] { "outlook", "email", "draft", "reply" },
                    "# Outlook Email Assistant\n\nUse this skill for email tasks.\n\n- Identify whether the user wants a draft, reply, summary, or extraction.\n- Match the requested tone and recipient context.\n- Keep replies concise unless asked otherwise.\n- Do not send mail unless the user explicitly requests sending and a tool supports it.\n- Preserve important dates, names, and commitments.")
            };
        }

        private static SkillDefinition Skill(string id, string host, string name, string description, string[] tags, string body)
        {
            return new SkillDefinition
            {
                Id = id,
                Host = host,
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
