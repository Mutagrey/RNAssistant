using System;
using System.Collections.Generic;

namespace RNAssistant.Core.Models
{
    public sealed class AppSettings
    {
        public string BaseUrl { get; set; }
        public string Model { get; set; }
        public string SystemPrompt { get; set; }
        public int MaxTokens { get; set; }
        public double Temperature { get; set; }
        public int ContextCharLimit { get; set; }
        public bool StreamResponses { get; set; }
        public Dictionary<string, string> CustomHeaders { get; set; }

        public AppSettings()
        {
            BaseUrl = "https://api.openai.com/v1";
            Model = "gpt-4o-mini";
            SystemPrompt = "You are an Office AI assistant. Use provided skills only through rnassistant-skill JSON blocks when document actions are required.";
            MaxTokens = 2048;
            Temperature = 0.2;
            ContextCharLimit = 24000;
            StreamResponses = false;
            CustomHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}

