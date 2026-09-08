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
        private static bool IsOwnedArtifactIdentity(ChatSession session, ResourceIdentity identity)
        {
            ResourceAddress address;
            return ResourceUri.TryParse(identity.Uri, out address) && address.Provider == "chat" &&
                address.Segments.Count == 3 && address.Segments[0] == session.DocumentAuthorityId && address.Segments[1] == "artifact";
        }

        // Disposable discovery over one authority capture. Historical metadata is
        // read only by explicit history/exact consumers, never used as a head fallback.
        public DocumentArtifactDiscovery InspectCurrentMetadata(ChatSession session)
        {
            var scope = Scope(session);
            var snapshot = _authority.Capture(scope);
            var items = new List<ChatArtifact>();
            var unavailable = snapshot.Heads.Values.Count(head => head.Knowledge != HeadKnowledge.Known &&
                IsOwnedArtifactIdentity(session, head.Identity));
            var visited = new HashSet<string>(StringComparer.Ordinal);
            foreach (var head in snapshot.Heads.Values.Where(item => Owns(session, item.Revision) && IsSnapshotReference(item.Revision)))
            {
                var reference = head.Revision;
                string owner, id; int version;
                ChatResourceUri.TryParseArtifactRevision(reference, out owner, out id, out version);
                var logicalId = IsPlan(session, reference) ? PlanIdFromSnapshot(reference)
                    : HtmlWorkspaceIdentity.LogicalId(id) ?? MarkdownDocumentIdentity.LogicalId(id);
                var key = logicalId ?? reference.Uri;
                if (!visited.Add(key)) continue;
                try
                {
                    if (logicalId != null)
                    {
                        var identity = new ResourceIdentity(ResourceUri.Create("state", scope.Kind, scope.Id, logicalId));
                        var current = snapshot.GetHead(identity);
                        if (current == null || current.Knowledge != HeadKnowledge.Known)
                            throw new InvalidDataException("The artifact head is unavailable.");
                        var refs = _revisions.GetRevision(scope, current.Revision)?.Dependencies
                            .Where(item => item.Kind == "immutable-snapshot").ToArray();
                        if (refs == null || refs.Length != 1 || !Owns(session, refs[0].Resource))
                            throw new InvalidDataException("The artifact head has no exact snapshot.");
                        reference = refs[0].Resource;
                        string currentOwner, currentId; int currentVersion;
                        if (!ChatResourceUri.TryParseArtifactRevision(reference, out currentOwner, out currentId, out currentVersion) ||
                            (IsPlan(session, reference) ? PlanIdFromSnapshot(reference)
                                : HtmlWorkspaceIdentity.LogicalId(currentId) ?? MarkdownDocumentIdentity.LogicalId(currentId)) != logicalId)
                            throw new InvalidDataException("The artifact head points to a different lineage.");
                    }
                    var artifact = InspectMetadata(session, reference);
                    if (artifact.AvailabilityIssue != null) unavailable++;
                    else items.Add(artifact);
                }
                catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
                { unavailable++; }
            }
            return new DocumentArtifactDiscovery(items, snapshot.Generation, unavailable);
        }
    }

    public sealed class DocumentArtifactDiscovery
    {
        public IReadOnlyList<ChatArtifact> Items { get; private set; }
        public long Generation { get; private set; }
        public int UnavailableResources { get; private set; }
        internal DocumentArtifactDiscovery(IReadOnlyList<ChatArtifact> items, long generation, int unavailableResources)
        { Items = items; Generation = generation; UnavailableResources = unavailableResources; }
    }
}
