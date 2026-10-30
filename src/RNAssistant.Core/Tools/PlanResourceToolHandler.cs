using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools.Contracts;

namespace RNAssistant.Core.Tools
{
    public sealed class PlanResourcePrepared
    {
        public int Version { get; set; } = 1;
        public string ToolId { get; set; }
        public string Target { get; set; }
        public string ArgumentsHash { get; set; }
        public string SourceHash { get; set; }
        public PlanResourceCommand Command { get; set; }
        public string Operation { get; set; }
        public static PlanResourcePrepared Read(string json)
        {
            var value = JsonConvert.DeserializeObject<PlanResourcePrepared>(json ?? "null");
            if (value?.Version != 1 || value.Command == null || !PlanDocumentToolCatalog.Owns(value.Operation))
                throw new InvalidDataException("Missing or incompatible Plan preparation.");
            return value;
        }
    }

    // Uses the existing Plan mutation/publication owners. Composition retains its gate and observer.
    public interface IPlanSourceToolHandler : IManagedMutationToolHandler { }

    public sealed class PlanResourceToolHandler : IPreparableToolHandler, IPlanSourceToolHandler
    {
        private readonly ChatSession _session;
        private readonly DocumentArtifactStore _owner;
        private readonly Func<string, ChatArtifact> _resolve;
        private readonly Action<ToolHandlerContext, ResourceReadResult, bool> _observe;

        public PlanResourceToolHandler(ChatSession session, DocumentArtifactStore owner,
            Func<string, ChatArtifact> resolve, Action<ToolHandlerContext, ResourceReadResult, bool> observe)
        { _session = session; _owner = owner; _resolve = resolve; _observe = observe; }

        public static ChatArtifact ResolveCurrent(DocumentArtifactStore owner, ChatSession session, string target)
        {
            if (!PlanSourceDocument.IsTarget(target)) throw new InvalidDataException("Use plan: followed by a unique title.");
            ChatArtifact match = null;
            int? offset = 0; long? generation = null;
            do
            {
                var page = owner.InspectCurrentMetadata(session, offset.Value, 100);
                if (page.UnavailableResources > 0 || generation.HasValue && generation.Value != page.Generation || offset.Value >= 4000)
                    throw new ResourceRequestException("Plan discovery is incomplete or changed. Read the current Plan before editing.", "source_changed", false);
                generation = page.Generation;
                foreach (var item in page.Items.Where(item => item.Kind == ChatArtifactKinds.PlanDocument &&
                    !PlanDocumentService.IsTombstone(item) && item.Title == target.Substring(6)))
                {
                    if (match != null) throw new ResourceRequestException("The Plan title is ambiguous; give plans distinct titles in the library.", "plan_target_ambiguous", false);
                    match = item;
                }
                offset = page.NextOffset;
            } while (offset.HasValue);
            return match;
        }

        public static string Operation(string toolId)
        {
            if (toolId == ResourceMutationToolCatalog.Write || toolId == ResourceMutationToolCatalog.Patch) return PlanDocumentToolCatalog.SaveToolId;
            if (toolId == ResourceMutationToolCatalog.Delete) return PlanDocumentToolCatalog.DeleteToolId;
            if (toolId == ResourceMutationToolCatalog.Restore) return PlanDocumentToolCatalog.RestoreToolId;
            throw new InvalidDataException("Plans support core.write/patch and resources.delete/restore.");
        }

