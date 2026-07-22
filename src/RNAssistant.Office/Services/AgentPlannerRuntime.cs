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
    internal sealed class OfficeIntentRouter
    {
        public RoutedTask Route(string userText, OfficeSnapshot snapshot, ChatSession session)
        {
            var route = Route(userText, snapshot);
            if (ShouldContinueHtmlWorkspace(userText, route, session))
            {
                var existingWorkspace = HasHtmlWorkspaceContent(session);
                route.Mode = "mutate_html";
                route.TaskType = "html";
                route.Phase = AgentPhases.Mutation;
                route.RiskAllowed = 1;
                route.RequiresTool = true;
                route.RequiresInspection = existingWorkspace;
                route.DecisionReason = session != null && session.HtmlModeEnabled
                    ? "html_mode"
                    : "html_workspace_follow_up";
            }
            return route;
        }

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
                RequiresInspection = false,
                DecisionReason = "default_answer"
            };

            if (LooksLikeGeneralQuestion(value) &&
                !LooksLikeCurrentOfficeQuestion(value) &&
                !MentionsCurrentOfficeContext(value))
            {
                route.DecisionReason = "general_question";
                return route;
            }

            if ((ContainsAny(value, "удали", "очисти") || ContainsAnyToken(value, "delete", "remove", "clear")) &&
                !ContainsAny(value, "custom tool", "tools", "prompt", "prompts", "skill", "skills", "инструмент", "промпт", "скилл"))
            {
                route.Mode = "destructive_mutation";
                route.TaskType = DestructiveTaskType(value);
                route.Phase = AgentPhases.ReadOnly;
                route.RiskAllowed = 0;
                route.RequiresTool = true;
                route.RequiresInspection = true;
                route.DecisionReason = "destructive_request";
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
                route.DecisionReason = "macro_execution";
                return route;
            }

            if (ContainsAny(value, "vba", "macro", "макрос", "макро", "visual basic"))
            {
                var mutatesVba = LooksLikeVbaMutation(value);
                var inspectsExistingVba = mutatesVba && LooksLikeExistingVbaEdit(value) && !LooksLikeNewVbaModule(value);
                route.Mode = mutatesVba ? "mutate_vba" : "read_vba";
                route.TaskType = "vba";
                route.Phase = mutatesVba && !inspectsExistingVba ? AgentPhases.Mutation : AgentPhases.ReadOnly;
                route.RiskAllowed = mutatesVba ? 3 : 0;
                route.RequiresTool = true;
                route.RequiresInspection = inspectsExistingVba;
                route.DecisionReason = "vba_request";
                return route;
            }

            if (ContainsAny(value, "html", "страниц", "web page", "webpage", "dashboard", "дашборд") ||
                ContainsAnyToken(value, "ui"))
            {
                route.Mode = "mutate_html";
                route.TaskType = "html";
                route.Phase = AgentPhases.Mutation;
                route.RiskAllowed = 1;
                route.RequiresTool = true;
                route.RequiresInspection = false;
                route.DecisionReason = "html_request";
                return route;
            }

            if (ContainsAny(value, "custom tool", "tools", "prompt", "prompts", "skill", "skills", "инструмент", "промпт", "скилл"))
            {
                var mutatesCatalog =
                    ContainsAny(value, "создай", "создать", "добавь", "измени", "обнови", "удали", "сохрани") ||
                    ContainsAnyToken(value, "create", "add", "update", "delete", "remove", "save");
                route.Mode = mutatesCatalog ? "mutate_tool_authoring" : "read_tool_authoring";
                route.TaskType = "tool_authoring";
                route.Phase = mutatesCatalog ? AgentPhases.Mutation : AgentPhases.ReadOnly;
                route.RiskAllowed = 1;
                route.RequiresTool = true;
                route.RequiresInspection = false;
                route.DecisionReason = mutatesCatalog ? "tool_catalog_mutation" : "tool_catalog_read";
                return route;
            }

            if (ContainsAny(value, "сделай", "создай", "создать", "построй", "сгенерируй", "заполни", "вставь", "замени", "измени", "добавь", "напиши") ||
                ContainsAnyToken(value, "create", "make", "add", "insert", "replace", "update", "write", "generate", "build", "draft"))
            {
                route.RequiresTool = true;
                route.RiskAllowed = 2;
                route.Phase = AgentPhases.Mutation;
                route.Mode = "mutate";
                route.TaskType = "content";
                route.DecisionReason = "content_mutation";
            }

            if (ContainsAny(value, "красив", "оформи", "автоподбор") ||
                ContainsAnyToken(value, "format", "style", "pretty", "autofit"))
            {
                route.RequiresTool = true;
                route.Mode = "mutate_formatting";
                route.TaskType = "formatting";
                route.Phase = AgentPhases.ReadOnly;
                route.RiskAllowed = 1;
                route.RequiresInspection = true;
                route.DecisionReason = "formatting_request";
            }
            else if ((ContainsAny(value, "график", "диаграм") || ContainsAnyToken(value, "chart", "plot")) &&
                !ContainsAny(value, "создай", "создать", "сгенерируй", "отчет") &&
                !ContainsAnyToken(value, "create", "generate", "report"))
            {
                route.RequiresTool = true;
                route.Mode = "mutate_chart";
                route.TaskType = "chart";
                route.Phase = AgentPhases.ReadOnly;
                route.RiskAllowed = 2;
                route.RequiresInspection = true;
                route.DecisionReason = "chart_request";
            }
            else if (!route.RequiresTool &&
                (ContainsAny(value, "прочитай", "покажи", "найди", "поиск", "перечисли", "перескажи", "проанализ", "проверь", "сводк", "резюм") ||
                 ContainsAnyToken(value, "summarize", "summarise", "summary", "analyze", "review", "inspect", "check", "read", "search", "find", "list") ||
                 LooksLikeCurrentOfficeQuestion(value) ||
                 MentionsCurrentOfficeContext(value)))
            {
                route.RequiresTool = true;
                route.Mode = ContainsAny(value, "перескажи", "проанализ", "сводк", "резюм") ||
                    ContainsAnyToken(value, "summarize", "summarise", "summary", "analyze", "review")
                    ? "analyze"
                    : "read";
                route.TaskType = ContainsAny(value, "mail", "email", "письм") ? "mail_search" : "read";
                route.Phase = AgentPhases.ReadOnly;
                route.RiskAllowed = 0;
                route.RequiresInspection = false;
                route.DecisionReason = "document_read";
            }

            return route;
        }

        private static string DestructiveTaskType(string value)
        {
            if (ContainsAny(value, "график", "диаграм") || ContainsAnyToken(value, "chart", "plot"))
            {
                return "chart";
            }
            if (ContainsAny(value, "vba", "macro", "макрос", "макро", "visual basic"))
            {
                return "vba";
            }
            return "content";
        }

        private static bool LooksLikeVbaMutation(string value)
        {
            return ContainsAny(
                    value,
                    "создай", "создать", "созд", "добавь", "добавить", "добав",
                    "напиши", "запиши", "встав", "замени", "измен", "исправ", "обнов", "удали", "очист") ||
                ContainsAnyToken(value, "create", "add", "write", "insert", "replace", "update", "edit", "fix", "delete", "remove", "generate");
        }

        private static bool LooksLikeExistingVbaEdit(string value)
        {
            return ContainsAny(value, "замени", "измен", "исправ", "обнов", "удали", "очист", "patch", "патч") ||
                ContainsAnyToken(value, "replace", "update", "edit", "fix", "delete", "remove", "patch");
        }

        private static bool LooksLikeNewVbaModule(string value)
        {
            return ContainsAny(value, "новый модул", "нового модул", "создай", "создать", "добавь", "добавить") ||
                ContainsAnyToken(value, "create", "add", "insert", "new");
        }

        private static bool HasHtmlWorkspaceContent(ChatSession session)
        {
            return session != null &&
                session.HtmlWorkspace != null &&
                ((session.HtmlWorkspace.Files != null && session.HtmlWorkspace.Files.Count > 0) ||
                 (session.HtmlWorkspace.DataSources != null && session.HtmlWorkspace.DataSources.Count > 0));
        }

        private static bool ShouldContinueHtmlWorkspace(string userText, RoutedTask route, ChatSession session)
        {
            if (session == null)
            {
                return false;
            }
            if (session.HtmlModeEnabled)
            {
                return true;
            }
            if (!HasHtmlWorkspaceContent(session))
            {
                return false;
            }
            if (route != null && string.Equals(route.TaskType, "html", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var value = (userText ?? string.Empty).ToLowerInvariant();
            if (ContainsAny(value, "лист", "ячейк", "диапазон", "книг", "документ", "слайд", "письм") ||
                ContainsAnyToken(
                    value,
                    "excel", "word", "powerpoint", "outlook", "vba",
                    "workbook", "worksheet", "sheet", "range", "document", "slide", "email"))
            {
                return false;
            }

            var routedWorkspaceChange =
                route != null &&
                route.RequiresTool &&
                (string.Equals(route.TaskType, "content", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(route.TaskType, "formatting", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(route.TaskType, "chart", StringComparison.OrdinalIgnoreCase));
            var explicitWorkspaceEdit = ContainsAny(
                    value,
                    "html", "workspace", "файл", "источник данных", "data source",
                    "зависим", "локальн", "цвет", "легенд", "анимац", "подсказ", "ползунк", "кноп",
                    "стил", "макет", "адаптив", "dependency", "local", "color", "legend", "animation",
                    "tooltip", "slider", "button", "layout", "responsive");
            return routedWorkspaceChange || explicitWorkspaceEdit;
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

        private static bool LooksLikeCurrentOfficeQuestion(string value)
        {
            return (ContainsAny(value, "текущ", "этой таблиц", "этом документ", "выделен", "лист", "книг", "слайд", "презентац", "письм") ||
                    ContainsAnyToken(value, "workbook", "spreadsheet", "document", "selection", "sheet", "slide", "presentation", "email")) &&
                (ContainsAny(value, "что", "какие", "где", "сколько") ||
                 ContainsAnyToken(value, "what", "which", "where") ||
                 value.IndexOf("how many", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool MentionsCurrentOfficeContext(string value)
        {
            return ContainsAny(
                value,
                "текущ",
                "этой таблиц",
                "этом документ",
                "активн",
                "выделен",
                "this workbook",
                "this spreadsheet",
                "this document",
                "active sheet",
                "active slide",
                "current selection");
        }

        private static bool ContainsAnyToken(string value, params string[] terms)
        {
            foreach (var term in terms ?? new string[0])
            {
                if (ContainsToken(value, term))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool ContainsToken(string value, string term)
        {
            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(term))
            {
                return false;
            }
            var start = 0;
            while (start < value.Length)
            {
                var index = value.IndexOf(term, start, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    return false;
                }
                var before = index == 0 || !IsWordCharacter(value[index - 1]);
                var end = index + term.Length;
                var after = end >= value.Length || !IsWordCharacter(value[end]);
                if (before && after)
                {
                    return true;
                }
                start = index + 1;
            }
            return false;
        }

        private static bool IsWordCharacter(char value)
        {
            return char.IsLetterOrDigit(value) || value == '_';
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
        public ToolCatalogSlice Slice(
            RoutedTask route,
            IEnumerable<ToolDefinition> tools,
            IReadOnlyList<AgentObservation> observations,
            int maxTools = 24,
            bool allowAgentToolAuthoring = false)
        {
            var slice = new ToolCatalogSlice();
            if (route != null && !route.RequiresTool)
            {
                return slice;
            }
            var host = route == null ? string.Empty : route.App ?? string.Empty;
            foreach (var tool in tools ?? new ToolDefinition[0])
            {
                var exclusion = CandidateExclusion(tool, host);
                if (exclusion != null)
                {
                    slice.Excluded.Add(exclusion);
                    continue;
                }
                if (!AllowedForPhase(tool, route))
                {
                    slice.Excluded.Add(Exclude(tool, "wrong_phase", "Tool risk or mutation mode is not allowed in phase " + (route == null ? string.Empty : route.Phase) + "."));
                    continue;
                }
                if (!Relevant(tool, route) && !OptionalToolAuthoring(tool, route, allowAgentToolAuthoring))
                {
                    slice.Excluded.Add(Exclude(tool, "not_relevant", "Tool does not match task type " + (route == null ? string.Empty : route.TaskType) + "."));
                    continue;
                }
                slice.Tools.Add(tool);
            }

            foreach (var recipe in Recipes(route))
            {
                slice.Tools.Add(recipe);
            }

            var ordered = slice.Tools
                .GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(t => ToolPriority(t, route))
                .ThenBy(t => t.RiskLevel)
                .ThenBy(t => t.Id)
                .ToList();
            slice.Tools = SelectBalancedTools(ordered, route, Math.Max(8, Math.Min(64, maxTools)));
            var selectedIds = new HashSet<string>(slice.Tools.Select(tool => tool.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var omitted in ordered.Where(tool => !selectedIds.Contains(tool.Id)))
            {
                slice.Excluded.Add(Exclude(omitted, "selection_limit", "A higher-priority balanced set filled the prompt tool budget."));
            }
            return slice;
        }

        private static ToolExclusion CandidateExclusion(ToolDefinition tool, string host)
        {
            if (tool == null)
            {
                return new ToolExclusion { ToolId = string.Empty, Reason = "invalid_definition", Detail = "Tool definition is null." };
            }
            if (string.IsNullOrWhiteSpace(tool.Id))
            {
                return Exclude(tool, "missing_id", "Tool id is empty.");
            }
            if (!tool.Enabled)
            {
                return Exclude(tool, "disabled", "Tool is disabled.");
            }
            if (!string.Equals(tool.CapabilityStatus ?? "available", "available", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(tool.CapabilityStatus ?? "available", "partial", StringComparison.OrdinalIgnoreCase))
            {
                return Exclude(tool, "capability_unavailable", "Capability status is " + tool.CapabilityStatus + ".");
            }
            if (!string.Equals(tool.Host, host, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(tool.Host, "Common", StringComparison.OrdinalIgnoreCase))
            {
                return Exclude(tool, "wrong_host", "Tool host " + tool.Host + " does not match " + host + ".");
            }
            return null;
        }

        private static List<ToolDefinition> SelectBalancedTools(
            IReadOnlyList<ToolDefinition> ordered,
            RoutedTask route,
            int limit)
        {
            var selected = new List<ToolDefinition>();
            Action<IEnumerable<ToolDefinition>, int> add = (source, count) =>
            {
                foreach (var tool in source)
                {
                    if (selected.Count >= limit || count <= 0)
                    {
                        break;
                    }
                    if (selected.Any(existing => string.Equals(existing.Id, tool.Id, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }
                    selected.Add(tool);
                    count -= 1;
                }
            };

            var mutationPhase = route != null &&
                string.Equals(route.Phase, AgentPhases.Mutation, StringComparison.OrdinalIgnoreCase);
            if (mutationPhase)
            {
                add(ordered.Where(tool => tool.MutatesDocument), Math.Max(1, (int)Math.Ceiling(limit * 0.6)));
                add(ordered.Where(tool => !tool.MutatesDocument && LooksLikeInspectionTool(tool)), Math.Max(2, limit / 4));
            }
            else
            {
                add(ordered.Where(tool => !tool.MutatesDocument && LooksLikeInspectionTool(tool)), Math.Max(4, limit / 3));
            }
            add(ordered, limit - selected.Count);
            return selected;
        }

        private static bool LooksLikeInspectionTool(ToolDefinition tool)
        {
            return tool != null && ContainsAny(
                (tool.Id ?? string.Empty) + " " + (tool.UseWhen ?? string.Empty),
                "context", "selection", "summary", "read", "profile", "list", "search", "inspect", "get_");
        }

        private static ToolExclusion Exclude(ToolDefinition tool, string reason, string detail)
        {
            return new ToolExclusion
            {
                ToolId = tool == null ? string.Empty : tool.Id,
                Reason = reason,
                Detail = detail
            };
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
            if (route.TaskType == "content")
            {
                return true;
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
            if (route.TaskType == "macro_execution")
            {
                return ContainsAny(id, "vba", "macro", "context");
            }
            if (route.TaskType == "destructive")
            {
                return true;
            }
            return !tool.MutatesDocument || ContainsAny(id, "read", "list", "search", "context", "summary");
        }

        private static bool OptionalToolAuthoring(ToolDefinition tool, RoutedTask route, bool enabled)
        {
            if (!enabled || tool == null || route == null ||
                !string.Equals(route.Phase, AgentPhases.Mutation, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return string.Equals(tool.Id, "common.tools_validate", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tool.Id, "common.tools_save", StringComparison.OrdinalIgnoreCase);
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
            if (ContainsAny(id, "common.tools_validate", "common.tools_save"))
            {
                return 5;
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
            var instruction = BuildInstructionPrompt(settings);
            var plannerContext = BuildPlannerContext(userText, snapshot, route, tools, observations, context, skills, settings);
            var systemRole = string.Equals(PromptRole(settings), "system", StringComparison.Ordinal);
            if (systemRole)
            {
                messages.Add(new ChatMessage { Role = "system", Content = instruction });
            }
            var current = new ChatMessage
            {
                Role = "user",
                Content = systemRole ? plannerContext : instruction + "\n\n" + plannerContext
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
                    prompts.ToolRoutingPrompt
                }.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()));
        }

        private static string PromptRole(AppSettings settings)
        {
            return settings != null &&
                string.Equals(settings.SystemPromptRole, "system", StringComparison.OrdinalIgnoreCase)
                ? "system"
                : "user";
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
            builder.AppendLine("maxPlanActions: " + Math.Max(1, settings == null ? 1 : settings.MaxAgentPlanSteps));
            builder.AppendLine("maxReadOnlyPlanActions: " + Math.Max(1, settings == null ? 4 : settings.MaxAgentReadOnlyPlanSteps));
            builder.AppendLine("Document mutation and VBA plans must contain exactly one action. A multi-action batch is allowed only for independent read-only tools that do not require confirmation.");
            if (route != null && string.Equals(route.TaskType, "html", StringComparison.OrdinalIgnoreCase))
            {
                builder.AppendLine("HTML MODE IS ENABLED FOR THIS CHAT.");
                builder.AppendLine("Use common.html_workspace_read before editing or deleting existing files. Use common.html_workspace_upsert_file/data for editable HTML workspace output and common.html_workspace_delete_file/data to remove items.");
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
                builder.AppendLine("   risk: level_" + tool.RiskLevel + "; mode: " + (tool.MutatesDocument || tool.MutatesLocalState ? "mutation" : "read"));
                builder.AppendLine("   confirmation: " + (tool.RequiresConfirmation ? "required" : "runtime policy"));
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
            var usedTokens = 0;
            var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var wroteAny = false;
            foreach (var note in context.Notes)
            {
                if (note == null)
                {
                    continue;
                }
                var content = FirstNonEmpty(note.Text, note.Preview);
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
                var entry = "- " + FirstNonEmpty(note.Title, note.Reference, note.Kind) + ": " + content;
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
        public PlannerValidationResult Validate(
            AgentPlannerStep step,
            ToolCatalogSlice slice,
            RoutedTask route,
            IReadOnlyList<AgentObservation> observations,
            IReadOnlyList<ToolDefinition> allTools)
        {
            if (step == null || string.IsNullOrWhiteSpace(step.ToolId))
            {
                return PlannerValidationResult.Fail("Planner step has no toolId.");
            }
            var tool = slice == null ? null : slice.Find(step.ToolId);
            if (tool == null)
            {
                var known = AgentToolCatalogResolver.Find(allTools, step.ToolId);
                if (known == null)
                {
                    var suggestions = ToolIdSuggestions.Find(step.ToolId, allTools, 3);
                    return PlannerValidationResult.Fail(
                        "Unknown tool id: " + step.ToolId + ". Use only exact ids from AVAILABLE_TOOLS." +
                        (suggestions.Count == 0 ? string.Empty : " Did you mean: " + string.Join(", ", suggestions.ToArray()) + "?"));
                }

                var exclusion = slice == null
                    ? null
                    : slice.Excluded.FirstOrDefault(item =>
                        item != null && string.Equals(item.ToolId, step.ToolId, StringComparison.OrdinalIgnoreCase));
                return PlannerValidationResult.Fail(
                    "Tool is excluded from the current route: " + step.ToolId + "." +
                    (exclusion == null
                        ? string.Empty
                        : " Reason: " + exclusion.Reason + ". " + exclusion.Detail));
            }
            if (route != null && string.Equals(route.Phase, AgentPhases.ReadOnly, StringComparison.OrdinalIgnoreCase) && tool.MutatesDocument)
            {
                return PlannerValidationResult.Fail("Mutation tool is not allowed during read_only_phase: " + step.ToolId);
            }
            if (route != null && tool.RiskLevel > route.RiskAllowed)
            {
                return PlannerValidationResult.Fail("Tool risk level is above current route allowance: " + step.ToolId);
            }
            if (route != null &&
                route.RequiresInspection &&
                (tool.MutatesDocument || tool.MutatesLocalState) &&
                !HasInspectionObservation(observations))
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
                if (observation != null &&
                    string.Equals(observation.Status, "success", StringComparison.OrdinalIgnoreCase) &&
                    !observation.Mutation &&
                    !observation.LocalMutation &&
                    string.Equals(observation.Purpose, AgentObservationPurposes.Inspection, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }

    internal static class ToolIdSuggestions
    {
        public static List<string> Find(string requestedToolId, IEnumerable<ToolDefinition> tools, int limit)
        {
            var requested = Tokens(requestedToolId);
            if (requested.Count == 0)
            {
                return new List<string>();
            }

            return (tools ?? new ToolDefinition[0])
                .Where(tool => tool != null && tool.Enabled && !string.IsNullOrWhiteSpace(tool.Id))
                .Select(tool => new
                {
                    Tool = tool,
                    Score = Tokens((tool.Id ?? string.Empty) + " " + (tool.Name ?? string.Empty) + " " + (tool.Description ?? string.Empty))
                        .Count(token => requested.Contains(token))
                })
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Tool.Id.Length)
                .Take(Math.Max(1, limit))
                .Select(item => item.Tool.Id)
                .ToList();
        }

        private static HashSet<string> Tokens(string value)
        {
            var tokens = new HashSet<string>(
                (value ?? string.Empty)
                    .ToLowerInvariant()
                    .Split(new[] { '.', '_', '-', ' ', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(token => token.Length > 1),
                StringComparer.OrdinalIgnoreCase);
            AddAliases(tokens, "create", "add", "insert");
            AddAliases(tokens, "creation", "add", "insert");
            AddAliases(tokens, "worksheet", "sheet");
            AddAliases(tokens, "graph", "chart");
            AddAliases(tokens, "remove", "delete");
            AddAliases(tokens, "macro", "vba");
            return tokens;
        }

        private static void AddAliases(ISet<string> tokens, string source, params string[] aliases)
        {
            if (tokens == null || !tokens.Contains(source))
            {
                return;
            }
            foreach (var alias in aliases ?? new string[0])
            {
                tokens.Add(alias);
            }
        }
    }

    internal sealed class ObservationNormalizer
    {
        private int _nextId = 1;

        public AgentObservation Normalize(ToolCommand command, ToolDefinition tool, ToolResult result, string purpose = null)
        {
            var id = "obs_" + _nextId++;
            var success = result != null && result.Success;
            var observation = new AgentObservation
            {
                Id = id,
                ToolId = command == null ? string.Empty : command.ToolId,
                Status = success ? "success" : "error",
                Mutation = tool != null && tool.MutatesDocument,
                LocalMutation = tool != null && tool.MutatesLocalState,
                RequiresVerification = success && tool != null && tool.MutatesDocument,
                Purpose = string.IsNullOrWhiteSpace(purpose)
                    ? tool != null && (tool.MutatesDocument || tool.MutatesLocalState)
                        ? AgentObservationPurposes.Mutation
                        : AgentObservationPurposes.Inspection
                    : purpose,
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

}
