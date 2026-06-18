using System.Collections.Generic;
using System.Text;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Llm
{
    public sealed class PromptComposer
    {
        public string ComposeSystemPrompt(AppSettings settings, string host, string documentSnapshot, string vbaSnapshot, IEnumerable<SkillDefinition> tools, DocumentContext context)
        {
            var builder = new StringBuilder();
            builder.AppendLine(settings.SystemPrompt ?? string.Empty);
            builder.AppendLine();
            builder.AppendLine("Host application: " + host);
            builder.AppendLine("Agent mode:");
            builder.AppendLine("- Understand the user's goal, break it into small local Office/VBA actions, and use tools when document state is needed.");
            builder.AppendLine("- Native API tool_calls are not available. Return local actions only as text JSON blocks that RNAssistant can parse.");
            builder.AppendLine("- Read-only tools may be used to inspect state. Mutating document and VBA actions may require user confirmation unless auto-confirm is enabled.");
            builder.AppendLine("- For agent-created executable code, write VBA code for the current Office host.");
            builder.AppendLine("When you need to invoke a local document action, return a fenced block exactly like:");
            builder.AppendLine("```rnassistant-skill");
            builder.AppendLine("{\"skillId\":\"skill.id\",\"arguments\":{\"name\":\"value\"}}");
            builder.AppendLine("```");
            builder.AppendLine("You may also use ```rnassistant-agent with {\"steps\":[...]} or a JSON array of commands.");
            builder.AppendLine("Each command must use an available skillId/toolId and an arguments object. Never invent tool ids.");
            builder.AppendLine("For multi-step Office work, return one rnassistant-agent block containing the next JSON array of tool commands in execution order.");
            builder.AppendLine("After tool results are provided, either answer normally if the task is complete or return the next tool block.");
            builder.AppendLine("For VBA edits, prefer the host vba_apply_patch tool for structured small patches; use vba_replace_module only when replacing the whole module is necessary.");
            builder.AppendLine();
            builder.AppendLine("Available tools:");
            foreach (var skill in tools)
            {
                builder.AppendLine("- " + skill.Id + " (" + skill.Host + "): " + skill.Description);
                builder.AppendLine("  args: " + skill.ArgumentSchemaJson);
                if (!skill.BuiltIn)
                {
                    builder.AppendLine("  executor: " + (string.IsNullOrWhiteSpace(skill.Executor) ? "pipeline" : skill.Executor));
                    builder.AppendLine("  requiresConfirmation: " + skill.RequiresConfirmation);
                    AppendToolSource(builder, skill);
                }
            }

            if (!string.IsNullOrWhiteSpace(documentSnapshot))
            {
                builder.AppendLine();
                builder.AppendLine("Current document snapshot:");
                builder.AppendLine(documentSnapshot);
            }

            AppendUserContext(builder, context);

            if (!string.IsNullOrWhiteSpace(vbaSnapshot))
            {
                builder.AppendLine();
                builder.AppendLine("Current VBA project snapshot:");
                builder.AppendLine(vbaSnapshot);
            }

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
            builder.AppendLine("These are explicit references the user added from the Office UI. Treat them as important task context.");

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

        private static void AppendToolSource(StringBuilder builder, SkillDefinition skill)
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
