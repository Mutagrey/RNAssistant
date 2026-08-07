using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    public sealed class SkillStore
    {
        private readonly AppDataPaths _paths;

        public SkillStore(AppDataPaths paths)
        {
            _paths = paths;
        }

        public List<SkillDefinition> Load()
        {
            var result = new List<SkillDefinition>();
            if (!Directory.Exists(_paths.SkillsDirectory))
            {
                return result;
            }

            foreach (var file in StorageFileSystem.GetFilesRecursive(_paths.SkillsDirectory, "SKILL.md"))
            {
                var skill = LoadSkill(file);
                if (skill == null || string.IsNullOrWhiteSpace(skill.Id))
                {
                    continue;
                }

                skill.BuiltIn = false;
                skill.StoragePath = Path.GetDirectoryName(file);
                result.Add(skill);
            }

            return result.OrderBy(s => s.Host).ThenBy(s => s.Id).ToList();
        }

        public void Save(IEnumerable<SkillDefinition> skills)
        {
            Reconcile(skills, null);
        }

        public void Save(IEnumerable<SkillDefinition> skills, string host)
        {
            var incoming = new List<SkillDefinition>((skills ?? new SkillDefinition[0])
                .Where(s => s != null && !s.BuiltIn && !string.IsNullOrWhiteSpace(s.Id)));
            Reconcile(incoming, host);
        }

        public SkillDefinition SaveOne(SkillDefinition skill)
        {
            if (skill == null || string.IsNullOrWhiteSpace(skill.Id))
            {
                throw new ArgumentException("Skill id is required.", "skill");
            }

            skill.BuiltIn = false;
            var targetDirectory = SkillDirectory(skill);
            var oldDirectories = Load()
                .Where(s => string.Equals(s.Id, skill.Id, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.StoragePath)
                .Where(path => !string.Equals(path, targetDirectory, StringComparison.OrdinalIgnoreCase))
                .ToList();
            SaveSkill(skill);
            foreach (var oldDirectory in oldDirectories)
            {
                StorageFileSystem.TryDeleteDirectory(oldDirectory);
            }
            return Load().FirstOrDefault(s => string.Equals(s.Id, skill.Id, StringComparison.OrdinalIgnoreCase));
        }

        public bool Delete(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            var found = false;
            foreach (var skill in Load())
            {
                if (!string.Equals(skill.Id, id, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(skill.StoragePath))
                {
                    continue;
                }

                found = true;
                StorageFileSystem.TryDeleteDirectory(skill.StoragePath);
            }

            return found;
        }

        private void Reconcile(IEnumerable<SkillDefinition> skills, string host)
        {
            var incoming = (skills ?? new SkillDefinition[0])
                .Where(s => s != null && !s.BuiltIn && !string.IsNullOrWhiteSpace(s.Id))
                .ToList();
            var incomingDirectories = new HashSet<string>(incoming.Select(SkillDirectory), StringComparer.OrdinalIgnoreCase);
            var existingSkills = Load();
            foreach (var skill in incoming)
            {
                SaveSkill(skill);
            }

            foreach (var existing in existingSkills)
            {
                var inScope = string.IsNullOrWhiteSpace(host) ||
                    string.Equals(existing.Host, host, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(existing.Host, "Common", StringComparison.OrdinalIgnoreCase);
                if (inScope && !incomingDirectories.Contains(existing.StoragePath ?? string.Empty))
                {
                    StorageFileSystem.TryDeleteDirectory(existing.StoragePath);
                }
            }
        }

        private void SaveSkill(SkillDefinition skill)
        {
            var directory = SkillDirectory(skill);
            Directory.CreateDirectory(directory);
            StorageFileSystem.WriteAllTextAtomic(Path.Combine(directory, "SKILL.md"), Serialize(skill), Encoding.UTF8);
        }

        private string SkillDirectory(SkillDefinition skill)
        {
            return Path.Combine(
                _paths.SkillsDirectory,
                HostFolder(skill == null ? null : skill.Host),
                StorageFileSystem.SafeSegment(skill == null || skill.Id == null ? "skill" : skill.Id.ToLowerInvariant(), "skill"));
        }

        private static SkillDefinition LoadSkill(string path)
        {
            try
            {
                return Parse(File.ReadAllLines(path), File.ReadAllText(path));
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static SkillDefinition Parse(string[] lines, string original)
        {
            var skill = new SkillDefinition();
            var bodyStart = 0;
            if (lines != null && lines.Length > 0 && string.Equals(lines[0].Trim(), "---", StringComparison.Ordinal))
            {
                var header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 1; i < lines.Length; i++)
                {
                    if (string.Equals(lines[i].Trim(), "---", StringComparison.Ordinal))
                    {
                        bodyStart = i + 1;
                        break;
                    }

                    var separator = lines[i].IndexOf(':');
                    if (separator <= 0)
                    {
                        continue;
                    }

                    header[lines[i].Substring(0, separator).Trim()] = lines[i].Substring(separator + 1).Trim();
                }

                skill.Id = Value(header, "id");
                skill.Host = FirstNonEmpty(Value(header, "host"), "Common");
                skill.Name = Value(header, "name");
                skill.Description = Value(header, "description");
                skill.Version = FirstNonEmpty(Value(header, "version"), "1.0.0");
                skill.Tags = ParseTags(Value(header, "tags"));
                skill.AppliesTo = ParseTags(Value(header, "appliesTo"));
                skill.Requires = ParseTags(Value(header, "requires"));
                skill.Conflicts = ParseTags(Value(header, "conflicts"));
                skill.ToolCapabilities = ParseTags(Value(header, "toolCapabilities"));
                skill.Resources = ParseTags(Value(header, "resources"));
                // Files under the user skill store are never trusted as built-ins,
                // regardless of self-declared frontmatter.
                skill.TrustLevel = "custom";
                bool enabled;
                skill.Enabled = !bool.TryParse(Value(header, "enabled"), out enabled) || enabled;
            }

            if (bodyStart <= 0)
            {
                skill.BodyMarkdown = original ?? string.Empty;
            }
            else
            {
                skill.BodyMarkdown = string.Join(Environment.NewLine, lines.Skip(bodyStart).ToArray());
            }

            return string.IsNullOrWhiteSpace(skill.Id) ? null : skill;
        }

        private static string Serialize(SkillDefinition skill)
        {
            var builder = new StringBuilder();
            builder.AppendLine("---");
            builder.AppendLine("id: " + (skill.Id ?? string.Empty));
            builder.AppendLine("host: " + FirstNonEmpty(skill.Host, "Common"));
            builder.AppendLine("name: " + FirstNonEmpty(skill.Name, skill.Id));
            builder.AppendLine("description: " + (skill.Description ?? string.Empty).Replace("\r", " ").Replace("\n", " "));
            builder.AppendLine("version: " + FirstNonEmpty(skill.Version, "1.0.0"));
            builder.AppendLine("tags: " + string.Join(", ", (skill.Tags ?? new List<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).ToArray()));
            builder.AppendLine("appliesTo: " + Join(skill.AppliesTo));
            builder.AppendLine("requires: " + Join(skill.Requires));
            builder.AppendLine("conflicts: " + Join(skill.Conflicts));
            builder.AppendLine("toolCapabilities: " + Join(skill.ToolCapabilities));
            builder.AppendLine("resources: " + Join(skill.Resources));
            builder.AppendLine("trustLevel: " + (skill.BuiltIn ? "built_in" : "custom"));
            builder.AppendLine("enabled: " + (skill.Enabled != false ? "true" : "false"));
            builder.AppendLine("---");
            builder.AppendLine();
            builder.Append(skill.BodyMarkdown ?? string.Empty);
            return builder.ToString();
        }

        private static List<string> ParseTags(string value)
        {
            return (value ?? string.Empty)
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string Join(IEnumerable<string> values)
        {
            return string.Join(", ", (values ?? new string[0]).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray());
        }

        private static string Value(IDictionary<string, string> values, string key)
        {
            string value;
            return values != null && values.TryGetValue(key, out value) ? value : string.Empty;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values ?? new string[0])
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return string.Empty;
        }

        private static string HostFolder(string host)
        {
            return StorageFileSystem.SafeSegment(
                string.IsNullOrWhiteSpace(host) ? "common" : host.ToLowerInvariant(),
                "common");
        }
    }
}
