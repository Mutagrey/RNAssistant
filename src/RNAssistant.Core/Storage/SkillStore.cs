using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    public sealed class SkillStore
    {
        public const int MaximumSkillReferenceCharacters = 500000;
        public const long MaximumSkillReferenceBytes = 2100000;
        public const int MaximumSkillReferences = 64;
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
                skill.References = LoadReferences(skill.StoragePath);
                if (skill.References == null) continue;
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

        public bool TryReadReference(
            SkillDefinition skill,
            string referencePath,
            out string content,
            out SkillReferenceMetadata metadata,
            out string error)
        {
            content = null;
            metadata = null;
            error = null;
            if (skill == null || string.IsNullOrWhiteSpace(skill.StoragePath))
            {
                error = "Skill has no readable references.";
                return false;
            }
            if (!IsRegularSkillPackage(skill.StoragePath))
            {
                error = "Skill package path is unavailable or unsafe.";
                return false;
            }

            string normalizedPath;
            if (!TryNormalizeReferencePath(referencePath, out normalizedPath))
            {
                error = "Reference path must be one UTF-8 Markdown file directly under references/.";
                return false;
            }

            var references = skill.References ?? LoadReferences(skill.StoragePath);
            if (references == null)
            {
                error = "Skill references are invalid or unreadable.";
                return false;
            }
            var expected = references.FirstOrDefault(item => item != null &&
                string.Equals(item.Path, normalizedPath, StringComparison.OrdinalIgnoreCase));
            if (expected == null)
            {
                error = "Skill reference not found: " + normalizedPath;
                return false;
            }

            normalizedPath = expected.Path;
            var referenceDirectory = Path.Combine(skill.StoragePath, "references");
            var path = Path.Combine(referenceDirectory, Path.GetFileName(normalizedPath));
            try
            {
                if (!Directory.Exists(referenceDirectory) ||
                    (File.GetAttributes(referenceDirectory) & FileAttributes.ReparsePoint) != 0)
                {
                    error = "Skill references directory is unavailable.";
                    return false;
                }
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > MaximumSkillReferenceBytes ||
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    error = "Skill reference is unavailable: " + normalizedPath;
                    return false;
                }

                var bytes = File.ReadAllBytes(path);
                var revision = Sha256(bytes);
                if (!string.Equals(revision, expected.Revision, StringComparison.OrdinalIgnoreCase))
                {
                    error = "Skill reference changed after the runtime catalog was built: " + normalizedPath;
                    return false;
                }

                var start = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
                content = new UTF8Encoding(false, true).GetString(bytes, start, bytes.Length - start);
                metadata = new SkillReferenceMetadata
                {
                    Path = expected.Path,
                    ByteLength = bytes.LongLength,
                    Revision = revision
                };
                return true;
            }
            catch (DecoderFallbackException)
            {
                error = "Skill reference must be valid UTF-8 Markdown: " + normalizedPath;
                return false;
            }
            catch (IOException)
            {
                error = "Skill reference could not be read: " + normalizedPath;
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                error = "Skill reference could not be read: " + normalizedPath;
                return false;
            }
            catch (SecurityException)
            {
                error = "Skill reference could not be read: " + normalizedPath;
                return false;
            }
        }

        public bool TrySaveReference(
            SkillDefinition skill,
            string referencePath,
            string content,
            out SkillReferenceMetadata metadata,
            out string error)
        {
            metadata = null;
            error = null;
            if (skill == null || string.IsNullOrWhiteSpace(skill.StoragePath))
            {
                error = "Custom skill not found.";
                return false;
            }
            if (!IsRegularSkillPackage(skill.StoragePath))
            {
                error = "Skill package path is unavailable or unsafe.";
                return false;
            }

            string normalizedPath;
            if (!TryNormalizeReferencePath(referencePath, out normalizedPath))
            {
                error = "Reference path must be one UTF-8 Markdown file directly under references/.";
                return false;
            }
            var value = content ?? string.Empty;
            if (value.Length > MaximumSkillReferenceCharacters || Encoding.UTF8.GetByteCount(value) > MaximumSkillReferenceBytes)
            {
                error = "Skill reference is too large.";
                return false;
            }

            var current = LoadReferences(skill.StoragePath);
            if (current == null)
            {
                error = "Existing skill references are invalid or unreadable.";
                return false;
            }
            var existing = current.FirstOrDefault(item => item != null &&
                string.Equals(item.Path, normalizedPath, StringComparison.OrdinalIgnoreCase));
            if (existing == null && current.Count >= MaximumSkillReferences)
            {
                error = "Skill reference limit reached: " + MaximumSkillReferences + ".";
                return false;
            }
            if (existing != null) normalizedPath = existing.Path;

            var directory = Path.Combine(skill.StoragePath, "references");
            var path = Path.Combine(directory, Path.GetFileName(normalizedPath));
            try
            {
                if (Directory.Exists(directory) &&
                    (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                {
                    error = "Skill references directory cannot be a symbolic link.";
                    return false;
                }
                StorageFileSystem.EnsureRegularDirectory(directory);
                if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    error = "Skill reference cannot be a symbolic link: " + normalizedPath;
                    return false;
                }

                StorageFileSystem.WriteAllTextAtomic(path, value, new UTF8Encoding(false));
                var refreshed = LoadReferences(skill.StoragePath);
                metadata = refreshed == null ? null : refreshed.FirstOrDefault(item => item != null &&
                    string.Equals(item.Path, normalizedPath, StringComparison.OrdinalIgnoreCase));
                if (metadata == null)
                {
                    error = "Skill reference was written but could not be verified: " + normalizedPath;
                    return false;
                }
                return true;
            }
            catch (IOException)
            {
                error = "Skill reference could not be saved: " + normalizedPath;
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                error = "Skill reference could not be saved: " + normalizedPath;
                return false;
            }
            catch (SecurityException)
            {
                error = "Skill reference could not be saved: " + normalizedPath;
                return false;
            }
        }

        public bool TryDeleteReference(SkillDefinition skill, string referencePath, out string error)
        {
            error = null;
            if (skill == null || string.IsNullOrWhiteSpace(skill.StoragePath))
            {
                error = "Custom skill not found.";
                return false;
            }
            if (!IsRegularSkillPackage(skill.StoragePath))
            {
                error = "Skill package path is unavailable or unsafe.";
                return false;
            }

            string normalizedPath;
            if (!TryNormalizeReferencePath(referencePath, out normalizedPath))
            {
                error = "Reference path must be one UTF-8 Markdown file directly under references/.";
                return false;
            }
            var current = LoadReferences(skill.StoragePath);
            if (current == null)
            {
                error = "Existing skill references are invalid or unreadable.";
                return false;
            }
            var existing = current.FirstOrDefault(item => item != null &&
                string.Equals(item.Path, normalizedPath, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                error = "Skill reference not found: " + normalizedPath;
                return false;
            }
            normalizedPath = existing.Path;

            var referenceDirectory = Path.Combine(skill.StoragePath, "references");
            var path = Path.Combine(referenceDirectory, Path.GetFileName(normalizedPath));
            try
            {
                if (!Directory.Exists(referenceDirectory) ||
                    (File.GetAttributes(referenceDirectory) & FileAttributes.ReparsePoint) != 0 ||
                    !File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    error = "Skill reference is unavailable: " + normalizedPath;
                    return false;
                }
                File.Delete(path);
                return true;
            }
            catch (IOException)
            {
                error = "Skill reference could not be deleted: " + normalizedPath;
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                error = "Skill reference could not be deleted: " + normalizedPath;
                return false;
            }
            catch (SecurityException)
            {
                error = "Skill reference could not be deleted: " + normalizedPath;
                return false;
            }
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
            StorageFileSystem.EnsureRegularDirectory(_paths.SkillsDirectory);
            StorageFileSystem.EnsureRegularDirectory(Path.GetDirectoryName(directory));
            if (Directory.Exists(directory)) StorageFileSystem.EnsureRegularDirectory(directory);
            MoveSkillPackage(skill == null ? null : skill.StoragePath, directory);
            StorageFileSystem.EnsureRegularDirectory(directory);
            StorageFileSystem.WriteAllTextAtomic(Path.Combine(directory, "SKILL.md"), Serialize(skill), Encoding.UTF8);
            skill.StoragePath = directory;
        }

        private static void MoveSkillPackage(string sourceDirectory, string targetDirectory)
        {
            if (string.IsNullOrWhiteSpace(sourceDirectory) || string.IsNullOrWhiteSpace(targetDirectory) ||
                string.Equals(sourceDirectory, targetDirectory, StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(sourceDirectory))
            {
                return;
            }
            if ((File.GetAttributes(sourceDirectory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Skill package directory cannot be a symbolic link.");
            }
            if (Directory.Exists(targetDirectory))
            {
                CopySkillReferences(sourceDirectory, targetDirectory);
                return;
            }

            StorageFileSystem.EnsureRegularDirectory(Path.GetDirectoryName(targetDirectory));
            Directory.Move(sourceDirectory, targetDirectory);
        }

        private static void CopySkillReferences(string sourceDirectory, string targetDirectory)
        {
            var sourceReferences = Path.Combine(sourceDirectory, "references");
            if (!Directory.Exists(sourceReferences)) return;
            if ((File.GetAttributes(sourceReferences) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Skill references directory cannot be a symbolic link.");
            }
            var targetReferences = Path.Combine(targetDirectory, "references");
            if (Directory.Exists(targetReferences) &&
                (File.GetAttributes(targetReferences) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Skill references directory cannot be a symbolic link.");
            }
            StorageFileSystem.EnsureRegularDirectory(targetReferences);
            var references = LoadReferences(sourceDirectory);
            if (references == null) throw new IOException("Skill references are invalid or unreadable.");
            foreach (var reference in references)
            {
                var sourcePath = Path.Combine(sourceReferences, Path.GetFileName(reference.Path));
                var targetPath = Path.Combine(targetReferences, Path.GetFileName(reference.Path));
                var bytes = File.ReadAllBytes(sourcePath);
                if (!string.Equals(Sha256(bytes), reference.Revision, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("Skill reference changed while its package was moving: " + reference.Path);
                }
                if (File.Exists(targetPath) && (File.GetAttributes(targetPath) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("Skill reference cannot be a symbolic link: " + reference.Path);
                }
                StorageFileSystem.WriteAtomic(targetPath, tempPath => File.WriteAllBytes(tempPath, bytes));
            }
        }

        private string SkillDirectory(SkillDefinition skill)
        {
            return Path.Combine(
                _paths.SkillsDirectory,
                HostFolder(skill == null ? null : skill.Host),
                SkillFolder(skill == null ? null : skill.Id));
        }

        private bool IsRegularSkillPackage(string path)
        {
            try
            {
                var root = Path.GetFullPath(_paths.SkillsDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var full = Path.GetFullPath(path ?? string.Empty);
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                    StorageFileSystem.IsRegularDirectory(_paths.SkillsDirectory) &&
                    StorageFileSystem.IsRegularDirectory(Path.GetDirectoryName(full)) &&
                    StorageFileSystem.IsRegularDirectory(full);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                ex is SecurityException || ex is ArgumentException || ex is NotSupportedException)
            {
                return false;
            }
        }

        private static SkillDefinition LoadSkill(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > MaxSkillFileBytes ||
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
                var bytes = File.ReadAllBytes(path);
                var start = Utf8BomLength(bytes);
                var text = new UTF8Encoding(false, true).GetString(bytes, start, bytes.Length - start);
                return Parse(text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'), text);
            }
            catch (DecoderFallbackException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
            catch (SecurityException)
            {
                return null;
            }
        }

        private static List<SkillReferenceMetadata> LoadReferences(string skillDirectory)
        {
            var result = new List<SkillReferenceMetadata>();
            if (string.IsNullOrWhiteSpace(skillDirectory)) return result;
            var directory = Path.Combine(skillDirectory, "references");
            try
            {
                if (!Directory.Exists(directory)) return result;
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return null;

                var files = Directory.GetFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(path => path, StringComparer.Ordinal)
                    .ToArray();
                if (files.Length > MaximumSkillReferences || files
                    .GroupBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                    .Any(group => group.Count() > 1)) return null;
                foreach (var file in files)
                {
                    var info = new FileInfo(file);
                    if (!info.Exists || info.Length > MaximumSkillReferenceBytes ||
                        (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    {
                        return null;
                    }
                    var bytes = File.ReadAllBytes(file);
                    var start = Utf8BomLength(bytes);
                    new UTF8Encoding(false, true).GetCharCount(bytes, start, bytes.Length - start);
                    result.Add(new SkillReferenceMetadata
                    {
                        Path = "references/" + Path.GetFileName(file),
                        ByteLength = info.Length,
                        Revision = Sha256(bytes)
                    });
                }
            }
            catch (DecoderFallbackException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
            catch (SecurityException)
            {
                return null;
            }
            return result;
        }

        private static int Utf8BomLength(byte[] bytes)
        {
            return bytes != null && bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf
                ? 3
                : 0;
        }

        public static bool TryNormalizeReferencePath(string value, out string normalized)
        {
            normalized = (value ?? string.Empty).Trim().Replace('\\', '/');
            const string prefix = "references/";
            if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            var fileName = normalized.Substring(prefix.Length);
            if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOf('/') >= 0 ||
                !string.Equals(fileName, fileName.Trim(), StringComparison.Ordinal) ||
                fileName.Any(char.IsControl) || fileName.IndexOfAny(new[] { '<', '>', ':', '"', '|', '?', '*' }) >= 0)
            {
                return false;
            }
            try
            {
                if (!string.Equals(Path.GetExtension(fileName), ".md", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
                {
                    return false;
                }
            }
            catch (ArgumentException)
            {
                return false;
            }
            normalized = prefix + fileName;
            return true;
        }

        private static string Sha256(byte[] value)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(value ?? new byte[0])).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        public static string ComputeReferenceRevision(string content)
        {
            return Sha256(new UTF8Encoding(false).GetBytes(content ?? string.Empty));
        }

        public static long ComputeReferenceByteLength(string content)
        {
            return new UTF8Encoding(false).GetByteCount(content ?? string.Empty);
        }

        private static SkillDefinition Parse(string[] lines, string original)
        {
            var skill = new SkillDefinition();
            var bodyStart = 0;
            if (lines != null && lines.Length > 0 && string.Equals(lines[0].Trim(), "---", StringComparison.Ordinal))
            {
                var header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var closed = false;
                for (var i = 1; i < lines.Length; i++)
                {
                    if (string.Equals(lines[i].Trim(), "---", StringComparison.Ordinal))
                    {
                        closed = true;
                        bodyStart = i + 1;
                        while (bodyStart < lines.Length && string.IsNullOrWhiteSpace(lines[bodyStart]))
                        {
                            bodyStart += 1;
                        }
                        break;
                    }

                    var separator = lines[i].IndexOf(':');
                    if (separator <= 0)
                    {
                        continue;
                    }
                    var key = lines[i].Substring(0, separator).Trim();
                    if (header.ContainsKey(key)) return null;
                    header[key] = lines[i].Substring(separator + 1).Trim();
                }
                if (!closed) return null;

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
            if (string.IsNullOrWhiteSpace(skill.Description)) return "Skill description is required.";
            if (string.IsNullOrWhiteSpace(skill.BodyMarkdown)) return "Skill bodyMarkdown is required.";
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
