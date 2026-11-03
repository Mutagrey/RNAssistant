using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Services
{
    // HTML domain intent/read-back only. The shared observer owns the lease,
    // attempt, effect and atomic authority publication for UI and native tools.
    internal static class HtmlWorkspacePublication
    {
        internal static bool Owns(string operation)
        { return operation.StartsWith("html.", StringComparison.Ordinal) &&
            operation != RNAssistant.Office.Tools.HtmlWorkspaceToolCatalog.PublishAssetToolId; }
        internal static string OperationKey(ChatSession session, ToolExecutionContext context)
        {
            using (var hash = System.Security.Cryptography.SHA256.Create())
                return "html_operation_" + BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(
                    session.Id + "\n" + context.RunId + "\n" + context.StepId + "\n" + context.Call.Id + (context.TargetOrdinal.HasValue ? "\npatch:" + context.TargetOrdinal.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "")))).Replace("-", "").ToLowerInvariant();
        }

        internal static ResourceIdentity OperationIdentity(ChatSession session, string key)
        { return new ResourceIdentity(ResourceUri.Create("state", "document", session.DocumentAuthorityId, key)); }

        internal static IReadOnlyList<ResourceImpact> Prepare(ChatSession session, ToolExecutionContext context,
            IDictionary<string, object> arguments, DocumentArtifactStore owner, ResourceAuthorityService authority, string operation = null, HtmlWorkspaceCommand command = null)
        {
            operation = operation ?? context.Call.Name;
            try
            {
                var scope = authority.Scope(session, true);
                var operationKey = OperationKey(session, context);
                if (authority.Store.GetHead(scope, OperationIdentity(session, operationKey)) != null)
                    throw new ToolMutationPreparationException("html_attempt_already_published", "This HTML operation already has a publication. Recover its exact result without replay.");
                if (command == null) throw new ToolMutationPreparationException("html_preparation_required", "Prepare an explicit HTML workspace command.");
                var mutation = command.Load(session, owner, authority);
                var logicalId = command.LogicalId;
                var includeAllFiles = operation == "html.workspace.restore" ||
                    operation == "html.workspace.redo" ||
                    operation == RNAssistant.Office.Tools.HtmlWorkspaceToolCatalog.ImportAssetToolId;
                var path = ResourceMutationDomains.Argument(arguments, "path");
                includeAllFiles |= string.IsNullOrWhiteSpace(path) && SourceFileOperation(operation);
                return new[] { new ResourceImpact(HtmlWorkspaceIdentity.Identity(session, logicalId), ResourceImpactRelation.Exact) }
                    .Concat(mutation.Workspace.Files.Where(file => file != null)
                        .Where(file => includeAllFiles || string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase))
                        .Select(file => new ResourceImpact(HtmlWorkspaceIdentity.MemberIdentity(session, logicalId,
                            ChatHtmlResourceCatalog.FileMemberKey(file.Id)), ResourceImpactRelation.Exact)))
                    .ToArray();
            }
            catch (ResourceRequestException ex) { throw new ToolMutationPreparationException(ex.ErrorCode, ex.Message); }
            catch (InvalidDataException ex) { throw new ToolMutationPreparationException("RESOURCE_SNAPSHOT_UNAVAILABLE", ex.Message); }
        }

        internal static IReadOnlyList<ResourceMutationReadBack> ReadBack(ChatSession session, MutationAttempt attempt,
            ToolExecutionContext context, string operationKey, DocumentArtifactStore owner, ChatBlobStore payloads,
            IDictionary<string, object> domainArguments = null, string domainOperation = null, ResourceRef output = null)
        {
            var logicalId = ResourceUri.Parse(attempt.Target.Uri).Segments.Last();
            var artifact = session.Artifacts.SingleOrDefault(item => output != null &&
                ChatResourceUri.CreateArtifactRevisionUri(session, item) == output.Uri);
            if (artifact?.Kind != ChatArtifactKinds.HtmlWorkspace || HtmlWorkspaceIdentity.LogicalId(artifact.Id) != logicalId)
                throw new InvalidDataException("HTML read-back does not match the prepared workspace.");
            var snapshot = JsonConvert.DeserializeObject<HtmlWorkspaceSnapshot>(artifact.InlineText ?? "null");
            if (snapshot == null) throw new InvalidDataException("The complete HTML aggregate is required for publication.");
            var dependencies = new List<ResourceDependency>();
            var result = new List<ResourceMutationReadBack>();
            foreach (var binding in (snapshot.DataSources ?? new List<HtmlWorkspaceDataSource>()).Select(item => item.Binding).Where(item => item != null))
            {
                foreach (var reference in new[] { binding.Resource, binding.Schema, binding.Mapping }.Where(item => item != null))
                {
                    // A head binding is a dynamic identity, not evidence of exact bytes.
                    // Its definition stays in the aggregate and is resolved at read/export time.
                    if (!reference.IsExact)
                    {
                        if (ReferenceEquals(reference, binding.Resource) && binding.Policy == "head") continue;
                        throw new InvalidDataException("HTML exact bindings require a retained revision.");
                    }
                    dependencies.Add(new ResourceDependency(reference, binding.View, kind: "html-binding"));
                    string id;
                    if (ChatResourceUri.TryGetArtifactId(session, reference, out id))
                    {
                        var member = session.Artifacts.SingleOrDefault(item => item.Id == id);
                        if (member != null && member.Id.StartsWith("artifact_", StringComparison.Ordinal) && !member.ContentByteLength.HasValue)
                            result.Add(owner.RetainAuthoredSnapshot(session, member, attempt.AttemptId, operationKey));
                    }
                }
            }
            ResourceRef restoredFrom = null;
            var restoredId = (string)JObject.Parse(artifact.MetadataJson ?? "{}")["restoredFromArtifactId"];
            if (!string.IsNullOrEmpty(restoredId))
                restoredFrom = ChatResourceUri.CreateArtifactRevision(session, session.Artifacts.Single(item => item.Id == restoredId));
            if (!artifact.ContentByteLength.HasValue)
            {
                // Retain provenance with the new immutable snapshot. Later chat
                // linking cannot repair it after preparation reloads this record.
                // An already retained revision keeps its original attribution.
                artifact.RunId = context.RunId;
                artifact.SourceMessageId = session.Messages?.SingleOrDefault(message =>
                    message != null && !message.ProtocolMessage && message.RunId == context.RunId &&
                    message.Activity?.ToolCallId == context.Call.Id &&
                    message.Activity.ToolId == context.Call.Name &&
                    message.Activity.StepId == context.StepId)?.Id;
            }
            var retained = owner.RetainAuthoredSnapshot(session, artifact, attempt.AttemptId, operationKey, restoredFrom, dependencies);
            result.Add(retained);
            var exact = retained.Revision;
            var link = PayloadRef.FromBlob(payloads.StoreText(JsonConvert.SerializeObject(exact), "application/json"));
            result.Add(new ResourceMutationReadBack(attempt.Target, true, "text", link.Sha256, link,
                dependencies: new[] { new ResourceDependency(exact, kind: "immutable-snapshot") },
                restoredFrom: restoredFrom == null ? null : owner.LogicalRevisionForSnapshot(session, attempt.Target, restoredFrom)));
            result.Add(new ResourceMutationReadBack(OperationIdentity(session, operationKey), true, "text", link.Sha256, link,
                dependencies: new[] { new ResourceDependency(exact, kind: "operation-result") }));
            var allFiles = (snapshot.Files ?? new List<HtmlWorkspaceFile>()).Where(file => file != null).ToArray();
            var includeAllFiles = (domainOperation ?? context.Call.Name) == "html.workspace.restore" ||
                context.Call.Name == "html.workspace.redo" ||
                context.Call.Name == RNAssistant.Office.Tools.HtmlWorkspaceToolCatalog.ImportAssetToolId;
            var path = domainArguments == null ? (string)JObject.Parse(context.Call.ArgumentsJson)["path"] : ResourceMutationDomains.Argument(domainArguments, "path");
            includeAllFiles |= string.IsNullOrWhiteSpace(path) && SourceFileOperation(domainOperation ?? context.Call.Name);
            foreach (var file in allFiles.Where(file => includeAllFiles ||
                string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                var memberIdentity = HtmlWorkspaceIdentity.MemberIdentity(session, logicalId,
                    ChatHtmlResourceCatalog.FileMemberKey(file.Id));
                var source = PayloadRef.FromBlob(payloads.StoreText(file.Content ?? string.Empty, "text/plain; charset=utf-8"));
                result.Add(new ResourceMutationReadBack(memberIdentity, true, ResourceRepresentations.Source,
                    source.Sha256, source));
            }
            var currentMembers = new HashSet<string>(allFiles.Select(file =>
                HtmlWorkspaceIdentity.MemberIdentity(session, logicalId,
                    ChatHtmlResourceCatalog.FileMemberKey(file.Id)).Uri), StringComparer.Ordinal);
            foreach (var old in attempt.IntendedImpacts.Where(item => HtmlWorkspaceIdentity.IsMemberIdentity(item.Identity) &&
                !currentMembers.Contains(item.Identity.Uri)))
                result.Add(new ResourceMutationReadBack(old.Identity, false));

            return result.GroupBy(item => item.Identity.Uri).Select(group => group.First()).ToArray();
        }

        private static bool SourceFileOperation(string operation)
        {
            return operation == RNAssistant.Office.Tools.HtmlWorkspaceToolCatalog.WriteFileToolId ||
                operation == RNAssistant.Office.Tools.HtmlWorkspaceToolCatalog.ApplyPatchToolId ||
                operation == RNAssistant.Office.Tools.HtmlWorkspaceToolCatalog.DeleteToolId;
        }
    }
}
