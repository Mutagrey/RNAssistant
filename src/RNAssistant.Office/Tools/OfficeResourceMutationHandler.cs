using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Services;
using Result = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    // The accepted public call never changes. This is private owner preparation, not another callable alias.
    internal sealed class ResourceMutationPrepared
    {
        public int Version { get; set; } = 1;
        public string ToolId { get; set; }
        public string ArgumentsHash { get; set; }
        public string Operation { get; set; }
        public string Target { get; set; }
        public ResourceRef Source { get; set; }
        public string SourceHash { get; set; }
        public string OwnerSourceHash { get; set; }
        public Dictionary<string, object> RoutingArguments { get; set; }
        public string OwnerState { get; set; }
        internal static ResourceMutationPrepared Read(string json)
        {
            var value = JsonConvert.DeserializeObject<ResourceMutationPrepared>(json ?? "null");
            if (value?.Version != 1 || !ResourceMutationToolCatalog.Owns(value.ToolId) || value.RoutingArguments == null)
                throw new InvalidDataException("Missing or incompatible resource mutation preparation.");
            return value;
        }
    }

    internal sealed class OfficeResourceMutationHandler : IPreparableToolHandler, IManagedMutationToolHandler
    {
        private readonly string _id;
        private readonly ResourceGatewayService _gateway;
        private readonly ChatSession _session;
        private readonly VbaToolExecutor _vba;
        private readonly HostRuntime _host;
        private readonly HtmlWorkspaceToolService _html;
        private readonly AuthoredFileService _files;
        private readonly MarkdownDocumentService _markdown;
        private readonly ResourceSearchObservation _search;
        internal OfficeResourceMutationHandler(string id, ResourceGatewayService gateway, ChatSession session,
            VbaToolExecutor vba, HostRuntime host, HtmlWorkspaceToolService html, AuthoredFileService files,
            MarkdownDocumentService markdown, ResourceSearchObservation search)
        { _id = id; _gateway = gateway; _session = session; _vba = vba; _host = host; _html = html; _files = files; _markdown = markdown; _search = search; }

        public async Task<ToolPreparationResult> PrepareAsync(ToolHandlerContext context, CancellationToken token)
        {
            try
            {
                using (DocumentAccessGate.BeginOperation())
                {
                    var target = Value(context.Arguments, "target");
                    var prepared = new ResourceMutationPrepared { ToolId = _id, Target = target,
                        ArgumentsHash = TextPatternEngine.Sha256(context.Execution.Call.ArgumentsJson),
                        RoutingArguments = new Dictionary<string, object>() };
                    ResourceIntentTarget selected = null;
                    try { selected = ResolveTarget(target); }
                    catch (ResourceRequestException ex) when (_id == ResourceMutationToolCatalog.Write &&
                        (ex.ErrorCode == "RESOURCE_NOT_FOUND" || ex.ErrorCode == "resource_not_found" || ex.ErrorCode == "resource_target_not_found")) { }
                    if (selected != null && context.Execution.ExpectedTargetAbsent)
                        throw new InvalidDataException("The requested new resource already exists.");
                    var filePatch = _id == ResourceMutationToolCatalog.Patch && selected?.Descriptor.Kind == ChatArtifactKinds.File;
                    var source = selected == null || filePatch ? null : Read(selected.Reference, token);
                    prepared.Source = filePatch ? selected.Reference : source?.Resource.Reference;
                    prepared.SourceHash = source == null ? null : TextPatternEngine.Sha256(source.Text);
                    prepared.OwnerSourceHash = source == null ? null : VbaTextCanonicalizer.LiveCodeSha256(source.Text);
                    if (source != null)
                    {
                        if (_id == ResourceMutationToolCatalog.Replace)
                        {
                            var pin = _search.Require(target, Value(context.Arguments, "query"), Mode(context), Flag(context, "matchCase"), Count(context), context.Execution.RunId + ":" + context.Execution.StepId);
                            if (pin.Uri != prepared.Source.Uri || pin.Revision != prepared.Source.Revision)
                                throw new ResourceRequestException("Source changed after search; repeat core.search.", "target_conflict", false);
                        }
                        else if (_id != ResourceMutationToolCatalog.Restore) RequireObservation(context, source, _id == ResourceMutationToolCatalog.Write);
                    }
                    Route(prepared, selected, context.Arguments);
                    if (_id == ResourceMutationToolCatalog.Copy || _id == ResourceMutationToolCatalog.Move)
                        RequireAbsent(Value(context.Arguments, "destination"));
                    if (source?.Text.Length == 0 && prepared.Operation == VbaToolCatalog.ApplyPatch)
                        prepared.Operation = VbaToolCatalog.WriteModule;
                    var owner = Owner(prepared);
                    var args = Arguments(prepared, context, source?.Text, token);
                    if (_id == ResourceMutationToolCatalog.Patch && selected.Descriptor.Kind == ChatHtmlResourceCatalog.FileKind)
                        _html.PrepareSourcePatch(_session, selected, source.Text, Value(args, "content"));
                    var preparable = owner as IPreparableToolHandler;
                    PatchTargetPlan filePlan = null;
                    if (preparable != null)
                    {
                        var result = await preparable.PrepareAsync(OwnerContext(context, prepared, args), token).ConfigureAwait(false);
                        if (result.Result.Status != RNAssistant.Core.Tools.Contracts.ToolResultStatus.Ok)
                        {
                            if (filePatch && result.PatchDiagnostic?.Code == "source_changed")
                                return new ToolPreparationResult(result.Result, recovery: new ToolRecoveryContract(
                                    ToolFailureKind.RejectedNoEffect, ToolRetryPolicy.RefreshRequired,
                                    selected.Reference.Identity, ResourceRepresentations.Text, target,
                                    readScope: TextPatch.Parse(Value(args, "patch")).RequiresWholeSource
                                        ? ToolRecoveryReadScope.Whole : ToolRecoveryReadScope.RelevantLines), patchDiagnostic: result.PatchDiagnostic);
                            return result;
                        }
                        prepared.OwnerState = result.PreparedStateJson;
                        if (filePatch)
                        {
                            filePlan = result.PatchTarget ?? throw new InvalidDataException("The file patch has no prepared source.");
                            prepared.SourceHash = filePlan.SourceHash;
                        }
                    }
                    return new ToolPreparationResult(Result.Ok("Resource operation prepared."), JsonConvert.SerializeObject(prepared),
                        patchTarget: filePlan ?? (_id == ResourceMutationToolCatalog.Patch ? new PatchTargetPlan {
                            Source = prepared.Source, SourceHash = prepared.SourceHash,
                            EffectIdentity = PatchEffectIdentity(selected, prepared.Source),
                            Preview = PatchPreview.From(SourcePatch(source.Text, prepared.Source, context, token).Edits) } : null));
                }
            }
            catch (Exception ex) when (ex is ResourceRequestException || ex is InvalidDataException || ex is TextPatternException)
            { return new ToolPreparationResult(Error(ex), recovery: Recovery(context, ex), patchDiagnostic: PatchDiagnostic.From(ex)); }
        }

        public async Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context, CancellationToken token)
        {
            try
            {
                using (DocumentAccessGate.BeginOperation())
                {
                    var prepared = ResourceMutationPrepared.Read(context.PreparedStateJson);
                    if (prepared.ToolId != _id || prepared.Target != Value(context.Arguments, "target") ||
                        prepared.ArgumentsHash != TextPatternEngine.Sha256(context.Execution.Call.ArgumentsJson))
                        throw new InvalidDataException("Prepared resource intent does not match this call.");
                    ResourceReadResult source = null;
                    if (prepared.Source != null)
                    {
                        var selected = ResolveTarget(prepared.Target);
                        if (prepared.Operation == AuthoredFileToolCatalog.PatchToolId)
                        {
                            if (selected.Reference.Uri != prepared.Source.Uri || selected.Reference.Revision != prepared.Source.Revision)
                                throw new ResourceRequestException("Resource changed after preparation; read it again.", "target_conflict", false);
                            return await Owner(prepared).ExecuteAsync(OwnerContext(context, prepared,
                                Arguments(prepared, context, null, token)), token).ConfigureAwait(false);
                        }
                        source = Read(selected.Reference, token);
                        if (source.Resource.Reference.Uri != prepared.Source.Uri || source.Resource.Reference.Revision != prepared.Source.Revision ||
                            TextPatternEngine.Sha256(source.Text) != prepared.SourceHash)
                            throw new ResourceRequestException("Resource changed after preparation; read it again.", "target_conflict", false);
                    }
                    else
                    {
                        RequireAbsent(prepared.Target);
                    }
                    if (_id == ResourceMutationToolCatalog.Copy || _id == ResourceMutationToolCatalog.Move)
                        RequireAbsent(Value(context.Arguments, "destination"));
                    if (_id == ResourceMutationToolCatalog.Patch && SourcePatch(source.Text, prepared.Source, context, token).Text == source.Text)
                        return new ToolHandlerResult(Result.Ok("Source already matches the prepared patch."), ToolEffectEvidence.VerifiedNoChange);
                    return await Owner(prepared).ExecuteAsync(OwnerContext(context, prepared,
                        Arguments(prepared, context, source?.Text, token)), token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is ResourceRequestException || ex is InvalidDataException || ex is TextPatternException)
            { return new ToolHandlerResult(context.MayHaveDispatched ? Result.Unknown(ex.Message) : Error(ex),
                context.MayHaveDispatched ? ToolEffectEvidence.Unknown : ToolEffectEvidence.None,
                recovery: context.MayHaveDispatched ? null : Recovery(context, ex)); }
        }

        private ResourceIdentity PatchEffectIdentity(ResourceIntentTarget selected, ResourceRef source)
        {
            if (selected.Descriptor.Kind != ChatHtmlResourceCatalog.FileKind) return source.Identity;
            return HtmlWorkspaceIdentity.MemberIdentity(_session,
                HtmlWorkspaceIdentity.LogicalId(selected.Descriptor.Metadata["revisionArtifactId"]),
                selected.Descriptor.Metadata["logicalMemberKey"]);
        }

        private ResourceIntentTarget ResolveTarget(string target)
        {
            if (target.StartsWith("HTML file: ", StringComparison.Ordinal) && !target.Contains(" in HTML workspace: "))
                target += " in " + ChatHtmlResourceCatalog.CurrentWorkspaceTarget;
            return _gateway.ResolveIntentTarget(_session, target, allowUniqueBase: true);
        }

        private void RequireAbsent(string target)
        {
            try { ResolveTarget(target); throw new InvalidDataException("The destination already exists: " + target); }
            catch (ResourceRequestException ex) when (ex.ErrorCode == "RESOURCE_NOT_FOUND" || ex.ErrorCode == "resource_not_found" || ex.ErrorCode == "resource_target_not_found") { }
        }

        private ResourceReadResult Read(ResourceRef reference, CancellationToken token)
        {
            var descriptor = _gateway.Resolve(_session, reference.Uri).Resource;
            var representation = descriptor.Representations.Contains(ResourceRepresentations.Source) ? ResourceRepresentations.Source : ResourceRepresentations.Text;
            var result = _gateway.ReadWhole(_session, new ResourceRef(reference.Uri), representation, token).Result;
            if (result.Text == null || !result.Complete) throw new InvalidDataException("Complete editable source is unavailable.");
            return result;
        }

        private void RequireObservation(ToolHandlerContext context, ResourceReadResult source, bool complete)
        {
            if (_id == ResourceMutationToolCatalog.Patch && TextPatch.Parse(Value(context.Arguments, "patch")).RequiresWholeSource) complete = true;
            var accepted = _session.Messages.FirstOrDefault(m => m.AcceptedCallOrigin != null && m.ToolCallId == context.Execution.Call.Id);
            if (accepted == null) return; // Manual editor has its explicit owner guard.
            var evidence = (accepted.ResourceEvidence ?? new List<ResourceEvidence>())
                .Where(e => e.Resource.Uri == source.Resource.Reference.Uri && e.Resource.Revision == source.Resource.Reference.Revision).ToArray();
            if (complete)
            {
                if (evidence.Any(e => e.Complete && e.Coverage.Kind == ResourceCoverageKinds.Whole)) return;
                long covered = 0;
                foreach (var e in evidence.Where(e => e.Coverage.Kind == ResourceCoverageKinds.CharacterRange).OrderBy(e => e.Coverage.Start))
                    if (e.Coverage.Start <= covered && e.Coverage.End.HasValue) covered = Math.Max(covered, e.Coverage.End.Value);
                if (covered == source.Text.Length && evidence.Any(e => e.Complete && e.Coverage.End == covered)) return;
            }
            else if (evidence.Length > 0)
            {
                if (_id != ResourceMutationToolCatalog.Patch) return;
                var plan = SourcePatch(source.Text, source.Resource.Reference, context);
                if (plan.Edits.All(edit => evidence.Any(e => e.Complete && e.Coverage.Kind == ResourceCoverageKinds.Whole ||
                    e.Coverage.Kind == ResourceCoverageKinds.CharacterRange && e.Coverage.Start <= edit.Offset &&
                    e.Coverage.End >= edit.Offset + edit.Before.Length))) return;
            }
            if (!complete && _id == ResourceMutationToolCatalog.Patch && CurrentVbaExcerptCoversHunks(accepted, source, context)) return;
            throw new ResourceRequestException(complete ? "core.write requires complete current source in model context; read it first." : "Read the current source before changing it.", "source_observation_required", false);
        }

        private bool CurrentVbaExcerptCoversHunks(ChatMessage accepted, ResourceReadResult source, ToolHandlerContext context)
        {
            ResourceAddress current;
            if (!ResourceUri.TryParse(source.Resource.Reference.Uri, out current) || current.Provider != "vba" || current.Segments.Count < 3) return false;
            var observed = new List<Tuple<int, int>>();
            foreach (var evidence in accepted.ResourceEvidence ?? new List<ResourceEvidence>())
            {
                ResourceAddress address; int first, end;
                if (!ResourceUri.TryParse(evidence.Resource.Uri, out address) || address.Provider != "vba" || address.Segments.Count != 5 ||
                    address.Segments[0] != current.Segments[0] || address.Segments[1] != "module-lines" ||
                    VbaResourceProvider.ComponentIdentity(current.Segments[0], address.Segments[2]).Uri != current.Uri ||
                    !int.TryParse(address.Segments[3], out first) || !int.TryParse(address.Segments[4], out end)) continue;
                var excerpt = _gateway.ReadObservedText(evidence);
                if (excerpt == null) continue;
                var lines = source.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                var live = string.Join("\n", lines.Skip(first - 1).Take(end - first));
                if (VbaTextCanonicalizer.NormalizeLiveCode(excerpt) == VbaTextCanonicalizer.NormalizeLiveCode(live))
                {
                    var beginOffset = TextEditPlan.Position(source.Text, first, 1);
                    var endOffset = end > lines.Length ? source.Text.Length : TextEditPlan.Position(source.Text, end, 1);
                    observed.Add(Tuple.Create(beginOffset, endOffset));
                }
            }
            var plan = SourcePatch(source.Text, source.Resource.Reference, context);
            return plan.Edits.All(edit => observed.Any(span => span.Item1 <= edit.Offset && span.Item2 >= edit.Offset + edit.Before.Length));
        }

        private void Route(ResourceMutationPrepared prepared, ResourceIntentTarget selected, IDictionary<string, object> input)
        {
            var target = prepared.Target;
            var args = prepared.RoutingArguments;
            if (_id == ResourceMutationToolCatalog.Restore && selected?.Descriptor.Kind == VbaResourceProvider.BackupKind)
            {
                prepared.Operation = VbaToolCatalog.RestoreBackup;
                prepared.OwnerSourceHash = null; // The backup is not the live target; restore pins its own before-state.
                args["target"] = selected.Target;
            }
            else if (target.StartsWith("VBA module: ", StringComparison.Ordinal))
            {
                if (_vba == null || !_vba.HostSupportsVba()) throw new InvalidDataException("VBA is unavailable in this host.");
                args["moduleName"] = selected?.Descriptor.Title ?? target.Substring(12);
                args["mode"] = prepared.Source == null ? "createOnly" : "updateOnly";
                args["componentType"] = Value(input, "resourceType") == "" || Value(input, "resourceType") == "text" ? "StdModule" : Value(input, "resourceType");
                string existingType;
                if (selected?.Descriptor.Metadata.TryGetValue("componentType", out existingType) == true)
                    args["componentType"] = existingType;
                prepared.Operation = _id == ResourceMutationToolCatalog.Write || _id == ResourceMutationToolCatalog.Patch ? VbaToolCatalog.WriteModule :
                    _id == ResourceMutationToolCatalog.Replace ? VbaToolCatalog.ApplyPatch :
                    _id == ResourceMutationToolCatalog.Delete ? VbaToolCatalog.DeleteModule :
                    _id == ResourceMutationToolCatalog.Move ? VbaToolCatalog.RenameModule :
                    _id == ResourceMutationToolCatalog.Restore ? VbaToolCatalog.RestoreBackup : null;
                if (_id == ResourceMutationToolCatalog.Copy)
                {
                    var destination = Value(input, "destination");
                    if (!destination.StartsWith("VBA module: ", StringComparison.Ordinal)) throw new InvalidDataException("Only same-project VBA component copies are supported.");
                    var componentType = selected.Descriptor.Metadata["componentType"];
                    if (componentType != "StdModule" && componentType != "ClassModule" && componentType != "MSForm")
                        throw new InvalidDataException("Document modules cannot be copied as ordinary components.");
                    prepared.Operation = VbaToolCatalog.WriteModule; prepared.OwnerSourceHash = null;
                    args["moduleName"] = destination.Substring(12); args["mode"] = "createOnly"; args["componentType"] = componentType;
                }
                if (_id == ResourceMutationToolCatalog.Move)
                {
                    var destination = Value(input, "destination");
                    if (!destination.StartsWith("VBA module: ", StringComparison.Ordinal)) throw new InvalidDataException("Only same-project VBA component rename is supported.");
                    args["newModuleName"] = destination.Substring(12);
                }

            }
            else if (_id == ResourceMutationToolCatalog.Delete && selected?.Descriptor.Kind == ChatHtmlResourceCatalog.DataKind)
            {
                prepared.Operation = HtmlWorkspaceToolCatalog.DeleteToolId;
                args["target"] = selected.Descriptor.Title;
            }
            else if (target.StartsWith("HTML file: ", StringComparison.Ordinal))
            {
                args["path"] = selected?.Descriptor.Title ?? target.Substring(11).Split(new[] { " in HTML workspace: " }, StringSplitOptions.None)[0];
                prepared.Operation = _id == ResourceMutationToolCatalog.Write ? HtmlWorkspaceToolCatalog.WriteFileToolId :
                    _id == ResourceMutationToolCatalog.Patch || _id == ResourceMutationToolCatalog.Replace ? HtmlWorkspaceToolCatalog.ApplyPatchToolId :
                    _id == ResourceMutationToolCatalog.Delete ? HtmlWorkspaceToolCatalog.DeleteToolId : null;
                if (_id == ResourceMutationToolCatalog.Delete) args["target"] = args["path"];
            }
            else if (selected?.Descriptor.Kind == ChatArtifactKinds.Markdown || target.StartsWith("note: ", StringComparison.Ordinal))
            {
                prepared.Operation = _id == ResourceMutationToolCatalog.Write || _id == ResourceMutationToolCatalog.Patch || _id == ResourceMutationToolCatalog.Replace
                    ? MarkdownDocumentToolCatalog.SaveToolId : _id == ResourceMutationToolCatalog.Restore ? MarkdownDocumentToolCatalog.RestoreToolId : null;
                if (selected != null) args["target"] = selected.Target;
                args["title"] = selected?.Descriptor.Title ?? target.Substring(6);
                string description;
                args["description"] = selected?.Descriptor.Metadata.TryGetValue("description", out description) == true ? description : Convert.ToString(args["title"]);
                if (_id == ResourceMutationToolCatalog.Restore)
                {
                    object version; if (!input.TryGetValue("version", out version) || version == null) throw new InvalidDataException("Select a version from the document history.");
                    args["version"] = version;
                }
            }
            else if (selected?.Descriptor.Kind == ChatArtifactKinds.File || target.StartsWith("file: ", StringComparison.Ordinal) ||
                _id == ResourceMutationToolCatalog.Copy && selected?.Descriptor.Kind == ChatArtifactKinds.Attachment)
            {
                prepared.Operation = _id == ResourceMutationToolCatalog.Write ? (selected == null ? AuthoredFileToolCatalog.CreateToolId : AuthoredFileToolCatalog.UpdateToolId) :
                    _id == ResourceMutationToolCatalog.Patch ? AuthoredFileToolCatalog.PatchToolId :
                    _id == ResourceMutationToolCatalog.Replace ? AuthoredFileToolCatalog.UpdateToolId :
                    _id == ResourceMutationToolCatalog.Copy ? AuthoredFileToolCatalog.CopyToolId : null;
                args["target"] = selected?.Target ?? target; args["name"] = selected?.Descriptor.Title ?? target.Substring(6);
                if (_id == ResourceMutationToolCatalog.Copy)
                {
                    var destination = Value(input, "destination");
                    if (!destination.StartsWith("file: ", StringComparison.Ordinal)) throw new InvalidDataException("Use a file: destination for an authored file copy.");
                    args["source"] = selected.Target; args["name"] = destination.Substring(6);
                }
            }
            if (prepared.Operation == null) throw new InvalidDataException("This resource owner does not support the requested operation. Use an explicit editable source target.");
        }

        private IDictionary<string, object> Arguments(ResourceMutationPrepared prepared, ToolHandlerContext context, string before, CancellationToken token)
        {
            var args = new Dictionary<string, object>(prepared.RoutingArguments);
            if (prepared.Operation == AuthoredFileToolCatalog.PatchToolId)
            {
                args["patch"] = Value(context.Arguments, "patch");
                return args;
            }
            string content = null;
            TextEditPlan plan = null;
            if (_id == ResourceMutationToolCatalog.Write) content = Value(context.Arguments, "content");
            else if (_id == ResourceMutationToolCatalog.Copy && prepared.Operation == VbaToolCatalog.WriteModule) content = before;
            else if (_id == ResourceMutationToolCatalog.Patch)
                plan = SourcePatch(before, prepared.Source, context, token);
            else if (_id == ResourceMutationToolCatalog.Replace)
                plan = TextEditPlan.Replace(before, Value(context.Arguments, "query"), Value(context.Arguments, "replacement"), Mode(context), Flag(context, "matchCase"), Count(context), token);
            if (plan != null) { content = plan.Text; args["resolvedTextEdits"] = plan.Edits; }
            if (content != null)
            {
                args["content"] = content; args["code"] = content; args["markdown"] = content;
                // The replace adapter still uses its private owner command. core.patch
                // passes complete code directly to the guarded whole-module writer.
                if (prepared.Operation == VbaToolCatalog.ApplyPatch)
                    args["patch"] = new JArray(new JObject { ["find"] = before, ["text"] = content });

            }
            return args;
        }
        private static TextEditPlan SourcePatch(string source, ResourceRef reference, ToolHandlerContext context,
            CancellationToken token = default(CancellationToken))
        {
            ResourceAddress address;
            var vba = reference != null && ResourceUri.TryParse(reference.Uri, out address) && address.Provider == "vba";
            return TextEditPlan.Patch(source, Value(context.Arguments, "patch"), token,
                vba ? (Func<string, string>)VbaTextCanonicalizer.NormalizeVbeComparableLine : null);
        }
        private IToolHandler Owner(ResourceMutationPrepared prepared)
        {
            var operation = prepared.Operation;
            if (VbaToolCatalog.Owns(operation)) return new VbaToolHandler(operation, _vba, _host, _session, prepared.OwnerSourceHash);
            if (HtmlWorkspaceToolCatalog.Owns(operation)) return new HtmlWorkspaceToolHandler(operation, _session, _html);
            if (AuthoredFileToolCatalog.Owns(operation)) return new AuthoredFileToolHandler(_files, _session);
            if (MarkdownDocumentToolCatalog.Owns(operation)) return new MarkdownDocumentToolHandler(_markdown, _session);
            throw new InvalidDataException("Unsupported source owner.");
        }
        private static ToolHandlerContext OwnerContext(ToolHandlerContext context, ResourceMutationPrepared prepared, IDictionary<string, object> args)
        { return new ToolHandlerContext(context.Execution, args, prepared.OwnerState, context.MarkDispatchPossible,
            result => context.Complete(result), prepared.Operation); }
        private static string Value(IDictionary<string, object> args, string key) { object value; return args.TryGetValue(key, out value) ? Convert.ToString(value) : ""; }
        private static string Mode(ToolHandlerContext context) { var mode = Value(context.Arguments, "mode"); return mode.Length == 0 ? "literal" : mode; }
        private static bool Flag(ToolHandlerContext context, string key) { object value; return context.Arguments.TryGetValue(key, out value) && value != null && Convert.ToBoolean(value); }
        private static int Count(ToolHandlerContext context) { return Convert.ToInt32(context.Arguments["expectedMatches"]); }
        private ToolRecoveryContract Recovery(ToolHandlerContext context, Exception error)
        {
            var code = (error as ResourceRequestException)?.ErrorCode;
            if (code == "source_observation_required" || code == "target_conflict" || code == "replacement_search_required")
            {
                try
                {
                    var target = Value(context.Arguments, "target");
                    var current = ResolveTarget(target).Descriptor;
                    var view = current.Representations.Contains(ResourceRepresentations.Source) ? ResourceRepresentations.Source : ResourceRepresentations.Text;
                    return new ToolRecoveryContract(ToolFailureKind.RejectedNoEffect, ToolRetryPolicy.RefreshRequired,
                        current.Reference.Identity, view, target,
                        readScope: _id == ResourceMutationToolCatalog.Write || view == ResourceRepresentations.Source
                            ? ToolRecoveryReadScope.Whole : ToolRecoveryReadScope.RelevantLines);
                }
                catch (ResourceRequestException) { }
            }
            return new ToolRecoveryContract(ToolFailureKind.RejectedNoEffect, ToolRetryPolicy.Replan);
        }
        private static Result Error(Exception error) { return Result.Error(error.Message, JsonConvert.SerializeObject(new ResourceMutationError { Code = error is ResourceRequestException resource ? resource.ErrorCode : error is TextPatternException text ? text.ErrorCode : "resource_mutation_conflict" })); }
        private sealed class ResourceMutationError { [JsonProperty("code")] public string Code { get; set; } }
    }
}
