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
                "Required tool response format:\n" +
                "```rnassistant-agent\n" +
                "{\"description\":\"short plan\",\"steps\":[{\"description\":\"step name\",\"toolId\":\"tool.id\",\"arguments\":{\"name\":\"value\"}}]}\n" +
                "```\n" +
                "Return exactly one fenced rnassistant-agent block for Office actions. Each step must use a toolId copied exactly from Available tools and an arguments object. Omit unused arguments or pass an empty string; never write placeholder words for missing values.";
            ToolRoutingPrompt =
                "Never invent tool ids or use API-style aliases such as create_worksheet, addWorksheet, create_sheet, worksheet.create, or action names.\n" +
                "Inspect unknown targets before mutation. Use read-only tools first when sheet, range, slide, selection, mail, or document location is unclear.\n" +
                "After tool results, answer normally if complete; otherwise return the next single tool block.\n" +
                "If no available tool can satisfy the request, say exactly what is missing.\n" +
                "For Excel chart-in-chat requests, prefer excel.create_chat_chart. Use excel.add_chart only to insert a chart into the workbook.\n" +
                "For HTML UI/report/page requests, use common.html_workspace_upsert_file with kind html, css, or script, and common.html_workspace_upsert_data so the HTML tab can edit and preview the result. Build default HTML workspace pages as full-page layouts that use the available preview viewport: body margin 0, no narrow centered card wrapper unless the user asks for a card, and responsive sections that fill the page width. For changes to an existing HTML page, read common.html_workspace_read first, then update only the needed file or data source. Do not create inline chat HTML artifacts for pages that should stay editable.\n" +
                "After any document or VBA mutation, verify with read-only tools before the final answer. If verification shows a problem, correct it with another small tool step.\n" +
                "For VBA edits, prefer the host vba_apply_patch tool for small patches; use vba_replace_module only for whole-module replacement.\n" +
                "Use VBA only when built-in tools cannot solve the task cleanly or when the user specifically asks for macros/VBA.\n" +
                "For reusable executable tools, use common.tools_validate before common.tools_save. Use common.skills_save only for markdown guidance. Use common.prompts_read_defaults before common.prompts_save.";
            ForceToolUsePrompt = "You are in RNAssistant Agent mode. The user asked for an Office action, so a prose-only answer is not acceptable. Return only one ```rnassistant-agent fenced JSON block with executable steps. Copy toolId values exactly from Available tools. If a tool is missing, say that plainly instead of inventing one.";
            RepairMalformedToolBlockPrompt = "Your previous response contained an RNAssistant tool block, but the local parser could not recover executable JSON. Return only one corrected ```rnassistant-agent fenced JSON block with executable steps. Copy toolId values exactly from the Available tools list. No prose.";
            AfterToolResultsPrompt = "Local tool results above are available. If the task is complete, answer the user normally. If more Office/VBA actions are needed, return one rnassistant-agent block with only the next commands.";
            VerifyMutationPrompt = "Local tool results above include a document or VBA mutation. Before the final answer, verify the result with read-only tools such as get_context, workbook_summary, read_range, list_charts, read_document, read_slides, or VBA read tools. If verification shows a problem, return the next corrective rnassistant-agent block; otherwise answer normally with what changed and what you verified.";
            ConfirmedToolContinuationPrompt = "The user confirmed and RNAssistant executed the pending local tool. Continue the same Office task from the stored local result. If a document or VBA mutation happened, verify it with read-only tools before the final answer.";
            RetryFailedToolPrompt =
                "A local tool call failed. Return only corrected rnassistant-agent JSON block(s), no prose.\n" +
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
        public bool? AgentModeEnabled { get; set; }
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
            SystemPrompt = "You are an Office AI assistant. Use local tools only through rnassistant-agent JSON blocks when Office actions are required.";
            AgentPrompt = "For Office actions, make small executable steps, use exact available tool ids, return parseable rnassistant-agent JSON, and summarize after tool results. Use VBA only for macros or when built-in tools are not enough.";
            MaxTokens = 2048;
            RequestTimeoutSeconds = 300;
            Temperature = 0.2;
            TopP = 1.0;
            ContextCharLimit = 24000;
            StreamResponses = false;
            AgentModeEnabled = true;
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
