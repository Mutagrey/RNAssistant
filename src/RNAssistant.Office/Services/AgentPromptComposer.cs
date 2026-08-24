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
            var adapterHost = SafeAdapterValue(adapter, item => item.HostName);
            var adapterDocumentKey = SafeAdapterValue(adapter, item => item.DocumentKey);
            var adapterDocumentTitle = SafeAdapterValue(adapter, item => item.DocumentTitle);
            var host = session != null && !string.IsNullOrWhiteSpace(session.Host)
                ? session.Host
                : adapterHost;
            var documentKey = session != null && !string.IsNullOrWhiteSpace(session.DocumentKey)
                ? session.DocumentKey
                : adapterDocumentKey;
            var documentTitle = session != null && !string.IsNullOrWhiteSpace(session.DocumentTitle)
                ? session.DocumentTitle
                : adapterDocumentTitle;
            var officeToolsAvailable = session == null
                ? adapter != null
                : OfficeDocumentExecutionGuardState.SessionMatchesAdapter(adapter, session);
            var root = new JObject
            {
                ["host"] = host ?? string.Empty,
                ["document"] = new JObject
                {
                    ["key"] = documentKey ?? string.Empty,
                    ["title"] = documentTitle ?? string.Empty,
                    ["office_tools_available"] = officeToolsAvailable,
                    ["office_tool_policy"] = officeToolsAvailable
                        ? "Office object-model tools may target this open document."
                        : "The chat document is closed or inactive. Do not call Office object-model tools until it is opened; continue with non-Office tools such as the HTML workspace when useful."
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

        private static string SafeAdapterValue(
            IOfficeApplicationAdapter adapter,
            Func<IOfficeApplicationAdapter, string> read)
        {
            if (adapter == null || read == null) return string.Empty;
            try
            {
                return read(adapter) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
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
                    ["description"] = skill.Description ?? string.Empty,
                    ["revision"] = SkillRevision.Compute(skill),
                    ["bodyChars"] = (skill.BodyMarkdown ?? string.Empty).Length,
                    ["referenceCount"] = (skill.References ?? new List<SkillReferenceMetadata>()).Count
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
