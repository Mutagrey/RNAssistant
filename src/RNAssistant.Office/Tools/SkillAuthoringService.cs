using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.Tools
{
    internal sealed partial class SkillAuthoringService
    {
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly SkillStore _skillStore;
        private readonly Func<string, bool> _isReservedCapabilityId;

        internal SkillAuthoringService(
            IOfficeApplicationAdapter adapter,
            SkillStore skillStore,
            Func<string, bool> isReservedCapabilityId)
        {
            _adapter = adapter;
            _skillStore = skillStore;
            _isReservedCapabilityId = isReservedCapabilityId;
        }

        internal bool CanUse { get { return _skillStore != null; } }

        private SkillAuthoringOutcome ResolveUpsert(
            IDictionary<string, object> arguments,
            out SkillDefinition current,
            out SkillDefinition intended,
            out string operation,
            out string referencePath)
        {
            current = null;
            intended = null;
            operation = string.Empty;
            referencePath = null;
            var id = ToolArgumentReader.String(arguments, "id", string.Empty);
            var reserved = ValidateAuthoredSkillId(id);
            if (reserved != null) return reserved;
            current = FindStoredSkill(id);

            var hasReferencePath = HasArgument(arguments, "referencePath");
            var hasReferenceBody = HasArgument(arguments, "referenceMarkdown");
            if (hasReferencePath || hasReferenceBody)
            {
                return ResolveReferenceUpsert(arguments, current,
                    out intended, out operation, out referencePath);
            }

            var mode = ToolArgumentReader.String(arguments, "mode", "upsert");
            if (current != null && string.Equals(mode, "createOnly",
                StringComparison.OrdinalIgnoreCase))
            {
                return SkillAuthoringOutcome.Error(
                    "Skill already exists: " + id +
                    ". Use mode=upsert or updateOnly.", null,
                    "skill_already_exists", false);
            }
            if (current == null && string.Equals(mode, "updateOnly",
                StringComparison.OrdinalIgnoreCase))
            {
                return SkillAuthoringOutcome.Error(
                    "Custom skill not found: " + id +
                    ". Use mode=upsert or createOnly.", null,
                    "skill_not_found", false);
            }
            if (current != null && !HasMutableArguments(arguments))
            {
                return SkillAuthoringOutcome.Error(
                    "Skill update requires at least one supplied field besides id/mode.",
                    null, "skill_update_empty", true);
            }

            intended = current == null
                ? ReadSkillDefinition(arguments)
                : UpdateSkillDefinition(current, arguments);
            var validation = SkillStore.ValidateDefinition(intended);
            if (!string.IsNullOrWhiteSpace(validation))
            {
                return SkillAuthoringOutcome.Error(validation, null,
                    "invalid_skill_definition", false);
            }
            operation = current == null ? "create" : "update";
            return null;
        }

        private SkillAuthoringOutcome ResolveReferenceUpsert(
            IDictionary<string, object> arguments,
            SkillDefinition current,
            out SkillDefinition intended,
            out string operation,
            out string referencePath)
        {
            intended = null;
            operation = string.Empty;
            referencePath = null;
            var id = ToolArgumentReader.String(arguments, "id", string.Empty);
            if (!HasArgument(arguments, "referencePath") ||
                !HasArgument(arguments, "referenceMarkdown"))
            {
                return SkillAuthoringOutcome.Error(
                    "referencePath and referenceMarkdown must be supplied together.",
                    null, "invalid_skill_reference", false);
            }
            if (CoreFields.Any(name => HasArgument(arguments, name)))
            {
                return SkillAuthoringOutcome.Error(
                    "Update a skill core and a reference in separate calls.",
                    null, "mixed_skill_reference_update", false);
            }
            if (current == null)
            {
                return SkillAuthoringOutcome.Error(
                    "Custom skill not found: " + id, null,
                    "skill_not_found", false);
            }
            string normalizedPath;
            if (!SkillStore.TryNormalizeReferencePath(
                ToolArgumentReader.String(arguments, "referencePath",
                    string.Empty), out normalizedPath))
            {
                return SkillAuthoringOutcome.Error(
                    "Reference path must be one Markdown file directly under references/.",
                    null, "invalid_skill_reference", false);
            }
            var content = ToolArgumentReader.String(
                arguments, "referenceMarkdown", string.Empty);
            if (content.Length > SkillStore.MaximumSkillReferenceCharacters ||
                SkillStore.ComputeReferenceByteLength(content) >
                    SkillStore.MaximumSkillReferenceBytes)
            {
                return SkillAuthoringOutcome.Error(
                    "Skill reference is too large.", null,
                    "invalid_skill_reference", false);
            }
            var existing = (current.References ??
                new List<SkillReferenceMetadata>()).FirstOrDefault(item =>
                    item != null && string.Equals(item.Path, normalizedPath,
                        StringComparison.OrdinalIgnoreCase));
            var mode = ToolArgumentReader.String(arguments, "mode", "upsert");
            if (existing != null && string.Equals(mode, "createOnly",
                StringComparison.OrdinalIgnoreCase))
            {
                return SkillAuthoringOutcome.Error(
                    "Skill reference already exists: " + normalizedPath,
                    null, "skill_reference_exists", false);
            }
            if (existing == null && string.Equals(mode, "updateOnly",
                StringComparison.OrdinalIgnoreCase))
            {
                return SkillAuthoringOutcome.Error(
                    "Skill reference not found: " + normalizedPath,
                    null, "skill_reference_not_found", false);
            }
            if (existing == null && (current.References == null ? 0 :
                current.References.Count) >= SkillStore.MaximumSkillReferences)
            {
                return SkillAuthoringOutcome.Error(
                    "Skill reference limit reached: " +
                    SkillStore.MaximumSkillReferences + ".", null,
                    "invalid_skill_reference", false);
            }

            intended = Clone(current);
            if (existing != null) normalizedPath = existing.Path;
            intended.References.RemoveAll(item => item != null &&
                string.Equals(item.Path, normalizedPath,
                    StringComparison.OrdinalIgnoreCase));
            intended.References.Add(new SkillReferenceMetadata
            {
                Path = normalizedPath,
                ByteLength = SkillStore.ComputeReferenceByteLength(content),
                Revision = SkillStore.ComputeReferenceRevision(content)
            });
            referencePath = normalizedPath;
            operation = existing == null
                ? "create_reference" : "update_reference";
            return null;
        }

        private SkillAuthoringOutcome ResolveDelete(
            IDictionary<string, object> arguments,
            out SkillDefinition current,
            out SkillDefinition intended,
            out string operation,
            out string referencePath)
        {
            current = null;
            intended = null;
            operation = "delete";
            referencePath = null;
            var id = ToolArgumentReader.String(arguments, "id", string.Empty);
            var reserved = ValidateAuthoredSkillId(id);
            if (reserved != null) return reserved;
            current = FindStoredSkill(id);
            if (current == null)
            {
                return SkillAuthoringOutcome.Error(
                    "Custom skill not found: " + id, null,
                    "skill_not_found", false);
            }
            if (!HasArgument(arguments, "referencePath")) return null;
            string normalizedPath;
            if (!SkillStore.TryNormalizeReferencePath(
                ToolArgumentReader.String(arguments, "referencePath",
                    string.Empty), out normalizedPath))
            {
                return SkillAuthoringOutcome.Error(
                    "Reference path must be one Markdown file directly under references/.",
                    null, "invalid_skill_reference", false);
            }
            var existing = (current.References ??
                new List<SkillReferenceMetadata>()).FirstOrDefault(item =>
                    item != null && string.Equals(item.Path, normalizedPath,
                        StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                return SkillAuthoringOutcome.Error(
                    "Skill reference not found: " + normalizedPath,
                    null, "skill_reference_not_found", false);
            }
            normalizedPath = existing.Path;
            intended = Clone(current);
            intended.References.RemoveAll(item => item != null &&
                string.Equals(item.Path, normalizedPath,
                    StringComparison.OrdinalIgnoreCase));
            referencePath = normalizedPath;
            operation = "delete_reference";
            return null;
        }

        private SkillAuthoringOutcome ValidateAuthoredSkillId(string id)
        {
            if (BuiltInSkillProvider.GetSkills(_adapter).Any(skill =>
                skill != null && string.Equals(skill.Id, id,
                    StringComparison.OrdinalIgnoreCase)))
            {
                return SkillAuthoringOutcome.Error(
                    "Built-in skill id is reserved: " + id, null,
                    "reserved_skill_id", false);
            }
            return _isReservedCapabilityId != null &&
                _isReservedCapabilityId(id)
                    ? SkillAuthoringOutcome.Error(
                        "Skill id collides with an existing tool: " + id,
                        null, "reserved_skill_id", false)
                    : null;
        }

        private SkillDefinition FindStoredSkill(string id)
        {
            return (_skillStore == null
                    ? new List<SkillDefinition>() : _skillStore.Load())
                .FirstOrDefault(skill => skill != null && string.Equals(
                    skill.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        private static SkillDefinition ReadSkillDefinition(
            IDictionary<string, object> arguments)
        {
            var id = ToolArgumentReader.String(arguments, "id", string.Empty);
            return new SkillDefinition
            {
                Id = id,
                Host = ToolArgumentReader.String(arguments, "host", "Common"),
                Name = ToolArgumentReader.String(arguments, "name", id),
                Description = ToolArgumentReader.String(
                    arguments, "description", string.Empty),
                Version = ToolArgumentReader.String(
                    arguments, "version", "1.0.0"),
                BodyMarkdown = ToolArgumentReader.String(
                    arguments, "bodyMarkdown", string.Empty),
                Enabled = ReadBool(arguments, "enabled", true),
                BuiltIn = false
            };
        }

        private static SkillDefinition UpdateSkillDefinition(
            SkillDefinition current,
            IDictionary<string, object> arguments)
        {
            var intended = Clone(current);
            SetString(arguments, "host", value => intended.Host = value);
            SetString(arguments, "name", value => intended.Name = value);
            SetString(arguments, "description",
                value => intended.Description = value);
            SetString(arguments, "version",
                value => intended.Version = value);
            SetString(arguments, "bodyMarkdown",
                value => intended.BodyMarkdown = value);
            if (HasArgument(arguments, "enabled"))
                intended.Enabled = ReadBool(
                    arguments, "enabled", intended.Enabled);
            return intended;
        }

        private static SkillDefinition Clone(SkillDefinition skill)
        {
            if (skill == null) return null;
            var clone = SkillPackageSource.Capture(skill).ToDefinition();
            clone.StoragePath = skill.StoragePath;
            return clone;
        }

        private static readonly string[] CoreFields =
        {
            "host", "name", "description", "version",
            "bodyMarkdown", "enabled"
        };

        private static bool HasArgument(
            IDictionary<string, object> arguments, string name)
        {
            return arguments != null && arguments.ContainsKey(name);
        }

        private static bool HasMutableArguments(
            IDictionary<string, object> arguments)
        {
            return arguments != null && arguments.Keys.Any(name =>
                !string.Equals(name, "id", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, "mode", StringComparison.OrdinalIgnoreCase));
        }

        private static bool ReadBool(
            IDictionary<string, object> arguments,
            string name, bool fallback)
        {
            if (arguments == null || !arguments.ContainsKey(name) ||
                arguments[name] == null) return fallback;
            bool value;
            return bool.TryParse(Convert.ToString(arguments[name]), out value)
                ? value : fallback;
        }

        private static void SetString(
            IDictionary<string, object> arguments,
            string name, Action<string> apply)
        {
            if (HasArgument(arguments, name) && apply != null)
                apply(ToolArgumentReader.String(arguments, name, string.Empty));
        }
    }
}
