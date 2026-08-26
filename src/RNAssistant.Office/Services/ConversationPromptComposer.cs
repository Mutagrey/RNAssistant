using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Services
{
    internal sealed class ConversationPromptComposer
    {
        public List<ChatMessage> BuildMessages(
            string mode,
            string userText,
            IOfficeApplicationAdapter adapter,
            IReadOnlyList<ToolDefinition> tools,
            IReadOnlyList<SkillDefinition> skills,
            DocumentContext context,
            AppSettings settings,
            ChatSession session,
            IReadOnlyList<ChatAttachment> attachments,
            bool replayCurrentUserInHistory = false,
            int historyBudgetTokens = 0,
            JObject toolDiscovery = null)
        {
            settings = settings ?? new AppSettings();
            mode = ChatModes.Normalize(mode);
            var instruction = BuildInstruction(mode, settings);
            var runtimeContext = BuildRuntimeContext(
                mode, adapter, tools, skills, context, session, settings, toolDiscovery);
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

        internal static string BuildInstruction(string mode, AppSettings settings)
        {
            return string.Equals(ChatModes.Normalize(mode), ChatModes.Chat, StringComparison.Ordinal)
                ? ResolveChatPrompt(settings)
                : BuildAgentInstruction(settings);
        }

        internal static string BuildAgentInstruction(AppSettings settings)
        {
            return string.Join("\n\n", new[]
            {
                ResolveGeneralPrompt(settings),
                ResolveToolPrompt(settings),
                ResolveSkillPrompt(settings)
            }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray());
        }

        internal static string ResolveChatPrompt(AppSettings settings)
        {
            return ResolvePrompt(settings == null ? null : settings.ChatSystemPrompt, AgentPromptDefaults.ChatInstructions);
        }

        internal static string ResolveGeneralPrompt(AppSettings settings)
        {
            return ResolvePrompt(settings == null ? null : settings.SystemPrompt, AgentPromptDefaults.GeneralInstructions);
        }

        internal static string ResolveToolPrompt(AppSettings settings)
        {
            return ResolvePrompt(settings == null ? null : settings.AgentToolsPrompt, AgentPromptDefaults.ToolInstructions);
        }

        internal static string ResolveSkillPrompt(AppSettings settings)
        {
            return ResolvePrompt(settings == null ? null : settings.AgentSkillsPrompt, AgentPromptDefaults.SkillInstructions);
        }

        internal static string BuildRuntimeContext(
            string mode,
            IOfficeApplicationAdapter adapter,
            IReadOnlyList<ToolDefinition> tools,
            IReadOnlyList<SkillDefinition> skills,
            DocumentContext context,
            ChatSession session,
            AppSettings settings = null,
            JObject toolDiscovery = null)
        {
            mode = ChatModes.Normalize(mode);
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
                ["mode"] = mode,
                ["host"] = host ?? string.Empty,
                ["document"] = new JObject
                {
                    ["key"] = documentKey ?? string.Empty,
                    ["title"] = documentTitle ?? string.Empty,
                    ["office_tools_available"] = officeToolsAvailable,
                    ["office_tool_policy"] = string.Equals(mode, ChatModes.Chat, StringComparison.Ordinal)
                        ? "Chat cannot call Office object-model or mutation tools. It may only use the read-only tools listed in this runtime context."
                        : officeToolsAvailable
                            ? "Office object-model tools may target this open document."
                            : "The chat document is closed or inactive. Do not call Office object-model tools until it is opened; continue with non-Office tools such as the HTML workspace when useful."
                },
                ["tools"] = BuildTools(tools),
                ["skills"] = BuildSkills(skills),
                ["user_context"] = BuildUserContext(context)
            };
            if (toolDiscovery != null)
            {
                root["tool_discovery"] = toolDiscovery.DeepClone();
            }
            var artifactBudget = Math.Max(
                192,
                Math.Min(600, ModelContextBudget.InputBudgetTokens(settings) / 20));
            var artifacts = ChatArtifactService.BuildPromptIndex(session, artifactBudget, settings);
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
                var descriptor = BuildTool(tool);
                if (descriptor != null) result.Add(descriptor);
            }
            return result;
        }

        internal static JObject BuildTool(ToolDefinition tool)
        {
            if (tool == null || string.IsNullOrWhiteSpace(tool.Id)) return null;
            JObject schema;
            string schemaError;
            if (!ToolSchemaSupport.TryParse(tool, out schema, out schemaError)) return null;
            return new JObject
            {
                ["type"] = "function",
                ["function"] = new JObject
                {
                    ["name"] = tool.Id,
                    ["description"] = BuildDescription(tool),
                    ["parameters"] = ToolSchemaSupport.ForPrompt(schema)
                },
                ["safety"] = new JObject
                {
                    ["mutates_document"] = tool.MutatesDocument,
                    ["mutates_local_state"] = tool.MutatesLocalState,
                    ["requires_confirmation"] = tool.RequiresConfirmation,
                    ["risk_level"] = tool.RiskLevel
                }
            };
        }

        internal static string BuildDescription(ToolDefinition tool)
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
            return new JArray((context == null ? new List<ContextNote>() : context.Notes ?? new List<ContextNote>())
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

        private static string ResolvePrompt(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value)
                ? (fallback ?? string.Empty).Trim()
                : value.Trim();
        }
    }
}
