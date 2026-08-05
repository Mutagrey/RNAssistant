using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal sealed class PlannerPromptComposer
    {
        public List<ChatMessage> BuildMessages(string userText, OfficeSnapshot snapshot, RoutedTask route, ToolCatalogSlice tools, IEnumerable<AgentObservation> observations, DocumentContext context, IEnumerable<SkillDefinition> skills, AppSettings settings)
        {
            return BuildMessages(userText, snapshot, route, tools, observations, context, skills, settings, null, null);
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
            IReadOnlyList<ChatAttachment> currentAttachments)
        {
            var messages = new List<ChatMessage>();
            var instruction = BuildInstructionPrompt(settings);
            var plannerContext = BuildPlannerContext(userText, snapshot, route, tools, observations, context, skills, settings);
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
            messages.Add(current);

            current.Attachments = currentAttachments == null
                ? new List<ChatAttachment>()
                : new List<ChatAttachment>(currentAttachments);
            new PromptBudgetComposer().AddConversationHistory(
                messages,
                messages.Count - 1,
                session,
                settings);
            return messages;
        }

        private static string BuildInstructionPrompt(AppSettings settings)
        {
            settings = settings ?? new AppSettings();
            var prompts = settings.AgentPrompts ?? new AgentPromptSettings();
            return string.Join(
                "\n\n",
                new[]
                {
                    settings.SystemPrompt,
                    prompts.ToolProtocolPrompt,
                    prompts.ToolRoutingPrompt,
                    TransportPrompt(settings)
                }.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()));
        }

        private static string TransportPrompt(AppSettings settings)
        {
            return settings != null && string.Equals(settings.AgentResponseMode, AgentResponseModes.NativeToolCalls, StringComparison.OrdinalIgnoreCase)
                ? "TRANSPORT OVERRIDE: For an Office action, emit exactly one native API function call and no kind=tool content. Use AgentDecision JSON content only for plan, clarify, final, or cannot_complete."
                : "TRANSPORT OVERRIDE: Do not emit API tool_calls. Select an Office action only through one AgentDecision kind=tool object.";
        }

        private static string PromptRole(AppSettings settings)
        {
            if (settings != null && string.Equals(settings.SystemPromptRole, "system", StringComparison.OrdinalIgnoreCase)) return "system";
            if (settings != null && string.Equals(settings.SystemPromptRole, "developer", StringComparison.OrdinalIgnoreCase)) return "developer";
            return "user";
        }

        private static string BuildPlannerContext(string userText, OfficeSnapshot snapshot, RoutedTask route, ToolCatalogSlice tools, IEnumerable<AgentObservation> observations, DocumentContext context, IEnumerable<SkillDefinition> skills, AppSettings settings)
        {
            var builder = new StringBuilder();
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
            builder.AppendLine("Return exactly one tool call per model turn. A visible kind=plan decision describes complex work but never executes tools.");
            builder.AppendLine("responseMode: " + (settings == null ? AgentResponseModes.JsonSchema : settings.AgentResponseMode));
            if (route != null && string.Equals(route.TaskType, "html", StringComparison.OrdinalIgnoreCase))
            {
                builder.AppendLine("HTML MODE IS ENABLED FOR THIS CHAT.");
                builder.AppendLine("Use common.html_workspace_read before editing or deleting existing files. Use common.html_workspace_upsert_file/data for editable HTML workspace output and common.html_workspace_delete_file/data to remove items.");
                builder.AppendLine("HTML preview supports normal fetch(http/https) through the RNAssistant host after the user explicitly allows the target origin. Do not suggest mode:no-cors and never embed RNAssistant API keys or credentials.");
                if (route.RequiresInspection)
                {
                    builder.AppendLine("This workspace already has content. Read it before any upsert, delete, or active-file change.");
                }
                builder.AppendLine("Keep HTML workspace output local. Put CSS and JavaScript in separate workspace files when content is large, and return at most one content-bearing upsert step per planner response. Continue with the next file after the tool observation.");
            }
            if (tools != null && tools.Tools.Any(tool => string.Equals(tool.Id, "common.tools_save", StringComparison.OrdinalIgnoreCase)))
            {
                builder.AppendLine("OPTIONAL TOOL AUTHORING IS ENABLED. Prefer an existing tool. Only when no existing capability can complete the task, define a narrowly scoped pipeline or VBA custom tool: call common.tools_validate, then common.tools_save, then use the saved exact tool id in a later planner step. Never claim the requested document change is complete after only saving the tool.");
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
                if (!string.IsNullOrWhiteSpace(tool.ExamplesJson))
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
            else
            {
                builder.AppendLine();
                builder.AppendLine("PLANNER_DIRECTIVE:");
                var promptSettings = settings == null || settings.AgentPrompts == null
                    ? new AgentPromptSettings()
                    : settings.AgentPrompts;
                var observationList = (observations ?? new AgentObservation[0]).Where(o => o != null).ToList();
                var latestObservation = observationList.LastOrDefault();
                if (latestObservation != null && !string.Equals(latestObservation.Status, "success", StringComparison.OrdinalIgnoreCase))
                {
                    builder.AppendLine("A local tool call failed or was rejected. Use the error observation to return one corrected kind=tool decision, or cannot_complete if it cannot be corrected.");
                }
                else if (observationList.Any(o => o.Mutation) && (settings == null || settings.RequireVerificationForMutations))
                {
                    builder.AppendLine(promptSettings.VerifyMutationPrompt);
                }
                else
                {
                    builder.AppendLine(promptSettings.AfterToolResultsPrompt);
                }
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
            if (string.IsNullOrEmpty(value) || ModelContextBudget.EstimateTextTokens(value) <= maxTokens)
            {
                return value ?? string.Empty;
            }
            var low = 0;
            var high = value.Length;
            while (low < high)
            {
                var middle = low + (high - low + 1) / 2;
                if (ModelContextBudget.EstimateTextTokens(value.Substring(0, middle)) <= maxTokens)
                    low = middle;
                else
                    high = middle - 1;
            }
            return value.Substring(0, low);
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
                    builder.AppendLine("Skills are guidance documents only; they are not executable tools.");
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




    }
}
