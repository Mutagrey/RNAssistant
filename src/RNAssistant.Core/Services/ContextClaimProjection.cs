using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    public static class ContextClaimProjection
    {
        public const string CapabilityContextNotice =
            "SKILL_CONTEXT_NOTICE: Reuse current SELECTED_SKILL bodies. Historical reads are not selection. " +
            "TOOL_SCHEMA_NOTICE: Available tools are callable with the supplied schemas; load missing capabilities explicitly. Compaction alone requires no reload.";

        public static ContextMessagePresentation CompactSelectedSkillRead(ChatMessage message,
            SkillDefinition skill, ChatMessage instruction, ModelAuthoritySnapshot authority)
        {
            if (message.ToolName != "core.read" || skill?.Publication?.IsExact != true || instruction == null) return null;
            ToolResultWireReadResult wire; string error;
            if (!ToolResultHistoryReader.TryRead(message, out wire, out error) || wire.Result.Status != ToolResultStatus.Ok) return null;
            var data = wire.Data as JObject;
            if ((string)data?["type"] != "skill" || (string)data?["representation"] != "text" ||
                (bool?)data?["complete"] != true || data?["text"]?.Type != JTokenType.String ||
                (string)data["text"] != skill.BodyMarkdown) return null;
            var resource = SkillPublicationService.SkillResource(skill);
            var evidence = message.ResourceEvidence.Where(item => item.ScopeId.Equals(SkillPublicationService.ScopeId) &&
                item.Resource.Uri == resource.Uri && item.Resource.Revision == resource.Revision && item.Complete &&
                item.View == ResourceRepresentations.Text && item.Coverage.Kind == ResourceCoverageKinds.Whole &&
                new EvidenceStateReducer().Reduce(item, authority.Resources).State == EvidenceState.Current).ToArray();
            if (evidence.Length != 1) return null;
            data.Remove("text");
            if (data.Property("rawContentIncluded") != null) data["rawContentIncluded"] = false;
            data["bodyIncluded"] = false;
            data["retainedAt"] = new JObject { ["kind"] = "selected_skill", ["id"] = skill.Id };
            var json = ToolResultWire.WriteParsed(wire.ToolCallId, wire.Name, wire.Result, data, wire.ResultResource);
            message.Content = message.Role == ToolResultRoles.Tool ? json : "TOOL_RESULT:\n" + json;
            return new ContextMessagePresentation { Kind = "data.text", Presentation = ContextPresentationKind.Summary,
                RetainedAtMessageId = instruction.Id,
                Reason = "Точное полное тело уже включено в выбранный skill; исходное чтение сохранено в истории." };
        }

        public static bool CurrentClaim(StructuredContextClaim claim, ModelAuthoritySnapshot authority)
        {
            return claim != null && claim.HasTypedProvenance() &&
                claim.Evidence.All(evidence => new EvidenceStateReducer().Reduce(evidence, authority.Resources).State == EvidenceState.Current);
        }

    }
}
