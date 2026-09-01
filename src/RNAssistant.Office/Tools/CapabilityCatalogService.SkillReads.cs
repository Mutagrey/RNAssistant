using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.Tools
{
    internal sealed partial class CapabilityCatalogService
    {
        private readonly SkillStore _skillStore;
        private readonly SkillCatalogService _skillCatalog;

        internal CapabilityCatalogService(
            IOfficeApplicationAdapter adapter,
            SkillStore skillStore)
        {
            _skillStore = skillStore ??
                throw new ArgumentNullException(nameof(skillStore));
            _skillCatalog = new SkillCatalogService(
                adapter ?? throw new ArgumentNullException(nameof(adapter)),
                skillStore);
        }

        private IReadOnlyList<SkillDefinition> CapabilitySkills(
            bool manualRun,
            IReadOnlyList<SkillDefinition> runtimeSkills)
        {
            if (runtimeSkills != null)
            {
                return runtimeSkills
                    .Where(item => item != null && item.Enabled)
                    .ToList();
            }
            return manualRun
                ? (IReadOnlyList<SkillDefinition>)_skillCatalog.GetVisibleSkills()
                : new SkillDefinition[0];
        }

        private CapabilityToolOutcome ReadSkill(
            IDictionary<string, object> arguments,
            SkillDefinition skill)
        {
            if (skill == null)
            {
                return CapabilityToolOutcome.Error(
                    "Skill reader is unavailable.", null,
                    "capability_reader_unavailable", false);
            }
            if (HasArgument(arguments, "referencePath"))
                return ReadSkillReference(arguments, skill);
            if (HasArgument(arguments, "offset") ||
                HasArgument(arguments, "maxChars"))
            {
                return CapabilityToolOutcome.Error(
                    "offset and maxChars require referencePath.", null,
                    "skill_reference_path_required", false);
            }

            return CapabilityToolOutcome.Ok(
                "Skill loaded: " + skill.Id +
                    ". Tool schemas named by this skill are not loaded automatically.",
                JsonConvert.SerializeObject(new
                {
                    kind = "skill",
                    loaded = true,
                    complete = true,
                    truncated = false,
                    id = skill.Id,
                    host = skill.Host,
                    name = skill.Name,
                    description = skill.Description,
                    version = string.IsNullOrWhiteSpace(skill.Version)
                        ? "1.0.0" : skill.Version,
                    revision = SkillRevision.Compute(skill),
                    bodyChars = (skill.BodyMarkdown ?? string.Empty).Length,
                    enabled = skill.Enabled,
                    format = "markdown",
                    references = (skill.References ??
                        new List<SkillReferenceMetadata>()).Select(item => new
                        {
                            path = item.Path,
                            byteLength = item.ByteLength,
                            revision = item.Revision
                        }).ToArray(),
                    capabilityUse = new
                    {
                        toolSchemasLoadedByThisRead = false,
                        instruction = "Before calling a tool id named in bodyMarkdown, ensure its schema is already callable. Otherwise call common.capabilities_read with that exact tool id, wait for a successful complete tool-schema result, and call the tool only in a later response."
                    },
                    bodyMarkdown = skill.BodyMarkdown ?? string.Empty
                }));
        }

        private CapabilityToolOutcome ReadSkillReference(
            IDictionary<string, object> arguments,
            SkillDefinition skill)
        {
            var referencePath = ToolArgumentReader.String(
                arguments, "referencePath", string.Empty);
            string content;
            string error;
            SkillReferenceMetadata metadata;
            if (!_skillStore.TryReadReference(
                skill, referencePath, out content, out metadata, out error))
            {
                var changed = (error ?? string.Empty).IndexOf(
                    "changed after", StringComparison.OrdinalIgnoreCase) >= 0;
                return CapabilityToolOutcome.Error(
                    error, null,
                    changed ? "skill_reference_changed" :
                        "skill_reference_unavailable",
                    false);
            }

            var offset = ToolArgumentReader.Int32(arguments, "offset", 0);
            if (offset < 0 || offset > (content ?? string.Empty).Length)
            {
                return CapabilityToolOutcome.Error(
                    "Skill reference offset is outside the file.", null,
                    "skill_reference_offset_invalid", false);
            }
            var maxChars = Math.Max(1, Math.Min(50000,
                ToolArgumentReader.Int32(arguments, "maxChars", 24000)));
            var end = Math.Min(content.Length, offset + maxChars);
            if (end > offset && end < content.Length &&
                char.IsHighSurrogate(content[end - 1]) &&
                char.IsLowSurrogate(content[end]))
            {
                end += 1;
            }
            var complete = end >= content.Length;
            return CapabilityToolOutcome.Ok(
                "Skill reference read: " + metadata.Path,
                JsonConvert.SerializeObject(new
                {
                    kind = "reference",
                    id = skill.Id,
                    skillRevision = SkillRevision.Compute(skill),
                    path = metadata.Path,
                    revision = metadata.Revision,
                    format = "markdown",
                    offset = offset,
                    returnedChars = end - offset,
                    totalChars = content.Length,
                    complete = complete,
                    nextOffset = complete ? (int?)null : end,
                    content = content.Substring(offset, end - offset)
                }));
        }
    }
}
