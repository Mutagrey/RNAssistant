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
                "A JSON array is also accepted inside the fence. Each command must use a toolId copied exactly from the Available tools list and an arguments/args/parameters object.";
            ToolRoutingPrompt =
                "Never invent tool ids or use API-style aliases such as create_worksheet, addWorksheet, create_sheet, worksheet.create, or action names instead of exact tool ids.\n" +
                "After tool results are provided, either answer normally if the task is complete or return the next tool block.\n" +
                "If no available tool can satisfy the request, say exactly what is missing.\n" +
                "For Excel requests to visualize selected data inside the chat, prefer excel.create_chat_chart. Use excel.add_chart only when the user wants a chart inserted into the workbook.\n" +
                "Use common.render_html only when the user explicitly asks to render an HTML component/report inside chat and unsafe HTML artifacts are enabled. If the user asks for HTML code/source/markup/file, answer with code instead of calling this tool. If the request is ambiguous, ask a short clarification before using common.render_html.\n" +
                "After any document or VBA mutation, verify the result with read-only tools before the final answer. If verification shows a problem, correct it with another small tool step.\n" +
                "For VBA edits, prefer the host vba_apply_patch tool for structured small patches; use vba_replace_module only when replacing the whole module is necessary.\n" +
                "Use VBA only when built-in tools cannot solve the task cleanly, or when the user specifically asks for macros/VBA. For agent-created executable code, write VBA code for the current Office host.\n" +
                "When creating reusable automations, use common.tools_save for executable tools and common.skills_save for markdown guidance. Validate generated pipeline/VBA tool definitions before saving.";
            ForceToolUsePrompt = "You are in RNAssistant Agent mode. The user asked for an Office action, so a prose-only answer is not acceptable. Return only one ```rnassistant-agent fenced JSON block with executable steps. Copy toolId values exactly from the Available tools list. If a tool is missing, say that plainly instead of inventing one.";
            RepairMalformedToolBlockPrompt = "Your previous response contained an RNAssistant tool block, but the local parser could not recover executable JSON. Return only one corrected ```rnassistant-agent fenced JSON block with executable steps. Copy toolId values exactly from the Available tools list. No prose.";
            AfterToolResultsPrompt = "Local tool results above are available. If the task is complete, answer the user normally. If more Office/VBA actions are needed, return one rnassistant-agent block with only the next commands.";
            VerifyMutationPrompt = "Local tool results above include a document or VBA mutation. Before the final answer, verify the result with read-only tools such as get_context, workbook_summary, read_range, list_charts, or VBA read tools. If verification shows a problem, return the next corrective rnassistant-agent block; otherwise answer normally with what changed and what you verified.";
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
            SystemPrompt = "You are an Office AI assistant. Use provided tools only through rnassistant-agent JSON blocks when document actions are required.";
            AgentPrompt = "For Office actions, act as an agent: decompose the task into small executable steps, use available tools, return parseable rnassistant-agent JSON, and after tool results summarize what was done. Use VBA only for agent-created executable code or VBA-specific tasks.";
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
