using System;
using System.Security.Cryptography;
using System.Text;

namespace RNAssistant.Core.Models
{
    public static class SkillRevision
    {
        public static string Compute(SkillDefinition skill)
        {
            var body = skill == null ? string.Empty : skill.BodyMarkdown ?? string.Empty;
            body = body.Replace("\r\n", "\n").Replace('\r', '\n');
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(body)))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
            }
        }
    }

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
