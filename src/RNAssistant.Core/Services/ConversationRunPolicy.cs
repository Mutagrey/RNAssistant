using RNAssistant.Core.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum MutationPolicy { Ask, Deny }

    // One per-run decision, independent of model output and tool descriptions.
    public sealed class ConversationRunPolicy
    {
        public string Mode { get; private set; }
        public MutationPolicy MutationPolicy { get; private set; }
        public bool AutoConfirmToolActions { get; private set; }
        [JsonConstructor]
        public ConversationRunPolicy(string mode, MutationPolicy mutationPolicy = MutationPolicy.Ask,
            bool autoConfirmToolActions = false)
        {
            if (mode != ChatModes.Agent && mode != ChatModes.Plan)
                throw new ArgumentException("New runs support agent or plan only.", nameof(mode));
            if (!Enum.IsDefined(typeof(MutationPolicy), mutationPolicy))
                throw new ArgumentOutOfRangeException(nameof(mutationPolicy));
            Mode = mode;
            MutationPolicy = mutationPolicy;
            AutoConfirmToolActions = autoConfirmToolActions;
        }
        public bool AllowsSkills { get { return true; } }
        public bool AllowsConfirmation { get { return Mode == ChatModes.Agent && MutationPolicy == MutationPolicy.Ask; } }
        [JsonIgnore]
        public bool AutoConfirmsToolActions { get { return AllowsConfirmation && AutoConfirmToolActions; } }
        public static ConversationRunPolicy For(string mode, MutationPolicy mutationPolicy = MutationPolicy.Ask,
            bool autoConfirmToolActions = false)
        { return new ConversationRunPolicy(mode, mutationPolicy, autoConfirmToolActions); }
        public static ConversationRunPolicy ForSession(ChatSession session, bool autoConfirmToolActions = false)
        {
            RequireCurrentSession(session);
            return For(session.Mode, session.MutationPolicy, autoConfirmToolActions);
        }
        public static ConversationRunPolicy ForAcceptedRun(ChatSession session)
        {
            RequireCurrentSession(session);
            var accepted = session.LastRun?.ConversationPolicy;
            if (accepted == null) throw new InvalidOperationException("The accepted run has no recorded permissions. Submit a new turn.");
            ForSession(session, accepted.AutoConfirmToolActions).RequireSame(accepted);
            return accepted;
        }
        public static void RequireCurrentSession(ChatSession session)
        {
            if (session == null || session.FormatVersion != ChatSession.CurrentFormatVersion)
                throw new InvalidOperationException("This chat is read-only history. Create a new chat to continue.");
            For(session.Mode, session.MutationPolicy);
            if (string.Equals(Environment.GetEnvironmentVariable("RNA_DISABLE_UNIFIED_AGENT"), "1", StringComparison.Ordinal))
                throw new InvalidOperationException("Unified agent execution is disabled. History remains available.");
        }
        public void RequireSame(ConversationRunPolicy other)
        {
            if (other == null || Mode != other.Mode || MutationPolicy != other.MutationPolicy ||
                AutoConfirmToolActions != other.AutoConfirmToolActions)
                throw new InvalidOperationException("The accepted run permissions changed. Submit a new turn.");
        }
        public static bool IsPlanningArtifact(string id, ToolBinding binding)
        {
            return binding != null && id == TaskListToolCatalog.SetToolId &&
                binding.HandlerId == TaskListToolHandler.BindingFor(id).HandlerId && string.IsNullOrEmpty(binding.EntryPoint);
        }
        public static bool IsPlanSourceTool(string id, ToolBinding binding)
        {
            return (id == ResourceMutationToolCatalog.Write || id == ResourceMutationToolCatalog.Patch) &&
                binding != null && binding.HandlerId == ResourceMutationToolCatalog.BindingFor(id).HandlerId &&
                string.IsNullOrEmpty(binding.EntryPoint);
        }
        public bool Allows(string id, ToolPolicy policy, ToolBinding binding)
        {
            if (policy == null || policy.Effect == ToolEffect.Unclassified) return false;
            if (!policy.AllowedModes.Contains(Mode, StringComparer.Ordinal)) return false;
            return (!policy.MayHaveSideEffects && !policy.ExecutesCode && !policy.RequiresConfirmation) ||
                IsPlanningArtifact(id, binding) || (Mode == ChatModes.Plan && IsPlanSourceTool(id, binding)) || AllowsConfirmation;
        }
        public ToolPolicy EffectiveToolPolicy(string id, ToolPolicy policy, ToolBinding binding)
        {
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            var confirmation = !IsPlanningArtifact(id, binding) && (policy.RequiresConfirmation || policy.MayHaveSideEffects || policy.ExecutesCode);
            return new ToolPolicy(policy.Effect, policy.Verification, confirmation,
                policy.IndependentLocalRead && !confirmation, policy.AllowedModes, policy.RiskLevel, policy.ExecutesCode);
        }
        public List<ToolCatalogEntry> SelectTools(IEnumerable<ToolCatalogEntry> tools)
        {
            return (tools ?? new ToolCatalogEntry[0]).Where(tool => tool != null && tool.AgentCanRun &&
                Allows(tool.Id, tool.Policy, tool.Binding)).Select(tool =>
                {
                    var copy = tool.Clone();
                    copy.Policy = EffectiveToolPolicy(copy.Id, copy.Policy, copy.Binding);
                    copy.RequiresConfirmation = copy.Policy.RequiresConfirmation;
                    return copy;
                }).ToList();
        }
        public List<SkillDefinition> SelectSkills(IEnumerable<SkillDefinition> skills)
        { return (skills ?? new SkillDefinition[0]).Where(skill => skill != null && skill.Enabled).ToList(); }
    }
}
