using System.Collections.Generic;
using System.Text;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Llm
{
    public sealed class PromptComposer
    {
        public string ComposeSystemPrompt(AppSettings settings, string host, string documentSnapshot, IEnumerable<SkillDefinition> tools)
        {
            var builder = new StringBuilder();
            builder.AppendLine(settings.SystemPrompt ?? string.Empty);
            builder.AppendLine();
            builder.AppendLine("Host application: " + host);
            builder.AppendLine("When you need to invoke a local document action, return a fenced block exactly like:");
            builder.AppendLine("```rnassistant-skill");
            builder.AppendLine("{\"skillId\":\"skill.id\",\"arguments\":{\"name\":\"value\"}}");
            builder.AppendLine("```");
            builder.AppendLine("You may include normal markdown before or after the command, but never invent tool ids.");
            builder.AppendLine("For multi-step Office work, return one rnassistant-skill block containing a JSON array of tool commands in execution order.");
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

            return builder.ToString();
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
