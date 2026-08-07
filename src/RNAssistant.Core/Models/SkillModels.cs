using System.Collections.Generic;

namespace RNAssistant.Core.Models
{
    public sealed class SkillDefinition
    {
        public string Id { get; set; }
        public string Host { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Version { get; set; }
        public List<string> Tags { get; set; }
        public List<string> AppliesTo { get; set; }
        public List<string> Requires { get; set; }
        public List<string> Conflicts { get; set; }
        public List<string> ToolCapabilities { get; set; }
        public List<string> Resources { get; set; }
        public string TrustLevel { get; set; }
        public string BodyMarkdown { get; set; }
        public string StoragePath { get; set; }
        public bool Enabled { get; set; }
        public bool BuiltIn { get; set; }

        public SkillDefinition()
        {
            Host = "Common";
            Tags = new List<string>();
            AppliesTo = new List<string>();
            Requires = new List<string>();
            Conflicts = new List<string>();
            ToolCapabilities = new List<string>();
            Resources = new List<string>();
            Version = "1.0.0";
            TrustLevel = "custom";
            BodyMarkdown = string.Empty;
            Enabled = true;
        }
    }
}
