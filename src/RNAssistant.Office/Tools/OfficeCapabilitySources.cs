using System;
using RNAssistant.Core.Services;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Office.Services;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Tools
{
    internal sealed class OfficeCapabilitySources
    {
        private readonly ResourceGatewayService _resources;
        internal CapabilityCatalogService Catalog { get; }
        internal OfficeCapabilitySources(IOfficeApplicationAdapter adapter,
            Func<SkillCatalogSnapshot> publishedSkills, ResourceGatewayService resources)
        {
            _resources = resources ?? throw new ArgumentNullException(nameof(resources));
            Catalog = new CapabilityCatalogService(new SkillCatalogService(
                (adapter ?? throw new ArgumentNullException(nameof(adapter))).HostName, publishedSkills), ReadSkill);
        }
        private static bool HasArgument(IDictionary<string, object> arguments, string name)
        { return arguments != null && arguments.ContainsKey(name); }
        private static ToolRecoveryContract DefectRecovery()
        { return new ToolRecoveryContract(ToolFailureKind.ToolDefect, ToolRetryPolicy.None); }

        private CapabilityToolOutcome ReadSkill(IDictionary<string, object> arguments, SkillDefinition skill, ChatSession session)
        {
            if (skill == null) return CapabilityToolOutcome.Error(
                "Skill reader is unavailable.", null,
                "capability_reader_unavailable", false,
                DefectRecovery());
            if (HasArgument(arguments, "referencePath")) return ReadSkillReference(arguments, skill, session);
            if (HasArgument(arguments, "offset") || HasArgument(arguments, "maxChars"))
                return CapabilityToolOutcome.Error("Offsets and page sizes belong to resource continuation.", null, "capability_runtime_state_not_allowed", false);
            var exact = SkillPublicationService.SkillResource(skill);
            var result = _resources.ReadWhole(session, exact, "text").Result;
            return CapabilityToolOutcome.Ok("Skill source inspected: " + skill.Id + ". Use common.skills_load to select it for the task.",
                JsonConvert.SerializeObject(new {
                    kind = "skill", loaded = result.Complete, complete = result.Complete, truncated = !result.Complete,
                    id = skill.Id, host = skill.Host, name = skill.Name, description = skill.Description,
                    version = skill.Version, revision = SkillRevision.Compute(skill), bodyChars = result.TotalCharacters,
                    enabled = skill.Enabled, format = "markdown",
                    references = (skill.References ?? new List<SkillReferenceMetadata>()).Select(item => new {
                        path = item.Path, byteLength = item.ByteLength, revision = item.Revision }).ToArray(),
                    bodyMarkdown = result.Text
                }), _resources.Evidence(session, result));
        }

        private CapabilityToolOutcome ReadSkillReference(IDictionary<string, object> arguments, SkillDefinition skill, ChatSession session)
        {
            var path = ToolArgumentReader.String(arguments, "referencePath", string.Empty);
            var metadata = (skill.References ?? new List<SkillReferenceMetadata>()).SingleOrDefault(item => item.Path == path);
            if (metadata == null) return CapabilityToolOutcome.Error("The selected reference is not part of this publication.", null, "skill_reference_unavailable", false);
            var exact = SkillPublicationService.SkillResource(skill, path);
            var action = ToolArgumentReader.String(arguments, "action", "read").Trim().ToLowerInvariant();
            string cursor = null;
            if (action == "next")
            {
                var previous = (session?.Messages ?? new List<ChatMessage>()).Where(message => message.ToolName == CapabilityToolCatalog.ReadToolId)
                    .Reverse().SelectMany(message => message.ResourceEvidence ?? new List<ResourceEvidence>())
                    .FirstOrDefault(evidence => evidence.Resource.Identity.Equals(exact.Identity));
                if (previous == null) return CapabilityToolOutcome.Error("No incomplete exact reference read exists.", null, "capability_continuation_missing", false);
                if (previous.Resource.Revision != exact.Revision)
                    return CapabilityToolOutcome.Error("The skill publication changed. Restart with action=read.", null, "skill_reference_changed", false);
                if (previous.Complete) return CapabilityToolOutcome.Error("This reference read is complete.", null, "capability_continuation_complete", false);
                if (!previous.Coverage.End.HasValue || previous.Coverage.End.Value <= 0 || previous.Coverage.End.Value > int.MaxValue)
                    return CapabilityToolOutcome.Error("Exact reference coverage is unavailable.", null, "capability_continuation_invalid", false);
                cursor = ResourceReadCursor.CreateRevisionBound((int)previous.Coverage.End.Value, previous.Resource.Revision,
                    ResourceReadCursor.ReadBinding(exact.Uri, "text"));
            }
            var result = _resources.Read(session, new ResourceReadRequest {
                Reference = exact, Representation = "text", Cursor = cursor, MaxChars = 24000 }).Result;
            return CapabilityToolOutcome.Ok(result.Complete ? "Skill reference read: " + path :
                "Reference chunk read. Continue the same id and referencePath with action=next.",
                JsonConvert.SerializeObject(new {
                    kind = "reference", id = skill.Id, skillRevision = SkillRevision.Compute(skill), path = path,
                    revision = metadata.Revision, format = "markdown", returnedChars = result.ReturnedCharacters,
                    totalChars = result.TotalCharacters, progressCharacters = result.Offset + result.ReturnedCharacters,
                    complete = result.Complete, hasMore = !result.Complete, content = result.Text
                }), _resources.Evidence(session, result));
        }
    }
}
