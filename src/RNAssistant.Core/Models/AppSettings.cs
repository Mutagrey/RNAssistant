using System;
using System.Collections.Generic;

namespace RNAssistant.Core.Models
{
    public sealed class AgentPromptSettings
    {
        public string ToolProtocolPrompt { get; set; }
        public string ToolRoutingPrompt { get; set; }
        public string ForceToolUsePrompt { get; set; }
        public string RepairMalformedToolBlockPrompt { get; set; }
        public string AfterToolResultsPrompt { get; set; }
        public string VerifyMutationPrompt { get; set; }
        public string ConfirmedToolContinuationPrompt { get; set; }
        public string RetryFailedToolPrompt { get; set; }

        public AgentPromptSettings()
        {
            ToolProtocolPrompt =
                "Return exactly one JSON object. No markdown. No code fences. No prose outside JSON.\n" +
                "Allowed shape: {\"kind\":\"tool_plan|final|clarify|cannot_do\",\"intent\":\"read|analyze|mutate|verify|answer|clarify\",\"message\":\"string|null\",\"steps\":[{\"toolId\":\"exact tool id from AVAILABLE_TOOLS\",\"arguments\":{},\"reason\":\"short reason\"}],\"expectedOutcome\":\"string|null\"}.";
            ToolRoutingPrompt =
                "Never invent tool ids or use API-style aliases such as create_worksheet, addWorksheet, create_sheet, worksheet.create, or action names.\n" +
                "Inspect unknown targets before mutation. Use read-only tools first when sheet, range, slide, selection, mail, or document location is unclear.\n" +
                "After tool results, return kind=final if complete; otherwise return the next tool_plan.\n" +
                "If no available tool can satisfy the request, say exactly what is missing.\n" +
                "For Excel chart-in-chat requests, prefer excel.create_chat_chart. Use excel.add_chart only to insert a chart into the workbook.\n" +
                "For HTML UI/report/page requests, use common.html_workspace_upsert_file with kind html, css, or script, and common.html_workspace_upsert_data so the HTML tab can edit and preview the result. Build default HTML workspace pages as full-page layouts that use the available preview viewport: body margin 0, no narrow centered card wrapper unless the user asks for a card, and responsive sections that fill the page width. For changes to an existing HTML page, read common.html_workspace_read first, then update only the needed file or data source. Do not create inline chat HTML artifacts for pages that should stay editable.\n" +
                "After any document or VBA mutation, the runtime runs deterministic read-only verification before the final answer.\n" +
                "For VBA edits, prefer the host vba_apply_patch tool for small patches; use vba_replace_module only for whole-module replacement.\n" +
                "Use VBA only when built-in tools cannot solve the task cleanly or when the user specifically asks for macros/VBA.\n" +
                "For reusable executable tools, use common.tools_validate before common.tools_save. Use common.skills_save only for markdown guidance. Use common.prompts_read_defaults before common.prompts_save.";
            ForceToolUsePrompt = "This task requires Office tool use before a final answer. Return kind=tool_plan with an available read/context tool, or kind=cannot_do if no available tool can satisfy it.";
            RepairMalformedToolBlockPrompt = "Your previous RNAssistant planner output was invalid. Return exactly one JSON object. No markdown. No prose.";
            AfterToolResultsPrompt = "Local normalized observations are available. If the task is complete, return kind=final. If more Office actions are needed, return the next tool_plan.";
            VerifyMutationPrompt = "Local deterministic verification observations are available. If verification succeeded, return kind=final with what changed and what was verified. If verification failed, return a corrective tool_plan or cannot_do.";
            ConfirmedToolContinuationPrompt = "The user confirmed and RNAssistant executed the pending local tool. Continue the same task from normalized observations.";
            RetryFailedToolPrompt =
                "A local tool call failed. Return exactly one corrected planner JSON object, no markdown and no prose.\n" +
                "Use only these exact available tool ids: {{availableToolIds}}\n" +
                "Original command: `{{toolId}}` with arguments:\n" +
                "```json\n" +
                "{{argumentsJson}}\n" +
                "```\n" +
                "Error: {{error}}\n" +
                "{{dataJsonBlock}}";
        }
    }

    public sealed class AppSettings
    {
        public string BaseUrl { get; set; }
        public string Model { get; set; }
        public string SystemPrompt { get; set; }
        public string AgentPrompt { get; set; }
        public int MaxTokens { get; set; }
        public int RequestTimeoutSeconds { get; set; }
        public double Temperature { get; set; }
        public double TopP { get; set; }
        public int ContextCharLimit { get; set; }
        public bool StreamResponses { get; set; }
        public bool? AutoRunToolCalls { get; set; }
        public bool AutoConfirmToolActions { get; set; }
        public bool? AutoRetryToolErrors { get; set; }
        public bool? SmartChatTitles { get; set; }
        public bool IncludeVbaContext { get; set; }
        public int VbaContextCharLimit { get; set; }
        public int MaxAgentIterations { get; set; }
        public int MaxAgentToolSteps { get; set; }
        public bool? RequireVerificationForMutations { get; set; }
        public bool? AutoContinueAfterConfirmation { get; set; }
        public bool AllowUnsafeHtmlArtifacts { get; set; }
        public AgentPromptSettings AgentPrompts { get; set; }
        public double UiFontScale { get; set; }
        public Dictionary<string, string> CustomHeaders { get; set; }

        public AppSettings()
        {
            BaseUrl = "https://api.openai.com";
            Model = "gpt-4o-mini";
            SystemPrompt = "You are an Office AI assistant. Always return the RNAssistant strict JSON planner envelope.";
            AgentPrompt = "For Office actions, make small executable steps, use exact available tool ids, return strict planner JSON, and summarize only in final.message after observations. Use VBA only for macros or when built-in tools are not enough.";
            MaxTokens = 2048;
            RequestTimeoutSeconds = 300;
            Temperature = 0.2;
            TopP = 1.0;
            ContextCharLimit = 24000;
            StreamResponses = false;
            AutoRunToolCalls = true;
            AutoConfirmToolActions = false;
            AutoRetryToolErrors = true;
            SmartChatTitles = true;
            IncludeVbaContext = false;
            VbaContextCharLimit = 30000;
            MaxAgentIterations = 8;
            MaxAgentToolSteps = 40;
            RequireVerificationForMutations = true;
            AutoContinueAfterConfirmation = true;
            AllowUnsafeHtmlArtifacts = false;
            AgentPrompts = new AgentPromptSettings();
            UiFontScale = 1.0;
            CustomHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
