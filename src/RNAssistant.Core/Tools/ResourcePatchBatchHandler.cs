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
        public int? Hunk { get; set; }
        public string Code { get; set; }
        public string OwnerCode { get; set; }
        public string Message { get; set; }
        public string NextAction { get; set; }
        public IReadOnlyList<PatchCandidate> Candidates { get; set; }
        public static PatchDiagnostic From(Exception error)
        {
            var pattern = error as TextPatternException;
            var code = pattern?.ErrorCode ?? (error as ResourceRequestException)?.ErrorCode ??
                (error as WorkspaceFileException)?.Code;
            var result = Create(code, error.Message);
            result.Hunk = pattern?.Hunk;
            result.Candidates = pattern?.Candidates;
            return result;
        }
        private sealed class OwnerFailure { public string Code { get; set; } }
        public static PatchDiagnostic From(ToolResult result)
        {
            string code = null;
            try { code = JsonConvert.DeserializeObject<OwnerFailure>(result.DataJson ?? "{}")?.Code; }
            catch (JsonException) { }
            return Create(code, result.Message);
        }
        public static PatchDiagnostic Create(string code, string message)
        {
            code = code?.ToLowerInvariant();
            var reason = code == "context_not_found" || code == "ambiguous_context" || code == "invalid_patch" || code == "overlap" ? code :
                code == "target_conflict" || code == "resource_revision_changed" || code == "source_changed" || code == "source_observation_required" ? "source_changed" : "owner_error";
            return new PatchDiagnostic { Code = reason, OwnerCode = reason == "owner_error" ? code : null,
                Message = Limit(message, 500), NextAction = reason == "source_changed" ? "read_target" :
                    reason == "ambiguous_context" || reason == "context_not_found" ? "refine_context" :
                    reason == "invalid_patch" || reason == "overlap" ? "correct_patch" : "inspect_owner_error" };
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
        { return From(edits.Select(e => new TextPatchEdit { Before = e.Before, After = e.After })); }
        public static List<PatchPreview> From(IEnumerable<TextPatchEdit> edits)
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
        public List<PatchPreview> Preview { get; set; }
    }
    [JsonObject(NamingStrategyType = typeof(Newtonsoft.Json.Serialization.CamelCaseNamingStrategy))]
    public sealed class PatchTargetResult
    {
        public string Target { get; set; }
        public ResourceTargetStatus Status { get; set; }
        public string CurrentTarget { get; set; }
        public bool WriteStarted { get; set; }
        public PatchDiagnostic Diagnostic { get; set; }
        public List<PatchPreview> Preview { get; set; }
    }
    [JsonObject(NamingStrategyType = typeof(Newtonsoft.Json.Serialization.CamelCaseNamingStrategy))]
    public sealed class PatchBatchResult
    {
        public string Type { get; set; } = "rnassistant.patch";
        public string Phase { get; set; }
        public bool WriteStarted { get; set; }
        public bool Partial { get; set; }
        public PatchDiagnostic Diagnostic { get; set; }
        public List<PatchTargetResult> Targets { get; set; } = new List<PatchTargetResult>();
    }
    public sealed class ResourcePatchSection
    {
        public string Target { get; internal set; }
        public string Body { get; internal set; }
    }
    public static class ResourcePatch
    {
        public const int MaximumTargets = 16;
        public const int MaximumPatchCharacters = 262144;
        public static IReadOnlyList<ResourcePatchSection> Parse(string patch)
        {
            if (patch?.Length > MaximumPatchCharacters) throw new TextPatternException("invalid_patch", "Patch exceeds 262144 characters; split related edits into smaller calls.");
            var sections = new List<ResourcePatchSection>();
            var body = new StringBuilder();
            ResourcePatchSection current = null;
            using (var reader = new StringReader(patch ?? ""))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.StartsWith("*** Update Target: ", StringComparison.Ordinal))
                    {
                        if (current != null) current.Body = body.ToString();
                        var target = line.Substring(19).Trim();
                        if (target.Length == 0 || target.Length > 512 || (target.StartsWith("rna:", StringComparison.OrdinalIgnoreCase) || target.Contains("://")))
                            throw new TextPatternException("invalid_patch", "Use a readable semantic target in *** Update Target:.");
                        current = new ResourcePatchSection { Target = target }; sections.Add(current); body.Clear();
                        if (sections.Count > MaximumTargets) throw new TextPatternException("invalid_patch", "Use at most 16 related targets per patch.");
                    }
                    else if (current == null)
                    {
                        if (!string.IsNullOrWhiteSpace(line)) throw new TextPatternException("invalid_patch", "patch must start with *** Update Target: <semantic target>.");
                    }
                    else body.Append(line).Append('\n');
                }
            }
            if (current == null) throw new TextPatternException("invalid_patch", "At least one *** Update Target: section is required.");
            current.Body = body.ToString();
            return sections.AsReadOnly();
        }
        public static ResourcePatchPreparedBatch ReadPreparation(string argumentsJson, string json)
        {
            var sections = Parse(Newtonsoft.Json.Linq.JObject.Parse(argumentsJson).Value<string>("patch"));
            var prepared = JsonConvert.DeserializeObject<ResourcePatchPreparedBatch>(json ?? "null");
            if (prepared?.Version != 1 ||
                prepared.ArgumentsHash != TextPatternEngine.Sha256(argumentsJson) || prepared.Targets == null ||
                prepared.Targets.Count != sections.Count || prepared.Targets.Where((t, i) => t?.Target != sections[i].Target || t.Plan?.Source?.IsExact != true).Any())
                throw new TextPatternException("invalid_patch", "Missing or incompatible prepared package; read sources and prepare a new call.");
            return prepared;
        }
        public static void ValidateCall(string argumentsJson)
        {
            var schema = ResourceMutationToolCatalog.GetTools().Single(t => t.Id == ResourceMutationToolCatalog.Patch).ArgumentSchemaJson;
            var args = Newtonsoft.Json.Linq.JObject.Parse(argumentsJson);
            string error;
            if (!ToolSchemaSupport.ValidateArguments(args, Newtonsoft.Json.Linq.JObject.Parse(schema), false, out error))
                throw new TextPatternException("invalid_patch", "Stored core.patch uses an incompatible contract. Start a new chat; stored history is preserved.");
            if (HasUriLikeTarget((string)args["patch"]))
                throw new TextPatternException("invalid_patch", "Stored core.patch contains a URI target instead of a semantic target.");
        }
        // A rejected patch body remains valid history. Runtime preparation owns
        // syntax checks before dispatch; history checks only model-facing shape.
        public static bool HasUriLikeTarget(string patch)
        {
            using (var reader = new StringReader(patch ?? string.Empty))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (!line.StartsWith("*** Update Target: ", StringComparison.Ordinal)) continue;
                    var target = line.Substring(19).Trim();
                    if (target.StartsWith("rna:", StringComparison.OrdinalIgnoreCase) || target.Contains("://"))
                        return true;
                }
            }
            return false;
        }
        internal static IDictionary<string, object> Arguments(ResourcePatchSection section)
        { return new Dictionary<string, object> { ["target"] = section.Target, ["patch"] = section.Body }; }
    }

    public sealed class ResourcePatchPreparedTarget
    {
        public string Target { get; set; }
        public string State { get; set; }
        public PatchTargetPlan Plan { get; set; }
    }
    public sealed class ResourcePatchPreparedBatch
    {
        [JsonProperty(Required = Required.Always)] public int Version { get; set; } = 1;
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
        { object value; return ResourcePatch.Parse(context.Arguments.TryGetValue("patch", out value) ? Convert.ToString(value) : null); }
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
            { return new ToolPreparationResult(ToolResult.Error(ex.Message, JsonConvert.SerializeObject(new PatchBatchResult { Phase = "preparation", Diagnostic = PatchDiagnostic.From(ex) }))); }
            var result = Result(sections, "preparation");
            var prepared = new ResourcePatchPreparedBatch { ArgumentsHash = TextPatternEngine.Sha256(context.Execution.Call.ArgumentsJson) };
            var identities = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < sections.Count; i++)
            {
                ToolPreparationResult next;
                try
                {
                    TextPatch.Parse(sections[i].Body);
                    next = await TargetHandler.PrepareAsync(TargetContext(context, sections[i], i), token).ConfigureAwait(false);
                    if (next.Result.Status != ToolResultStatus.Ok) return RejectPreparation(result, i, next.PatchDiagnostic ?? PatchDiagnostic.From(next.Result), next.Recovery);
                    if (next.PatchTarget?.Source?.IsExact != true) throw new InvalidOperationException("Owner did not pin a patch source.");
                    if (!identities.Add((next.PatchTarget.EffectIdentity ?? next.PatchTarget.Source.Identity).Uri))
                        return RejectPreparation(result, i, PatchDiagnostic.Create("invalid_patch", "Two target sections resolve to the same resource; combine their hunks."));
                }
                catch (Exception ex) { return RejectPreparation(result, i, PatchDiagnostic.From(ex)); }
                prepared.Targets.Add(new ResourcePatchPreparedTarget { Target = sections[i].Target, State = next.PreparedStateJson, Plan = next.PatchTarget });
                result.Targets[i].Status = ResourceTargetStatus.Prepared;
                result.Targets[i].Preview = next.PatchTarget.Preview;
            }
            var state = JsonConvert.SerializeObject(prepared);
            var preview = JsonConvert.SerializeObject(result);
            if (state.Length > ToolPreparationResult.MaxPreparedStateChars || preview.Length > ToolPreparationResult.MaxConfirmationDataChars)
                return RejectPreparation(result, 0, PatchDiagnostic.Create("invalid_patch", "Prepared package exceeds its bounded preview/state budget; split the task into smaller calls."));
            return new ToolPreparationResult(ToolResult.Ok("All targets prepared. Approve this package; writes are sequential, not transactional.", preview), state);
        }
        private static ToolPreparationResult RejectPreparation(PatchBatchResult result, int index, PatchDiagnostic diagnostic, ToolRecoveryContract recovery = null)
        {
            foreach (var target in result.Targets) { target.Status = ResourceTargetStatus.NotStarted; target.Preview = null; }
            result.Targets[index].Status = ResourceTargetStatus.Rejected; result.Targets[index].Diagnostic = diagnostic;
            return new ToolPreparationResult(ToolResult.Error("Patch rejected before any write.", JsonConvert.SerializeObject(result)),
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
                var rejection = RejectPreparation(result, 0, PatchDiagnostic.Create("invalid_patch", ex.Message));
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
                    if (status == ResourceTargetStatus.Changed && effects[i].After == null && completed.Result != null)
                    {
                        var provider = ResourceUri.Parse(source.Uri).Provider;
                        var candidates = completed.Result.Resources.Where(r => ResourceUri.Parse(r.Uri).Provider == provider).Distinct().ToArray();
                        if (candidates.Length == 1) effects[i].After = candidates[0];
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
                            ? new PatchDiagnostic { Code = "effect_unknown", Message = "Write may have happened. Reconcile this target; do not replay the package.", NextAction = "verify_unknown_effect" }
                            : PatchDiagnostic.From(completed.Result ?? ToolResult.Error(completed.Message));
                        break;
                    }
                }
                catch (Exception ex)
                {
                    row.Status = effects[i].Status = ResourceTargetStatus.Unknown;
                    row.WriteStarted = effects[i].WriteStarted = true;
                    context.MarkDispatchPossible();
                    row.Diagnostic = new PatchDiagnostic { Code = "effect_unknown", Message = PatchDiagnostic.Limit(ex.Message, 500), NextAction = "verify_unknown_effect" };
                    break;
                }
            }
            var unknown = result.Targets.Any(t => t.Status == ResourceTargetStatus.Unknown);
            var changed = result.Targets.Any(t => t.Status == ResourceTargetStatus.Changed);
            var complete = result.Targets.All(t => t.Status == ResourceTargetStatus.Changed || t.Status == ResourceTargetStatus.Unchanged);
            result.WriteStarted = effects.Any(e => e.WriteStarted);
            result.Partial = !complete && changed;
            var json = JsonConvert.SerializeObject(result);
            var message = complete ? "Every patch target was verified." : "Patch stopped. Completed targets are retained; remaining targets were not executed. Reconcile before another call.";
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
