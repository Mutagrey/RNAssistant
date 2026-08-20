namespace RNAssistant.Core.Models
{
    public sealed class SkillDefinition
    {
        public string Id { get; set; }
        public string Host { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Version { get; set; }
        public string BodyMarkdown { get; set; }
        public string StoragePath { get; set; }
        public bool Enabled { get; set; }
        public bool BuiltIn { get; set; }

        public SkillDefinition()
        {
            Host = "Common";
            Version = "1.0.0";
            BodyMarkdown = string.Empty;
            Enabled = true;
        }
    }
}
