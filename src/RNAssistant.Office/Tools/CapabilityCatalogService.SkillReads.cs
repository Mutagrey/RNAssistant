using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.ModelProtocol;
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
            SkillDefinition skill,
            ChatSession session)
        {
            if (skill == null)
            {
                return CapabilityToolOutcome.Error(
                    "Skill reader is unavailable.", null,
                    "capability_reader_unavailable", false);
            }
            if (HasArgument(arguments, "referencePath"))
                return ReadSkillReference(arguments, skill, session);
            if (HasArgument(arguments, "offset") ||
                HasArgument(arguments, "maxChars"))
            {
                return CapabilityToolOutcome.Error(
                    "Capability reference offsets and page sizes are runtime-owned.", null,
                    "capability_runtime_state_not_allowed", false);
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
            SkillDefinition skill,
            ChatSession session)
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

            var action = ToolArgumentReader.String(
                arguments, "action", "read").Trim().ToLowerInvariant();
            var offset = 0;
            if (action == "next")
            {
                string continuationError;
                string continuationCode;
                if (!TryReferenceContinuation(
                    session,
                    skill,
                    metadata,
                    out offset,
                    out continuationError,
                    out continuationCode))
                {
                    return CapabilityToolOutcome.Error(
                        continuationError,
                        null,
                        continuationCode,
                        false);
                }
            }
            if (offset < 0 || offset > (content ?? string.Empty).Length)
            {
                return CapabilityToolOutcome.Error(
                    "Stored capability continuation is outside the current reference. Restart with action=read.", null,
                    "capability_continuation_invalid", false);
            }
            const int maxChars = 24000;
            var end = Math.Min(content.Length, offset + maxChars);
            if (end > offset && end < content.Length &&
                char.IsHighSurrogate(content[end - 1]) &&
                char.IsLowSurrogate(content[end]))
            {
                end += 1;
            }
            var complete = end >= content.Length;
            return CapabilityToolOutcome.Ok(
                complete
                    ? "Skill reference read: " + metadata.Path
                    : "Skill reference chunk read. Call the same id and referencePath with action=next to continue.",
                JsonConvert.SerializeObject(new
                {
                    kind = "reference",
                    id = skill.Id,
                    skillRevision = SkillRevision.Compute(skill),
                    path = metadata.Path,
                    revision = metadata.Revision,
                    format = "markdown",
                    returnedChars = end - offset,
                    totalChars = content.Length,
                    progressCharacters = end,
                    complete = complete,
                    hasMore = !complete,
                    content = content.Substring(offset, end - offset)
                }));
        }

        private static bool TryReferenceContinuation(
            ChatSession session,
            SkillDefinition skill,
            SkillReferenceMetadata metadata,
            out int offset,
            out string error,
            out string code)
        {
            offset = 0;
            error = null;
            code = null;
            foreach (var message in (session == null
                    ? new List<ChatMessage>()
                    : session.Messages ?? new List<ChatMessage>())
                .Where(item => item != null)
                .Reverse())
            {
                ToolResultWireReadResult wire;
                string parseError;
                if (!ToolResultHistoryReader.TryRead(
                        message, out wire, out parseError) ||
                    !string.Equals(
                        wire.Name,
                        CapabilityToolCatalog.ReadToolId,
                        StringComparison.Ordinal) ||
                    wire.Result.Status != RNAssistant.Core.Tools.Contracts.ToolResultStatus.Ok ||
                    string.IsNullOrWhiteSpace(wire.Result.DataJson)) continue;
                JObject data;
                try
                {
                    data = JObject.Parse(wire.Result.DataJson);
                }
                catch (JsonException)
                {
                    continue;
                }
                if (!string.Equals((string)data["kind"], "reference", StringComparison.Ordinal) ||
                    !string.Equals((string)data["id"], skill.Id, StringComparison.Ordinal) ||
                    !string.Equals((string)data["path"], metadata.Path, StringComparison.Ordinal)) continue;
                if ((bool?)data["complete"] == true)
                {
                    error = "The latest read for this skill reference is already complete. Use action=read to start again.";
                    code = "capability_continuation_complete";
                    return false;
                }
                if (!string.Equals(
                        (string)data["skillRevision"],
                        SkillRevision.Compute(skill),
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        (string)data["revision"],
                        metadata.Revision,
                        StringComparison.Ordinal))
                {
                    error = "The skill reference changed after the previous chunk. Restart with action=read.";
                    code = "skill_reference_changed";
                    return false;
                }
                offset = (int?)data["progressCharacters"] ?? 0;
                if (offset > 0) return true;
                break;
            }
            error = "No incomplete accepted read exists for this skill reference. Start with action=read.";
            code = "capability_continuation_missing";
            return false;
        }
    }
}
