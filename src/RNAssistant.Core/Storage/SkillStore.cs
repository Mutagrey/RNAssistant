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
        private const long MaxSkillFileBytes = 2100000;
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
                if (skill == null || !string.IsNullOrWhiteSpace(ValidateDefinition(skill)))
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
                .Where(s => s != null && !s.BuiltIn));
            Reconcile(incoming, host);
        }

        public SkillDefinition SaveOne(SkillDefinition skill)
        {
            var validationError = ValidateDefinition(skill);
            if (!string.IsNullOrWhiteSpace(validationError))
            {
                throw new ArgumentException(validationError, "skill");
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
                .Where(s => s != null && !s.BuiltIn)
                .ToList();
            foreach (var skill in incoming)
            {
                var validationError = ValidateDefinition(skill);
                if (!string.IsNullOrWhiteSpace(validationError)) throw new ArgumentException(validationError, "skills");
            }
            var duplicate = incoming.GroupBy(skill => skill.Id, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate != null) throw new ArgumentException("Duplicate skill id: " + duplicate.Key, "skills");
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
                SkillFolder(skill == null ? null : skill.Id));
        }

        private static SkillDefinition LoadSkill(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > MaxSkillFileBytes) return null;
                var text = File.ReadAllText(path);
                return Parse(text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'), text);
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
            builder.AppendLine("id: " + FrontMatterScalar(skill.Id));
            builder.AppendLine("host: " + FrontMatterScalar(FirstNonEmpty(skill.Host, "Common")));
            builder.AppendLine("name: " + FrontMatterScalar(FirstNonEmpty(skill.Name, skill.Id)));
            builder.AppendLine("description: " + FrontMatterScalar(skill.Description));
            builder.AppendLine("version: " + FrontMatterScalar(FirstNonEmpty(skill.Version, "1.0.0")));
            builder.AppendLine("enabled: " + (skill.Enabled != false ? "true" : "false"));
            builder.AppendLine("---");
            builder.AppendLine();
            builder.Append(skill.BodyMarkdown ?? string.Empty);
            return builder.ToString();
        }

        public static string ValidateDefinition(SkillDefinition skill)
        {
            if (skill == null || string.IsNullOrWhiteSpace(skill.Id)) return "Skill id is required.";
            var id = skill.Id.Trim();
            if (!string.Equals(skill.Id, id, StringComparison.Ordinal))
            {
                return "Skill id cannot have leading or trailing whitespace.";
            }
            if (id.Length > 128 || HasLineBreak(id))
            {
                return "Skill id must be 1-128 characters without line breaks.";
            }
            if ((skill.Name ?? string.Empty).Length > 200) return "Skill name is too long.";
            if ((skill.Description ?? string.Empty).Length > 4000) return "Skill description is too long.";
            if ((skill.Version ?? string.Empty).Length > 64) return "Skill version is too long.";
            if ((skill.BodyMarkdown ?? string.Empty).Length > 500000) return "Skill bodyMarkdown is too large.";
            var skillHost = FirstNonEmpty(skill.Host, "Common");
            if (!new[] { "Common", "Excel", "Word", "PowerPoint", "Outlook" }
                .Any(host => string.Equals(host, skillHost, StringComparison.OrdinalIgnoreCase)))
            {
                return "Unsupported skill host: " + (skill.Host ?? string.Empty) + ".";
            }
            if (HasLineBreak(skill.Host) || HasLineBreak(skill.Name) || HasLineBreak(skill.Version))
            {
                return "Skill front-matter fields cannot contain line breaks.";
            }
            return null;
        }

        private static bool HasLineBreak(string value)
        {
            return !string.IsNullOrEmpty(value) && (value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0);
        }

        private static string FrontMatterScalar(string value)
        {
            return (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        }

        private static string SkillFolder(string id)
        {
            var normalized = string.IsNullOrWhiteSpace(id) ? "skill" : id.Trim().ToLowerInvariant();
            var readable = StorageFileSystem.SafeSegment(normalized, "skill");
            if (readable.Length > 40) readable = readable.Substring(0, 40).TrimEnd('_');
            return readable + "_" + AppDataPaths.SafeFileName(normalized).Substring(0, 16);
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
