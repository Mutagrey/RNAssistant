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
        internal static bool Owns(string operation) { return operation.StartsWith("common.html_", StringComparison.Ordinal); }
        internal static string OperationKey(ChatSession session, ToolExecutionContext context)
        {
            using (var hash = System.Security.Cryptography.SHA256.Create())
                return "html_operation_" + BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(
                    session.Id + "\n" + context.RunId + "\n" + context.StepId + "\n" + context.Call.Id))).Replace("-", "").ToLowerInvariant();
        }

        internal static ResourceIdentity OperationIdentity(ChatSession session, string key)
        { return new ResourceIdentity(ResourceUri.Create("state", "document", session.DocumentAuthorityId, key)); }

        internal static IReadOnlyList<ResourceImpact> Prepare(ChatSession session, ToolExecutionContext context,
            IDictionary<string, object> arguments, DocumentArtifactStore owner, ResourceAuthorityService authority)
        {
            try
            {
                var scope = authority.Scope(session, true);
                var operationKey = OperationKey(session, context);
                if (authority.Store.GetHead(scope, OperationIdentity(session, operationKey)) != null)
                    throw new ToolMutationPreparationException("html_attempt_already_published", "This HTML operation already has a publication. Recover its exact result without replay.");
                var logicalId = HtmlWorkspaceIdentity.LogicalId(session.ActiveHtmlArtifactId);
                if (string.IsNullOrWhiteSpace(session.ActiveHtmlArtifactId))
                {
                    if (session.HtmlWorkspace?.Files.Count > 0 || session.HtmlWorkspace?.DataSources.Count > 0)
                        throw new ToolMutationPreparationException("html_workspace_unpublished", "Select a published workspace or create an empty workspace explicitly.");
                    logicalId = operationKey.Replace("html_operation_", "html_ws_");
                }
                else
                {
                    var selectedItems = session.Artifacts.Where(item => item.Id == session.ActiveHtmlArtifactId).Take(2).ToArray();
                    if (selectedItems.Length != 1)
                        throw new ToolMutationPreparationException("html_active_revision_invalid", "The selected HTML snapshot is missing or ambiguous. Reload its exact document revision.");
                    var selected = selectedItems[0];
                    if (logicalId == null || selected?.DocumentAuthorityId != session.DocumentAuthorityId)
                        throw new ToolMutationPreparationException("html_owner_incompatible", "This HTML workspace uses an incompatible chat-owned format. Start a new workspace explicitly; no implicit migration is performed.");
                    var current = owner.CurrentSnapshot(session, HtmlWorkspaceIdentity.Identity(session, logicalId));
                    var expected = ChatResourceUri.CreateArtifactRevision(session, selected);
                    if (current == null || current.Uri != expected.Uri || current.Revision != expected.Revision)
                        throw new ToolMutationPreparationException("RESOURCE_REVISION_CHANGED", "HTML changed in another chat. Read and select the current document revision before editing.");
                    var history = owner.SnapshotHistory(session, logicalId).ToList();
                    var restoreId = ResourceMutationDomains.Argument(arguments, "snapshotId");
                    if (!history.Any(item => item.Id == selected.Id) ||
                        !string.IsNullOrEmpty(restoreId) && !history.Any(item => item.Id == restoreId))
                        throw new InvalidDataException("The selected or restore-source HTML revision is not published in this workspace.");
                    for (var index = 0; index < history.Count; index++)
                        if (history[index].Id == selected.Id || history[index].Id == restoreId)
                            history[index] = owner.Read(session, ChatResourceUri.CreateArtifactRevision(session, history[index]));
                    session.Artifacts.RemoveAll(item => HtmlWorkspaceIdentity.LogicalId(item.Id) == logicalId);
                    session.Artifacts.AddRange(history);
                    if (!HtmlWorkspaceArtifactService.Restore(session, selected.Id))
                        throw new InvalidDataException("The selected HTML aggregate is invalid or unavailable.");
                }
                session.PreparedHtmlWorkspaceId = logicalId;
                return new[] { new ResourceImpact(HtmlWorkspaceIdentity.Identity(session, logicalId), ResourceImpactRelation.Exact) };
            }
            catch (InvalidDataException ex) { throw new ToolMutationPreparationException("RESOURCE_SNAPSHOT_UNAVAILABLE", ex.Message); }
        }

        internal static IReadOnlyList<ResourceMutationReadBack> ReadBack(ChatSession session, MutationAttempt attempt,
            string operationKey, DocumentArtifactStore owner, ChatBlobStore payloads)
        {
            var logicalId = ResourceUri.Parse(attempt.Target.Uri).Segments.Last();
            var artifact = session.Artifacts.SingleOrDefault(item => item.Id == session.ActiveHtmlArtifactId);
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
            var retained = owner.RetainAuthoredSnapshot(session, artifact, attempt.AttemptId, operationKey, restoredFrom, dependencies);
            result.Add(retained);
            var exact = retained.Revision;
            var link = PayloadRef.FromBlob(payloads.StoreText(JsonConvert.SerializeObject(exact), "application/json"));
            result.Add(new ResourceMutationReadBack(attempt.Target, true, "text", link.Sha256, link,
                dependencies: new[] { new ResourceDependency(exact, kind: "immutable-snapshot") },
                restoredFrom: restoredFrom == null ? null : owner.LogicalRevisionForSnapshot(session, attempt.Target, restoredFrom)));
            result.Add(new ResourceMutationReadBack(OperationIdentity(session, operationKey), true, "text", link.Sha256, link,
                dependencies: new[] { new ResourceDependency(exact, kind: "operation-result") }));
            ArtifactWorkingSet.Set(session, artifact, false);
            return result.GroupBy(item => item.Identity.Uri).Select(group => group.First()).ToArray();
        }
    }
}
