using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Services
{
    internal sealed class AgentPromptComposer
    {
        public List<ChatMessage> BuildMessages(
            string userText,
            IOfficeApplicationAdapter adapter,
            IReadOnlyList<ToolDefinition> tools,
            IReadOnlyList<SkillDefinition> skills,
            DocumentContext context,
            AppSettings settings,
            ChatSession session,
            IReadOnlyList<ChatAttachment> attachments,
            bool replayCurrentUserInHistory = false,
            int historyBudgetTokens = 0)
        {
            settings = settings ?? new AppSettings();
            var instruction = string.IsNullOrWhiteSpace(settings.SystemPrompt)
                ? new AppSettings().SystemPrompt
                : settings.SystemPrompt.Trim();
            var runtimeContext = BuildRuntimeContext(adapter, tools, skills, context, session, settings);
            var role = NormalizeInstructionRole(settings.SystemPromptRole);
            var messages = new List<ChatMessage>();
            if (!string.Equals(role, "user", StringComparison.Ordinal))
            {
                messages.Add(new ChatMessage
                {
                    Role = role,
                    Content = instruction + "\n\nRUNTIME_CONTEXT:\n" + runtimeContext
                });
            }

            if (replayCurrentUserInHistory)
            {
                if (string.Equals(role, "user", StringComparison.Ordinal))
                {
                    messages.Add(new ChatMessage
                    {
                        Role = "user",
                        Content = instruction + "\n\nRUNTIME_CONTEXT:\n" + runtimeContext
                    });
                }
                new PromptBudgetComposer().AddConversationHistory(
                    messages,
                    messages.Count,
                    session,
                    settings,
                    historyBudgetTokens,
                    true,
                    false);
                return messages;
            }

            var currentText = userText ?? string.Empty;
            if (string.Equals(role, "user", StringComparison.Ordinal))
            {
                currentText = instruction + "\n\nRUNTIME_CONTEXT:\n" + runtimeContext + "\n\nUSER_REQUEST:\n" + currentText;
            }
            var current = new ChatMessage
            {
                Role = "user",
                Content = currentText,
                Attachments = attachments == null
                    ? new List<ChatAttachment>()
                    : new List<ChatAttachment>(attachments)
            };
            var currentIndex = messages.Count;
            messages.Add(current);
            new PromptBudgetComposer().AddConversationHistory(
                messages,
                currentIndex,
                session,
                settings,
                historyBudgetTokens);
            return messages;
        }

        internal static string BuildRuntimeContext(
            IOfficeApplicationAdapter adapter,
            IReadOnlyList<ToolDefinition> tools,
            IReadOnlyList<SkillDefinition> skills,
            DocumentContext context,
            ChatSession session,
            AppSettings settings = null)
        {
            var root = new JObject
            {
                ["host"] = adapter == null ? string.Empty : adapter.HostName ?? string.Empty,
                ["document"] = new JObject
                {
                    ["key"] = adapter == null ? string.Empty : adapter.DocumentKey ?? string.Empty,
                    ["title"] = adapter == null ? string.Empty : adapter.DocumentTitle ?? string.Empty
                },
                ["chat"] = new JObject
                {
                    ["html_workspace_preferred"] = session != null && session.HtmlModeEnabled
                },
                ["tools"] = BuildTools(tools),
                ["skills"] = BuildSkills(skills),
                ["user_context"] = BuildUserContext(context)
            };
            var artifacts = ChatArtifactService.BuildPromptIndex(session, 2000, settings);
            if (!string.IsNullOrWhiteSpace(artifacts)) root["artifacts"] = artifacts;
            return root.ToString(Formatting.None);
        }

        internal static JArray BuildTools(IEnumerable<ToolDefinition> tools)
        {
            var result = new JArray();
            foreach (var tool in tools ?? new ToolDefinition[0])
            {
                if (tool == null || string.IsNullOrWhiteSpace(tool.Id)) continue;
                JObject schema;
                string schemaError;
                if (!ToolSchemaSupport.TryParse(tool, out schema, out schemaError))
                {
                    continue;
                }
                result.Add(new JObject
                {
                    ["type"] = "function",
                    ["function"] = new JObject
                    {
                        ["name"] = tool.Id,
                        ["description"] = BuildDescription(tool),
                        ["parameters"] = schema
                    },
                    ["safety"] = new JObject
                    {
                        ["mutates_document"] = tool.MutatesDocument,
                        ["mutates_local_state"] = tool.MutatesLocalState,
                        ["requires_confirmation"] = tool.RequiresConfirmation,
                        ["risk_level"] = tool.RiskLevel
                    }
                });
            }
            return result;
        }

        private static string BuildDescription(ToolDefinition tool)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(tool.Description)) parts.Add(tool.Description.Trim());
            if (!string.IsNullOrWhiteSpace(tool.UseWhen)) parts.Add("Use when: " + tool.UseWhen.Trim());
            if (!string.IsNullOrWhiteSpace(tool.DoNotUseWhen)) parts.Add("Do not use when: " + tool.DoNotUseWhen.Trim());
            if (!string.IsNullOrWhiteSpace(tool.Limitations)) parts.Add("Limitations: " + tool.Limitations.Trim());
            return string.Join(" ", parts.ToArray());
        }

        private static JArray BuildSkills(IEnumerable<SkillDefinition> skills)
        {
            return new JArray((skills ?? new SkillDefinition[0])
                .Where(skill => skill != null && skill.Enabled)
                .Select(skill => new JObject
                {
                    ["id"] = skill.Id ?? string.Empty,
                    ["name"] = skill.Name ?? string.Empty,
                    ["description"] = skill.Description ?? string.Empty
                }));
        }

        private static JArray BuildUserContext(DocumentContext context)
        {
            return new JArray((context == null ? null : context.Notes ?? new List<ContextNote>())
                .Where(note => note != null)
                .Select(note => new JObject
                {
                    ["title"] = note.Title ?? string.Empty,
                    ["kind"] = note.Kind ?? string.Empty,
                    ["reference"] = note.Reference ?? string.Empty,
                    ["content"] = !string.IsNullOrWhiteSpace(note.Text) ? note.Text : note.Preview ?? string.Empty
                }));
        }

        private static string NormalizeInstructionRole(string role)
        {
            if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase)) return "system";
            if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase)) return "user";
            return "developer";
        }
    }
}
