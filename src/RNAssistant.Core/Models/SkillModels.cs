using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace RNAssistant.Core.Models
{
    public static class SkillRevision
    {
        public const int CurrentContractVersion = 1;

        public static string Compute(SkillDefinition skill)
        {
            var canonical = new StringBuilder();
            Append(canonical, "contractVersion",
                CurrentContractVersion.ToString());
            Append(canonical, "id", skill == null ? null : skill.Id);
            Append(canonical, "host", skill == null ? null : skill.Host);
            Append(canonical, "name", skill == null ? null : skill.Name);
            Append(canonical, "description",
                skill == null ? null : skill.Description);
            Append(canonical, "version", skill == null ? null : skill.Version);
            Append(canonical, "enabled",
                skill != null && skill.Enabled ? "true" : "false");
            Append(canonical, "bodyMarkdown", NormalizeMarkdown(
                skill == null ? null : skill.BodyMarkdown));
            var references = (skill == null ? null : skill.References) ?? new List<SkillReferenceMetadata>();
            foreach (var reference in references
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Path))
                .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Path, StringComparer.Ordinal))
            {
                Append(canonical, "referencePath",
                    (reference.Path ?? string.Empty).Replace('\\', '/')
                        .ToLowerInvariant());
                Append(canonical, "referenceRevision",
                    (reference.Revision ?? string.Empty).ToLowerInvariant());
            }
            return ComputeMarkdown(canonical.ToString());
        }

        public static string ComputeMarkdown(string markdown)
        {
            var body = NormalizeMarkdown(markdown);
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(body)))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
            }
        }

        private static string NormalizeMarkdown(string markdown)
        {
            return (markdown ?? string.Empty)
                .Replace("\r\n", "\n").Replace('\r', '\n');
        }

        private static void Append(
            StringBuilder target, string name, string value)
        {
            value = value ?? string.Empty;
            target.Append(name);
            target.Append(':');
            target.Append(value.Length);
            target.Append(':');
            target.Append(value);
            target.Append('\n');
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
