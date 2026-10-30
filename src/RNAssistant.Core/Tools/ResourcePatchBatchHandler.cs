using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools.Contracts;

namespace RNAssistant.Core.Tools
{
    [JsonObject(NamingStrategyType = typeof(Newtonsoft.Json.Serialization.CamelCaseNamingStrategy))]
    public sealed class PatchCandidate
    {
        public long Line { get; set; }
        public string Context { get; set; }
    }
    [JsonObject(NamingStrategyType = typeof(Newtonsoft.Json.Serialization.CamelCaseNamingStrategy))]
    public sealed class PatchDiagnostic
    {
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public int? Edit { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public int? TargetIndex { get; set; }
        public string Code { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string OwnerCode { get; set; }
        public string Message { get; set; }
        public string NextAction { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public IReadOnlyList<PatchCandidate> Candidates { get; set; }
        public static PatchDiagnostic From(Exception error)
        {
            var pattern = error as TextPatternException;
            var code = pattern?.ErrorCode ?? (error as ResourceRequestException)?.ErrorCode ??
                (error as WorkspaceFileException)?.Code;
            var result = Create(code, error.Message);
            result.Edit = pattern?.Hunk;
            result.TargetIndex = pattern?.TargetIndex;
            result.Candidates = pattern?.Candidates;
            return result.Bounded();
        }
        private sealed class OwnerFailure { public string Code { get; set; } }
        public static PatchDiagnostic From(ToolResult result, ToolRecoveryContract recovery = null)
        {
            string code = null;
            try { code = JsonConvert.DeserializeObject<OwnerFailure>(result.DataJson ?? "{}")?.Code; }
            catch (JsonException) { }
            return Create(code, result.Message, recovery);
        }
        public static PatchDiagnostic Create(string code, string message, ToolRecoveryContract recovery = null)
        {
            var key = code?.ToLowerInvariant();
            var reason = key == "context_not_found" || key == "ambiguous_context" || key == "invalid_edit" || key == "overlap" ? key :
                key == "source_observation_required" || key == "observation_required" ? "observation_required" :
                key == "target_conflict" || key == "resource_revision_changed" || key == "source_changed" ? "source_changed" : "owner_error";
            return new PatchDiagnostic { Code = reason, OwnerCode = reason == "owner_error" ? code : null,
                Message = message, NextAction = reason == "source_changed" || reason == "observation_required" || reason == "context_not_found" ? "read_target" :
                    reason == "ambiguous_context" ? "refine_context" : reason == "invalid_edit" || reason == "overlap" ? "fix_patch" :
                    recovery?.RetryPolicy == ToolRetryPolicy.RefreshRequired ? "read_target" :
                    recovery?.RetryPolicy == ToolRetryPolicy.Replan || recovery?.RetryPolicy == ToolRetryPolicy.ChangeRequired || key == "vba_code_invalid" ? "fix_patch" : "inspect_owner_error" }.Bounded();
        }
        public static PatchDiagnostic Unknown(ToolResult result = null)
        {
            var diagnostic = From(result ?? ToolResult.Unknown("Write may have occurred; read-back unconfirmed."));
            diagnostic.Code = "owner_error";
            diagnostic.Message = "Write may have occurred; read-back unconfirmed. Reconcile this target before another write.";
            diagnostic.NextAction = "verify_effect";
            return diagnostic;
        }
        public PatchDiagnostic Bounded()
        {
            var message = ModelResultProjection.SanitizeOperationalText(Message ?? "");
            var newline = message.IndexOfAny(new[] { '\r', '\n' });
            if (newline >= 0) message = message.Substring(0, newline);
            return new PatchDiagnostic { Edit = Edit, TargetIndex = TargetIndex, Code = Limit(Code, 80), OwnerCode = Limit(OwnerCode, 100),
                Message = Limit(message, 500), NextAction = Limit(NextAction, 80),
                Candidates = Candidates?.Where(c => c != null && c.Line > 0).Take(5)
                    .Select(c => new PatchCandidate { Line = c.Line, Context = Limit(c.Context, 160) }).ToArray() };
        }
        internal static string Limit(string value, int limit)
        {
            if (value == null || value.Length <= limit) return value;
            if (limit > 0 && char.IsHighSurrogate(value[limit - 1]) && char.IsLowSurrogate(value[limit])) limit--;
            return value.Substring(0, limit) + "…";
        }
    }
    [JsonObject(NamingStrategyType = typeof(Newtonsoft.Json.Serialization.CamelCaseNamingStrategy))]
    public sealed class PatchPreview
    {
        public string Before { get; set; }
        public string After { get; set; }
        public bool Truncated { get; set; }
        public static List<PatchPreview> From(IEnumerable<ResolvedTextEdit> edits)
        {
            var result = new List<PatchPreview>();
            var remaining = 1200;
            foreach (var edit in edits)
            {
                if (remaining <= 0) { result.Add(new PatchPreview { Truncated = true }); break; }
                var limit = Math.Min(300, remaining / 2);
                result.Add(new PatchPreview { Before = PatchDiagnostic.Limit(edit.Before, limit), After = PatchDiagnostic.Limit(edit.After, limit),
                    Truncated = edit.Before.Length > limit || edit.After.Length > limit });
                remaining -= Math.Min(edit.Before.Length, limit) + Math.Min(edit.After.Length, limit) + 100;
            }
            return result;
        }
    }
    [JsonObject(NamingStrategyType = typeof(Newtonsoft.Json.Serialization.CamelCaseNamingStrategy))]
    public sealed class PatchTargetPlan
    {
        public ResourceRef Source { get; set; }
        public string SourceHash { get; set; }
        public ResourceIdentity EffectIdentity { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public List<PatchPreview> Preview { get; set; }
    }
    [JsonObject(NamingStrategyType = typeof(Newtonsoft.Json.Serialization.CamelCaseNamingStrategy))]
    public sealed class PatchTargetResult
    {
        public string Target { get; set; }
        public ResourceTargetStatus Status { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string CurrentTarget { get; set; }
        public bool WriteStarted { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public PatchDiagnostic Diagnostic { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public List<PatchPreview> Preview { get; set; }
    }
    [JsonObject(NamingStrategyType = typeof(Newtonsoft.Json.Serialization.CamelCaseNamingStrategy))]
    public sealed class PatchBatchResult
    {
        public string Type { get; set; } = "rnassistant.patch";
        public string Phase { get; set; }
        public bool WriteStarted { get; set; }
        public bool Partial { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public PatchDiagnostic Diagnostic { get; set; }
        public List<PatchTargetResult> Targets { get; set; } = new List<PatchTargetResult>();
    }
    [JsonObject(MemberSerialization.OptIn)]
    public sealed class ResourcePatchSection
    {
        [JsonProperty("target", Required = Required.Always)] public string Target { get; set; }
        [JsonProperty("edits", Required = Required.Always)] public List<TextEditIntent> Edits { get; set; }
        public TextPatch Body => new TextPatch(Edits);
    }
    [JsonObject(MemberSerialization.OptIn)]
    public sealed class ResourcePatchRequest
    {
        [JsonProperty("targets", Required = Required.Always)] public List<ResourcePatchSection> Targets { get; set; }
    }
    public static class ResourcePatch
    {
        public const int MaximumTargets = 16;
        public const int MaximumPatchCharacters = 262144;
        public static IReadOnlyList<ResourcePatchSection> Parse(string argumentsJson)
        {
            try
            {
                if (argumentsJson == null || argumentsJson.Length > MaximumPatchCharacters)
                    throw new TextPatternException("invalid_edit", "Patch arguments exceed 262144 characters or are absent.");
                var args = Newtonsoft.Json.Linq.JObject.Parse(argumentsJson);
                var schema = ResourceMutationToolCatalog.GetTools().Single(t => t.Id == ResourceMutationToolCatalog.Patch).ArgumentSchemaJson;
                string schemaError;
                if (!ToolSchemaSupport.ValidateArguments(args, Newtonsoft.Json.Linq.JObject.Parse(schema), false, out schemaError))
                    throw new TextPatternException("invalid_edit", "Invalid core.patch: " + PatchDiagnostic.Limit(schemaError, 200));
                var request = JsonConvert.DeserializeObject<ResourcePatchRequest>(argumentsJson,
                    new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error });
                if (request?.Targets == null || request.Targets.Count == 0 || request.Targets.Count > MaximumTargets)
                    throw new TextPatternException("invalid_edit", "Use 1 to 16 targets.");
                foreach (var section in request.Targets)
                {
                    if (section == null || string.IsNullOrWhiteSpace(section.Target) || section.Target.Length > 512 || IsUri(section.Target))
                        throw new TextPatternException("invalid_edit", "Use an exact readable semantic target.");
                    try { var validated = section.Body; }
                    catch (TextPatternException error) { error.TargetIndex = request.Targets.IndexOf(section) + 1; throw; }
                }
                return request.Targets.AsReadOnly();
            }
            catch (JsonException) { throw new TextPatternException("invalid_edit", "Invalid patch JSON; use targets with old/new edits."); }
        }
        public static ResourcePatchPreparedBatch ReadPreparation(string argumentsJson, string json)
        {
            var sections = Parse(argumentsJson);
            var prepared = JsonConvert.DeserializeObject<ResourcePatchPreparedBatch>(json ?? "null");
            if (prepared?.Version != 3 || prepared.ArgumentsHash != TextPatternEngine.Sha256(argumentsJson) || prepared.Targets == null ||
                prepared.Targets.Count != sections.Count || prepared.Targets.Where((t, i) => t?.Target != sections[i].Target || t.Plan?.Source?.IsExact != true).Any())
                throw new TextPatternException("invalid_edit", "Missing or incompatible prepared package; read sources and prepare a new call. History is retained.");
            return prepared;
        }
        public static void ValidateCall(string argumentsJson)
        {
            Parse(argumentsJson);
        }
        private static bool IsUri(string target) { return target.TrimStart().StartsWith("rna:", StringComparison.OrdinalIgnoreCase) || target.Contains("://"); }
        public static bool HasUriLikeTarget(string argumentsJson)
        {
            var request = JsonConvert.DeserializeObject<ResourcePatchRequest>(argumentsJson);
            return request?.Targets?.Any(t => t?.Target != null && IsUri(t.Target)) == true;
        }
        internal static IDictionary<string, object> Arguments(ResourcePatchSection section)
        { return new Dictionary<string, object> { ["target"] = section.Target, ["edits"] = section.Body }; }
    }

    public sealed class ResourcePatchPreparedTarget
    {
        public string Target { get; set; }
        public string State { get; set; }
        public PatchTargetPlan Plan { get; set; }
    }
    public sealed class ResourcePatchPreparedBatch
    {
        [JsonProperty(Required = Required.Always)] public int Version { get; set; } = 3;
        public string ArgumentsHash { get; set; }
        public List<ResourcePatchPreparedTarget> Targets { get; set; } = new List<ResourcePatchPreparedTarget>();
    }

    // Coordinates one public patch, while each existing owner keeps its journal,
    // authority publication and immediately-before-write guard under ToolRuntime.
    public sealed class ResourcePatchBatchHandler : IManagedMutationToolHandler, IPreparableToolHandler
    {
        public IPreparableToolHandler TargetHandler { get; private set; }
        public ResourcePatchBatchHandler(IPreparableToolHandler targetHandler) { TargetHandler = targetHandler; }
        private sealed class OwnerTarget { public string Target { get; set; } }
        private static IReadOnlyList<ResourcePatchSection> Sections(ToolHandlerContext context)
        { return ResourcePatch.Parse(context.Execution.Call.ArgumentsJson); }
        private static ToolHandlerContext TargetContext(ToolHandlerContext parent, ResourcePatchSection section, int index)
        {
            var e = parent.Execution;
            var scoped = new ToolExecutionContext(e.Call, e.Policy, e.RunId, e.TurnId, e.StepId,
                e.StartedUtc, e.IsConfirmed, e.RemainingToolSteps, targetOrdinal: index);
            return new ToolHandlerContext(scoped, ResourcePatch.Arguments(section));
        }
        private static PatchBatchResult Result(IReadOnlyList<ResourcePatchSection> sections, string phase)
        { return new PatchBatchResult { Phase = phase, Targets = sections.Select(s => new PatchTargetResult { Target = s.Target, Status = ResourceTargetStatus.NotStarted }).ToList() }; }
        public async Task<ToolPreparationResult> PrepareAsync(ToolHandlerContext context, CancellationToken token)
        {
            IReadOnlyList<ResourcePatchSection> sections;
            try { sections = Sections(context); }
            catch (Exception ex) when (ex is TextPatternException || ex is ArgumentException)
            { return InvalidArguments(PatchDiagnostic.From(ex)); }
            var result = Result(sections, "preparation");
            var prepared = new ResourcePatchPreparedBatch { ArgumentsHash = TextPatternEngine.Sha256(context.Execution.Call.ArgumentsJson) };
            var identities = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < sections.Count; i++)
            {
                ToolPreparationResult next;
                try
                {
                    next = await TargetHandler.PrepareAsync(TargetContext(context, sections[i], i), token).ConfigureAwait(false);
                    if (next.Result.Status != ToolResultStatus.Ok) return RejectPreparation(result, i, next.PatchDiagnostic ?? PatchDiagnostic.From(next.Result, next.Recovery), next.Recovery);
                    if (next.PatchTarget?.Source?.IsExact != true) throw new InvalidOperationException("Owner did not pin a patch source.");
                    if (!identities.Add((next.PatchTarget.EffectIdentity ?? next.PatchTarget.Source.Identity).Uri))
                        return RejectPreparation(result, i, PatchDiagnostic.Create("invalid_edit", "Two target sections resolve to the same resource; combine their edits."));
                }
                catch (Exception ex) { return RejectPreparation(result, i, PatchDiagnostic.From(ex)); }
                prepared.Targets.Add(new ResourcePatchPreparedTarget { Target = sections[i].Target, State = next.PreparedStateJson, Plan = next.PatchTarget });
                result.Targets[i].Status = ResourceTargetStatus.Prepared;
                result.Targets[i].Preview = next.PatchTarget.Preview;
            }
            var state = JsonConvert.SerializeObject(prepared);
            var preview = JsonConvert.SerializeObject(result);
            if (state.Length > ToolPreparationResult.MaxPreparedStateChars || preview.Length > ToolPreparationResult.MaxConfirmationDataChars)
                return RejectPreparation(result, 0, PatchDiagnostic.Create("invalid_edit", "Prepared package exceeds its bounded preview/state budget; split the task into smaller calls."));
            return new ToolPreparationResult(ToolResult.Ok("All targets prepared. Approve this package; writes are sequential, not transactional.", preview), state);
        }
        internal static ToolPreparationResult InvalidArguments(PatchDiagnostic diagnostic)
        {
            diagnostic = diagnostic.Bounded();
            return new ToolPreparationResult(ToolResult.Error("Patch rejected before writing. " + diagnostic.Message,
                JsonConvert.SerializeObject(new PatchBatchResult { Phase = "preparation", Diagnostic = diagnostic })),
                recovery: new ToolRecoveryContract(ToolFailureKind.RejectedNoEffect, ToolRetryPolicy.Replan));
        }
        private static ToolPreparationResult RejectPreparation(PatchBatchResult result, int index, PatchDiagnostic diagnostic, ToolRecoveryContract recovery = null)
        {
            foreach (var target in result.Targets) { target.Status = ResourceTargetStatus.NotStarted; target.Preview = null; }
            diagnostic = diagnostic?.Bounded();
            if (diagnostic != null) diagnostic.TargetIndex = index + 1;
            result.Targets[index].Status = ResourceTargetStatus.Rejected; result.Targets[index].Diagnostic = diagnostic;
            return new ToolPreparationResult(ToolResult.Error(
                "Patch rejected. No target in this call changed; follow the rejected target diagnostic.",
                JsonConvert.SerializeObject(result)),
                recovery: recovery ?? new ToolRecoveryContract(ToolFailureKind.RejectedNoEffect, ToolRetryPolicy.Replan));
        }
        public async Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context, CancellationToken token)
        {
            var sections = Sections(context);
            var result = Result(sections, "execution");
            ResourcePatchPreparedBatch prepared;
            try { prepared = ResourcePatch.ReadPreparation(context.Execution.Call.ArgumentsJson, context.PreparedStateJson); }
            catch (Exception ex) when (ex is JsonException || ex is TextPatternException)
            {
                var rejection = RejectPreparation(result, 0, PatchDiagnostic.Create("invalid_edit", ex.Message));
                return new ToolHandlerResult(rejection.Result, ToolEffectEvidence.None, recovery: rejection.Recovery);
            }
            // Validate the entire approved package again before starting any target.
            for (var i = 0; i < sections.Count; i++)
            {
                try
                {
                    var current = await TargetHandler.PrepareAsync(TargetContext(context, sections[i], i), token).ConfigureAwait(false);
                    var expected = prepared.Targets[i].Plan;
                    if (current.Result.Status != ToolResultStatus.Ok || current.PatchTarget?.Source?.Uri != expected.Source.Uri ||
                        current.PatchTarget.Source.Revision != expected.Source.Revision || current.PatchTarget.SourceHash != expected.SourceHash)
                    {
                        var rejected = RejectPreparation(result, i, current.PatchDiagnostic ?? PatchDiagnostic.Create("source_changed", "Prepared target is no longer current; read this target and rebuild the package."), current.Recovery);
                        return new ToolHandlerResult(rejected.Result, ToolEffectEvidence.None, recovery: rejected.Recovery);
                    }
                }
                catch (Exception ex)
                {
                    var rejected = RejectPreparation(result, i, PatchDiagnostic.From(ex));
                    return new ToolHandlerResult(rejected.Result, ToolEffectEvidence.None, recovery: rejected.Recovery);
                }
            }
            var effects = prepared.Targets.Select(t => new ResourceTargetEffect { Target = t.Target, Source = t.Plan.Source, Status = ResourceTargetStatus.NotStarted }).ToList();
            var evidence = new List<ResourceEvidence>();
            var resources = new List<ResourceRef>();
            for (var i = 0; i < sections.Count; i++)
            {
                if (token.IsCancellationRequested) { result.Targets[i].Diagnostic = PatchDiagnostic.Create("cancelled", "Stopped before this target; reconcile completed targets before another call."); break; }
                var row = result.Targets[i];
                try
                {
                    var completed = await context.ExecuteTargetAsync(TargetHandler, ResourcePatch.Arguments(sections[i]), prepared.Targets[i].State, i, token).ConfigureAwait(false);
                    var status = completed.Outcome == ToolExecutionOutcome.Unknown ? ResourceTargetStatus.Unknown :
                        completed.Outcome != ToolExecutionOutcome.Ok ? ResourceTargetStatus.Rejected :
                        completed.Evidence.Effect == ToolEffectEvidence.VerifiedChange ? ResourceTargetStatus.Changed : ResourceTargetStatus.Unchanged;
                    row.Status = effects[i].Status = status;
                    row.WriteStarted = effects[i].WriteStarted = completed.MayHaveDispatched;
                    effects[i].Effect = completed.ResourceEffect;
                    var source = prepared.Targets[i].Plan.Source;
                    effects[i].After = status == ResourceTargetStatus.Unchanged ? source :
                        completed.ResourceEffect?.Impacts.FirstOrDefault(impact => impact.Relation == ResourceImpactRelation.Exact && impact.Identity.Equals(prepared.Targets[i].Plan.EffectIdentity ?? source.Identity))?.After;
                    if (status == ResourceTargetStatus.Changed && effects[i].After == null && ResourceUri.Parse(source.Uri).Provider == "catalog")
                    {
                        var root = new ResourceIdentity(ResourceUri.Create("catalog", ResourceUri.Parse(source.Uri).Segments[0]));
                        var published = completed.ResourceEffect?.Impacts.FirstOrDefault(impact => impact.Identity.Equals(root))?.After;
                        if (published != null) effects[i].After = new ResourceRef(source.Uri, published.Revision);
                    }
                    // A provider-level result reference can belong to a sibling or
                    // backup. Only the exact target's published after-state proves a
                    // changed patch and can become the next model source.
                    if (status == ResourceTargetStatus.Changed &&
                        (effects[i].After?.IsExact != true ||
                         effects[i].After.Identity.Equals(prepared.Targets[i].Plan.EffectIdentity ?? source.Identity) == false &&
                         effects[i].After.Identity.Equals(source.Identity) == false))
                    {
                        status = row.Status = effects[i].Status = ResourceTargetStatus.Unknown;
                        effects[i].After = null;
                    }
                    if (status == ResourceTargetStatus.Changed || status == ResourceTargetStatus.Unchanged)
                    {
                        try
                        {
                            var currentTarget = JsonConvert.DeserializeObject<OwnerTarget>(completed.Result?.DataJson ?? "{}")?.Target;
                            if (!string.IsNullOrWhiteSpace(currentTarget) && currentTarget.Length <= 512 && !currentTarget.Contains("://")) row.CurrentTarget = currentTarget;
                        }
                        catch (JsonException) { }
                    }
                    evidence.AddRange(completed.ResourceEvidence);
                    if (completed.Result != null) resources.AddRange(completed.Result.Resources);
                    if (status == ResourceTargetStatus.Rejected || status == ResourceTargetStatus.Unknown)
                    {
                        row.Diagnostic = status == ResourceTargetStatus.Unknown
                            ? PatchDiagnostic.Unknown(completed.Result)
                            : PatchDiagnostic.From(completed.Result ?? ToolResult.Error(completed.Message), completed.Recovery);
                        break;
                    }
                }
                catch (Exception ex)
                {
                    var dispatched = context.TargetMayHaveDispatched(i);
                    row.Status = effects[i].Status = dispatched ? ResourceTargetStatus.Unknown : ResourceTargetStatus.Rejected;
                    row.WriteStarted = effects[i].WriteStarted = dispatched;
                    row.Diagnostic = dispatched ? PatchDiagnostic.Unknown() : PatchDiagnostic.From(ex);
                    break;
                }
            }
            var unknown = result.Targets.Any(t => t.Status == ResourceTargetStatus.Unknown);
            var changed = result.Targets.Any(t => t.Status == ResourceTargetStatus.Changed);
            var complete = result.Targets.All(t => t.Status == ResourceTargetStatus.Changed || t.Status == ResourceTargetStatus.Unchanged);
            result.WriteStarted = effects.Any(e => e.WriteStarted);
            result.Partial = !complete && changed;
            var json = JsonConvert.SerializeObject(result);
            var message = complete ? "Every patch target was verified." :
                "Patch stopped. Changed/Unchanged are verified; Rejected/NotStarted were not applied; reconcile Unknown before another write. Do not replay the package.";
            var toolResult = unknown ? ToolResult.Unknown(message, json, resources) : complete ? ToolResult.Ok(message, json, resources) : ToolResult.Error(message, json, resources);
            var effect = new ResourceEffect("patch_" + Guid.NewGuid().ToString("N"), ResourceMutationToolCatalog.Patch,
                unknown ? ResourceEffectOutcome.UnknownAfterDispatch : changed ? ResourceEffectOutcome.VerifiedChanged : complete ? ResourceEffectOutcome.VerifiedNoChange : ResourceEffectOutcome.FailedNoEffect,
                effects.Where(e => e.Effect != null).SelectMany(e => e.Effect.Impacts), targets: effects);
            return new ToolHandlerResult(toolResult, unknown ? ToolEffectEvidence.Unknown : changed ? ToolEffectEvidence.VerifiedChange : complete ? ToolEffectEvidence.VerifiedNoChange : ToolEffectEvidence.None,
                resourceEvidence: evidence, resourceEffect: effect,
                recovery: complete || unknown || changed ? null : new ToolRecoveryContract(ToolFailureKind.RejectedNoEffect, ToolRetryPolicy.Replan));
        }
    }
}
