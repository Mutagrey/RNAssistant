using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    internal sealed class ConversationPromptComposer
    {
        public List<ChatMessage> BuildRequiredMessages(
            string mode,
            string userText,
            IOfficeApplicationAdapter adapter,
            IReadOnlyList<ToolCatalogEntry> tools,
            IReadOnlyList<SkillDefinition> skills,
            DocumentContext context,
            AppSettings settings,
            ChatSession session,
            IReadOnlyList<ChatAttachment> attachments,
            bool replayCurrentUserInHistory = false,
            int historyBudgetTokens = 0,
            JObject capabilityCatalog = null)
        {
            settings = settings ?? new AppSettings();
            mode = ChatModes.Normalize(mode);
            var instruction = BuildInstruction(mode, settings);
            var runtimeContext = BuildRuntimeContext(
                mode, adapter, tools, skills, context, session, settings, capabilityCatalog);
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
            messages.Add(current);
            return messages;
        }

        internal static string BuildInstruction(string mode, AppSettings settings)
        {
            mode = ChatModes.Normalize(mode);
            if (string.Equals(mode, ChatModes.Chat, StringComparison.Ordinal)) return ResolveChatPrompt(settings);
            if (string.Equals(mode, ChatModes.Plan, StringComparison.Ordinal)) return BuildPlanInstruction(settings);
            return BuildAgentInstruction(settings);
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

        internal static string BuildPlanInstruction(AppSettings settings)
        {
            return string.Join("\n\n", new[]
            {
                ResolvePlanPrompt(settings),
                ResolveToolPrompt(settings),
                ResolveSkillPrompt(settings)
            }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray());
        }

        internal static string ResolveChatPrompt(AppSettings settings)
        {
            return ResolvePrompt(settings == null ? null : settings.ChatSystemPrompt, AgentPromptDefaults.ChatInstructions);
        }

        internal static string ResolvePlanPrompt(AppSettings settings)
        {
            return ResolvePrompt(settings == null ? null : settings.PlanSystemPrompt, AgentPromptDefaults.PlanInstructions);
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
            IReadOnlyList<ToolCatalogEntry> tools,
            IReadOnlyList<SkillDefinition> skills,
            DocumentContext context,
            ChatSession session,
            AppSettings settings = null,
            JObject capabilityCatalog = null)
        {
            mode = ChatModes.Normalize(mode);
            var adapterHost = session == null ? SafeAdapterValue(adapter, item => item.HostName) : session.Host;
            var adapterDocumentTitle = session == null ? SafeAdapterValue(adapter, item => item.DocumentTitle) : session.DocumentTitle;
            var host = session != null && !string.IsNullOrWhiteSpace(session.Host)
                ? session.Host
                : adapterHost;
            var documentTitle = session != null && !string.IsNullOrWhiteSpace(session.DocumentTitle)
                ? session.DocumentTitle
                : adapterDocumentTitle;
            var officeToolsAvailable = adapter == null ? session != null && session.LastRun != null &&
                !string.IsNullOrWhiteSpace(session.LastRun.DocumentRuntimeKey) : session == null ||
                OfficeDocumentExecutionGuardState.SessionMatchesAdapter(adapter, session);
            var document = new JObject
            {
                ["title"] = documentTitle ?? string.Empty,
                ["office_tools_available"] = officeToolsAvailable,
                ["office_tool_policy"] = string.Equals(mode, ChatModes.Chat, StringComparison.Ordinal)
                    ? "Chat cannot call Office object-model or mutation tools. It may only use the read-only tools listed in this runtime context."
                    : officeToolsAvailable
                        ? "Office object-model tools may target this open document."
                        : "The chat document is closed or inactive. Do not call Office object-model tools until it is opened; continue with non-Office tools such as the HTML workspace when useful."
            };
            if (officeToolsAvailable && VbaResourceProvider.SupportsHost(adapterHost))
            {
                document["vba_project_target"] =
                    VbaResourceProvider.ProjectSemanticTarget(adapterDocumentTitle);
            }
            var root = new JObject
            {
                ["mode"] = mode,
                ["host"] = host ?? string.Empty,
                ["document"] = document,
                ["tools"] = BuildTools(tools),
                ["capabilities"] = !string.Equals(mode, ChatModes.Chat, StringComparison.Ordinal)
                    ? (JToken)(capabilityCatalog ?? CapabilityCatalogService.BuildPromptCatalog(tools, skills, tools))
                    : new JObject
                    {
                        ["items"] = new JArray(),
                        ["shown"] = 0,
                        ["total"] = 0,
                        ["truncated"] = false
                    },
                ["context_policy"] = "User-selected document observations are compiled separately against current resource authority."
            };
            var artifactBudget = Math.Max(
                192,
                Math.Min(600, ModelContextBudget.InputBudgetTokens(settings) / 20));
            var artifacts = ChatResourcePromptIndex.Build(session, artifactBudget, settings);
            if (!string.IsNullOrWhiteSpace(artifacts)) root["artifacts"] = artifacts;
            var activePlan = BuildActivePlan(session);
            if (activePlan != null) root["active_plan"] = activePlan;
            return root.ToString(Formatting.None);
        }

        private static JObject BuildActivePlan(ChatSession session)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.ActivePlanDocumentArtifactId)) return null;
            var matches = (session.Artifacts ?? new List<ChatArtifact>()).Where(item => item != null &&
                string.Equals(item.Id, session.ActivePlanDocumentArtifactId, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToList();
            if (matches.Count != 1 || !string.Equals(
                matches[0].Kind,
                ChatArtifactKinds.PlanDocument,
                StringComparison.OrdinalIgnoreCase)) return null;
            var artifact = matches[0];
            var status = "draft";
            var planId = string.Empty;
            try
            {
                var metadata = JObject.Parse(artifact.MetadataJson ?? "{}");
                status = (string)metadata["status"] ?? status;
                planId = (string)metadata["planId"] ?? string.Empty;
            }
            catch (JsonException)
            {
            }
            return new JObject
            {
                ["id"] = planId,
                ["status"] = status,
                ["title"] = artifact.Title ?? string.Empty
            };
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

        internal static JArray BuildTools(IEnumerable<ToolCatalogEntry> tools)
        {
            var result = new JArray();
            foreach (var tool in tools ?? new ToolCatalogEntry[0])
            {
                var descriptor = BuildTool(tool);
                if (descriptor != null) result.Add(descriptor);
            }
            return result;
        }

        internal static JObject BuildTool(ToolCatalogEntry tool)
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

        internal static string BuildDescription(ToolCatalogEntry tool)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(tool.Description)) parts.Add(tool.Description.Trim());
            if (!string.IsNullOrWhiteSpace(tool.UseWhen)) parts.Add("Use when: " + tool.UseWhen.Trim());
            if (!string.IsNullOrWhiteSpace(tool.DoNotUseWhen)) parts.Add("Do not use when: " + tool.DoNotUseWhen.Trim());
            if (!string.IsNullOrWhiteSpace(tool.Limitations)) parts.Add("Limitations: " + tool.Limitations.Trim());
            return string.Join(" ", parts.ToArray());
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
