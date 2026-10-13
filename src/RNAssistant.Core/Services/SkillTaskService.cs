using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Services
{
    public sealed class ArsenalSkill
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Host { get; set; }
        public string Revision { get; set; }
        public string Status { get; set; }
        public string Reason { get; set; }
        public List<string> Tools { get; set; } = new List<string>();
        public List<string> References { get; set; } = new List<string>();
    }

    public sealed class ArsenalTool
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Revision { get; set; }
        public string Origin { get; set; }
        public bool RequiresConfirmation { get; set; }
        public List<string> Skills { get; set; } = new List<string>();
    }

    // Metadata only. Saved with the existing context receipt; never an authority store.
    public sealed class AgentArsenal
    {
        public string RunId { get; set; }
        public string StepId { get; set; }
        public string ToolFingerprint { get; set; }
        public string Policy { get; set; }
        public List<ArsenalSkill> Selected { get; set; } = new List<ArsenalSkill>();
        public List<ArsenalTool> Tools { get; set; } = new List<ArsenalTool>();
        public List<ArsenalSkill> Available { get; set; } = new List<ArsenalSkill>();
        public int InstructionTokens { get; set; }
    }

    public sealed class SkillRequestSurface
    {
        public IReadOnlyList<ToolCatalogEntry> Tools { get; internal set; }
        public IReadOnlyList<SkillDefinition> Instructions { get; internal set; }
        public AgentArsenal Arsenal { get; internal set; }
    }

    // One resolver for both hosts. Only frozen published metadata enters this owner.
    // SelectedSkills is part of run.updated in the append-only conversation stream.
    public static class SkillTaskService
    {
        public static string Validate(SkillPackageManifest manifest)
        {
            if (manifest == null) return null; // Portable instruction-only SKILL.md.
            if (manifest.FormatVersion != 1 || string.IsNullOrWhiteSpace(manifest.PackageId) ||
                manifest.PackageId.Length > 128 || manifest.PackageId.Any(char.IsWhiteSpace))
                return "Invalid skill package identity or formatVersion.";
            var required = manifest.RequiredTools ?? new List<SkillToolDependency>();
            var optional = manifest.OptionalTools ?? new List<SkillToolDependency>();
            var all = required.Concat(optional).ToArray();
            if (all.Length > 64 || all.Any(item => item == null || string.IsNullOrWhiteSpace(item.Id) ||
                item.Id.Length > 128 || item.Id.Any(char.IsWhiteSpace) || string.IsNullOrWhiteSpace(item.ContractVersion) ||
                item.ContractVersion.Length > 64 || (item.Fallback ?? "").Length > 2000 ||
                item.Revision != null && (item.Revision.Length != 64 || item.Revision.Any(c => !Uri.IsHexDigit(c)))))
                return "Invalid or excessive skill tool dependencies.";
            if (all.GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
                return "Duplicate skill tool dependency.";
            if (optional.Any(item => string.IsNullOrWhiteSpace(item.Fallback)))
                return "Optional dependencies require an explicit fallback.";
            return null;
        }

        private static List<SkillToolPin> Resolve(SkillDefinition skill, ToolPackSnapshot pack, out string error,
            out string optionalNotice)
        {
            error = Validate(skill.Package); optionalNotice = null;
            var pins = new List<SkillToolPin>();
            if (error != null) return pins;
            foreach (var requirement in skill.Package?.RequiredTools ?? new List<SkillToolDependency>())
            {
                var registration = pack.Find(requirement.Id);
                if (!Matches(registration, requirement))
                { error = "Required tool unavailable or incompatible: " + requirement.Id; return pins; }
                pins.Add(new SkillToolPin { Id = requirement.Id, Revision = registration.Revision });
            }
            var missing = new List<string>();
            foreach (var requirement in skill.Package?.OptionalTools ?? new List<SkillToolDependency>())
            {
                var registration = pack.Find(requirement.Id);
                if (Matches(registration, requirement))
                    pins.Add(new SkillToolPin { Id = requirement.Id, Revision = registration.Revision });
                else missing.Add(requirement.Id + ": " + requirement.Fallback);
            }
            optionalNotice = missing.Count == 0 ? null : string.Join("\n", missing);
            return pins.OrderBy(item => item.Id, StringComparer.Ordinal).ToList();
        }

        private static bool Matches(ToolRegistration registration, SkillToolDependency requirement)
        {
            return registration != null && registration.Descriptor.ContractVersion == requirement.ContractVersion &&
                (requirement.Revision == null || requirement.Revision == registration.Revision);
        }

        public static ArsenalSkill Select(ChatSession session, string id, SkillCatalogSnapshot skills,
            ToolPackSnapshot pack, AppSettings settings, string callId)
        {
            if (session?.LastRun == null) throw new InvalidOperationException("An active task is required.");
            var skill = skills.Skills.SingleOrDefault(item => item.Id == id && item.Enabled);
            if (skill == null) throw new InvalidOperationException("Published skill unavailable: " + id);
            string error, optional;
            var pins = Resolve(skill, pack, out error, out optional);
            if (error != null) throw new InvalidOperationException(error);
            var selection = new SelectedSkill { Id = id, Revision = skills.Revision(id),
                Publication = skill.Publication?.Copy(), SelectionCallId = callId, Tools = pins };
            var previous = session.LastRun.SelectedSkills ?? new List<SelectedSkill>();
            var candidate = previous.Where(item => item.Id != id).Concat(new[] { selection }).ToList();
            foreach (var group in candidate.SelectMany(item => item.Tools).GroupBy(item => item.Id, StringComparer.Ordinal))
                if (group.Select(item => item.Revision).Distinct(StringComparer.Ordinal).Count() != 1)
                    throw new InvalidOperationException("Selected skills require conflicting bindings: " + group.Key);
            var definitions = skills.Skills;
            var bodies = candidate.Select(item => definitions.SingleOrDefault(def => def.Id == item.Id &&
                skills.Revision(def.Id) == item.Revision)).Where(item => item != null).ToArray();
            var tokens = bodies.Sum(item => ModelContextBudget.EstimateTextTokens(item.BodyMarkdown, settings));
            tokens += candidate.SelectMany(item => item.Tools).Select(item => item.Id).Distinct(StringComparer.Ordinal)
                .Sum(tool => ModelContextBudget.EstimateTextTokens(pack.Find(tool)?.Descriptor.ParametersJson ?? "", settings));
            if (tokens > ModelContextBudget.InputBudgetTokens(settings) / 2)
                throw new InvalidOperationException("Skill instructions and dependencies exceed the task context budget.");
            // Commit only after the complete candidate has passed validation. The caller
            // persists this together with the accepted terminal through the existing store.
            if (!previous.Any(item => item.Id == id && item.Revision == selection.Revision &&
                JsonConvert.SerializeObject(item.Tools) == JsonConvert.SerializeObject(pins)))
                session.LastRun.SelectedSkills = candidate;
            return Describe(skill, "pending", optional, pins, skills.Revision(id));
        }

        public static SkillRequestSurface Compile(ChatSession session, SkillCatalogSnapshot skills,
            ToolPackSnapshot pack, IReadOnlyList<ToolCatalogEntry> catalog,
            IReadOnlyList<ToolCatalogEntry> directTools, string stepId, AppSettings settings)
        {
            var arsenal = new AgentArsenal { RunId = session?.LastRun?.RunId, StepId = stepId,
                Policy = session?.LastRun?.ConversationPolicy == null ? null :
                    session.LastRun.ConversationPolicy.Mode + "/" + session.LastRun.ConversationPolicy.MutationPolicy +
                    (session.LastRun.ConversationPolicy.AutoConfirmsToolActions ? "/auto" : "") };
            var selected = session?.LastRun?.SelectedSkills ?? new List<SelectedSkill>();
            var definitions = skills.Skills;
            var instructions = new List<SkillDefinition>();
            var ids = new HashSet<string>((directTools ?? new ToolCatalogEntry[0])
                .Where(item => item.Exposure != ToolExposure.Skill).Select(item => item.Id), StringComparer.Ordinal);
            foreach (var selection in selected.OrderBy(item => item.Id, StringComparer.Ordinal))
            {
                var skill = definitions.SingleOrDefault(item => item.Id == selection.Id && item.Enabled);
                string error = null, optional = null;
                var pins = skill == null ? new List<SkillToolPin>() : Resolve(skill, pack, out error, out optional);
                if (skill == null || skills.Revision(skill.Id) != selection.Revision)
                    error = "Selected package is no longer published at its exact revision. Load the current skill explicitly.";
                else if (selection.Tools == null || pins.Count != selection.Tools.Count ||
                    pins.Any(pin => !selection.Tools.Any(saved => saved.Id == pin.Id && saved.Revision == pin.Revision)))
                    error = "A selected tool binding changed or became unavailable. Load a compatible skill explicitly.";
                var entry = skill == null ? new ArsenalSkill { Id = selection.Id, Name = selection.Id } :
                    Describe(skill, "selected", optional, pins, selection.Revision);
                entry.Revision = selection.Revision;
                if (error != null) { entry.Status = "unavailable"; entry.Reason = error; }
                else { instructions.Add(skill); ids.UnionWith(pins.Select(item => item.Id)); }
                arsenal.Selected.Add(entry);
            }
            foreach (var skill in definitions.Where(item => item.Enabled).OrderBy(item => item.Id, StringComparer.Ordinal))
            {
                string error, optional;
                var pins = Resolve(skill, pack, out error, out optional);
                arsenal.Available.Add(Describe(skill, error == null ? "available" : "unavailable", error ?? optional, pins, skills.Revision(skill.Id)));
            }
            var tools = catalog.Where(item => ids.Contains(item.Id)).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
            foreach (var tool in tools)
            {
                var registration = pack.Find(tool.Id);
                if (registration == null) throw new InvalidOperationException("Missing exact tool binding: " + tool.Id);
                var owners = arsenal.Selected.Where(item => item.Status == "selected" && item.Tools.Contains(tool.Id)).Select(item => item.Id).ToList();
                arsenal.Tools.Add(new ArsenalTool { Id = tool.Id, Name = tool.Name, Description = tool.Description,
                    Revision = registration.Revision, Origin = owners.Count > 0 ? "skill" : "direct",
                    Skills = owners, RequiresConfirmation = registration.Policy.RequiresConfirmation });
            }
            arsenal.InstructionTokens = instructions.Sum(item => ModelContextBudget.EstimateTextTokens(item.BodyMarkdown, settings));
            arsenal.ToolFingerprint = SkillRevision.ComputeMarkdown(string.Join("\n", arsenal.Tools.Select(item => item.Id + ":" + item.Revision)));
            return new SkillRequestSurface { Tools = tools, Instructions = instructions, Arsenal = arsenal };
        }

        private static ArsenalSkill Describe(SkillDefinition skill, string status, string reason, List<SkillToolPin> pins, string revision)
        {
            return new ArsenalSkill { Id = skill.Id, Name = skill.Name, Description = skill.Description,
                Host = skill.Host,
                Revision = revision, Status = status, Reason = reason,
                Tools = pins.Select(item => item.Id).ToList(),
                References = (skill.References ?? new List<SkillReferenceMetadata>()).Select(item => item.Path).ToList() };
        }

        public static string CheckDispatch(ChatSession session, ToolExecutionContext context, ToolPackSnapshot pack,
            RNAssistant.Core.Persistence.AgentRunState confirmationOrigin = null)
        {
            var request = session?.LastContextReceipt?.Arsenal;
            var requestRunId = context.RunId;
            var pending = confirmationOrigin?.Summary.PendingConfirmation;
            // Controller invocations get a new run id at approval. Only the exact
            // durable pending call may use the request that originally offered it.
            if (context.IsConfirmed && pending != null &&
                confirmationOrigin.Summary.Lifecycle == RNAssistant.Core.Agent.RunLifecycle.AwaitingConfirmation &&
                confirmationOrigin.Summary.TurnId == context.TurnId && pending.StepId == context.StepId &&
                pending.Call.Id == context.Call.Id && pending.Call.Name == context.Call.Name &&
                pending.Call.ArgumentsJson == context.Call.ArgumentsJson && pending.Policy.Revision == context.Policy.Revision &&
                pending.PreparedStateJson == context.PreparedStateJson)
                requestRunId = confirmationOrigin.Summary.RunId;
            if (request == null || request.RunId != requestRunId || request.StepId != context.StepId)
                return "The exact originating request bindings are unavailable.";
            var offered = request.Tools.SingleOrDefault(item => item.Id == context.Call.Name);
            var actual = pack.Find(context.Call.Name);
            if (offered == null) return "Tool was not exposed in the originating request. Load its skill for the next step.";
            if (actual == null || offered.Revision != actual.Revision)
                return "The exact originating tool binding is unavailable or revoked.";
            return null;
        }
    }
}
