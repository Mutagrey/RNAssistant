using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace RNAssistant.Core.Models
{
    public static class SkillRevision
    {
        public static string Compute(SkillDefinition skill)
        {
            var body = skill == null ? string.Empty : skill.BodyMarkdown ?? string.Empty;
            var bodyRevision = ComputeMarkdown(body);
            var references = (skill == null ? null : skill.References) ?? new List<SkillReferenceMetadata>();
            if (references.Count == 0) return bodyRevision;

            var canonical = new StringBuilder(bodyRevision);
            foreach (var reference in references
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Path))
                .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Path, StringComparer.Ordinal))
            {
                canonical.Append('\n');
                canonical.Append((reference.Path ?? string.Empty).Replace('\\', '/').ToLowerInvariant());
                canonical.Append('\0');
                canonical.Append(reference.Revision ?? string.Empty);
            }
            return ComputeMarkdown(canonical.ToString());
        }

        public static string ComputeMarkdown(string markdown)
        {
            var body = (markdown ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(body)))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
            }
        }
    }

    public sealed class SkillReferenceMetadata
    {
        public string Path { get; set; }
        public long ByteLength { get; set; }
        public string Revision { get; set; }
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
        public List<SkillReferenceMetadata> References { get; set; }

        public SkillDefinition()
        {
            Host = "Common";
            Version = "1.0.0";
            BodyMarkdown = string.Empty;
            Enabled = true;
            References = new List<SkillReferenceMetadata>();
        }
    }
}
