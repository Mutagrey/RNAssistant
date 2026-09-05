using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.Tools
{
    internal sealed partial class CapabilityCatalogService
    {
        private readonly SkillCatalogService _skillCatalog;
        private readonly ResourceGatewayService _resources;
        internal SkillCatalogSnapshot CaptureSkills() { return _skillCatalog.Capture(); }
        internal SkillCatalogSnapshot SelectPublishedSkills(SkillCatalogSnapshot published)
        { return _skillCatalog.SelectPublished(published); }

        internal CapabilityCatalogService(IOfficeApplicationAdapter adapter,
            Func<SkillCatalogSnapshot> publishedSkills, ResourceGatewayService resources)
        {
            _resources = resources ?? throw new ArgumentNullException(nameof(resources));
            _skillCatalog = new SkillCatalogService(adapter ?? throw new ArgumentNullException(nameof(adapter)), publishedSkills);
        }

        private IReadOnlyList<SkillDefinition> CapabilitySkills(bool manualRun, IReadOnlyList<SkillDefinition> runtimeSkills)
        {
            if (runtimeSkills != null) return runtimeSkills.Where(item => item != null && item.Enabled).ToList();
            return manualRun ? (IReadOnlyList<SkillDefinition>)_skillCatalog.GetVisibleSkills() : new SkillDefinition[0];
        }

        private CapabilityToolOutcome ReadSkill(IDictionary<string, object> arguments, SkillDefinition skill, ChatSession session)
        {
            if (skill == null) return CapabilityToolOutcome.Error("Skill reader is unavailable.", null, "capability_reader_unavailable", false);
            if (HasArgument(arguments, "referencePath")) return ReadSkillReference(arguments, skill, session);
            if (HasArgument(arguments, "offset") || HasArgument(arguments, "maxChars"))
                return CapabilityToolOutcome.Error("Offsets and page sizes belong to resource continuation.", null, "capability_runtime_state_not_allowed", false);
            var exact = CatalogResourceProvider.SkillResource(skill);
            var result = _resources.Read(session, new ResourceReadRequest { Reference = exact, Representation = "text", MaxChars = 24000 }).Result;
            return CapabilityToolOutcome.Ok(result.Complete ? "Skill loaded: " + skill.Id + ". No tool schema was admitted by this resource read." :
                "Partial skill body. Read the skill target through common.resources_read for complete content.",
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
            var exact = CatalogResourceProvider.SkillResource(skill, path);
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
                cursor = ResourceReadCursor.CreateRevisionBound((int)previous.Coverage.End.Value, previous.ContentSha256,
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