        public Task<ToolPreparationResult> PrepareAsync(ToolHandlerContext context, CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var target = Value(context.Arguments, "target");
                if (!PlanSourceDocument.IsTarget(target)) throw new InvalidDataException("An exact plan: target is required.");
                var operation = Operation(context.Execution.Call.Name);
                var current = _resolve(target);
                if (context.Execution.ExpectedArtifactId != null && current?.Id != context.Execution.ExpectedArtifactId)
                    throw new ResourceRequestException("The Plan editor shows an older version. Reopen the current Plan before saving.", "source_changed", false);
                if (current == null && context.Execution.Call.Name != ResourceMutationToolCatalog.Write)
                    throw new ResourceRequestException("The Plan does not exist. Read an existing Plan target.", "resource_not_found", false);
                if (current != null && context.Execution.ExpectedTargetAbsent)
                    throw new InvalidDataException("The requested new Plan already exists.");
                var command = new PlanResourceCommand();
                string before = null;
                if (current != null)
                {
                    if (current.Kind != ChatArtifactKinds.PlanDocument || PlanDocumentService.IsTombstone(current))
                        throw new InvalidDataException("An editable current Plan is required.");
                    command.Source = ChatResourceUri.CreateArtifactRevision(_session, current);
                    command.PlanId = PlanDocumentService.PlanId(current);
                    command.ArtifactId = current.Id;
                    RequireHead(command);
                    current = _owner.Read(_session, command.Source);
                    before = PlanSourceDocument.Serialize(current);
                    if (context.Execution.ExpectedContentSha256 != null &&
                        context.Execution.ExpectedContentSha256 != TextPatternEngine.Sha256(before))
                        throw new ResourceRequestException("The Plan source changed.", "source_changed", false);
                    if (context.Execution.Call.Name != ResourceMutationToolCatalog.Restore)
                        _observe(context, Source(current, before), context.Execution.Call.Name == ResourceMutationToolCatalog.Write);
                }
                TextEditPlan edits = null;
                if (operation == PlanDocumentToolCatalog.SaveToolId)
                {
                    var content = Value(context.Arguments, "content");
                    if (context.Execution.Call.Name == ResourceMutationToolCatalog.Patch)
                    { edits = TextEditPlan.Patch(before, TextPatch.FromArguments(context.Arguments), token); content = edits.Text; }
                    command.Content = PlanSourceDocument.Parse(content);
                    var sameTitle = _resolve(PlanSourceDocument.Target(command.Content.Title));
                    if (sameTitle != null && PlanDocumentService.PlanId(sameTitle) != command.PlanId)
                        throw new InvalidDataException("Another Plan already uses this title.");
                    if (current == null && target.Substring(6) != command.Content.Title)
                        throw new InvalidDataException("A new Plan target must match its source title. Use a distinct title to create a separate Plan.");
                    if (Value(context.Arguments, "resourceType") != "" && Value(context.Arguments, "resourceType") != "text")
                        throw new InvalidDataException("A Plan does not accept a VBA creation kind.");
                }
                else if (operation == PlanDocumentToolCatalog.RestoreToolId)
                {
                    object version;
                    if (!context.Arguments.TryGetValue("version", out version) || version == null || Convert.ToInt32(version) < 1)
                        throw new InvalidDataException("Select a retained Plan version.");
                    command.RestoreVersion = Convert.ToInt32(version);
                    var restored = _owner.PlanHistory(_session, command.PlanId, false)
                        .SingleOrDefault(item => item.Revision == command.RestoreVersion && !PlanDocumentService.IsTombstone(item));
                    if (restored == null) throw new InvalidDataException("That Plan version is unavailable.");
                    // Ensure the complete immutable body is available before any target writes.
                    command.RestoreTitle = _owner.Read(_session, ChatResourceUri.CreateArtifactRevision(_session, restored)).Title;
                    var collision = _resolve(PlanSourceDocument.Target(command.RestoreTitle));
                    if (collision != null && PlanDocumentService.PlanId(collision) != command.PlanId)
                        throw new InvalidDataException("Another Plan already uses the restored title.");
                }
                var prepared = new PlanResourcePrepared { ToolId = context.Execution.Call.Name, Target = target,
                    ArgumentsHash = TextPatternEngine.Sha256(context.Execution.Call.ArgumentsJson),
                    SourceHash = before == null ? null : TextPatternEngine.Sha256(before), Command = command, Operation = operation };
                return Task.FromResult(new ToolPreparationResult(ToolResult.Ok("Plan source prepared."), JsonConvert.SerializeObject(prepared),
                    patchTarget: edits == null ? null : new PatchTargetPlan { Source = command.Source, SourceHash = prepared.SourceHash,
                        EffectIdentity = DocumentArtifactStore.PlanIdentity(_session, command.PlanId), Preview = PatchPreview.From(edits.Edits) }));
            }
            catch (Exception error) when (error is IOException || error is ResourceRequestException || error is TextPatternException || error is JsonException)
            { return Task.FromResult(new ToolPreparationResult(Failure(error), patchDiagnostic: PatchDiagnostic.From(error))); }
        }

