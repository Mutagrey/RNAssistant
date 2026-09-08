using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Core.Storage
{
    public sealed partial class DocumentArtifactStore
    {
        public ResourceRef ResolveSnapshotIdentity(ChatSession session, ResourceIdentity identity)
        {
            var scope = Scope(session);
            var address = ResourceUri.Parse(identity.Uri);
            if (address.Provider != "chat" || address.Segments.Count != 3 || address.Segments[0] != scope.Id || address.Segments[1] != "artifact")
                throw new InvalidDataException("The snapshot identity belongs to another scope.");
            var head = _authority.GetHead(scope, identity);
            if (head?.Knowledge != HeadKnowledge.Known || !Owns(session, head.Revision) || !IsSnapshotReference(head.Revision))
                throw new InvalidDataException("The exact snapshot identity is unavailable.");
            return head.Revision.Copy();
        }

        // Page only discovery roots. Immutable history and operation receipts are
        // outside these ordered ranges and require no metadata IO for discovery.
        public DocumentArtifactDiscovery InspectCurrentMetadata(ChatSession session, int offset = 0, int limit = 50)
        {
            var scope = Scope(session);
            var roots = ResourceUri.Create("state", scope.Kind, scope.Id) + "/";
            var artifacts = ResourceUri.Create("chat", scope.Id, "artifact") + "/";
            var ranges = new List<ResourceHeadRange>();
            foreach (var prefix in new[] { roots + "plan_doc_", roots + "html_ws_", roots + "artifact_md_", artifacts + "attachment_" })
                ranges.Add(new ResourceHeadRange(prefix, prefix + "\uffff"));
            // Authored file roots share artifact_; MD snapshots are represented by
            // their logical head above, never enumerated again as history.
            ranges.Add(new ResourceHeadRange(artifacts + "artifact_", artifacts + "artifact_md_"));
            ranges.Add(new ResourceHeadRange(artifacts + "artifact_md_\uffff", artifacts + "artifact_\uffff"));
            var page = _authority.ReadHeads(scope, ranges, offset, limit);
            var items = new List<ChatArtifact>();
            var unavailable = 0;
            foreach (var head in page.Items)
            {
                try
                {
                    var address = ResourceUri.Parse(head.Identity.Uri);
                    if (address.Segments.Count != 3) continue;
                    if (head.Knowledge != HeadKnowledge.Known) throw new InvalidDataException("The artifact head is unavailable.");
                    var reference = head.Revision;
                    if (address.Provider == "state")
                    {
                        var logicalId = address.Segments.Last();
                        var refs = _revisions.GetRevision(scope, head.Revision)?.Dependencies
                            .Where(item => item.Kind == "immutable-snapshot").ToArray();
                        if (refs == null || refs.Length != 1 || !Owns(session, refs[0].Resource))
                            throw new InvalidDataException("The artifact head has no exact snapshot.");
                        reference = refs[0].Resource;
                        string owner, id; int version;
                        if (!ChatResourceUri.TryParseArtifactRevision(reference, out owner, out id, out version) ||
                            (IsPlan(session, reference) ? PlanIdFromSnapshot(reference)
                                : HtmlWorkspaceIdentity.LogicalId(id) ?? MarkdownDocumentIdentity.LogicalId(id)) != logicalId)
                            throw new InvalidDataException("The artifact head points to a different lineage.");
                    }
                    if (!IsSnapshotReference(reference)) continue;
                    var artifact = InspectMetadata(session, reference);
                    if (artifact.AvailabilityIssue != null) unavailable++;
                    else items.Add(artifact);
                }
                catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
                { unavailable++; }
            }
            return new DocumentArtifactDiscovery(items, page.Generation, unavailable, page.Total, page.NextOffset);
        }
    }

    public sealed class DocumentArtifactDiscovery
    {
        public IReadOnlyList<ChatArtifact> Items { get; private set; }
        public long Generation { get; private set; }
        public int UnavailableResources { get; private set; }
        public int Total { get; private set; }
        public int? NextOffset { get; private set; }
        internal DocumentArtifactDiscovery(IReadOnlyList<ChatArtifact> items, long generation, int unavailableResources, int total, int? nextOffset)
        { Items = items; Generation = generation; UnavailableResources = unavailableResources; Total = total; NextOffset = nextOffset; }
    }
}
