using System;
using System.Collections.Generic;

namespace RNAssistant.Core.Models
{
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
        public bool IncludeVbaContext { get; set; }
        public int VbaContextCharLimit { get; set; }
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
            IncludeVbaContext = false;
            VbaContextCharLimit = 30000;
            CustomHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
