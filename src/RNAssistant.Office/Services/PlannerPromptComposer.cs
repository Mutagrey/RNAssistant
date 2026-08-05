using System;
using System.Collections.Generic;
using System.Text;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal sealed class PlannerPromptComposer
    {
        public List<ChatMessage> BuildMessages(string userText, OfficeSnapshot snapshot, RoutedTask route, ToolCatalogSlice tools, IEnumerable<AgentObservation> observations, DocumentContext context, IEnumerable<SkillDefinition> skills, AppSettings settings)
        {
            return BuildMessages(userText, snapshot, route, tools, observations, context, skills, settings, null, null, null, null);
        }

        public List<ChatMessage> BuildMessages(
            string userText,
            OfficeSnapshot snapshot,
            RoutedTask route,
            ToolCatalogSlice tools,
            IEnumerable<AgentObservation> observations,
            DocumentContext context,
            IEnumerable<SkillDefinition> skills,
            AppSettings settings,
            ChatSession session,
            IReadOnlyList<ChatAttachment> currentAttachments,
            IReadOnlyList<ChatMessage> protocolMessages = null,
            LlmRequestOptions requestOptions = null)
        {
            settings = settings ?? new AppSettings();
            var messages = new List<ChatMessage>();
            var instruction = BuildInstructionPrompt(settings);
            var plannerContext = BuildPlannerContext(userText, snapshot, route, tools, observations, context, skills, settings, requestOptions);
            var instructionRole = PromptRole(settings);
            var separateInstruction = string.Equals(instructionRole, "system", StringComparison.Ordinal) ||
                string.Equals(instructionRole, "developer", StringComparison.Ordinal);
            if (separateInstruction)
            {
                messages.Add(new ChatMessage { Role = instructionRole, Content = instruction });
            }
            var current = new ChatMessage
            {
                Role = "user",
                Content = separateInstruction ? plannerContext : instruction + "\n\n" + plannerContext
            };
            var currentIndex = messages.Count;
            messages.Add(current);

            current.Attachments = currentAttachments == null
                ? new List<ChatAttachment>()
                : new List<ChatAttachment>(currentAttachments);
            var options = requestOptions ?? AgentPlannerCompletionRunner.BuildOptions(settings.AgentResponseMode, tools == null ? null : tools.Tools);
            var inputBudget = Math.Max(
                256,
                ModelContextBudget.InputBudgetTokens(settings) - ModelContextBudget.EstimateRequestOptionsTokens(options));
            var budgetComposer = new PromptBudgetComposer();
            budgetComposer.AddProtocolHistory(messages, protocolMessages, inputBudget);
            budgetComposer.AddConversationHistory(
                messages,
                currentIndex,
                session,
                settings,
                inputBudget);
            return messages;
        }

        private static string BuildInstructionPrompt(AppSettings settings)
        {
            settings = settings ?? new AppSettings();
            return string.IsNullOrWhiteSpace(settings.SystemPrompt)
                ? new AppSettings().SystemPrompt
                : settings.SystemPrompt.Trim();
        }

        private static string PromptRole(AppSettings settings)
        {
            if (settings != null && string.Equals(settings.SystemPromptRole, "system", StringComparison.OrdinalIgnoreCase)) return "system";
            if (settings != null && string.Equals(settings.SystemPromptRole, "developer", StringComparison.OrdinalIgnoreCase)) return "developer";
            return "user";
        }

        private static string BuildPlannerContext(
            string userText,
            OfficeSnapshot snapshot,
            RoutedTask route,
            ToolCatalogSlice tools,
            IEnumerable<AgentObservation> observations,
            DocumentContext context,
            IEnumerable<SkillDefinition> skills,
            AppSettings settings,
            LlmRequestOptions requestOptions)
        {
            var builder = new StringBuilder();
            var structuredTools = requestOptions != null &&
                (requestOptions.NativeTools ||
                 string.Equals(requestOptions.ResponseFormat, LlmResponseFormats.JsonSchema, StringComparison.OrdinalIgnoreCase));
            builder.AppendLine("USER_REQUEST:");
            builder.AppendLine(userText ?? string.Empty);
            builder.AppendLine();
            builder.AppendLine("ROUTE:");
            builder.AppendLine("app: " + (route == null ? string.Empty : route.App));
            builder.AppendLine("mode: " + (route == null ? string.Empty : route.Mode));
            builder.AppendLine("taskType: " + (route == null ? string.Empty : route.TaskType));
            builder.AppendLine("riskAllowed: level_" + (route == null ? 0 : route.RiskAllowed));
            builder.AppendLine("phase: " + (route == null ? string.Empty : route.Phase));
            builder.AppendLine("requiresTool: " + (route != null && route.RequiresTool ? "true" : "false"));
            builder.AppendLine("requiresInspection: " + (route != null && route.RequiresInspection ? "true" : "false"));
            builder.AppendLine("Return exactly one tool call per model turn. decisionSummary is displayed as a normal chat message immediately before the selected action: briefly state established progress and the next action without exposing internal reasoning. For a visible kind=plan, keep steps ordered and observable, including expected inspection, mutation, and verification actions; the runtime advances one visible step per executed tool.");
            builder.AppendLine("responseMode: " + RequestResponseMode(requestOptions, settings));
            if (route != null && string.Equals(route.TaskType, "html", StringComparison.OrdinalIgnoreCase))
            {
                builder.AppendLine("HTML MODE IS ENABLED FOR THIS CHAT.");
                if (route.RequiresInspection)
                {
                    builder.AppendLine("This workspace already has content. Read it before any upsert, delete, or active-file change.");
                }
            }
            builder.AppendLine();
            builder.AppendLine("CURRENT_OFFICE_CONTEXT:");
            builder.AppendLine("Host: " + (snapshot == null ? string.Empty : snapshot.Host));
            builder.AppendLine("Document: " + (snapshot == null ? string.Empty : snapshot.DocumentTitle));
            builder.AppendLine("Container: " + (snapshot == null ? string.Empty : snapshot.ContainerName));
            builder.AppendLine("Selection: " + (snapshot == null ? string.Empty : snapshot.SelectionAddress));
            if (!string.IsNullOrWhiteSpace(snapshot == null ? null : snapshot.SelectionText))
            {
                builder.AppendLine("Selection preview: " + AgentText.Truncate(snapshot.SelectionText, 500));
            }
            if (!string.IsNullOrWhiteSpace(snapshot == null ? null : snapshot.SnapshotText))
            {
                builder.AppendLine("Snapshot summary:");
                builder.AppendLine(AgentText.Truncate(snapshot.SnapshotText, 1800));
            }
            AppendUserContext(builder, context, Math.Max(512, ModelContextBudget.InputBudgetTokens(settings) / 3));
            builder.AppendLine();
            builder.AppendLine("AVAILABLE_TOOLS:");
            var index = 1;
            foreach (var tool in tools == null ? (IEnumerable<ToolDefinition>)new ToolDefinition[0] : tools.Tools)
            {
                builder.AppendLine(index + ". " + tool.Id);
                builder.AppendLine("   " + AgentText.Truncate(tool.Description, 240));
                builder.AppendLine("   risk: level_" + tool.RiskLevel + "; mode: " + (tool.MutatesDocument || tool.MutatesLocalState ? "mutation" : "read"));
                builder.AppendLine("   confirmation: " + (tool.RequiresConfirmation ? "required" : "runtime policy"));
                builder.AppendLine("   args: " + (string.IsNullOrWhiteSpace(tool.ArgumentSchemaJson) ? "{}" : tool.ArgumentSchemaJson));
                if (!string.IsNullOrWhiteSpace(tool.UseWhen))
                {
                    builder.AppendLine("   useWhen: " + AgentText.Truncate(tool.UseWhen, 220));
                }
                if (!string.IsNullOrWhiteSpace(tool.DoNotUseWhen))
                {
                    builder.AppendLine("   doNotUseWhen: " + AgentText.Truncate(tool.DoNotUseWhen, 220));
                }
                if (!structuredTools && !string.IsNullOrWhiteSpace(tool.ExamplesJson))
                {
                    builder.AppendLine("   examples: " + AgentText.Truncate(tool.ExamplesJson, 500));
                }
                index += 1;
            }
            builder.AppendLine();
            builder.AppendLine("OBSERVATIONS:");
            var any = false;
            foreach (var observation in observations ?? new AgentObservation[0])
            {
                any = true;
                builder.AppendLine("[" + observation.Id + "] " + observation.Summary);
                if (!string.IsNullOrWhiteSpace(observation.FactsJson))
                {
                    builder.AppendLine("facts: " + AgentText.Truncate(observation.FactsJson, 1200));
                }
            }
            if (!any)
            {
                builder.AppendLine("none");
            }
            AppendSkills(builder, skills, settings);
            return builder.ToString();
        }

        private static void AppendUserContext(StringBuilder builder, DocumentContext context, int maxTokens)
        {
            if (context == null || context.Notes == null || context.Notes.Count == 0)
            {
                return;
            }
            var usedTokens = 0;
            var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var wroteAny = false;
            foreach (var note in context.Notes)
            {
                if (note == null)
                {
                    continue;
                }
                var content = AgentText.FirstNonEmpty(note.Text, note.Preview);
                if (string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }
                var identity = !string.IsNullOrWhiteSpace(note.Reference)
                    ? note.Host + "|" + note.Kind + "|" + note.Reference
                    : note.Id;
                if (!included.Add(identity))
                {
                    continue;
                }
                var entry = "- " + AgentText.FirstNonEmpty(note.Title, note.Reference, note.Kind) + ": " + content;
                var remaining = maxTokens - usedTokens;
                if (remaining <= 0)
                {
                    builder.AppendLine("[additional context omitted by token budget]");
                    break;
                }
                if (!wroteAny)
                {
                    builder.AppendLine("User-added context:");
                }
                var selected = TruncateToTokens(entry, remaining);
                builder.AppendLine(selected);
                wroteAny = true;
                usedTokens += ModelContextBudget.EstimateTextTokens(selected);
                if (selected.Length < entry.Length)
                {
                    builder.AppendLine("[context truncated]");
                    break;
                }
            }
        }

        private static string TruncateToTokens(string value, int maxTokens)
        {
            return ModelContextBudget.TruncateText(value, maxTokens);
        }

        private static void AppendSkills(StringBuilder builder, IEnumerable<SkillDefinition> skills, AppSettings settings)
        {
            var limit = Math.Max(1000, Math.Min(12000, ModelContextBudget.InputBudgetTokens(settings) * 3 / 8));
            var used = 0;
            var any = false;
            foreach (var skill in skills ?? new SkillDefinition[0])
            {
                if (skill == null || !skill.Enabled)
                {
                    continue;
                }
                if (!any)
                {
                    builder.AppendLine();
                    builder.AppendLine("RELEVANT_SKILLS:");
                    any = true;
                }
                var body = skill.BodyMarkdown ?? string.Empty;
                var remaining = limit - used;
                if (remaining <= 0)
                {
                    break;
                }
                body = AgentText.Truncate(body, remaining);
                used += body.Length;
                builder.AppendLine("- " + skill.Id + ": " + AgentText.Truncate(skill.Description, 160));
                if (!string.IsNullOrWhiteSpace(body))
                {
                    builder.AppendLine(body);
                }
            }
        }

        private static string RequestResponseMode(LlmRequestOptions requestOptions, AppSettings settings)
        {
            if (requestOptions != null && requestOptions.NativeTools) return AgentResponseModes.NativeToolCalls;
            if (requestOptions != null && string.Equals(requestOptions.ResponseFormat, LlmResponseFormats.JsonObject, StringComparison.OrdinalIgnoreCase))
            {
                return AgentResponseModes.JsonObject;
            }
            if (requestOptions != null && string.Equals(requestOptions.ResponseFormat, LlmResponseFormats.JsonSchema, StringComparison.OrdinalIgnoreCase))
            {
                return AgentResponseModes.JsonSchema;
            }
            return settings == null ? AgentResponseModes.JsonSchema : settings.AgentResponseMode;
        }




    }
}
