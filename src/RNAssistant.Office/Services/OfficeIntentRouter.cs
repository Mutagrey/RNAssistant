using System;
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
            var host = AgentText.FirstNonEmpty(snapshot == null ? null : snapshot.Host, "Office");
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

            if (LooksLikeConversationHistoryQuestion(value))
            {
                route.Mode = "answer";
                route.TaskType = "conversation_history";
                route.Phase = AgentPhases.Final;
                route.RequiresTool = false;
                route.RequiresInspection = false;
                route.DecisionReason = "conversation_history";
                return route;
            }

            if (LooksLikeGeneralQuestion(value) &&
                !LooksLikeCurrentOfficeQuestion(value) &&
                !MentionsCurrentOfficeContext(value))
            {
                route.DecisionReason = "general_question";
                return route;
            }

            if ((AgentText.ContainsAny(value, "удали", "очисти") || ContainsAnyToken(value, "delete", "remove", "clear")) &&
                !AgentText.ContainsAny(value, "custom tool", "tools", "prompt", "prompts", "skill", "skills", "инструмент", "промпт", "скилл"))
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

            if (AgentText.ContainsAny(value, "запусти макрос", "run macro", "execute macro"))
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

            if (AgentText.ContainsAny(value, "vba", "macro", "макрос", "макро", "visual basic"))
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

            if (AgentText.ContainsAny(value, "html", "страниц", "web page", "webpage", "dashboard", "дашборд") ||
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

            if (AgentText.ContainsAny(value, "custom tool", "tools", "prompt", "prompts", "skill", "skills", "инструмент", "промпт", "скилл"))
            {
                var mutatesCatalog =
                    AgentText.ContainsAny(value, "создай", "создать", "добавь", "измени", "обнови", "удали", "сохрани") ||
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

            if (AgentText.ContainsAny(value, "сделай", "создай", "создать", "построй", "сгенерируй", "заполни", "вставь", "замени", "измени", "добавь", "напиши") ||
                ContainsAnyToken(value, "create", "make", "add", "insert", "replace", "update", "write", "generate", "build", "draft"))
            {
                route.RequiresTool = true;
                route.RiskAllowed = 2;
                route.Phase = AgentPhases.Mutation;
                route.Mode = "mutate";
                route.TaskType = "content";
                route.DecisionReason = "content_mutation";
            }

            if (AgentText.ContainsAny(value, "красив", "оформи", "автоподбор") ||
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
            else if ((AgentText.ContainsAny(value, "график", "диаграм") || ContainsAnyToken(value, "chart", "plot")) &&
                !AgentText.ContainsAny(value, "создай", "создать", "сгенерируй", "отчет") &&
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
                (AgentText.ContainsAny(value, "прочитай", "покажи", "найди", "поиск", "перечисли", "перескажи", "проанализ", "проверь", "сводк", "резюм") ||
                 ContainsAnyToken(value, "summarize", "summarise", "summary", "analyze", "review", "inspect", "check", "read", "search", "find", "list") ||
                 LooksLikeCurrentOfficeQuestion(value) ||
                 MentionsCurrentOfficeContext(value)))
            {
                route.RequiresTool = true;
                route.Mode = AgentText.ContainsAny(value, "перескажи", "проанализ", "сводк", "резюм") ||
                    ContainsAnyToken(value, "summarize", "summarise", "summary", "analyze", "review")
                    ? "analyze"
                    : "read";
                route.TaskType = AgentText.ContainsAny(value, "mail", "email", "письм") ? "mail_search" : "read";
                route.Phase = AgentPhases.ReadOnly;
                route.RiskAllowed = 0;
                route.RequiresInspection = false;
                route.DecisionReason = "document_read";
            }

            return route;
        }

        private static string DestructiveTaskType(string value)
        {
            if (AgentText.ContainsAny(value, "график", "диаграм") || ContainsAnyToken(value, "chart", "plot"))
            {
                return "chart";
            }
            if (AgentText.ContainsAny(value, "vba", "macro", "макрос", "макро", "visual basic"))
            {
                return "vba";
            }
            return "content";
        }

        private static bool LooksLikeVbaMutation(string value)
        {
            return AgentText.ContainsAny(
                    value,
                    "создай", "создать", "созд", "добавь", "добавить", "добав",
                    "напиши", "запиши", "встав", "замени", "измен", "исправ", "обнов", "удали", "очист") ||
                ContainsAnyToken(value, "create", "add", "write", "insert", "replace", "update", "edit", "fix", "delete", "remove", "generate");
        }

        private static bool LooksLikeConversationHistoryQuestion(string value)
        {
            var mentionsConversation = AgentText.ContainsAny(value,
                "чат", "переписк", "диалог", "сообщени", "мы обсуждали", "мы общались",
                "conversation", "chat history", "previous message", "earlier message");
            if (!mentionsConversation) return false;
            return AgentText.ContainsAny(value,
                "перв", "предыдущ", "раньше", "помни", "истори", "саммари", "сводк", "резюм", "контекст",
                "first", "previous", "earlier", "remember", "history", "summary", "context", "what did");
        }

        private static bool LooksLikeExistingVbaEdit(string value)
        {
            return AgentText.ContainsAny(value, "замени", "измен", "исправ", "обнов", "удали", "очист", "patch", "патч") ||
                ContainsAnyToken(value, "replace", "update", "edit", "fix", "delete", "remove", "patch");
        }

        private static bool LooksLikeNewVbaModule(string value)
        {
            return AgentText.ContainsAny(value, "новый модул", "нового модул", "создай", "создать", "добавь", "добавить") ||
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
            if (AgentText.ContainsAny(value, "лист", "ячейк", "диапазон", "книг", "документ", "слайд", "письм") ||
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
            var explicitWorkspaceEdit = AgentText.ContainsAny(
                    value,
                    "html", "workspace", "файл", "источник данных", "data source",
                    "зависим", "локальн", "цвет", "легенд", "анимац", "подсказ", "ползунк", "кноп",
                    "стил", "макет", "адаптив", "dependency", "local", "color", "legend", "animation",
                    "tooltip", "slider", "button", "layout", "responsive");
            return routedWorkspaceChange || explicitWorkspaceEdit;
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
            return (AgentText.ContainsAny(value, "текущ", "этой таблиц", "этом документ", "выделен", "лист", "книг", "слайд", "презентац", "письм") ||
                    ContainsAnyToken(value, "workbook", "spreadsheet", "document", "selection", "sheet", "slide", "presentation", "email")) &&
                (AgentText.ContainsAny(value, "что", "какие", "где", "сколько") ||
                 ContainsAnyToken(value, "what", "which", "where") ||
                 value.IndexOf("how many", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool MentionsCurrentOfficeContext(string value)
        {
            return AgentText.ContainsAny(
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


    }
}
