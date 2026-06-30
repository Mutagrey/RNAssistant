using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal sealed class OfficeSnapshot
    {
        public string Host { get; set; }
        public string DocumentTitle { get; set; }
        public string ContainerName { get; set; }
        public string SelectionAddress { get; set; }
        public string SelectionText { get; set; }
        public string SnapshotText { get; set; }
    }

    internal sealed class RoutedTask
    {
        public string App { get; set; }
        public string Mode { get; set; }
        public string TaskType { get; set; }
        public string Phase { get; set; }
        public int RiskAllowed { get; set; }
        public bool RequiresTool { get; set; }
        public bool RequiresInspection { get; set; }
    }

    internal sealed class AgentObservation
    {
        public string Id { get; set; }
        public string ToolId { get; set; }
        public string Status { get; set; }
        public string Summary { get; set; }
        public string FactsJson { get; set; }
        public bool Mutation { get; set; }
        public bool RequiresVerification { get; set; }
    }

    internal sealed class ToolCatalogSlice
    {
        public List<ToolDefinition> Tools { get; set; }

        public ToolCatalogSlice()
        {
            Tools = new List<ToolDefinition>();
        }

        public ToolDefinition Find(string id)
        {
            return Tools.FirstOrDefault(t => t != null && string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    internal sealed class PlannerValidationResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public ToolCommand Command { get; set; }
        public ToolDefinition Tool { get; set; }

        public static PlannerValidationResult Ok(ToolCommand command, ToolDefinition tool)
        {
            return new PlannerValidationResult { Success = true, Command = command, Tool = tool };
        }

        public static PlannerValidationResult Fail(string message)
        {
            return new PlannerValidationResult { Success = false, Message = message };
        }
    }

    internal sealed class OfficeIntentRouter
    {
        public RoutedTask Route(string userText, OfficeSnapshot snapshot)
        {
            var host = FirstNonEmpty(snapshot == null ? null : snapshot.Host, "Office");
            var value = (userText ?? string.Empty).ToLowerInvariant();
            var route = new RoutedTask
            {
                App = host,
                Mode = "answer",
                TaskType = "general",
                Phase = AgentPhases.Final,
                RiskAllowed = 0,
                RequiresTool = false,
                RequiresInspection = false
            };

            if (LooksLikeGeneralQuestion(value))
            {
                return route;
            }

            if (ContainsAny(value, "удали", "delete", "remove", "clear", "очисти") &&
                !ContainsAny(value, "custom tool", "tools", "prompt", "prompts", "skill", "skills", "инструмент", "промпт", "скилл"))
            {
                route.Mode = "destructive_mutation";
                route.TaskType = "destructive";
                route.Phase = AgentPhases.ReadOnly;
                route.RiskAllowed = 0;
                route.RequiresTool = true;
                route.RequiresInspection = true;
                return route;
            }

            if (ContainsAny(value, "запусти макрос", "run macro", "execute macro"))
            {
                route.Mode = "high_risk_execution";
                route.TaskType = "macro_execution";
                route.Phase = AgentPhases.ReadOnly;
                route.RiskAllowed = 0;
                route.RequiresTool = true;
                route.RequiresInspection = true;
                return route;
            }

            if (ContainsAny(value, "vba", "macro", "макрос", "макро", "visual basic"))
            {
                route.Mode = "code_generation";
                route.TaskType = "vba";
                route.Phase = ContainsAny(value, "replace", "замени", "insert", "встав", "run", "запусти")
                    ? AgentPhases.Mutation
                    : AgentPhases.ReadOnly;
                route.RiskAllowed = string.Equals(route.Phase, AgentPhases.Mutation, StringComparison.OrdinalIgnoreCase) ? 3 : 0;
                route.RequiresTool = true;
                route.RequiresInspection = string.Equals(route.Phase, AgentPhases.ReadOnly, StringComparison.OrdinalIgnoreCase);
                return route;
            }

            if (ContainsAny(value, "html", "страниц", "page", "ui", "dashboard", "дашборд"))
            {
                route.Mode = "mutate_html";
                route.TaskType = "html";
                route.Phase = AgentPhases.Mutation;
                route.RiskAllowed = 1;
                route.RequiresTool = true;
                route.RequiresInspection = false;
                return route;
            }

            if (ContainsAny(value, "custom tool", "tools", "prompt", "prompts", "skill", "skills", "инструмент", "промпт", "скилл"))
            {
                var mutatesCatalog = ContainsAny(value, "создай", "создать", "добавь", "измени", "обнови", "удали", "сохрани", "create", "add", "update", "delete", "remove", "save");
                route.Mode = mutatesCatalog ? "mutate_tool_authoring" : "read_tool_authoring";
                route.TaskType = "tool_authoring";
                route.Phase = mutatesCatalog ? AgentPhases.Mutation : AgentPhases.ReadOnly;
                route.RiskAllowed = 1;
                route.RequiresTool = true;
                route.RequiresInspection = false;
                return route;
            }

            if (ContainsAny(value, "сделай", "создай", "создать", "построй", "сгенерируй", "заполни", "вставь", "замени", "измени", "добавь", "напиши", "create", "make", "add", "insert", "replace", "update", "write", "generate", "build", "draft"))
            {
                route.RequiresTool = true;
                route.RiskAllowed = 2;
                route.Phase = AgentPhases.Mutation;
                route.Mode = "mutate";
                route.TaskType = "content";
            }

            if (ContainsAny(value, "красив", "оформи", "format", "style", "pretty", "autofit", "автоподбор"))
            {
                route.RequiresTool = true;
                route.Mode = "mutate_formatting";
                route.TaskType = "formatting";
                route.Phase = AgentPhases.ReadOnly;
                route.RiskAllowed = 1;
                route.RequiresInspection = true;
            }
            else if (ContainsAny(value, "график", "диаграм", "chart", "plot") &&
                !ContainsAny(value, "создай", "создать", "create", "generate", "сгенерируй", "report", "отчет"))
            {
                route.RequiresTool = true;
                route.Mode = "mutate_chart";
                route.TaskType = "chart";
                route.Phase = AgentPhases.ReadOnly;
                route.RiskAllowed = 2;
                route.RequiresInspection = true;
            }
            else if (!route.RequiresTool && ContainsAny(value, "прочитай", "покажи", "найди", "поиск", "перечисли", "summarize", "summary", "перескажи", "analyze", "review", "inspect", "read", "search", "find", "list"))
            {
                route.RequiresTool = true;
                route.Mode = ContainsAny(value, "summarize", "summary", "перескажи", "analyze", "review") ? "analyze" : "read";
                route.TaskType = ContainsAny(value, "mail", "email", "письм") ? "mail_search" : "read";
                route.Phase = AgentPhases.ReadOnly;
                route.RiskAllowed = 0;
                route.RequiresInspection = false;
            }

            return route;
        }

        private static bool ContainsAny(string value, params string[] terms)
        {
            foreach (var term in terms ?? new string[0])
            {
                if (!string.IsNullOrWhiteSpace(term) && value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool LooksLikeGeneralQuestion(string value)
        {
            value = (value ?? string.Empty).TrimStart();
            return value.StartsWith("что такое ", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("объясни ", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("расскажи ", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("как ", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("почему ", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("what is ", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("explain ", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("how ", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("why ", StringComparison.OrdinalIgnoreCase);
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values ?? new string[0])
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
            return string.Empty;
        }
    }

    internal sealed class ToolCatalogSlicer
    {
        public ToolCatalogSlice Slice(RoutedTask route, IEnumerable<ToolDefinition> tools, IReadOnlyList<AgentObservation> observations)
        {
            var slice = new ToolCatalogSlice();
            if (route != null && !route.RequiresTool)
            {
                return slice;
            }
            var host = route == null ? string.Empty : route.App ?? string.Empty;
            foreach (var tool in tools ?? new ToolDefinition[0])
            {
                if (!IsCandidate(tool, host))
                {
                    continue;
                }
                if (!AllowedForPhase(tool, route))
                {
                    continue;
                }
                if (!Relevant(tool, route))
                {
                    continue;
                }
                slice.Tools.Add(tool);
            }

            foreach (var recipe in Recipes(route))
            {
                slice.Tools.Add(recipe);
            }

            return new ToolCatalogSlice { Tools = slice.Tools.OrderBy(t => ToolPriority(t, route)).ThenBy(t => t.RiskLevel).ThenBy(t => t.Id).Take(12).ToList() };
        }

        private static bool IsCandidate(ToolDefinition tool, string host)
        {
            if (tool == null || !tool.Enabled || string.IsNullOrWhiteSpace(tool.Id))
            {
                return false;
            }
            if (!string.Equals(tool.CapabilityStatus ?? "available", "available", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(tool.CapabilityStatus ?? "available", "partial", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return string.Equals(tool.Host, host, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tool.Host, "Common", StringComparison.OrdinalIgnoreCase);
        }

        private static bool AllowedForPhase(ToolDefinition tool, RoutedTask route)
        {
            if (route == null)
            {
                return true;
            }
            if (string.Equals(route.Phase, AgentPhases.ReadOnly, StringComparison.OrdinalIgnoreCase))
            {
                return !tool.MutatesDocument && tool.RiskLevel <= 0;
            }
            if (string.Equals(route.Phase, AgentPhases.Verification, StringComparison.OrdinalIgnoreCase))
            {
                return !tool.MutatesDocument;
            }
            return tool.RiskLevel <= route.RiskAllowed;
        }

        private static bool Relevant(ToolDefinition tool, RoutedTask route)
        {
            if (route == null || string.IsNullOrWhiteSpace(route.TaskType))
            {
                return true;
            }

            var id = tool.Id ?? string.Empty;
            if (id.StartsWith("common.", StringComparison.OrdinalIgnoreCase) && route.TaskType != "html" && route.TaskType != "tool_authoring")
            {
                return false;
            }
            if (route.TaskType == "formatting")
            {
                return ContainsAny(id, "context", "selection", "summary", "read", "profile", "format", "autofit", "recipe.");
            }
            if (route.TaskType == "html")
            {
                return ContainsAny(id, "html_workspace", "prompts_read", "skills_read", "tools_read");
            }
            if (route.TaskType == "tool_authoring")
            {
                return ContainsAny(id, "tools_", "skills_", "prompts_");
            }
            if (route.TaskType == "chart")
            {
                return ContainsAny(id, "context", "selection", "summary", "read", "profile", "chart", "recipe.");
            }
            if (route.TaskType == "mail_search")
            {
                return ContainsAny(id, "context", "read", "search", "mail", "attachment", "collect");
            }
            if (route.TaskType == "vba")
            {
                return ContainsAny(id, "vba", "macro", "context");
            }
            if (route.TaskType == "destructive")
            {
                return !tool.MutatesDocument || ContainsAny(id, "search", "read", "list", "context");
            }
            if (route.TaskType == "content")
            {
                return true;
            }
            return !tool.MutatesDocument || ContainsAny(id, "read", "list", "search", "context", "summary");
        }

        private static int ToolPriority(ToolDefinition tool, RoutedTask route)
        {
            if (tool == null || route == null)
            {
                return 50;
            }
            var id = tool.Id ?? string.Empty;
            if (route.TaskType == "html" && ContainsAny(id, "html_workspace"))
            {
                return 0;
            }
            if (route.TaskType == "formatting" && ContainsAny(id, "format", "autofit", "recipe."))
            {
                return string.Equals(route.Phase, AgentPhases.Mutation, StringComparison.OrdinalIgnoreCase) ? 0 : 20;
            }
            if (route.TaskType == "chart" && ContainsAny(id, "chart"))
            {
                return string.Equals(route.Phase, AgentPhases.Mutation, StringComparison.OrdinalIgnoreCase) ? 0 : 20;
            }
            if (route.TaskType == "content" && tool.MutatesDocument)
            {
                return ContainsAny(id, "add_sheet", "add_slide", "write_table", "write_range", "set_formula", "add_chart", "add_table", "insert", "replace", "comment")
                    ? 0
                    : 20;
            }
            if (!tool.MutatesDocument && ContainsAny(id, "context", "selection", "summary", "read", "profile", "list", "search"))
            {
                return 10;
            }
            return 30;
        }

        private static IEnumerable<ToolDefinition> Recipes(RoutedTask route)
        {
            if (route == null || route.App == null)
            {
                yield break;
            }
            if (string.Equals(route.App, "Excel", StringComparison.OrdinalIgnoreCase) && route.TaskType == "formatting" && !string.Equals(route.Phase, AgentPhases.ReadOnly, StringComparison.OrdinalIgnoreCase))
            {
                yield return new ToolDefinition
                {
                    Id = "recipe.excel.make_table_pretty",
                    Host = "Excel",
                    Name = "Make active table pretty",
                    Description = "High-level recipe: inspect active sheet, format detected used range as a clean table, autofit, and verify.",
                    ArgumentSchemaJson = "{\"target\":\"active_sheet\"}",
                    BuiltIn = true,
                    Enabled = true,
                    MutatesDocument = true,
                    AgentCanRun = true,
                    RiskLevel = 1,
                    UseWhen = "User asks to make the active Excel table pretty or clean."
                };
            }
        }

        private static bool ContainsAny(string value, params string[] terms)
        {
            foreach (var term in terms ?? new string[0])
            {
                if (!string.IsNullOrWhiteSpace(term) && value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }
    }

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
            var system = new ChatMessage { Role = "system", Content = BuildSystemPrompt() };
            var current = new ChatMessage { Role = "user", Content = BuildPlannerContext(userText, snapshot, route, tools, observations, context, skills, settings) };
            messages.Add(system);
            messages.Add(current);

            var budget = ModelContextBudget.InputBudgetTokens(settings);
            var used = ModelContextBudget.EstimateMessagesTokens(messages) + EstimateAttachmentTokens(currentAttachments);
            var history = session == null || session.Messages == null
                ? new List<ChatMessage>()
                : session.Messages.Take(Math.Max(0, session.Messages.Count - 1))
                    .Where(message => message != null && message.Activity == null &&
                        (string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)))
                    .ToList();
            for (var index = history.Count - 1; index >= 0; index--)
            {
                var source = history[index];
                var candidate = new ChatMessage { Role = source.Role, Content = source.Content ?? string.Empty };
                var estimate = ModelContextBudget.EstimateMessagesTokens(new[] { candidate });
                if (used + estimate > budget)
                {
                    break;
                }
                messages.Insert(1, candidate);
                used += estimate;
            }
            return messages;
        }

        private static int EstimateAttachmentTokens(IEnumerable<ChatAttachment> attachments)
        {
            var total = 0;
            foreach (var attachment in attachments ?? new ChatAttachment[0])
            {
                if (attachment == null)
                {
                    continue;
                }
                total += Math.Max(0, attachment.ExtractedCharCount) / 2;
                if (attachment.Kind == "image")
                {
                    total += ModelContextBudget.EstimatedImageTokens;
                }
            }
            return total;
        }

        private static string BuildSystemPrompt()
        {
            return "You are RNAssistant Office Action Planner.\n" +
                "Return exactly one JSON object. No markdown. No code fences. No prose outside JSON.\n" +
                "Allowed shape: {\"kind\":\"tool_plan|final|clarify|cannot_do\",\"intent\":\"read|analyze|mutate|verify|answer|clarify\",\"message\":\"string|null\",\"steps\":[{\"toolId\":\"exact tool id from AVAILABLE_TOOLS\",\"arguments\":{},\"reason\":\"short reason\"}],\"expectedOutcome\":\"string|null\"}.\n" +
                "Use only AVAILABLE_TOOLS. Never invent tool ids, workbook, sheet, range, email, or document content.\n" +
                "Call a context/read tool only when the request depends on current Office content or ROUTE requires inspection. Do not inspect Office for general questions.\n" +
                "A mutation with an explicit target and complete arguments does not need a preliminary read unless ROUTE requires inspection.\n" +
                "For claims about current Office content, use only CURRENT_OFFICE_CONTEXT and OBSERVATIONS. If no tool is needed, return kind=final.";
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
            if (route != null && string.Equals(route.TaskType, "html", StringComparison.OrdinalIgnoreCase))
            {
                builder.AppendLine("HTML MODE IS ENABLED FOR THIS CHAT.");
                builder.AppendLine("Use common.html_workspace_read before editing existing files. Use common.html_workspace_upsert_file/data for editable HTML workspace output.");
            }
            builder.AppendLine();
            builder.AppendLine("CURRENT_OFFICE_CONTEXT:");
            builder.AppendLine("Host: " + (snapshot == null ? string.Empty : snapshot.Host));
            builder.AppendLine("Document: " + (snapshot == null ? string.Empty : snapshot.DocumentTitle));
            builder.AppendLine("Container: " + (snapshot == null ? string.Empty : snapshot.ContainerName));
            builder.AppendLine("Selection: " + (snapshot == null ? string.Empty : snapshot.SelectionAddress));
            if (!string.IsNullOrWhiteSpace(snapshot == null ? null : snapshot.SelectionText))
            {
                builder.AppendLine("Selection preview: " + Trim(snapshot.SelectionText, 500));
            }
            if (!string.IsNullOrWhiteSpace(snapshot == null ? null : snapshot.SnapshotText))
            {
                builder.AppendLine("Snapshot summary:");
                builder.AppendLine(Trim(snapshot.SnapshotText, 1800));
            }
            AppendUserContext(builder, context, Math.Max(512, ModelContextBudget.InputBudgetTokens(settings) / 3));
            builder.AppendLine();
            builder.AppendLine("AVAILABLE_TOOLS:");
            var index = 1;
            foreach (var tool in tools == null ? (IEnumerable<ToolDefinition>)new ToolDefinition[0] : tools.Tools)
            {
                builder.AppendLine(index + ". " + tool.Id);
                builder.AppendLine("   " + Trim(tool.Description, 240));
                builder.AppendLine("   risk: level_" + tool.RiskLevel + "; mode: " + (tool.MutatesDocument ? "mutation" : "read"));
                builder.AppendLine("   args: " + (string.IsNullOrWhiteSpace(tool.ArgumentSchemaJson) ? "{}" : tool.ArgumentSchemaJson));
                if (!string.IsNullOrWhiteSpace(tool.UseWhen))
                {
                    builder.AppendLine("   useWhen: " + Trim(tool.UseWhen, 220));
                }
                if (!string.IsNullOrWhiteSpace(tool.DoNotUseWhen))
                {
                    builder.AppendLine("   doNotUseWhen: " + Trim(tool.DoNotUseWhen, 220));
                }
                if (!string.IsNullOrWhiteSpace(tool.ExamplesJson))
                {
                    builder.AppendLine("   examples: " + Trim(tool.ExamplesJson, 500));
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
                    builder.AppendLine("facts: " + Trim(observation.FactsJson, 1200));
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
                    builder.AppendLine("A local tool call failed or was rejected. Use the error observation to return a corrected tool_plan, or cannot_do if it cannot be corrected.");
                }
                else if (observationList.Any(o => o.Mutation) && (settings == null || settings.RequireVerificationForMutations != false))
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
            builder.AppendLine("User-added context:");
            var usedTokens = 0;
            foreach (var note in context.Notes)
            {
                if (note == null)
                {
                    continue;
                }
                var entry = "- " + FirstNonEmpty(note.Title, note.Reference, note.Kind) + ": " + FirstNonEmpty(note.Text, note.Preview);
                var remaining = maxTokens - usedTokens;
                if (remaining <= 0)
                {
                    builder.AppendLine("[additional context omitted by token budget]");
                    break;
                }
                var selected = TruncateToTokens(entry, remaining);
                builder.AppendLine(selected);
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
                    any = true;
                }
                var body = skill.BodyMarkdown ?? string.Empty;
                var remaining = limit - used;
                if (remaining <= 0)
                {
                    break;
                }
                body = Trim(body, remaining);
                used += body.Length;
                builder.AppendLine("- " + skill.Id + ": " + Trim(skill.Description, 160));
                if (!string.IsNullOrWhiteSpace(body))
                {
                    builder.AppendLine(body);
                }
            }
        }

        private static string Trim(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
            {
                return value ?? string.Empty;
            }
            return value.Substring(0, Math.Max(0, maxChars)) + "\n[truncated]";
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values ?? new string[0])
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
            return string.Empty;
        }
    }

    internal sealed class AgentActionValidator
    {
        public PlannerValidationResult Validate(AgentPlannerStep step, ToolCatalogSlice slice, RoutedTask route, IReadOnlyList<AgentObservation> observations)
        {
            if (step == null || string.IsNullOrWhiteSpace(step.ToolId))
            {
                return PlannerValidationResult.Fail("Planner step has no toolId.");
            }
            var tool = slice == null ? null : slice.Find(step.ToolId);
            if (tool == null)
            {
                return PlannerValidationResult.Fail("Tool is not available in the current route/phase: " + step.ToolId);
            }
            if (route != null && string.Equals(route.Phase, AgentPhases.ReadOnly, StringComparison.OrdinalIgnoreCase) && tool.MutatesDocument)
            {
                return PlannerValidationResult.Fail("Mutation tool is not allowed during read_only_phase: " + step.ToolId);
            }
            if (route != null && tool.RiskLevel > route.RiskAllowed)
            {
                return PlannerValidationResult.Fail("Tool risk level is above current route allowance: " + step.ToolId);
            }
            if (route != null && route.RequiresInspection && tool.MutatesDocument && !HasInspectionObservation(observations))
            {
                return PlannerValidationResult.Fail("Target must be inspected before mutation. Use a read/context tool first.");
            }

            var command = new ToolCommand { ToolId = step.ToolId, Description = step.Reason };
            foreach (var pair in step.Arguments ?? new Dictionary<string, object>())
            {
                command.Arguments[pair.Key] = pair.Value;
            }
            return PlannerValidationResult.Ok(command, tool);
        }

        private static bool HasInspectionObservation(IEnumerable<AgentObservation> observations)
        {
            foreach (var observation in observations ?? new AgentObservation[0])
            {
                if (observation != null && string.Equals(observation.Status, "success", StringComparison.OrdinalIgnoreCase) && !observation.Mutation)
                {
                    return true;
                }
            }
            return false;
        }
    }

    internal sealed class ObservationNormalizer
    {
        private int _nextId = 1;

        public AgentObservation Normalize(ToolCommand command, ToolDefinition tool, ToolResult result)
        {
            var id = "obs_" + _nextId++;
            var success = result != null && result.Success;
            var observation = new AgentObservation
            {
                Id = id,
                ToolId = command == null ? string.Empty : command.ToolId,
                Status = success ? "success" : "error",
                Mutation = tool != null && tool.MutatesDocument,
                RequiresVerification = success && tool != null && tool.MutatesDocument,
                Summary = BuildSummary(command, result, tool),
                FactsJson = BuildFactsJson(command, result)
            };
            return observation;
        }

        private static string BuildSummary(ToolCommand command, ToolResult result, ToolDefinition tool)
        {
            var toolId = command == null ? string.Empty : command.ToolId;
            var status = result != null && result.Success ? "succeeded" : "failed";
            var message = result == null ? string.Empty : result.Message ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(message))
            {
                return toolId + " " + status + ": " + Trim(message, 500);
            }
            return toolId + " " + status + ".";
        }

        private static string BuildFactsJson(ToolCommand command, ToolResult result)
        {
            if (result == null || string.IsNullOrWhiteSpace(result.DataJson))
            {
                return null;
            }
            try
            {
                var token = JToken.Parse(result.DataJson);
                return token.ToString(Formatting.None);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string Trim(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
            {
                return value ?? string.Empty;
            }
            return value.Substring(0, Math.Max(0, maxChars)) + "\n[truncated]";
        }
    }

    internal sealed class VerificationRunner
    {
        public IEnumerable<ToolCommand> BuildVerificationCommands(ToolCommand command, ToolDefinition tool, IReadOnlyList<ToolDefinition> allTools)
        {
            if (command == null || tool == null || !tool.MutatesDocument)
            {
                yield break;
            }

            ToolCommand explicitCommand;
            if (TryBuildExplicitVerification(command, tool, out explicitCommand) &&
                HasReadOnlyTool(allTools, explicitCommand.ToolId))
            {
                yield return explicitCommand;
                yield break;
            }

            var host = tool.Host ?? string.Empty;
            if (string.Equals(host, "Excel", StringComparison.OrdinalIgnoreCase))
            {
                if (HasReadOnlyTool(allTools, "excel.list_charts") && Contains(command.ToolId, "chart"))
                {
                    yield return CopyArgs(new ToolCommand { ToolId = "excel.list_charts", Description = "Deterministic verification" }, command, "sheet");
                    yield break;
                }
                if (HasReadOnlyTool(allTools, "excel.read_range") && (command.Arguments.ContainsKey("address") || command.Arguments.ContainsKey("range") || command.Arguments.ContainsKey("sourceRange") || command.Arguments.ContainsKey("startAddress")))
                {
                    var verify = new ToolCommand { ToolId = "excel.read_range", Description = "Deterministic verification" };
                    CopyArg(command, verify, "sheet");
                    var address = FirstArg(command, "address", "range", "sourceRange", "startAddress");
                    if (!string.IsNullOrWhiteSpace(address))
                    {
                        verify.Arguments["address"] = address;
                    }
                    yield return verify;
                    yield break;
                }
                if (HasReadOnlyTool(allTools, "excel.workbook_summary"))
                {
                    yield return new ToolCommand { ToolId = "excel.workbook_summary", Description = "Deterministic verification" };
                    yield break;
                }
            }

            if (string.Equals(host, "Word", StringComparison.OrdinalIgnoreCase) && HasReadOnlyTool(allTools, "word.read_document"))
            {
                var verify = new ToolCommand { ToolId = "word.read_document", Description = "Deterministic verification" };
                verify.Arguments["maxChars"] = 12000;
                yield return verify;
                yield break;
            }

            if (string.Equals(host, "PowerPoint", StringComparison.OrdinalIgnoreCase) && HasReadOnlyTool(allTools, "powerpoint.read_slides"))
            {
                var verify = new ToolCommand { ToolId = "powerpoint.read_slides", Description = "Deterministic verification" };
                verify.Arguments["maxSlides"] = 20;
                yield return verify;
                yield break;
            }

            if (string.Equals(host, "Outlook", StringComparison.OrdinalIgnoreCase) && HasReadOnlyTool(allTools, "outlook.get_context"))
            {
                yield return new ToolCommand { ToolId = "outlook.get_context", Description = "Deterministic verification" };
            }
        }

        private static bool TryBuildExplicitVerification(ToolCommand command, ToolDefinition tool, out ToolCommand verify)
        {
            verify = null;
            if (string.IsNullOrWhiteSpace(tool.VerifyJson))
            {
                return false;
            }
            try
            {
                var root = JObject.Parse(tool.VerifyJson);
                var toolId = (string)root["toolId"];
                if (string.IsNullOrWhiteSpace(toolId))
                {
                    return false;
                }
                verify = new ToolCommand { ToolId = toolId, Description = "Deterministic verification" };
                var args = root["argumentsFrom"] as JObject;
                if (args != null)
                {
                    foreach (var property in args.Properties())
                    {
                        var source = (property.Value.Value<string>() ?? string.Empty).Replace("previous.arguments.", string.Empty);
                        if (command.Arguments.ContainsKey(source))
                        {
                            verify.Arguments[property.Name] = command.Arguments[source];
                        }
                    }
                }
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool HasReadOnlyTool(IEnumerable<ToolDefinition> tools, string id)
        {
            return (tools ?? new ToolDefinition[0]).Any(t =>
                t != null &&
                t.Enabled &&
                !t.MutatesDocument &&
                string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        private static bool Contains(string value, string term)
        {
            return (value ?? string.Empty).IndexOf(term ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static ToolCommand CopyArgs(ToolCommand target, ToolCommand source, params string[] names)
        {
            foreach (var name in names ?? new string[0])
            {
                CopyArg(source, target, name);
            }
            return target;
        }

        private static void CopyArg(ToolCommand source, ToolCommand target, string name)
        {
            if (source != null && target != null && source.Arguments.ContainsKey(name))
            {
                target.Arguments[name] = source.Arguments[name];
            }
        }

        private static string FirstArg(ToolCommand command, params string[] names)
        {
            foreach (var name in names ?? new string[0])
            {
                if (command != null && command.Arguments.ContainsKey(name) && command.Arguments[name] != null)
                {
                    return Convert.ToString(command.Arguments[name]);
                }
            }
            return null;
        }
    }

    internal sealed class RecipeExpander
    {
        public IEnumerable<ToolCommand> Expand(ToolCommand recipe, IReadOnlyList<AgentObservation> observations)
        {
            if (recipe == null || !string.Equals(recipe.ToolId, "recipe.excel.make_table_pretty", StringComparison.OrdinalIgnoreCase))
            {
                yield return recipe;
                yield break;
            }

            var range = FindRange(observations);
            var format = new ToolCommand { ToolId = "excel.format_range", Description = "Apply clean table formatting" };
            format.Arguments["sheet"] = "active";
            format.Arguments["address"] = string.IsNullOrWhiteSpace(range) ? "used_range" : range;
            format.Arguments["bold"] = true;
            format.Arguments["horizontalAlignment"] = "center";
            yield return format;

            var autofit = new ToolCommand { ToolId = "excel.autofit", Description = "Autofit formatted table" };
            autofit.Arguments["sheet"] = "active";
            autofit.Arguments["address"] = string.IsNullOrWhiteSpace(range) ? string.Empty : range;
            yield return autofit;
        }

        private static string FindRange(IEnumerable<AgentObservation> observations)
        {
            foreach (var observation in observations ?? new AgentObservation[0])
            {
                var text = (observation == null ? string.Empty : observation.Summary + " " + observation.FactsJson) ?? string.Empty;
                var match = System.Text.RegularExpressions.Regex.Match(text, "[A-Z]{1,3}[0-9]+:[A-Z]{1,3}[0-9]+");
                if (match.Success)
                {
                    return match.Value;
                }
            }
            return null;
        }
    }
}