        public Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var prepared = PlanResourcePrepared.Read(context.PreparedStateJson);
            if (prepared.ToolId != context.Execution.Call.Name || prepared.Target != Value(context.Arguments, "target") ||
                prepared.ArgumentsHash != TextPatternEngine.Sha256(context.Execution.Call.ArgumentsJson))
                throw new InvalidDataException("Prepared Plan does not match the accepted call.");
            var current = _resolve(prepared.Target);
            var title = prepared.Command.Content?.Title ?? prepared.Command.RestoreTitle;
            if (title != null)
            {
                var collision = _resolve(PlanSourceDocument.Target(title));
                if (collision != null && PlanDocumentService.PlanId(collision) != prepared.Command.PlanId)
                    throw new ResourceRequestException("Another Plan now uses the requested title.", "source_changed", false);
            }
            if (prepared.Command.Source == null)
            { if (current != null) throw new InvalidDataException("The Plan destination appeared after preparation."); }
            else
            {
                if (current == null || current.Id != prepared.Command.ArtifactId) throw new ResourceRequestException("Plan source changed.", "source_changed", false);
                RequireHead(prepared.Command);
                if (TextPatternEngine.Sha256(PlanSourceDocument.Serialize(_owner.Read(_session, prepared.Command.Source))) != prepared.SourceHash)
                    throw new ResourceRequestException("Plan source changed.", "source_changed", false);
                if (prepared.Command.Content != null && TextPatternEngine.Sha256(PlanSourceDocument.Serialize(prepared.Command.Content)) == prepared.SourceHash)
                    return Task.FromResult(context.Complete(new ToolHandlerResult(ToolResult.Ok("Plan already matches."), ToolEffectEvidence.VerifiedNoChange)));
            }
            var mutation = new PlanDocumentService().ApplyResource(_session, prepared.Operation, prepared.Command,
                context.MarkDispatchPossible, PlanDocumentService.CreationId(_session, context.Execution));
            if (!mutation.Success)
                return Task.FromResult(context.Complete(new ToolHandlerResult(context.MayHaveDispatched
                    ? ToolResult.Unknown(mutation.Message) : ToolResult.Error(mutation.Message),
                    context.MayHaveDispatched ? ToolEffectEvidence.Unknown : ToolEffectEvidence.None)));
            if (!context.MayHaveDispatched || mutation.Artifact == null || !_session.Artifacts.Contains(mutation.Artifact))
                return Task.FromResult(context.Complete(new ToolHandlerResult(ToolResult.Unknown("Plan mutation could not be verified."), ToolEffectEvidence.Unknown)));
            mutation.Artifact.RunId = context.Execution.RunId;
            return Task.FromResult(context.Complete(new ToolHandlerResult(ToolResult.Ok(mutation.Message,
                JsonConvert.SerializeObject(new PlanResourceResult { Target = PlanSourceDocument.Target(mutation.Artifact.Title),
                    Status = mutation.Status, Removed = mutation.Removed, PlanId = mutation.PlanId, ArtifactId = mutation.Artifact.Id, Revision = mutation.Artifact.Revision, Version = mutation.Artifact.Revision, RemovedRevisions = mutation.AffectedRevisions, ReferencingMessageIds = mutation.ReferencingMessageIds })), ToolEffectEvidence.VerifiedChange)));
        }

        private void RequireHead(PlanResourceCommand command)
        {
            var current = _owner.CurrentPlan(_session, command.PlanId);
            if (current == null || current.Uri != command.Source.Uri || current.Revision != command.Source.Revision)
                throw new ResourceRequestException("Read the current Plan source before editing.", "source_changed", false);
        }

        public static void RequireObservation(ToolHandlerContext context, ResourceReadResult source,
            bool whole, IEnumerable<ResourceEvidence> observations)
        {
            var evidence = observations.Where(e => e.View == ResourceRepresentations.Source &&
                (e.Resource.Uri == source.Resource.Reference.Uri && e.Resource.Revision == source.Resource.Reference.Revision ||
                 e.Dependencies.Any(d => d.Kind == "immutable-snapshot" && d.Resource.Uri == source.Resource.Reference.Uri &&
                    d.Resource.Revision == source.Resource.Reference.Revision))).ToArray();
            if (context.Execution.Call.Name == ResourceMutationToolCatalog.Patch)
                context.Arguments["edits"] = TextPatch.FromArguments(context.Arguments).WithCoverage(TextPatch.Coverage(evidence));
            var patch = context.Execution.Call.Name == ResourceMutationToolCatalog.Patch
                ? TextEditPlan.Patch(source.Text, TextPatch.FromArguments(context.Arguments)) : null;
            if (evidence.Any(e => e.Complete && e.Coverage.Kind == ResourceCoverageKinds.Whole)) return;
            long covered = 0;
            foreach (var item in evidence.Where(e => e.Coverage.Kind == ResourceCoverageKinds.CharacterRange).OrderBy(e => e.Coverage.Start))
                if (item.Coverage.Start <= covered && item.Coverage.End.HasValue) covered = Math.Max(covered, item.Coverage.End.Value);
            if (whole && covered == source.Text.Length && evidence.Any(e => e.Complete && e.Coverage.End == covered)) return;
            if (!whole && evidence.Length > 0 && (patch == null || patch.Edits.All(edit => evidence.Any(e =>
                e.Coverage.Kind == ResourceCoverageKinds.CharacterRange && e.Coverage.Start <= edit.Offset &&
                e.Coverage.End >= edit.Offset + edit.Before.Length)))) return;
            throw new ResourceRequestException("Read the current Plan with representation=source before editing; body-only reads do not cover its source header.", "source_observation_required", false);
        }
        public ResourceReadResult Source(ChatArtifact artifact, string text = null)
        {
            text = text ?? PlanSourceDocument.Serialize(artifact);
            return new ResourceReadResult { Resource = new ResourceDescriptor { Reference = ChatResourceUri.CreateArtifactRevision(_session, artifact),
                Kind = ChatArtifactKinds.PlanDocument, Title = artifact.Title }, Representation = ResourceRepresentations.Source,
                Text = text, Complete = true, ContentSha256 = TextPatternEngine.Sha256(text), ReturnedCharacters = text.Length, TotalCharacters = text.Length };
        }
        private static string Value(IDictionary<string, object> args, string name)
        { object value; return args.TryGetValue(name, out value) && value != null ? Convert.ToString(value) : ""; }
        private static ToolResult Failure(Exception error)
        { return ToolResult.Error(error.Message, JsonConvert.SerializeObject(PatchDiagnostic.From(error))); }
        private sealed class PlanResourceResult
        {
            [JsonProperty("planId")] public string PlanId { get; set; }
            [JsonProperty("artifactId")] public string ArtifactId { get; set; }
            [JsonProperty("version")] public int Version { get; set; }
            [JsonProperty("revision")] public int Revision { get; set; }
            [JsonProperty("target")] public string Target { get; set; }
            [JsonProperty("status")] public string Status { get; set; }
            [JsonProperty("referencingMessageIds")] public IReadOnlyList<string> ReferencingMessageIds { get; set; }
            [JsonProperty("removedRevisions")] public int RemovedRevisions { get; set; }
            [JsonProperty("removed")] public bool Removed { get; set; }
        }
    }
}
