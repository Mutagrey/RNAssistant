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

            foreach (var file in SafeGetFiles(_paths.SkillsDirectory, "SKILL.md"))
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
            SaveAll(skills);
        }

        public void Save(IEnumerable<SkillDefinition> skills, string host)
        {
            var incoming = new List<SkillDefinition>((skills ?? new SkillDefinition[0])
                .Where(s => s != null && !s.BuiltIn && !string.IsNullOrWhiteSpace(s.Id)));
            var keep = Load().Where(s =>
                !string.Equals(s.Host, "Common", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(s.Host, host, StringComparison.OrdinalIgnoreCase));

            SaveAll(keep.Concat(incoming));
        }

        public SkillDefinition SaveOne(SkillDefinition skill)
        {
            if (skill == null || string.IsNullOrWhiteSpace(skill.Id))
            {
                throw new ArgumentException("Skill id is required.", "skill");
            }

            var all = Load().Where(s => !string.Equals(s.Id, skill.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            skill.BuiltIn = false;
            all.Add(skill);
            SaveAll(all);
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
                TryDeleteDirectory(skill.StoragePath);
            }

            return found;
        }

        private void SaveAll(IEnumerable<SkillDefinition> skills)
        {
            if (Directory.Exists(_paths.SkillsDirectory))
            {
                Directory.Delete(_paths.SkillsDirectory, true);
            }

            Directory.CreateDirectory(_paths.SkillsDirectory);
            foreach (var skill in skills ?? new SkillDefinition[0])
            {
                if (skill == null || skill.BuiltIn || string.IsNullOrWhiteSpace(skill.Id))
                {
                    continue;
                }

                SaveSkill(skill);
            }
        }

        private void SaveSkill(SkillDefinition skill)
        {
            var directory = Path.Combine(_paths.SkillsDirectory, HostFolder(skill.Host), SafeSegment(skill.Id.ToLowerInvariant()));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "SKILL.md"), Serialize(skill), Encoding.UTF8);
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
                skill.Tags = ParseTags(Value(header, "tags"));
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
            builder.AppendLine("tags: " + string.Join(", ", (skill.Tags ?? new List<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).ToArray()));
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
            return SafeSegment(string.IsNullOrWhiteSpace(host) ? "common" : host.ToLowerInvariant());
        }

        private static string SafeSegment(string value)
        {
            var chars = (value ?? "skill").Select(c =>
                char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray();
            var result = new string(chars).Trim('_');
            return string.IsNullOrWhiteSpace(result) ? "skill" : result;
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                Directory.Delete(path, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static IEnumerable<string> SafeGetFiles(string directory, string pattern)
        {
            var files = new List<string>();
            AddFiles(directory, pattern, files);
            return files;
        }

        private static void AddFiles(string directory, string pattern, List<string> files)
        {
            string[] localFiles;
            string[] childDirectories;
            try
            {
                localFiles = Directory.GetFiles(directory, pattern);
                childDirectories = Directory.GetDirectories(directory);
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            files.AddRange(localFiles);
            foreach (var childDirectory in childDirectories)
            {
                AddFiles(childDirectory, pattern, files);
            }
        }
    }
}
