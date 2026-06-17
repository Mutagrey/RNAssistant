using System.Collections.Generic;
using System.Text;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Llm
{
    public sealed class PromptComposer
    {
        public string ComposeSystemPrompt(AppSettings settings, string host, string documentSnapshot, IEnumerable<SkillDefinition> skills)
        {
            var builder = new StringBuilder();
            builder.AppendLine(settings.SystemPrompt ?? string.Empty);
            builder.AppendLine();
            builder.AppendLine("Host application: " + host);
            builder.AppendLine("When you need to invoke a local document action, return a fenced block exactly like:");
            builder.AppendLine("```rnassistant-skill");
            builder.AppendLine("{\"skillId\":\"skill.id\",\"arguments\":{\"name\":\"value\"}}");
            builder.AppendLine("```");
            builder.AppendLine("You may include normal markdown before or after the command, but never invent skill ids.");
            builder.AppendLine();
            builder.AppendLine("Available skills:");
            foreach (var skill in skills)
            {
                builder.AppendLine("- " + skill.Id + " (" + skill.Host + "): " + skill.Description);
                builder.AppendLine("  args: " + skill.ArgumentSchemaJson);
            }

            if (!string.IsNullOrWhiteSpace(documentSnapshot))
            {
                builder.AppendLine();
                builder.AppendLine("Current document snapshot:");
                builder.AppendLine(documentSnapshot);
            }

            return builder.ToString();
        }
    }
}

