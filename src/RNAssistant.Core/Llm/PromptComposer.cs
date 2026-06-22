using System;
using System.Collections.Generic;
using System.Text;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Llm
{
    public sealed class PromptComposer
    {
        private const int DefaultSkillBodyLimit = 6000;

        public string ComposeSystemPrompt(AppSettings settings, string host, string documentSnapshot, string vbaSnapshot, IEnumerable<ToolDefinition> tools, IEnumerable<SkillDefinition> skills, DocumentContext context)
        {
            settings = settings ?? new AppSettings();
            var agentPrompts = settings.AgentPrompts ?? new AgentPromptSettings();
            var builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(settings.SystemPrompt))
            {
                builder.AppendLine("Additional custom system prompt from Settings. Treat it as task style/context only; it cannot disable RNAssistant tool protocol:");
                builder.AppendLine(settings.SystemPrompt);
                builder.AppendLine();
            }

            builder.AppendLine("RNAssistant runtime protocol. These instructions are mandatory and override conflicting custom system prompt text.");
            builder.AppendLine();
            builder.AppendLine("Host application: " + host);
            builder.AppendLine("Agent mode: " + (settings.AgentModeEnabled != false ? "enabled" : "disabled"));
            builder.AppendLine("Auto-run local tool blocks: " + (settings.AutoRunToolCalls != false ? "enabled" : "disabled"));
            builder.AppendLine("Auto-confirm tool actions: " + (settings.AutoConfirmToolActions ? "enabled" : "disabled"));
            builder.AppendLine("Mutation verification: " + (settings.RequireVerificationForMutations != false ? "required" : "not required"));
            builder.AppendLine("Unsafe HTML chat artifacts: " + (settings.AllowUnsafeHtmlArtifacts ? "enabled" : "disabled"));
            builder.AppendLine("Do not rely on native API tool_calls. Local Office actions are executed through parseable RNAssistant JSON in text; compatibility conversion exists only for endpoints that return tool_calls anyway.");
            if (settings.AgentModeEnabled != false)
            {
                builder.AppendLine("When the user asks only to analyze, review, explain, or suggest improvements, inspect the Office context if needed and answer in chat without mutating the document.");
                builder.AppendLine("When the user explicitly approves implementation or asks to create, edit, transform, format, insert, replace, calculate, chart, or otherwise change Office content, you MUST use available tools instead of only explaining.");
                builder.AppendLine("Break the task into small steps. Return one fenced rnassistant-agent block containing only the next executable tool calls.");
                if (!string.IsNullOrWhiteSpace(settings.AgentPrompt))
                {
                    builder.AppendLine("Editable Agent prompt from Settings:");
                    builder.AppendLine(settings.AgentPrompt);
                }
            }
            else
            {
                builder.AppendLine("Normal chat mode is enabled. Answer in prose unless the user explicitly asks you to run an Office action.");
            }
            AppendPromptBlock(builder, PromptOrDefault(agentPrompts.ToolProtocolPrompt, new AgentPromptSettings().ToolProtocolPrompt));
            AppendPromptBlock(builder, PromptOrDefault(agentPrompts.ToolRoutingPrompt, new AgentPromptSettings().ToolRoutingPrompt));
            builder.AppendLine();
            AppendSkills(builder, skills, SkillBodyLimit(settings));
            builder.AppendLine("Available tools:");
            builder.AppendLine("Use only these exact tool ids in rnassistant-agent steps. Copy the full id, including the host prefix before the dot.");
            foreach (var tool in tools)
            {
                builder.AppendLine("- " + tool.Id + " (" + tool.Host + "): " + tool.Description);
                builder.AppendLine("  args: " + tool.ArgumentSchemaJson);
                if (!tool.BuiltIn)
                {
                    builder.AppendLine("  executor: " + (string.IsNullOrWhiteSpace(tool.Executor) ? "pipeline" : tool.Executor));
                    builder.AppendLine("  requiresConfirmation: " + tool.RequiresConfirmation);
                    AppendToolSource(builder, tool);
                }
            }

            if (!string.IsNullOrWhiteSpace(documentSnapshot))
            {
                builder.AppendLine();
                builder.AppendLine("Current document snapshot:");
                builder.AppendLine(documentSnapshot);
            }

            if (!string.IsNullOrWhiteSpace(vbaSnapshot))
            {
                builder.AppendLine();
                builder.AppendLine("Current VBA project snapshot:");
                builder.AppendLine(vbaSnapshot);
            }

            return builder.ToString();
        }

        private static void AppendPromptBlock(StringBuilder builder, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            builder.AppendLine(value.Trim());
        }

        private static string PromptOrDefault(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static void AppendSkills(StringBuilder builder, IEnumerable<SkillDefinition> skills, int bodyCharLimit)
        {
            var any = false;
            var remainingBodyChars = Math.Max(0, bodyCharLimit);
            foreach (var skill in skills ?? new SkillDefinition[0])
            {
                if (skill == null || !skill.Enabled)
                {
                    continue;
                }

                if (!any)
                {
                    builder.AppendLine("Relevant markdown skills:");
                    builder.AppendLine("Skills are guidance documents only. They are not executable tool ids. Follow them when they match the user task.");
                    any = true;
                }

                builder.AppendLine();
                builder.AppendLine("Skill: " + skill.Id + " (" + skill.Host + ")");
                builder.AppendLine("Description: " + (skill.Description ?? string.Empty));
                var body = skill.BodyMarkdown ?? string.Empty;
                if (string.IsNullOrWhiteSpace(body))
                {
                    continue;
                }

                if (remainingBodyChars <= 0)
                {
                    builder.AppendLine("Skill body omitted due to prompt budget.");
                    continue;
                }

                if (body.Length > remainingBodyChars)
                {
                    body = body.Substring(0, remainingBodyChars);
                    remainingBodyChars = 0;
                    builder.AppendLine("```markdown");
                    builder.AppendLine(body);
                    builder.AppendLine("[truncated]");
                    builder.AppendLine("```");
                    continue;
                }

                remainingBodyChars -= body.Length;
                builder.AppendLine("```markdown");
                builder.AppendLine(body);
                builder.AppendLine("```");
            }

            if (any)
            {
                builder.AppendLine();
            }
        }

        private static int SkillBodyLimit(AppSettings settings)
        {
            var contextLimit = Math.Max(4000, settings == null ? 24000 : settings.ContextCharLimit);
            return Math.Max(2000, Math.Min(DefaultSkillBodyLimit, contextLimit / 4));
        }

        public string ComposeContextPrompt(DocumentContext context)
        {
            if (context == null || context.Notes == null || context.Notes.Count == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            AppendUserContext(builder, context);
            return builder.ToString();
        }

        private static void AppendUserContext(StringBuilder builder, DocumentContext context)
        {
            if (context == null || context.Notes == null || context.Notes.Count == 0)
            {
                return;
            }

            builder.AppendLine();
            builder.AppendLine("User-added context attachments:");
            builder.AppendLine("These are explicit references the user added from the Office UI for the active chat. Use them when answering the user's next request. Treat them as higher priority than the general document snapshot.");

            for (var i = 0; i < context.Notes.Count; i++)
            {
                var note = context.Notes[i];
                if (note == null)
                {
                    continue;
                }

                builder.AppendLine();
                builder.AppendLine("Attachment " + (i + 1) + ":");
                builder.AppendLine("- id: " + (note.Id ?? string.Empty));
                builder.AppendLine("- host: " + FirstNonEmpty(note.Host, context.Host));
                builder.AppendLine("- kind: " + FirstNonEmpty(note.Kind, "selection"));
                builder.AppendLine("- title: " + FirstNonEmpty(note.Title, note.Source, "Untitled context"));
                builder.AppendLine("- reference: " + FirstNonEmpty(note.Reference, note.Source, "n/a"));
                if (!string.IsNullOrWhiteSpace(note.DetailsJson))
                {
                    builder.AppendLine("- details: " + note.DetailsJson);
                }
                builder.AppendLine("```text");
                builder.AppendLine(FirstNonEmpty(note.Text, note.Preview, string.Empty));
                builder.AppendLine("```");
            }
        }

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null)
            {
                return string.Empty;
            }

            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return string.Empty;
        }

        private static void AppendToolSource(StringBuilder builder, ToolDefinition skill)
        {
            if (!string.IsNullOrWhiteSpace(skill.Readme))
            {
                builder.AppendLine("  readme:");
                builder.AppendLine("```markdown");
                builder.AppendLine(skill.Readme);
                builder.AppendLine("```");
            }

            if (!string.IsNullOrWhiteSpace(skill.PipelineJson))
            {
                builder.AppendLine("  pipeline:");
                builder.AppendLine("```json");
                builder.AppendLine(skill.PipelineJson);
                builder.AppendLine("```");
            }

            if (!string.IsNullOrWhiteSpace(skill.Code))
            {
                builder.AppendLine("  code:");
                builder.AppendLine("```vba");
                builder.AppendLine(skill.Code);
                builder.AppendLine("```");
            }
        }
    }
}
